using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using RDL.GerberStitch.Facade;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Console harness for exercising GerberStitchFacade with real data in two modes: alignstitch (default) and createsample. See docs/Phase1_Task06.md.]
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // The default system code page can corrupt non-ASCII Core log messages, so force UTF-8.
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            AppDomain.CurrentDomain.AssemblyResolve += ResolveHalconFromEnvironment;

            var mode = GetArg(args, "--mode", "alignstitch");
            var configPath = GetArg(args, "--config", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_config.json"));
            var config = GlobalConfig.ReadOrNull(configPath);

            if (config == null && !File.Exists(configPath))
                Console.WriteLine("(global_config.json was not found at " + configPath + " — using command-line arguments only)");
            Console.WriteLine();

            if (string.Equals(mode, "createsample", StringComparison.OrdinalIgnoreCase))
                return RunCreateSample(args, config);

            if (string.Equals(mode, "alignstitchmem", StringComparison.OrdinalIgnoreCase))
                return RunAlignStitchMem(args, config);

            if (string.Equals(mode, "createsamplemem", StringComparison.OrdinalIgnoreCase))
                return RunCreateSampleMem(args, config);

            return RunAlignStitch(args, config);
        }

        // [Claude] [Change time: 2026-08-11] [Purpose: Let testers tune AlignStitchConfig (NccMinScore,
        // StitchingEngine, CalculateTimeDetail, ...) from a file instead of editing source, reusing the
        // same AlignStitchConfigIniReader Master/Worker uses so the harness stays representative of production.]
        private static AlignStitchConfig ReadAlignStitchOptions(string[] args)
        {
            var iniPath = GetArg(args, "--ini", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "align_stitch.ini"));
            if (!File.Exists(iniPath))
            {
                Console.WriteLine("(align_stitch.ini was not found at " + iniPath + " — using AlignStitchConfig defaults)");
                return new AlignStitchConfig();
            }
            Console.WriteLine("ini      = " + iniPath);
            return AlignStitchConfigIniReader.ReadFromIni(iniPath);
        }

        // ── Mode: alignstitch (default) ──────────────────────────────────────

        private static int RunAlignStitch(string[] args, GlobalConfig config)
        {
            var alignStitchCfg = config != null ? config.AlignStitch : null;
            var manifestPath = GetArg(args, "--manifest", alignStitchCfg != null ? alignStitchCfg.ManifestPath : null);
            var imagesFolder = GetArg(args, "--images", alignStitchCfg != null ? alignStitchCfg.ImagesPath : null);
            var outputRoot = GetArg(args, "--out", alignStitchCfg != null ? alignStitchCfg.OutputPath : null);

            if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(imagesFolder) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Missing arguments. Pass --manifest/--images/--out, or configure the \"AlignStitch\" section in global_config.json.");
                return 2;
            }

            Console.WriteLine("mode     = alignstitch");
            Console.WriteLine("manifest = " + manifestPath);
            Console.WriteLine("images   = " + imagesFolder);
            Console.WriteLine("output   = " + outputRoot);
            Console.WriteLine();

            if (!File.Exists(manifestPath)) { Console.Error.WriteLine("Manifest not found: " + manifestPath); return 2; }
            if (!Directory.Exists(imagesFolder)) { Console.Error.WriteLine("Images folder not found: " + imagesFolder); return 2; }
            Directory.CreateDirectory(outputRoot);

            var facade = new GerberStitchFacade();
            var options = ReadAlignStitchOptions(args);
            Console.WriteLine("engine   = " + options.StitchingEngine + " (CalculateTimeDetail=" + options.CalculateTimeDetail + ")");
            Console.WriteLine();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            var proc = Process.GetCurrentProcess();
            long peakWorkingSetBytes = 0;
            var peakAtStage = "-";
            var lastStage = "startup";
            using (var sampler = new Timer(_ =>
            {
                proc.Refresh();
                if (proc.WorkingSet64 > peakWorkingSetBytes)
                {
                    peakWorkingSetBytes = proc.WorkingSet64;
                    peakAtStage = lastStage;
                }
            }, null, 0, 500))
            {
                var progress = new Progress<AlignStitchProgress>(p =>
                {
                    lastStage = p.Stage ?? lastStage;
                    Console.Write("\r{0,-28} {1,5}/{2,-5}    ", p.Stage ?? "-", p.Current, p.Total);
                });

                var stopwatch = Stopwatch.StartNew();
                AlignStitchResult result;
                try
                {
                    result = facade.RunAlignStitch(manifestPath, imagesFolder, options, outputRoot, progress, cts.Token)
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine("Cancelled.");
                    return 3;
                }
                stopwatch.Stop();
                Console.WriteLine();
                proc.Refresh();

                Console.WriteLine("=== RESULT ===");
                Console.WriteLine("Success        : " + result.Success);
                Console.WriteLine("TiffPath       : " + result.TiffPath);
                Console.WriteLine("ElapsedMs      : " + result.ElapsedMs + " (wall clock " + stopwatch.ElapsedMilliseconds + " ms)");
                Console.WriteLine("TileCount      : " + result.TileCount);
                Console.WriteLine("AlignedTiles   : " + result.AlignedTileCount);
                Console.WriteLine("BlankTiles     : " + result.BlankTileCount);
                Console.WriteLine("FailedTiles    : " + result.FailedTiles.Count);
                foreach (var f in result.FailedTiles)
                    Console.WriteLine("  - OrderIndex=" + f.OrderIndex + " Row=" + f.Row + " Col=" + f.Column + " Reason=" + f.Reason);
                Console.WriteLine("ErrorCode      : " + result.ErrorCode);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    Console.WriteLine("ErrorMessage   : " + result.ErrorMessage);
                Console.WriteLine("Warnings       : " + result.Warnings.Count);
                foreach (var w in result.Warnings)
                    Console.WriteLine("  - " + w);
                Console.WriteLine("PeakWorkingSet : " + (peakWorkingSetBytes / 1024 / 1024) + " MB (at stage: " + peakAtStage + ")");

                var reportFolder = !string.IsNullOrWhiteSpace(result.TiffPath) ? Path.GetDirectoryName(result.TiffPath) : outputRoot;
                RunReport.WriteTo(reportFolder, new RunReport
                {
                    Mode = "alignstitch",
                    RunUtc = DateTime.UtcNow.ToString("O"),
                    ElapsedMs = result.ElapsedMs,
                    WallClockMs = stopwatch.ElapsedMilliseconds,
                    PeakWorkingSetMb = peakWorkingSetBytes / 1024 / 1024,
                    PeakAtStage = peakAtStage,
                    Success = result.Success,
                    TiffPath = result.TiffPath,
                    TileCount = result.TileCount,
                    AlignedTileCount = result.AlignedTileCount,
                    BlankTileCount = result.BlankTileCount,
                    FailedTileCount = result.FailedTiles.Count,
                    ErrorCode = result.ErrorCode,
                    WarningCount = result.Warnings.Count,
                    ErrorMessage = result.ErrorMessage
                });

                return result.Success ? 0 : 1;
            }
        }

        // ── Mode: alignstitchmem ─────────────────────────────────────────────────

        // [Claude] [Change time: 2026-08-14] [Purpose: Exercise the in-memory RunAlignStitch(sampleImagePath,
        // tiles, ...) overload (Task 4 of the settings-file plan) against real Master-shaped payload data,
        // side by side with the unchanged "alignstitch" (manifest-path) mode above -- so a tester can diff
        // Debug_<date>.html's Dx/Dy/DAngle between the two modes to confirm the Task 2 safety checkpoint
        // (crop-in-RAM must be pixel-identical to crop-to-disk) on real data, not just on the build gate.]
        private static int RunAlignStitchMem(string[] args, GlobalConfig config)
        {
            var memCfg = config != null ? config.AlignStitchMem : null;
            var payloadPath = GetArg(args, "--payload", memCfg != null ? memCfg.PayloadPath : null);
            if (string.IsNullOrWhiteSpace(payloadPath) || !File.Exists(payloadPath))
            {
                Console.Error.WriteLine("Missing or missing-on-disk --payload (GerberCommonFileInfo-shaped JSON), " +
                                        "or configure the \"AlignStitchMem\" section in global_config.json.");
                return 2;
            }
            var payload = BatchPayload.ReadOrNull(payloadPath);
            if (payload == null) { Console.Error.WriteLine("Could not parse payload: " + payloadPath); return 2; }

            var sampleImagePath = payload.GerberSampleImagePath;
            var imagesFolder = GetArg(args, "--images", memCfg != null ? memCfg.ImagesPath : null) ?? payload.Folder_CaptureImages;
            var outputRoot = GetArg(args, "--out", memCfg != null ? memCfg.OutputPath : null) ?? payload.GerberManifestOutputDir;

            if (string.IsNullOrWhiteSpace(sampleImagePath) || string.IsNullOrWhiteSpace(imagesFolder) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Payload/config did not resolve sampleImagePath/imagesFolder/outputRoot.");
                return 2;
            }
            if (payload.GerberTiles == null || payload.GerberTiles.Length == 0)
            {
                Console.Error.WriteLine("Payload has no GerberTiles.");
                return 2;
            }

            Console.WriteLine("mode     = alignstitchmem");
            Console.WriteLine("payload  = " + payloadPath);
            Console.WriteLine("sample   = " + sampleImagePath);
            Console.WriteLine("images   = " + imagesFolder);
            Console.WriteLine("output   = " + outputRoot);
            Console.WriteLine("tiles    = " + payload.GerberTiles.Length);
            Console.WriteLine();

            if (!File.Exists(sampleImagePath)) { Console.Error.WriteLine("Sample image not found: " + sampleImagePath); return 2; }
            if (!Directory.Exists(imagesFolder)) { Console.Error.WriteLine("Images folder not found: " + imagesFolder); return 2; }
            Directory.CreateDirectory(outputRoot);

            var facade = new GerberStitchFacade();
            var options = ReadAlignStitchOptions(args);

            // [Claude] [Change time: 2026-08-14] [Purpose: Test Task 6/7/8's settings-file precedence chain.
            // Command-line/global_config.json paths win; the ini's own [GerberAlignStitch] section has no
            // SettingsPath key in this harness (that wiring is Worker-side, deferred), so this is the harness's
            // only way to point at a settings file.]
            options.SettingsFilePath = GetArg(args, "--settings", memCfg != null ? memCfg.SettingsPath : null);
            options.RecipeSettingsPath = GetArg(args, "--recipe-settings", memCfg != null ? memCfg.RecipeSettingsPath : null);
            options.LogWarning = text => Console.WriteLine("[settings] " + text);
            if (HasFlag(args, "--debug")) options.EmitDebugPreview = true;
            if (options.EmitDebugPreview && !string.IsNullOrWhiteSpace(payloadPath) && payload.DebugMode)
                Console.WriteLine("(DebugMode=true from payload)");

            Console.WriteLine("engine   = " + options.StitchingEngine + " (CalculateTimeDetail=" + options.CalculateTimeDetail +
                              ", EmitDebugPreview=" + options.EmitDebugPreview + ")");
            if (!string.IsNullOrWhiteSpace(options.SettingsFilePath)) Console.WriteLine("settings = " + options.SettingsFilePath);
            if (!string.IsNullOrWhiteSpace(options.RecipeSettingsPath)) Console.WriteLine("recipe   = " + options.RecipeSettingsPath);
            Console.WriteLine();

            var tiles = new List<TileRect>(payload.GerberTiles.Length);
            foreach (var t in payload.GerberTiles)
            {
                tiles.Add(new TileRect
                {
                    OrderIndex = t.OrderIndex, Row = t.Row, Column = t.Column,
                    X = t.ExpectedX, Y = t.ExpectedY, Width = t.Width, Height = t.Height
                });
            }

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            var proc = Process.GetCurrentProcess();
            long peakWorkingSetBytes = 0;
            var peakAtStage = "-";
            var lastStage = "startup";
            using (var sampler = new Timer(_ =>
            {
                proc.Refresh();
                if (proc.WorkingSet64 > peakWorkingSetBytes)
                {
                    peakWorkingSetBytes = proc.WorkingSet64;
                    peakAtStage = lastStage;
                }
            }, null, 0, 500))
            {
                var progress = new Progress<AlignStitchProgress>(p =>
                {
                    lastStage = p.Stage ?? lastStage;
                    Console.Write("\r{0,-28} {1,5}/{2,-5}    ", p.Stage ?? "-", p.Current, p.Total);
                });

                var stopwatch = Stopwatch.StartNew();
                AlignStitchResult result;
                try
                {
                    result = facade.RunAlignStitch(sampleImagePath, tiles, imagesFolder, options, outputRoot, progress, cts.Token)
                        .GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine();
                    Console.WriteLine("Cancelled.");
                    return 3;
                }
                stopwatch.Stop();
                Console.WriteLine();
                proc.Refresh();

                Console.WriteLine("=== RESULT ===");
                Console.WriteLine("Success        : " + result.Success);
                Console.WriteLine("TiffPath       : " + result.TiffPath);
                Console.WriteLine("ElapsedMs      : " + result.ElapsedMs + " (wall clock " + stopwatch.ElapsedMilliseconds + " ms)");
                Console.WriteLine("TileCount      : " + result.TileCount);
                Console.WriteLine("AlignedTiles   : " + result.AlignedTileCount);
                Console.WriteLine("BlankTiles     : " + result.BlankTileCount);
                Console.WriteLine("FailedTiles    : " + result.FailedTiles.Count);
                foreach (var f in result.FailedTiles)
                    Console.WriteLine("  - OrderIndex=" + f.OrderIndex + " Row=" + f.Row + " Col=" + f.Column + " Reason=" + f.Reason);
                Console.WriteLine("ErrorCode      : " + result.ErrorCode);
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    Console.WriteLine("ErrorMessage   : " + result.ErrorMessage);
                Console.WriteLine("Warnings       : " + result.Warnings.Count);
                foreach (var w in result.Warnings)
                    Console.WriteLine("  - " + w);
                Console.WriteLine("PeakWorkingSet : " + (peakWorkingSetBytes / 1024 / 1024) + " MB (at stage: " + peakAtStage + ")");

                var reportFolder = !string.IsNullOrWhiteSpace(result.TiffPath) ? Path.GetDirectoryName(result.TiffPath) : outputRoot;
                RunReport.WriteTo(reportFolder, new RunReport
                {
                    Mode = "alignstitchmem",
                    RunUtc = DateTime.Now.ToString("O"),
                    ElapsedMs = result.ElapsedMs,
                    WallClockMs = stopwatch.ElapsedMilliseconds,
                    PeakWorkingSetMb = peakWorkingSetBytes / 1024 / 1024,
                    PeakAtStage = peakAtStage,
                    Success = result.Success,
                    TiffPath = result.TiffPath,
                    TileCount = result.TileCount,
                    AlignedTileCount = result.AlignedTileCount,
                    BlankTileCount = result.BlankTileCount,
                    FailedTileCount = result.FailedTiles.Count,
                    ErrorCode = result.ErrorCode,
                    WarningCount = result.Warnings.Count,
                    ErrorMessage = result.ErrorMessage
                });

                return result.Success ? 0 : 1;
            }
        }

        // ── Mode: createsample ─────────────────────────────────────────────────

        private static int RunCreateSample(string[] args, GlobalConfig config)
        {
            var createSampleCfg = config != null ? config.CreateSample : null;
            var rasterPath = GetArg(args, "--raster", createSampleCfg != null ? createSampleCfg.RasterImagePath : null);
            var outputRoot = GetArg(args, "--out", createSampleCfg != null ? createSampleCfg.OutputPath : null);
            var folderName = GetArg(args, "--folder", null);
            if (string.IsNullOrWhiteSpace(folderName) && !string.IsNullOrWhiteSpace(rasterPath))
                folderName = "Sample_" + Path.GetFileNameWithoutExtension(rasterPath);

            if (string.IsNullOrWhiteSpace(rasterPath) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Missing arguments. Pass --raster/--out, or configure the \"CreateSample\" section in global_config.json.");
                return 2;
            }

            Console.WriteLine("mode   = createsample");
            Console.WriteLine("raster = " + rasterPath);
            Console.WriteLine("output = " + outputRoot);
            Console.WriteLine("folder = " + folderName);
            Console.WriteLine();

            if (!File.Exists(rasterPath)) { Console.Error.WriteLine("Raster not found: " + rasterPath); return 2; }
            Directory.CreateDirectory(outputRoot);

            var facade = new GerberStitchFacade();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            var stopwatch = Stopwatch.StartNew();
            GenerateManifestResult result;
            try
            {
                // Unlike alignstitch, this branch must reference GerberStitching.Core because the
                // GenerateSampleManifestFromRaster signature exposes Configuration.GerberSampleConfig.
                // C# requires the defining assembly even when passing null, so instantiate the Core type
                // explicitly and use its defaults (Rows=8,
                // Columns=10, ProcessedWidth/Height=4096, OverlapValue=70px, Zigzag/TopLeftDown).
                var gridConfig = new GerberViewer.Stitching.Configuration.GerberSampleConfig();
                result = facade.GenerateSampleManifestFromRaster(rasterPath, gridConfig, outputRoot, folderName, cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Cancelled.");
                return 3;
            }
            stopwatch.Stop();

            Console.WriteLine("=== RESULT ===");
            Console.WriteLine("Success          : " + result.Success);
            Console.WriteLine("ManifestPath     : " + result.ManifestPath);
            Console.WriteLine("OutputDirectory  : " + result.OutputDirectory);
            Console.WriteLine("ElapsedMs        : " + stopwatch.ElapsedMilliseconds);
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                Console.WriteLine("ErrorMessage     : " + result.ErrorMessage);

            var reportFolder = !string.IsNullOrWhiteSpace(result.OutputDirectory) ? result.OutputDirectory : outputRoot;
            RunReport.WriteTo(reportFolder, new RunReport
            {
                Mode = "createsample",
                RunUtc = DateTime.UtcNow.ToString("O"),
                ElapsedMs = stopwatch.ElapsedMilliseconds,
                WallClockMs = stopwatch.ElapsedMilliseconds,
                Success = result.Success,
                ManifestPath = result.ManifestPath,
                OutputDirectory = result.OutputDirectory,
                ErrorMessage = result.ErrorMessage
            });

            return result.Success ? 0 : 1;
        }

        // ── Mode: createsamplemem ──────────────────────────────────────────────
        // [Claude] [Change time: 2026-08-15] [Purpose: Dựng lưới theo công thức Master và in ra để đối chiếu,
        // không cần chạy cả pipeline. Task 2 sẽ thêm phần ghi file JSON.]

        private static int RunCreateSampleMem(string[] args, GlobalConfig config)
        {
            var cfg = config != null ? config.CreateSampleMem : null;
            if (cfg == null)
            {
                Console.Error.WriteLine("Thiếu section \"CreateSampleMem\" trong global_config.json.");
                return 2;
            }

            // [Claude] [Change time: 2026-08-16] [Purpose: --tile mo phong "neu camera AOI thuc su
            // chup FOV lon hon", nen ImageWidth/Height (kich thuoc anh chup that) phai doi CUNG voi
            // TileWidth/Height (cua so crop tren raster) -- khong chi rieng crop window nhu ban dau.
            // Buoc luoi (CapturePitch/CamRes) la hang so vat ly, KHONG doi theo --tile, nen
            // CaptureOverlap = ImageWidth - step se lon dan theo --tile, dung tinh than cau hoi goc:
            // FOV cang lon thi overlap khai bao cang lon (4096/63, 4192/159, 4240/207, 4320/287).
            // --tileh cho phep ghi de rieng chieu cao (vd test khong vuong); mac dinh bang --tile.]
            int tileWidth = ParseIntArg(args, "--tile", cfg.TileWidth > 0 ? cfg.TileWidth : cfg.ImageWidth);
            int tileHeight = ParseIntArg(args, "--tileh", tileWidth);

            var spec = new CaptureGridSpec
            {
                CapturePitchX = cfg.CapturePitchX,
                CapturePitchY = cfg.CapturePitchY,
                CamResX = cfg.CamResX,
                CamResY = cfg.CamResY,
                ImageWidth = tileWidth,
                ImageHeight = tileHeight,
                Rows = cfg.Rows,
                Columns = cfg.Columns,
                StartOffsetX = cfg.StartOffsetX,
                StartOffsetY = cfg.StartOffsetY,
                Order = CaptureOrder.ColumnMajorZigzag
            };

            var rasterPath = GetArg(args, "--raster", cfg.RasterImagePath);
            if (!string.IsNullOrWhiteSpace(rasterPath) && File.Exists(rasterPath))
            {
                int rw, rh;
                if (TryReadImageDimensions(rasterPath, out rw, out rh))
                {
                    spec.RasterWidth = rw;
                    spec.RasterHeight = rh;
                }
                else
                {
                    Console.Error.WriteLine("CANH BAO: khong doc duoc kich thuoc raster " + rasterPath +
                                            " -- bo qua kiem tra tran luoi/kep tile.");
                }
            }

            CaptureGridResult grid;
            try
            {
                grid = new GerberStitchFacade().BuildCaptureGrid(spec);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("Spec không hợp lệ: " + ex.Message);
                return 2;
            }

            Console.WriteLine("mode              = createsamplemem");
            Console.WriteLine("raster            = " + rasterPath + "  (" + spec.RasterWidth + " x " +
                              spec.RasterHeight + ")");
            Console.WriteLine("StepX / StepY     = " + grid.StepXPx.ToString("0.####") + " / " +
                              grid.StepYPx.ToString("0.####"));
            Console.WriteLine("CaptureOverlap    = " + grid.CaptureOverlapXPx.ToString("0.##") + " / " +
                              grid.CaptureOverlapYPx.ToString("0.##") + "   (vat ly, theo ban may)");
            Console.WriteLine("TileOverlap       = " + grid.TileOverlapXPx.ToString("0.##") + " / " +
                              grid.TileOverlapYPx.ToString("0.##") + "   (cua so crop)");
            Console.WriteLine("Tile size         = " + grid.TileWidth + " x " + grid.TileHeight);
            Console.WriteLine("Required          = " + grid.RequiredWidth + " x " + grid.RequiredHeight);
            Console.WriteLine("Tiles             = " + grid.Tiles.Count);
            Console.WriteLine("Clamped tiles     = " + grid.ClampedTileIndices.Count);
            foreach (var w in grid.Warnings)
                Console.WriteLine("  WARN: " + w);

            Console.WriteLine();
            Console.WriteLine("10 tile dau (OrderIndex Row Col X Y W H):");
            for (int i = 0; i < Math.Min(10, grid.Tiles.Count); i++)
            {
                var t = grid.Tiles[i];
                Console.WriteLine(string.Format("  {0,3} ({1},{2}) {3,7} {4,7} {5,5} {6,5}",
                                                t.OrderIndex, t.Row, t.Column, t.X, t.Y, t.Width, t.Height));
            }

            // [Claude] [Change time: 2026-08-15] [Purpose: Task 2 -- merge lưới vừa dựng vào template payload
            // Master thật, ghi ra sample_<fov>_o<overlap>.json để Task 5/6 dùng test nhiều FOV.]
            var templatePath = GetArg(args, "--template", cfg.TemplatePayloadPath);
            var outputPath = GetArg(args, "--out", cfg.OutputPath);
            if (!string.IsNullOrWhiteSpace(templatePath) && !string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var written = CaptureGridJsonWriter.Write(templatePath, outputPath, rasterPath, spec, grid);
                    Console.WriteLine();
                    Console.WriteLine("Payload           = " + written);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Ghi payload that bai: " + ex.Message);
                    return 1;
                }
            }
            return 0;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static string GetArg(string[] args, string name, string defaultValue)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return defaultValue;
        }

        private static int ParseIntArg(string[] args, string name, int fallback)
        {
            var raw = GetArg(args, name, null);
            int parsed;
            return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out parsed) ? parsed : fallback;
        }

        private static bool HasFlag(string[] args, string name)
        {
            foreach (var a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // [Claude] [Change time: 2026-08-16] [Purpose: System.Drawing.Image.FromFile giai ma toan bo
        // pixel qua GDI+ de lay Width/Height. Voi raster Gerber TIFF hang GB tren dia (vd
        // "2-2 Gerber fix black.tiff" ~1.28GB), GDI+ nem OutOfMemoryException -- day la loi codec
        // TIFF cu cua GDI+ (bo giai ma khong scale duoc voi anh lon), khong phai thieu RAM that.
        // Doc thang header (TIFF IFD tag 256/257, PNG IHDR, BMP DIB header) ma khong giai ma pixel
        // nao. JPEG/dinh dang khac van roi ve GDI+ vi hiem khi dat kich thuoc gay OOM trong domain
        // nay (luon la .tif/.tiff cho raster Gerber that).]
        private static bool TryReadImageDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".tif" || ext == ".tiff")
                    return TryReadTiffDimensions(path, out width, out height);
                if (ext == ".png")
                    return TryReadPngDimensions(path, out width, out height);
                if (ext == ".bmp")
                    return TryReadBmpDimensions(path, out width, out height);

                using (var img = System.Drawing.Image.FromFile(path))
                {
                    width = img.Width;
                    height = img.Height;
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        // [Claude] [Change time: 2026-08-16] [Purpose: File Gerber raster hang GB thuc te tren may
        // nay ("2-2 Gerber fix black.tiff", 1.28GB) la BigTIFF (magic=43), khong phai classic TIFF
        // (magic=42) -- da xac nhan bang cach doc header tho: "49 49 2b 00 08 00 00 00 10 00 ...".
        // BigTIFF doi offset IFD tu 4 byte len 8 byte, so luong entry tu 2 byte len 8 byte, va moi
        // entry tu 12 byte len 20 byte {tag(2),type(2),count(8),value(8)}. Ho tro ca hai dang.]
        private static bool TryReadTiffDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var mark = br.ReadBytes(4);
                if (mark.Length < 4) return false;
                bool little;
                if (mark[0] == 'I' && mark[1] == 'I') little = true;
                else if (mark[0] == 'M' && mark[1] == 'M') little = false;
                else return false;

                Func<int> readUInt16 = () =>
                {
                    var b = br.ReadBytes(2);
                    return little ? (b[0] | (b[1] << 8)) : ((b[0] << 8) | b[1]);
                };
                Func<long> readUInt32 = () =>
                {
                    var b = br.ReadBytes(4);
                    return little
                               ? ((uint)b[0] | ((uint)b[1] << 8) | ((uint)b[2] << 16) | ((uint)b[3] << 24))
                               : ((uint)b[3] | ((uint)b[2] << 8) | ((uint)b[1] << 16) | ((uint)b[0] << 24));
                };
                Func<long> readUInt64 = () =>
                {
                    var b = br.ReadBytes(8);
                    if (!little) Array.Reverse(b);
                    return BitConverter.ToInt64(b, 0);
                };

                int magic = little ? (mark[2] | (mark[3] << 8)) : ((mark[2] << 8) | mark[3]);
                bool isBigTiff = magic == 43;
                if (magic != 42 && !isBigTiff) return false;

                long ifdOffset;
                int entrySize;
                if (isBigTiff)
                {
                    readUInt16(); // bytesize cua offset, luon = 8, khong can doc
                    readUInt16(); // hang so 0
                    ifdOffset = readUInt64();
                    entrySize = 20;
                }
                else
                {
                    ifdOffset = readUInt32();
                    entrySize = 12;
                }
                if (ifdOffset <= 0 || ifdOffset >= fs.Length) return false;

                fs.Seek(ifdOffset, SeekOrigin.Begin);
                long entryCount = isBigTiff ? readUInt64() : readUInt16();
                for (long i = 0; i < entryCount; i++)
                {
                    long entryStart = fs.Position;
                    int tag = readUInt16();
                    int type = readUInt16();
                    // count -- khong can gia tri, chi can bo qua dung 4 (classic) hoac 8 (BigTIFF) byte
                    long value;
                    if (isBigTiff)
                    {
                        readUInt64(); // count
                        if (type == 3) value = readUInt16(); // SHORT
                        else if (type == 4) value = readUInt32(); // LONG
                        else if (type == 16 || type == 17) value = readUInt64(); // LONG8/SLONG8
                        else
                        {
                            fs.Seek(entryStart + entrySize, SeekOrigin.Begin);
                            continue;
                        }
                    }
                    else
                    {
                        readUInt32(); // count
                        if (type == 3) value = readUInt16(); // SHORT
                        else if (type == 4) value = readUInt32(); // LONG
                        else
                        {
                            fs.Seek(entryStart + entrySize, SeekOrigin.Begin);
                            continue;
                        }
                    }

                    if (tag == 256) width = (int)value;
                    else if (tag == 257) height = (int)value;

                    fs.Seek(entryStart + entrySize, SeekOrigin.Begin);
                    if (width > 0 && height > 0) return true;
                }
                return width > 0 && height > 0;
            }
        }

        private static bool TryReadPngDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                var sig = br.ReadBytes(8);
                byte[] pngSig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                if (sig.Length < 8) return false;
                for (int i = 0; i < 8; i++)
                    if (sig[i] != pngSig[i])
                        return false;

                br.ReadBytes(4); // length cua chunk IHDR, luon = 13, khong can doc
                var chunkType = br.ReadBytes(4);
                if (chunkType.Length < 4 || chunkType[0] != 'I' || chunkType[1] != 'H' ||
                    chunkType[2] != 'D' || chunkType[3] != 'R')
                    return false;

                var w = br.ReadBytes(4); // big-endian
                var h = br.ReadBytes(4);
                if (w.Length < 4 || h.Length < 4) return false;
                width = (w[0] << 24) | (w[1] << 16) | (w[2] << 8) | w[3];
                height = (h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3];
                return width > 0 && height > 0;
            }
        }

        private static bool TryReadBmpDimensions(string path, out int width, out int height)
        {
            width = 0;
            height = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var br = new BinaryReader(fs))
            {
                if (br.ReadByte() != 'B' || br.ReadByte() != 'M') return false;
                fs.Seek(18, SeekOrigin.Begin); // bfSize+reserved+bfOffBits+biSize = 18 byte
                width = br.ReadInt32();
                height = Math.Abs(br.ReadInt32());
                return width > 0 && height > 0;
            }
        }

        // [Claude] [Change time: 2026-08-07] [Purpose: The standalone harness has no production Master/Worker deployment folder containing HALCON assemblies. Because the library references intentionally use Private=False, resolve them from HALCONROOT at runtime.]
        private static Assembly ResolveHalconFromEnvironment(object sender, ResolveEventArgs e)
        {
            var simpleName = new AssemblyName(e.Name).Name;
            if (simpleName != "halcondotnetxl" && simpleName != "hdevenginedotnetxl") return null;

            var halconRoot = Environment.GetEnvironmentVariable("HALCONROOT");
            if (string.IsNullOrEmpty(halconRoot)) return null;

            var path = Path.Combine(halconRoot, "bin", "dotnet35", simpleName + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}
