using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
    /// <summary>GenerateSampleManifest result.</summary>
    public sealed class GenerateManifestResult
    {
        public bool Success { get; set; }
        public string ManifestPath { get; set; }
        public string OutputDirectory { get; set; }
        public string ErrorMessage { get; set; }

        internal static GenerateManifestResult Fail(string message)
        {
            return new GenerateManifestResult
            {
                Success = false,
                ErrorMessage = message
            };
        }
    }

    // [Tony] [Change time: 2026-08-11] [Purpose: Hình học một ô cắt, do Master tính từ MapCore và gửi sang.
    // Kiểu phẳng đặt ở tầng façade để RDL_Worker không phải tham chiếu kiểu nội bộ của Core.
    // Khớp 1-1 với GerberTileGeometry bên Master/Worker.]
    public sealed class TileRect
    {
        /// <summary>= CaptureID của Master; cũng là số thứ tự file ảnh (0.bmp, 1.bmp, ...).</summary>
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        /// <summary>Toạ độ pixel trên ảnh sample (đã qua preprocess).</summary>
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>RunAlignStitch progress wrapper for the Core WorkflowProgress model.</summary>
    public sealed class AlignStitchProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Stage { get; set; }

        // [Tony] [Change time: 2026-08-10] [Purpose: Core already reports which tile a progress event belongs to and
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

        /// <summary>Core OrderNodeState name (Processing, SampleAlignOk, NeighborAlignOk, Failed, ...); null when
        /// absent.</summary>
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

    /// <summary>RunAlignStitch result.</summary>
    public sealed class AlignStitchResult
    {
        public bool Success { get; set; }
        public string TiffPath { get; set; }
        public long ElapsedMs { get; set; }

        /// <summary>Total tile count from manifest.Tiles.Count, not report.TileReports.Count; see the unexplained
        /// 85-versus-80 reference-run discrepancy in Phase0_Closeout.</summary>
        public int TileCount { get; set; }

        /// <summary>Tiles aligned by image matching or valid interpolation.</summary>
        public int AlignedTileCount { get; set; }

        /// <summary>Blank or unmeasurable sample tiles placed at nominal grid poses. These are not failures.</summary>
        public int BlankTileCount { get; set; }

        public IList<FailedTileInfo> FailedTiles { get; set; } = new List<FailedTileInfo>();

        /// <summary>RDL error code. Zero means success, including partial success with FailedTiles; -300 means
        /// total failure without output.</summary>
        public int ErrorCode { get; set; }

        public IList<string> Warnings { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }

        /// <summary>Đường dẫn DebugPreview_&lt;yyyyHHmm&gt;.jpg. null khi EmitDebugPreview=false (hợp lệ).</summary>
        public string DebugPreviewPath { get; set; }

        internal static AlignStitchResult Fail(string message)
        {
            return new AlignStitchResult
            {
                Success = false,
                ErrorMessage = message,
                ErrorCode = -300
            };
        }
    }

    /// <summary>
    /// The only facade referenced by RDL Master and Worker. It exposes sample-manifest generation and whole-lot
    /// align/stitch execution over GerberStitching.Core and GerberEngine.
    /// </summary>
    public sealed class GerberStitchFacade
    {
        /// <summary>
        /// Render Gerber input and generate the sample-tile grid and manifest during Master Prepare processing.
        /// </summary>
        public async Task<GenerateManifestResult> GenerateSampleManifest(
            string gerberFilePath, GerberViewer.Stitching.Configuration.GerberSampleConfig gridConfig,
            string outputRoot, int renderDpi = 600, string sampleFolderName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(gerberFilePath) || !File.Exists(gerberFilePath))
                return GenerateManifestResult.Fail("Gerber file not found: " + gerberFilePath);
            if (string.IsNullOrWhiteSpace(outputRoot))
                return GenerateManifestResult.Fail("Output root is required.");

            // GerberSampleConfig exists in both Configuration and Models; fully qualify the Configuration type used
            // by this pipeline.
            var config = gridConfig ?? new GerberViewer.Stitching.Configuration.GerberSampleConfig();

            Bitmap rendered = null;
            HObject sourceImage = null;
            try
            {
                Directory.CreateDirectory(outputRoot);

                // 1) Load + render Gerber -> Bitmap (giống ReadGerberControl.RenderPreviewAsync).
                var engine = new GerberEngineFacade();
                engine.LoadLayer(gerberFilePath);
                // ColorMode conflicts with the BCL type, so fully qualify the GerberEngine type.
                var renderOptions = new RenderOptions
                {
                    Dpi = renderDpi,
                    Mode = GerberEngine.ColorMode.Realistic
                };
                cancellationToken.ThrowIfCancellationRequested();
                rendered = await Task.Run(() => engine.RenderCombined(renderOptions), cancellationToken);

                // Save the in-memory raster to disk for traceability and reuse it as SourceRasterPath without
                // rendering twice.
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
                if (sourceImage != null && sourceImage.IsInitialized())
                    sourceImage.Dispose();
                if (rendered != null)
                    rendered.Dispose();
            }
        }

        /// <summary>
        /// Use the Core GerberSampleConfig data-only DTO directly for grid and crop settings; this method
        /// overwrites SourceRasterPath and OutputDirectory.
        /// </summary>
        public async Task<GenerateManifestResult> GenerateSampleManifestFromRaster(
            string rasterImagePath, GerberViewer.Stitching.Configuration.GerberSampleConfig gridConfig,
            string outputRoot, string sampleFolderName = null,
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

                // Read the raster directly with HALCON, matching the GerberViewer ReplaceSampleImage flow.
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
                if (sourceImage != null && sourceImage.IsInitialized())
                    sourceImage.Dispose();
            }
        }

        /// <summary>
        /// Cắt ảnh raster theo danh sách rect TƯỜNG MINH do Master tính từ MapCore, rồi ghi manifest.
        /// Khác GenerateSampleManifestFromRaster ở chỗ KHÔNG suy lưới từ Rows/Columns/Overlap —
        /// lưới thật của Master ở chế độ EdgePath không đều nên không biểu diễn được bằng lưới đều.
        /// </summary>
        /// <param name="rasterImagePath">Ảnh sample raster (.tiff/.png/.bmp). Không nhận .gbr/.ger.</param>
        /// <param name="rects">Rect từng ô. OrderIndex phải liên tục 0..N-1.</param>
        /// <param name="outputRoot">Thư mục chứa tile + manifest.</param>
        /// <param name="sampleFolderName">Tên thư mục con; null thì Core tự đặt.</param>
        public async Task<GenerateManifestResult> GenerateSampleManifestFromRects(
            string rasterImagePath, IList<TileRect> rects, string outputRoot,
            string sampleFolderName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(rasterImagePath) || !File.Exists(rasterImagePath))
                return GenerateManifestResult.Fail("Raster image not found: " + rasterImagePath);
            if (rects == null || rects.Count == 0)
                return GenerateManifestResult.Fail("Tile rect list is empty.");
            if (string.IsNullOrWhiteSpace(outputRoot))
                return GenerateManifestResult.Fail("Output root is required.");

            HObject sourceImage = null;
            try
            {
                Directory.CreateDirectory(outputRoot);
                cancellationToken.ThrowIfCancellationRequested();

                var image = sourceImage;
                await Task.Run(() => HOperatorSet.ReadImage(out image, rasterImagePath), cancellationToken);
                sourceImage = image;
                if (sourceImage == null || !sourceImage.IsInitialized())
                    return GenerateManifestResult.Fail("HALCON did not return a valid image: " + rasterImagePath);

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

                string boundsError = ValidateRects(rects, sourceSize);
                if (boundsError != null)
                    return GenerateManifestResult.Fail(boundsError);

                var ordered = rects.OrderBy(r => r.OrderIndex).ToList();
                var tiles = ordered
                    .Select(r => new SampleTileLayout
                    {
                        OrderIndex = r.OrderIndex,
                        Row = r.Row,
                        Column = r.Column,
                        Rectangle = new Rectangle(r.X, r.Y, r.Width, r.Height)
                    })
                    .ToList();

                int rows = ordered.Max(r => r.Row) + 1;
                int columns = ordered.Max(r => r.Column) + 1;
                var layout = SampleGeometryCalculator.FromExplicitRects(
                    tiles, ordered[0].Width, ordered[0].Height, rows, columns);

                // PreprocessMode phải là None: rect do Master tính theo kích thước ảnh gốc, mọi phép
                // zoom/resize sẽ làm rect lệch khỏi nội dung.
                var config = new GerberViewer.Stitching.Configuration.GerberSampleConfig
                {
                    Rows = rows,
                    Columns = columns,
                    ProcessedWidth = sourceSize.Width,
                    ProcessedHeight = sourceSize.Height,
                    PreprocessMode = SamplePreprocessMode.None,
                    ModelGeneration = SampleModelGenerationMode.OnTheFly,
                    SourceRasterPath = rasterImagePath,
                    OutputDirectory = outputRoot
                };

                using (var prepared = await Task.Run(
                           () => new SamplePreparationService().Prepare(sourceImage, config, layout,
                                                                        cancellationToken),
                           cancellationToken))
                {
                    var cropResult = string.IsNullOrWhiteSpace(sampleFolderName)
                        ? await new SampleTileGenerator().GenerateAsync(prepared, outputRoot,
                                                                        cancellationToken, null)
                        : await new SampleTileGenerator().GenerateAsync(prepared, outputRoot, sampleFolderName,
                                                                        cancellationToken, null);

                    return new GenerateManifestResult
                    {
                        Success = cropResult.Completed,
                        ManifestPath = cropResult.ManifestPath,
                        OutputDirectory = cropResult.OutputDirectory
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return GenerateManifestResult.Fail(ex.Message);
            }
            finally
            {
                if (sourceImage != null && sourceImage.IsInitialized())
                    sourceImage.Dispose();
            }
        }

        /// <summary>Kiểm rect nằm trong ảnh và OrderIndex liên tục. Trả null nếu hợp lệ.</summary>
        private static string ValidateRects(IList<TileRect> rects, Size imageSize)
        {
            var ids = rects.Select(r => r.OrderIndex).ToList();

            var dup = ids.GroupBy(x => x).Where(g => g.Count() > 1).ToList();
            if (dup.Count > 0)
                return "OrderIndex trùng: " + string.Join(", ", dup.Select(g => g.Key.ToString()));

            var missing = Enumerable.Range(0, ids.Count).Except(ids).ToList();
            if (missing.Count > 0)
                return "OrderIndex thiếu (phải liên tục 0.." + (ids.Count - 1) + "): " +
                       string.Join(", ", missing);

            foreach (TileRect r in rects)
            {
                if (r.Width <= 0 || r.Height <= 0)
                    return "Tile #" + r.OrderIndex + " có kích thước không hợp lệ: " +
                           r.Width + "x" + r.Height;

                if (r.X < 0 || r.Y < 0 ||
                    r.X + r.Width > imageSize.Width || r.Y + r.Height > imageSize.Height)
                    return "Tile #" + r.OrderIndex + " (" + r.X + "," + r.Y + "," +
                           r.Width + "," + r.Height + ") vượt biên ảnh sample " +
                           imageSize.Width + "x" + imageSize.Height + ".";
            }

            return null;
        }

        /// <summary>
        /// Shared implementation for both manifest-generation entry points: validate dimensions, prepare
        /// preprocessing and grid layout, then write tiles and manifest. The caller retains ownership of
        /// sourceImage.
        /// </summary>
        private static async Task<GenerateManifestResult> PrepareAndCropAsync(
            HObject sourceImage, GerberViewer.Stitching.Configuration.GerberSampleConfig config, string outputRoot,
            string sampleFolderName, CancellationToken cancellationToken)
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
                if (w != null)
                    w.Dispose();
                if (h != null)
                    h.Dispose();
            }

            config.OutputDirectory = outputRoot;

            var validation = GerberSampleConfigValidator.Validate(config, sourceSize);
            if (!validation.IsValid)
                return GenerateManifestResult.Fail(string.Join("; ", validation.Errors));

            cancellationToken.ThrowIfCancellationRequested();

            // Prepare the preprocessed image and grid layout. SamplePreparationService owns its internal image
            // copy; the caller retains sourceImage.
            using (var prepared = await Task.Run(
                       () => new SamplePreparationService().Prepare(sourceImage, config, cancellationToken),
                       cancellationToken))
            {
                // Write tiles and the manifest using SampleTileGenerator. A null sampleFolderName uses the
                // Core-generated name.
                var cropResult =
                    string.IsNullOrWhiteSpace(sampleFolderName)
                        ? await new SampleTileGenerator().GenerateAsync(prepared, outputRoot, cancellationToken, null)
                        : await new SampleTileGenerator().GenerateAsync(prepared, outputRoot, sampleFolderName,
                                                                        cancellationToken, null);

                return new GenerateManifestResult { Success = cropResult.Completed,
                                                    ManifestPath = cropResult.ManifestPath,
                                                    OutputDirectory = cropResult.OutputDirectory };
            }
        }

        /// <summary>
        /// Run the full Align -> PoseGraph -> Stitch workflow for a batch received by the Worker.
        /// </summary>
        public async Task<AlignStitchResult> RunAlignStitch(
            string manifestPath, string capturedImagesFolder, AlignStitchConfig options, string outputRoot,
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

            // Use Core CapturedImageLoader for OrderIndex mapping rather than duplicating mapping logic in the facade.
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
                    // [Tony] [Change time: 2026-08-10] [Purpose: Forward the per-tile fields Core already fills in.
                    // p.Image is null on batch-level reports (stitching, pose graph, ...), so every access is guarded
                    // and the defaults on AlignStitchProgress mark those reports as "no tile".]
                    coreProgress = new Progress<WorkflowProgress>(p => progress.Report(new AlignStitchProgress {
                        Current = p.Current, Total = p.Total, Stage = p.Stage,
                        OrderIndex = p.Image == null ? -1 : p.Image.OrderIndex,
                        Row = p.Image == null ? -1 : p.Image.Row, Column = p.Image == null ? -1 : p.Image.Column,
                        FileName = p.Image == null || string.IsNullOrEmpty(p.Image.FilePath)
                                       ? null
                                       : Path.GetFileName(p.Image.FilePath),
                        TileState = p.TileState == null ? null : p.TileState.Value.ToString(), ElapsedMs = p.ElapsedMs
                    }));
                }

                // Use the Core default aligner and manual provider, matching the production full-workflow path.
                var service = new AlignStitchWorkflowService(null, null);
                var workflowResult =
                    await service.RunAsync(coreConfig, manifest, loadResult.Images, coreProgress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var report = workflowResult.Report;
                if (report == null || string.IsNullOrWhiteSpace(report.FinalOutputPath) ||
                    !File.Exists(report.FinalOutputPath))
                {
                    RunOutputLifecycle.Cleanup(finalRunDir, creatingDir);
                    return AlignStitchResult.Fail("Stitch did not produce an output image.");
                }

                // Publish .creating -> final run dir, giống PublishRunDirectory trong AlignStitchingControl.
                var stitchedFileName = Path.GetFileName(report.FinalOutputPath);
                RunOutputLifecycle.Publish(finalRunDir, creatingDir);
                var publishedTiffPath = Path.Combine(finalRunDir, stitchedFileName);

                return MapResult(workflowResult, publishedTiffPath, stopwatch.ElapsedMilliseconds,
                                 manifest.Tiles.Count, finalRunDir);
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

        private static GerberViewer.Stitching.Models.AlignStitchConfig BuildCoreConfig(AlignStitchConfig options,
                                                                                       string manifestPath,
                                                                                       string capturedFolderPath,
                                                                                       string outputPath)
        {
            // Set ConfigVersion to 3 before EnsureComposite; otherwise legacy migration overwrites structured
            // options with mismatched flat defaults.
            var baseConfig = new GerberViewer.Stitching.Models.AlignStitchConfig { ConfigVersion = 3 };
            baseConfig.Input.ManifestPath = manifestPath;
            baseConfig.Input.CapturedFolderPath = capturedFolderPath;
            // [Tony] [Change time: 2026-08-11] [Purpose: Truyền cờ preview từ facade AlignStitchConfig
            // (task07) xuống Core AlignStitchConfig (task04). CloneForRun ở dưới sao chép field này sang
            // bản clone cuối cùng mà AlignStitchWorkflowService thực sự đọc.]
            baseConfig.EmitDebugPreview = options.EmitDebugPreview;

            var engine = AlignStitchConfig.ParseEngine(options.StitchingEngine);
            if (engine.HasValue)
                baseConfig.Stitching.Engine = engine.Value;
            baseConfig.Stitching.EnableBlending = options.EnableBlending;
            baseConfig.CalculateTimeDetail = options.CalculateTimeDetail;

            baseConfig.DirectAlignment.Ncc.MinScore = options.NccMinScore;
            baseConfig.DirectAlignment.Ecc.MinCorrelation = options.EccMinCorrelation;
            baseConfig.DirectAlignment.Geometry.MaxTranslationPixels = options.MaxTranslationPixels;
            baseConfig.DirectAlignment.Geometry.MaxAbsRotationDeg = options.MaxAbsRotationDeg;

            // [Tony 20260813] Direct-pipeline policy from the ini. BuildCoreConfig sets ConfigVersion = 3,
            // so the legacy flat-config migration in AlignStitchConfigMapper (lines 45-62) returns early and
            // never touches Policy - without these two lines the ini values could not reach Core at all.
            baseConfig.DirectAlignment.Policy.AllowCoarseOnlyAcceptance =
                options.AllowCoarseOnlyAcceptance;
            baseConfig.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails =
                options.AllowRefinementFromExpectedWhenCoarseFails;

            AlignStitchConfigMapper.EnsureComposite(baseConfig);

            // CloneForRun assigns OutputPath, locks ConfigVersion to 3, and synchronizes legacy fields, matching
            // production.
            return AlignStitchConfigMapper.CloneForRun(baseConfig, outputPath);
        }

        private static AlignStitchResult MapResult(AlignStitchWorkflowResult workflowResult, string tiffPath,
                                                   long elapsedMs, int manifestTileCount, string finalRunDir)
        {
            var result = new AlignStitchResult { Success = true,
                                                 TiffPath = tiffPath,
                                                 ElapsedMs = elapsedMs,
                                                 TileCount = manifestTileCount,
                                                 ErrorCode = 0,
                                                 Warnings = workflowResult.Report != null &&
                                                                    workflowResult.Report.Warnings != null
                                                                ? new List<string>(workflowResult.Report.Warnings)
                                                                : new List<string>() };

            // [Tony] [Change time: 2026-08-11] [Purpose: WriteStitchedPreview writes into config.OutputPath =
            // .creating, which no longer exists after RunOutputLifecycle.Publish moved everything to
            // finalRunDir. Remap the same way publishedTiffPath already is, just above this call.]
            string previewInCreating =
                workflowResult.Report == null ? null : workflowResult.Report.DebugPreviewPath;
            result.DebugPreviewPath = string.IsNullOrWhiteSpace(previewInCreating)
                ? null
                : Path.Combine(finalRunDir, Path.GetFileName(previewInCreating));

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
                    // Blank or unmeasurable samples placed at nominal grid poses are valid and must not be added to
                    // FailedTiles.
                    result.BlankTileCount++;
                    break;
                default:
                    if (!state.IsStitchable)
                    {
                        result.FailedTiles.Add(new FailedTileInfo { OrderIndex = state.OrderIndex, Row = state.Row,
                                                                    Column = state.Column, Reason = state.Reason });
                    }
                    break;
                }
            }

            // No whole-lot failed-tile threshold is finalized. A valid Stitched.tiff currently means success, while
            // FailedTiles remain diagnostic.
            return result;
        }
    }
}
