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
    /// <summary>Kết quả GenerateSampleManifest.</summary>
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

    /// <summary>Tiến độ RunAlignStitch, bọc GerberViewer.Stitching.Models.WorkflowProgress của Core.</summary>
    public sealed class AlignStitchProgress
    {
        public int Current { get; set; }
        public int Total { get; set; }
        public string Stage { get; set; }
    }

    public sealed class FailedTileInfo
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Kết quả RunAlignStitch.</summary>
    public sealed class AlignStitchResult
    {
        public bool Success { get; set; }
        public string TiffPath { get; set; }
        public long ElapsedMs { get; set; }

        /// <summary>Tổng tile theo manifest (manifest.Tiles.Count) — KHÔNG lấy report.TileReports.Count,
        /// xem docs/Phase0_Closeout.md §6.1 (chênh lệch 85 vs 80 tile chưa lý giải được ở run tham chiếu).</summary>
        public int TileCount { get; set; }

        /// <summary>Tile khớp bằng ảnh hoặc nội suy hợp lệ (SampleAlignment/NeighborAlignment/AnchorAdjusted/Interpolated/Manual).</summary>
        public int AlignedTileCount { get; set; }

        /// <summary>Tile sample rỗng/không đo được, đặt theo grid pose danh định. KHÔNG phải lỗi — xem docs/Phase0_Closeout.md §4.2.</summary>
        public int BlankTileCount { get; set; }

        public IList<FailedTileInfo> FailedTiles { get; set; } = new List<FailedTileInfo>();

        /// <summary>Mã lỗi RDL. 0 = OK (kể cả khi có vài FailedTiles — partial success vẫn stitch được),
        /// -300 = fail toàn bộ, không có ảnh output.</summary>
        public int ErrorCode { get; set; }

        public IList<string> Warnings { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }

        internal static AlignStitchResult Fail(string message)
        {
            return new AlignStitchResult { Success = false, ErrorMessage = message, ErrorCode = -300 };
        }
    }

    /// <summary>
    /// Façade duy nhất mà RDL Master/Worker được reference. Bọc GerberStitching.Core +
    /// GerberEngine sau 2 hàm: GenerateSampleManifest (Master, lúc Prepare) và
    /// RunAlignStitch (Worker, khi nhận lô).
    ///
    /// Luồng thực thi tham khảo trực tiếp từ code sản xuất trong repo GerberViewer:
    /// - GenerateSampleManifest: ReadGerberControl (render Gerber → Bitmap) +
    ///   CreateGerberSampleControl.PrepareCurrentSampleAsync/GenerateAsync (Bitmap → sample tile + manifest).
    /// - RunAlignStitch: AlignStitchingControl.RunWorkflowAsync (CloneConfigForRun →
    ///   AlignStitchWorkflowService.RunAsync → validate → publish qua RunOutputLifecycle).
    /// </summary>
    public sealed class GerberStitchFacade
    {
        /// <summary>
        /// Render Gerber, cắt thành lưới sample tile, ghi manifest. Master gọi khi Prepare (S002),
        /// khi đầu vào là file Gerber gốc (.gbr/.ger) cần render trước — giống luồng Tab 1
        /// (ReadGerberControl) nối sang Tab 2 (CreateGerberSampleControl) trong GerberViewer.
        /// Nếu đầu vào đã là ảnh raster có sẵn (đã render/scan), dùng
        /// <see cref="GenerateSampleManifestFromRaster"/> thay vì hàm này.
        /// </summary>
        /// <param name="gerberFilePath">File Gerber gốc (.gbr/.ger).</param>
        /// <param name="gridConfig">
        /// Cấu hình lưới/crop — dùng thẳng kiểu GerberSampleConfig của Core (Rows, Columns,
        /// OverlapValue/Unit, ProcessedWidth/Height, StartOrder, CropOrder, ModelGeneration...).
        /// Đây là DTO thuần dữ liệu (không HObject/HALCON handle) nên façade tái dùng trực
        /// tiếp thay vì nhân đôi một lớp gần giống hệt — SourceRasterPath/OutputDirectory
        /// trên tham số này sẽ bị ghi đè bởi hàm này, không cần set trước.
        /// </param>
        /// <param name="outputRoot">Thư mục ghi ảnh raster trung gian + tile + manifest.</param>
        /// <param name="renderDpi">DPI render Gerber→raster. Default 600 (khớp combobox mặc định Tab 1 GerberViewer).</param>
        /// <param name="sampleFolderName">
        /// Tên thư mục con chứa tile+manifest bên trong outputRoot. Null → Core tự đặt tên
        /// "GerberSample_&lt;timestamp&gt;" (mặc định của SampleTileGenerator).
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

            // GerberSampleConfig là tên trùng giữa Configuration/ (đúng, dùng cho pipeline này) và
            // Models/ (nghèo trường hơn, không dùng ở đây) — phải viết đủ tên để tránh CS0104.
            var config = gridConfig ?? new GerberViewer.Stitching.Configuration.GerberSampleConfig();

            Bitmap rendered = null;
            HObject sourceImage = null;
            try
            {
                Directory.CreateDirectory(outputRoot);

                // 1) Load + render Gerber -> Bitmap (giống ReadGerberControl.RenderPreviewAsync).
                var engine = new GerberEngineFacade();
                engine.LoadLayer(gerberFilePath);
                // ColorMode trùng tên với System.Drawing.Imaging.ColorMode (BCL) — viết đủ tên để tránh CS0104.
                var renderOptions = new RenderOptions { Dpi = renderDpi, Mode = GerberEngine.ColorMode.Realistic };
                cancellationToken.ThrowIfCancellationRequested();
                rendered = await Task.Run(() => engine.RenderCombined(renderOptions), cancellationToken);

                // 2) Lưu raster trung gian ra đĩa để SourceRasterPath có nguồn thật, đối chiếu
                //    được sau này (giống việc user chọn file .png ở CreateGerberSampleControl) —
                //    tránh double-render bằng cách convert thẳng Bitmap đang có trong bộ nhớ.
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
        /// Cắt thành lưới sample tile, ghi manifest — từ một ảnh **raster có sẵn** (đã render/scan),
        /// KHÔNG render lại từ Gerber. Đúng luồng CreateGerberSampleControl.ReplaceSampleImage +
        /// PrepareCurrentSampleAsync trong GerberViewer (chọn file raster qua OpenFileDialog rồi
        /// HOperatorSet.ReadImage), không đụng GerberEngineFacade.
        /// </summary>
        /// <param name="rasterImagePath">File ảnh raster đã render sẵn (.tiff/.png/.bmp...).</param>
        /// <param name="gridConfig">Cấu hình lưới/crop — xem GenerateSampleManifest.</param>
        /// <param name="outputRoot">Thư mục ghi tile + manifest.</param>
        /// <param name="sampleFolderName">Tên thư mục con — xem GenerateSampleManifest.</param>
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

                // Đọc raster trực tiếp bằng HALCON — giống ReplaceSampleImage trong
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
        /// Phần dùng chung giữa GenerateSampleManifest và GenerateSampleManifestFromRaster: validate
        /// config theo kích thước ảnh, Prepare (tiền xử lý + layout lưới), rồi cắt tile + ghi manifest.
        /// KHÔNG dispose sourceImage — caller sở hữu và tự dispose (façade chỉ tạo bản sao qua Prepare).
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

            // Prepare: dựng ảnh đã tiền xử lý + layout lưới (giống PrepareCurrentSampleAsync).
            // SamplePreparationService.Prepare tự CopyImage sourceImage bên trong (PreparedSampleRun
            // sở hữu bản sao riêng) — sourceImage của caller không bị đụng tới, caller tự dispose.
            using (var prepared = await Task.Run(() => new SamplePreparationService().Prepare(sourceImage, config, cancellationToken), cancellationToken))
            {
                // Cắt tile + ghi manifest (giống CreateGerberSampleControl dòng
                // await new SampleTileGenerator().GenerateAsync). sampleFolderName null -> Core tự đặt tên.
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
        /// Chạy trọn Align → PoseGraph → Stitch cho cả lô. Worker gọi khi nhận batch dispatch.
        /// </summary>
        /// <param name="manifestPath">Đường dẫn sample_manifest.json (từ GenerateSampleManifest).</param>
        /// <param name="capturedImagesFolder">
        /// Thư mục ảnh đã chụp. Khớp với manifest bằng CapturedImageLoader (Core) — natural-sort
        /// tên file rồi ghép theo thứ tự OrderIndex của manifest; KHÔNG cần ExpectedX/Y đi kèm ảnh
        /// (toạ độ kỳ vọng đã nằm sẵn trong manifest — xem docs/Phase1_Task02.md §1).
        /// </param>
        /// <param name="options">Config align/stitch (façade). Null → dùng default.</param>
        /// <param name="outputRoot">Thư mục gốc ghi kết quả; mỗi lần chạy tạo 1 folder con "AlignStitch_&lt;timestamp&gt;".</param>
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

            // CapturedImageLoader (Core) tự đọc lại manifest để ghép ảnh theo thứ tự OrderIndex —
            // không tự chế logic ghép tay ở façade, tránh lệch OrderIndex âm thầm (xem docs/Phase1_Task05.md §2.1).
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
                    coreProgress = new Progress<WorkflowProgress>(p => progress.Report(new AlignStitchProgress
                    {
                        Current = p.Current,
                        Total = p.Total,
                        Stage = p.Stage
                    }));
                }

                // AlignStitchWorkflowService(null, null): dùng aligner/manual-provider mặc định của
                // Core, đúng như GerberViewer.Views.AlignStitchingControl.RunWorkflowAsync (nhánh full workflow).
                var service = new AlignStitchWorkflowService(null, null);
                var workflowResult = await service.RunAsync(coreConfig, manifest, loadResult.Images, coreProgress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var report = workflowResult.Report;
                if (report == null || string.IsNullOrWhiteSpace(report.FinalOutputPath) || !File.Exists(report.FinalOutputPath))
                {
                    RunOutputLifecycle.Cleanup(finalRunDir, creatingDir);
                    return AlignStitchResult.Fail("Stitch did not produce an output image.");
                }

                // Publish .creating -> final run dir, giống PublishRunDirectory trong AlignStitchingControl.
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
            // [Claude] [Change time: 2026-08-07] [Purpose: ConfigVersion phải đặt 3 TRƯỚC EnsureComposite,
            // nếu không nhánh migration 0/1 sẽ ghi đè options cấu trúc bằng field phẳng legacy (giá trị
            // mặc định lệch nhau) — xem docs/Phase1_Task04.md §2 (bẫy ConfigVersion).]
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

            // CloneForRun gán Output.OutputPath=outputPath, khoá lại ConfigVersion=3, đồng bộ SyncLegacy —
            // đúng đường production dùng (AlignStitchingControl.CloneConfigForRun).
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
                        // Sample rỗng/không đo được nội dung, đặt theo grid pose danh định — hợp lệ,
                        // KHÔNG phải lỗi (xem docs/Phase0_Closeout.md §4.2). Không đưa vào FailedTiles.
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

            // Ngưỡng "fail quá nhiều tile -> báo lỗi toàn lô" chưa được chốt (open item) — hiện tại
            // façade coi đã có Stitched.tiff hợp lệ là thành công, FailedTiles chỉ mang tính báo cáo.
            return result;
        }
    }
}
