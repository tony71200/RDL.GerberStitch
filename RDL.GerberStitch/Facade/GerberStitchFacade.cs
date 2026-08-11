using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GerberEngine;
using GerberViewer.Stitching.Alignment;
using GerberViewer.Stitching.Arrangement;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Imaging;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using HalconDotNet;

namespace RDL.GerberStitch.Facade
{
    /// <summary>Result returned by GenerateSampleManifest.</summary>
    public sealed class GenerateManifestResult
    {
        public bool Success { get; set; }
        public string ManifestPath { get; set; }
        public string OutputDirectory { get; set; }
        public string ErrorMessage { get; set; }

        internal static GenerateManifestResult Fail(string message)
        {
            return new GenerateManifestResult { Success = false, ErrorMessage = message };
        }
    }

    /// <summary>RunAlignStitch progress data wrapping the Core WorkflowProgress type.</summary>
    public sealed class AlignStitchProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Stage { get; set; }

        // [Claude] [Change time: 2026-08-10] [Purpose: Core already reports which tile a progress event belongs to and
        // what state it ended in; the old shape dropped all of it, leaving hosts with only "n/N stage". Carrying the
        // tile identity, state and per-stage duration lets a host show real per-image progress. TileState is a string
        // so hosts do not have to reference GerberStitching.Core just to read the enum. Additive - existing consumers
        // that only read Current/Total/Stage keep working.]

        /// <summary>Tile order index; -1 for batch-level reports that belong to no single tile.</summary>
        public int OrderIndex { get; set; } = -1;
        public int Row { get; set; } = -1;
        public int Column { get; set; } = -1;

        /// <summary>Captured image file name for this tile; null on batch-level reports.</summary>
        public string FileName { get; set; }

        /// <summary>Core OrderNodeState name (Processing, SampleAlignOk, NeighborAlignOk, Failed, ...); null when absent.</summary>
        public string TileState { get; set; }

        /// <summary>Elapsed milliseconds of this stage for this tile only; 0 on batch-level reports.</summary>
        public long ElapsedMs { get; set; }
    }

    public sealed class FailedTileInfo
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Result returned by RunAlignStitch.</summary>
    public sealed class AlignStitchResult
    {
        public bool Success { get; set; }
        public string TiffPath { get; set; }
        public long ElapsedMs { get; set; }

        /// <summary>Total tiles in the manifest (manifest.Tiles.Count); do not use report.TileReports.Count.
        /// See docs/Phase0_Closeout.md section 6.1 for the unexplained 85-versus-80 discrepancy in the reference run.</summary>
        public int TileCount { get; set; }

        /// <summary>Tiles aligned from an image or a valid interpolation (SampleAlignment/NeighborAlignment/AnchorAdjusted/Interpolated/Manual).</summary>
        public int AlignedTileCount { get; set; }

        /// <summary>Blank or unmeasurable sample tiles placed at their nominal grid pose. These are not failures; see docs/Phase0_Closeout.md section 4.2.</summary>
        public int BlankTileCount { get; set; }

        public IList<FailedTileInfo> FailedTiles { get; set; } = new List<FailedTileInfo>();

        /// <summary>RDL error code. 0 means success, including partial success with some FailedTiles;
        /// -300 means the entire operation failed and produced no output image.</summary>
        public int ErrorCode { get; set; }

        public IList<string> Warnings { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }

        internal static AlignStitchResult Fail(string message)
        {
            return new AlignStitchResult { Success = false, ErrorMessage = message, ErrorCode = -300 };
        }
    }

    /// <summary>
    /// The only façade that RDL Master/Worker should reference. It wraps GerberStitching.Core and
    /// GerberEngine behind GenerateSampleManifest (called by Master during Prepare) and
    /// RunAlignStitch (called by Worker when it receives a batch).
    ///
    /// The execution flow is based directly on production code in the GerberViewer repository:
    /// - GenerateSampleManifest: ReadGerberControl (render Gerber → Bitmap) +
    ///   CreateGerberSampleControl.PrepareCurrentSampleAsync/GenerateAsync (Bitmap → sample tile + manifest).
    /// - RunAlignStitch: AlignStitchingControl.RunWorkflowAsync (CloneConfigForRun →
    ///   AlignStitchWorkflowService.RunAsync → validate → publish qua RunOutputLifecycle).
    /// </summary>
    public sealed class GerberStitchFacade
    {
        /// <summary>
        /// Renders a Gerber file, cuts a sample-tile grid, and writes a manifest. Master calls this during Prepare (S002)
        /// when the original input is a Gerber file (.gbr/.ger) that must be rendered first, matching the flow from Tab 1
        /// (ReadGerberControl) to Tab 2 (CreateGerberSampleControl) in GerberViewer.
        /// If the input is already a rendered or scanned raster image, use
        /// <see cref="GenerateSampleManifestFromRaster"/> instead.
        /// </summary>
        /// <param name="gerberFilePath">Original Gerber file (.gbr/.ger).</param>
        /// <param name="gridConfig">
        /// Grid and crop settings; this directly uses the Core GerberSampleConfig type (Rows, Columns,
        /// OverlapValue/Unit, ProcessedWidth/Height, StartOrder, CropOrder, ModelGeneration...).
        /// This is a data-only DTO with no HObject or HALCON handles, so the façade reuses it instead
        /// of duplicating an almost identical class. This method overwrites SourceRasterPath and
        /// OutputDirectory, so callers do not need to set them first.
        /// </param>
        /// <param name="outputRoot">Directory for the intermediate raster, tiles, and manifest.</param>
        /// <param name="renderDpi">Gerber-to-raster DPI. The default is 600, matching the default GerberViewer Tab 1 selection.</param>
        /// <param name="sampleFolderName">
        /// Name of the child directory containing tiles and the manifest. When null, Core assigns
        /// "GerberSample_&lt;timestamp&gt;", the SampleTileGenerator default.
        /// </param>
        public async Task<GenerateManifestResult> GenerateSampleManifest(
            string gerberFilePath,
            GerberViewer.Stitching.Configuration.GerberSampleConfig gridConfig,
            string outputRoot,
            int renderDpi = 600,
            string sampleFolderName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(gerberFilePath) || !File.Exists(gerberFilePath))
                return GenerateManifestResult.Fail("Gerber file not found: " + gerberFilePath);
            if (string.IsNullOrWhiteSpace(outputRoot))
                return GenerateManifestResult.Fail("Output root is required.");

            // GerberSampleConfig exists in both Configuration (the pipeline type used here) and
            // Models (a smaller type not used here), so use the fully qualified name to avoid CS0104.
            var config = gridConfig ?? new GerberViewer.Stitching.Configuration.GerberSampleConfig();

            Bitmap rendered = null;
            HObject sourceImage = null;
            try
            {
                Directory.CreateDirectory(outputRoot);

                // 1) Load and render the Gerber into a Bitmap, as ReadGerberControl.RenderPreviewAsync does.
                var engine = new GerberEngineFacade();
                engine.LoadLayer(gerberFilePath);
                // ColorMode conflicts with System.Drawing.Imaging.ColorMode; qualify it to avoid CS0104.
                var renderOptions = new RenderOptions { Dpi = renderDpi, Mode = GerberEngine.ColorMode.Realistic };
                cancellationToken.ThrowIfCancellationRequested();
                rendered = await Task.Run(() => engine.RenderCombined(renderOptions), cancellationToken);

                // 2) Persist an intermediate raster so SourceRasterPath identifies a real, auditable source,
                //    as when a user selects a PNG in CreateGerberSampleControl. Avoid rendering twice by
                //    converting the in-memory Bitmap directly.
                var rasterPath = Path.Combine(outputRoot, "source_raster.png");
                var sourceSize = new Size(rendered.Width, rendered.Height);
                rendered.Save(rasterPath, ImageFormat.Png);
                sourceImage = new ImageInteropService().ToHObjectCopy(rendered, InteropPixelFormat.Bgr8);
                rendered.Dispose();
                rendered = null;

                config.SourceRasterPath = rasterPath;

                return await PrepareAndCropAsync(sourceImage, config, outputRoot, sampleFolderName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GenerateManifestResult.Fail(ex.Message);
            }
            finally
            {
                if (sourceImage != null && sourceImage.IsInitialized()) sourceImage.Dispose();
                if (rendered != null) rendered.Dispose();
            }
        }

        /// <summary>
        /// Cuts an existing rendered or scanned raster into a sample-tile grid and writes a manifest;
        /// it does not render the source again from Gerber. This matches CreateGerberSampleControl.ReplaceSampleImage and
        /// PrepareCurrentSampleAsync in GerberViewer, where a raster is selected and read through
        /// HOperatorSet.ReadImage without using GerberEngineFacade.
        /// </summary>
        /// <param name="rasterImagePath">Pre-rendered raster image (.tiff/.png/.bmp, etc.).</param>
        /// <param name="gridConfig">Grid and crop configuration; see GenerateSampleManifest.</param>
        /// <param name="outputRoot">Directory for the tiles and manifest.</param>
        /// <param name="sampleFolderName">Child-directory name; see GenerateSampleManifest.</param>
        public async Task<GenerateManifestResult> GenerateSampleManifestFromRaster(
            string rasterImagePath,
            GerberViewer.Stitching.Configuration.GerberSampleConfig gridConfig,
            string outputRoot,
            string sampleFolderName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(rasterImagePath) || !File.Exists(rasterImagePath))
                return GenerateManifestResult.Fail("Raster image not found: " + rasterImagePath);
            if (string.IsNullOrWhiteSpace(outputRoot))
                return GenerateManifestResult.Fail("Output root is required.");

            var config = gridConfig ?? new GerberViewer.Stitching.Configuration.GerberSampleConfig();

            HObject sourceImage = null;
            try
            {
                Directory.CreateDirectory(outputRoot);
                cancellationToken.ThrowIfCancellationRequested();

                // Read the raster directly with HALCON, as ReplaceSampleImage does in
                // CreateGerberSampleControl (OpenFileDialog + HOperatorSet.ReadImage).
                var image = sourceImage;
                await Task.Run(() => HOperatorSet.ReadImage(out image, rasterImagePath), cancellationToken);
                sourceImage = image;
                if (sourceImage == null || !sourceImage.IsInitialized())
                    return GenerateManifestResult.Fail("HALCON did not return a valid image: " + rasterImagePath);

                config.SourceRasterPath = rasterImagePath;

                return await PrepareAndCropAsync(sourceImage, config, outputRoot, sampleFolderName, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GenerateManifestResult.Fail(ex.Message);
            }
            finally
            {
                if (sourceImage != null && sourceImage.IsInitialized()) sourceImage.Dispose();
            }
        }

        /// <summary>
        /// Shared implementation for both manifest-generation methods: validate the configuration against
        /// image dimensions, prepare the preprocessed image and grid layout, then cut tiles and write the manifest.
        /// This method does not dispose sourceImage; the caller owns it and Prepare creates its own copy.
        /// </summary>
        private static async Task<GenerateManifestResult> PrepareAndCropAsync(
            HObject sourceImage,
            GerberViewer.Stitching.Configuration.GerberSampleConfig config,
            string outputRoot,
            string sampleFolderName,
            CancellationToken cancellationToken)
        {
            HTuple w = null, h = null;
            Size sourceSize;
            try
            {
                HOperatorSet.GetImageSize(sourceImage, out w, out h);
                sourceSize = new Size(w.I, h.I);
            }
            finally
            {
                if (w != null) w.Dispose();
                if (h != null) h.Dispose();
            }

            config.OutputDirectory = outputRoot;

            var validation = GerberSampleConfigValidator.Validate(config, sourceSize);
            if (!validation.IsValid)
                return GenerateManifestResult.Fail(string.Join("; ", validation.Errors));

            cancellationToken.ThrowIfCancellationRequested();

            // Prepare the preprocessed image and grid layout, matching PrepareCurrentSampleAsync.
            // SamplePreparationService.Prepare copies sourceImage internally; PreparedSampleRun owns
            // that copy, while the caller retains ownership of and disposes sourceImage.
            using (var prepared = await Task.Run(() => new SamplePreparationService().Prepare(sourceImage, config, cancellationToken), cancellationToken))
            {
                // Cut tiles and write the manifest as CreateGerberSampleControl does through
                // SampleTileGenerator.GenerateAsync. A null sampleFolderName lets Core assign the name.
                var cropResult = string.IsNullOrWhiteSpace(sampleFolderName)
                    ? await new SampleTileGenerator().GenerateAsync(prepared, outputRoot, cancellationToken, null)
                    : await new SampleTileGenerator().GenerateAsync(prepared, outputRoot, sampleFolderName, cancellationToken, null);

                return new GenerateManifestResult
                {
                    Success = cropResult.Completed,
                    ManifestPath = cropResult.ManifestPath,
                    OutputDirectory = cropResult.OutputDirectory
                };
            }
        }

        /// <summary>
        /// Runs Align → Pose Graph → Stitch for the entire batch. Worker calls this after batch dispatch.
        /// </summary>
        /// <param name="manifestPath">Path to sample_manifest.json produced by GenerateSampleManifest.</param>
        /// <param name="capturedImagesFolder">
        /// Folder containing captured images. Core CapturedImageLoader natural-sorts file names and
        /// maps them to the manifest OrderIndex sequence. Images do not need accompanying ExpectedX/Y values
        /// because expected coordinates are already in the manifest; see docs/Phase1_Task02.md section 1.</param>
        /// </param>
        /// <param name="options">Façade alignment/stitching configuration. Null selects defaults.</param>
        /// <param name="outputRoot">Root result directory; each run creates an "AlignStitch_&lt;timestamp&gt;" child directory.</param>
        public async Task<AlignStitchResult> RunAlignStitch(
            string manifestPath,
            string capturedImagesFolder,
            AlignStitchConfig options,
            string outputRoot,
            IProgress<AlignStitchProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var opts = options ?? new AlignStitchConfig();
            try
            {
                opts.Validate();
            }
            catch (Exception ex)
            {
                return AlignStitchResult.Fail("Invalid options: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                return AlignStitchResult.Fail("Manifest not found: " + manifestPath);
            if (string.IsNullOrWhiteSpace(capturedImagesFolder) || !Directory.Exists(capturedImagesFolder))
                return AlignStitchResult.Fail("Captured images folder not found: " + capturedImagesFolder);
            if (string.IsNullOrWhiteSpace(outputRoot))
                return AlignStitchResult.Fail("Output root is required.");

            SampleManifest manifest;
            try
            {
                manifest = SampleManifestSerializer.Read(manifestPath);
            }
            catch (Exception ex)
            {
                return AlignStitchResult.Fail("Manifest unreadable: " + ex.Message);
            }

            // Core CapturedImageLoader rereads the manifest and maps images by OrderIndex.
            // Do not duplicate that mapping in the façade, which could silently misalign indexes.
            var loadResult = new CapturedImageLoader().Load(capturedImagesFolder, manifestPath);
            if (!loadResult.Succeeded)
                return AlignStitchResult.Fail("Captured image mapping failed: " + string.Join("; ", loadResult.Errors));

            var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var finalRunDir = Path.Combine(outputRoot, "AlignStitch_" + runId);
            var creatingDir = Path.Combine(finalRunDir, ".creating");
            Directory.CreateDirectory(creatingDir);

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var coreConfig = BuildCoreConfig(opts, manifestPath, capturedImagesFolder, creatingDir);

                IProgress<WorkflowProgress> coreProgress = null;
                if (progress != null)
                {
                    // [Claude] [Change time: 2026-08-10] [Purpose: Forward the per-tile fields Core already fills in.
                    // p.Image is null on batch-level reports (stitching, pose graph, ...), so every access is guarded
                    // and the defaults on AlignStitchProgress mark those reports as "no tile".]
                    coreProgress = new Progress<WorkflowProgress>(p => progress.Report(new AlignStitchProgress
                    {
                        Current = p.Current,
                        Total = p.Total,
                        Stage = p.Stage,
                        OrderIndex = p.Image == null ? -1 : p.Image.OrderIndex,
                        Row = p.Image == null ? -1 : p.Image.Row,
                        Column = p.Image == null ? -1 : p.Image.Column,
                        FileName = p.Image == null || string.IsNullOrEmpty(p.Image.FilePath)
                            ? null : Path.GetFileName(p.Image.FilePath),
                        TileState = p.TileState == null ? null : p.TileState.Value.ToString(),
                        ElapsedMs = p.ElapsedMs
                    }));
                }

                // Null constructor dependencies select the default Core aligner and manual provider,
                // matching the full-workflow branch in AlignStitchingControl.RunWorkflowAsync.
                var service = new AlignStitchWorkflowService(null, null);
                var workflowResult = await service.RunAsync(coreConfig, manifest, loadResult.Images, coreProgress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var report = workflowResult.Report;
                if (report == null || string.IsNullOrWhiteSpace(report.FinalOutputPath) || !File.Exists(report.FinalOutputPath))
                {
                    RunOutputLifecycle.Cleanup(finalRunDir, creatingDir);
                    return AlignStitchResult.Fail("Stitch did not produce an output image.");
                }

                // Publish .creating as the final run directory, matching AlignStitchingControl.
                var stitchedFileName = Path.GetFileName(report.FinalOutputPath);
                RunOutputLifecycle.Publish(finalRunDir, creatingDir);
                var publishedTiffPath = Path.Combine(finalRunDir, stitchedFileName);

                return MapResult(workflowResult, publishedTiffPath, stopwatch.ElapsedMilliseconds, manifest.Tiles.Count);
            }
            catch (OperationCanceledException)
            {
                RunOutputLifecycle.Cleanup(finalRunDir, creatingDir);
                throw;
            }
            catch (Exception ex)
            {
                RunOutputLifecycle.Cleanup(finalRunDir, creatingDir);
                return AlignStitchResult.Fail(ex.Message);
            }
        }

        private static GerberViewer.Stitching.Models.AlignStitchConfig BuildCoreConfig(
            AlignStitchConfig options, string manifestPath, string capturedFolderPath, string outputPath)
        {
            // Set ConfigVersion to 3 before EnsureComposite; otherwise migration for versions 0/1
            // overwrites structured options with legacy flat fields whose defaults differ.
            // See docs/Phase1_Task04.md section 2 for this ConfigVersion pitfall.
            var baseConfig = new GerberViewer.Stitching.Models.AlignStitchConfig { ConfigVersion = 3 };
            baseConfig.Input.ManifestPath = manifestPath;
            baseConfig.Input.CapturedFolderPath = capturedFolderPath;

            var engine = AlignStitchConfig.ParseEngine(options.StitchingEngine);
            if (engine.HasValue) baseConfig.Stitching.Engine = engine.Value;
            baseConfig.Stitching.EnableBlending = options.EnableBlending;

            baseConfig.DirectAlignment.Ncc.MinScore = options.NccMinScore;
            baseConfig.DirectAlignment.Ecc.MinCorrelation = options.EccMinCorrelation;
            baseConfig.DirectAlignment.Geometry.MaxTranslationPixels = options.MaxTranslationPixels;
            baseConfig.DirectAlignment.Geometry.MaxAbsRotationDeg = options.MaxAbsRotationDeg;

            AlignStitchConfigMapper.EnsureComposite(baseConfig);

            // CloneForRun assigns Output.OutputPath, locks ConfigVersion to 3, and calls SyncLegacy,
            // matching the production path in AlignStitchingControl.CloneConfigForRun.
            return AlignStitchConfigMapper.CloneForRun(baseConfig, outputPath);
        }

        private static AlignStitchResult MapResult(AlignStitchWorkflowResult workflowResult, string tiffPath, long elapsedMs, int manifestTileCount)
        {
            var result = new AlignStitchResult
            {
                Success = true,
                TiffPath = tiffPath,
                ElapsedMs = elapsedMs,
                TileCount = manifestTileCount,
                ErrorCode = 0,
                Warnings = workflowResult.Report != null && workflowResult.Report.Warnings != null
                    ? new List<string>(workflowResult.Report.Warnings)
                    : new List<string>()
            };

            foreach (var state in workflowResult.States)
            {
                switch (state.Source)
                {
                    case PoseSource.SampleAlignment:
                    case PoseSource.NeighborAlignment:
                    case PoseSource.AnchorAdjusted:
                    case PoseSource.Interpolated:
                    case PoseSource.Manual:
                        result.AlignedTileCount++;
                        break;
                    case PoseSource.BlankSampleExpectedPose:
                    case PoseSource.ExpectedGridOffset:
                        // A blank or unmeasurable sample uses its nominal grid pose and remains valid;
                        // it is not a failure and must not be included in FailedTiles.
                        result.BlankTileCount++;
                        break;
                    default:
                        if (!state.IsStitchable)
                        {
                            result.FailedTiles.Add(new FailedTileInfo
                            {
                                OrderIndex = state.OrderIndex,
                                Row = state.Row,
                                Column = state.Column,
                                Reason = state.Reason
                            });
                        }
                        break;
                }
            }

            // The threshold for failing a whole batch because too many tiles failed remains open.
            // For now, a valid Stitched.tiff means success; FailedTiles is reporting information only.
            return result;
        }
    }
}
