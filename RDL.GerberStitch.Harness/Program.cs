using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using RDL.GerberStitch.Facade;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Console harness chạy thử GerberStitchFacade với dữ liệu thật — 2 mode: alignstitch (mặc định) và createsample. Xem docs/Phase1_Task06.md.]
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // Console mặc định dùng codepage hệ thống (vd CP1252/CP437) -> chữ có dấu tiếng Việt
            // (message log của Core) hiển thị sai. Ép UTF-8.
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            AppDomain.CurrentDomain.AssemblyResolve += ResolveHalconFromEnvironment;

            var mode = GetArg(args, "--mode", "alignstitch");
            var configPath = GetArg(args, "--config", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "global_config.json"));
            var config = GlobalConfig.ReadOrNull(configPath);

            if (config == null && !File.Exists(configPath))
                Console.WriteLine("(global_config.json không tồn tại tại " + configPath + " — chỉ dùng CLI arg)");
            Console.WriteLine();

            if (string.Equals(mode, "createsample", StringComparison.OrdinalIgnoreCase))
                return RunCreateSample(args, config);

            return RunAlignStitch(args, config);
        }

        // ── Mode: alignstitch (mặc định) ──────────────────────────────────────

        private static int RunAlignStitch(string[] args, GlobalConfig config)
        {
            var alignStitchCfg = config != null ? config.AlignStitch : null;
            var manifestPath = GetArg(args, "--manifest", alignStitchCfg != null ? alignStitchCfg.ManifestPath : null);
            var imagesFolder = GetArg(args, "--images", alignStitchCfg != null ? alignStitchCfg.ImagesPath : null);
            var outputRoot = GetArg(args, "--out", alignStitchCfg != null ? alignStitchCfg.OutputPath : null);

            if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(imagesFolder) || string.IsNullOrWhiteSpace(outputRoot))
            {
                Console.Error.WriteLine("Thiếu tham số. Truyền --manifest/--images/--out, hoặc đặt section \"AlignStitch\" trong global_config.json.");
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
            var options = new AlignStitchConfig();

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
                Console.WriteLine("PeakWorkingSet : " + (peakWorkingSetBytes / 1024 / 1024) + " MB (tại stage: " + peakAtStage + ")");

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
                Console.Error.WriteLine("Thiếu tham số. Truyền --raster/--out, hoặc đặt section \"CreateSample\" trong global_config.json.");
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
                // Khác với nhánh alignstitch, nhánh này CẦN reference GerberStitching.Core: tham số
                // gridConfig của GenerateSampleManifestFromRaster có kiểu Configuration.GerberSampleConfig
                // của Core (đánh đổi có chủ đích của Task 1.2 — xem docs/Phase1_Task02.md "Đã triển khai"
                // mục 2). C# đòi hỏi assembly định nghĩa type trong signature phải được reference để
                // compile, ngay cả khi chỉ truyền null — nên harness không tránh được việc này, và ở
                // đây tạo tường minh thay vì giả vờ "ẩn" bằng null. Dùng default của Core (Rows=8,
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

            return result.Success ? 0 : 1;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static string GetArg(string[] args, string name, string defaultValue)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return defaultValue;
        }

        // [Claude] [Change time: 2026-08-07] [Purpose: Harness là project tự do, không có folder deploy Master/Worker sẵn halcondotnetxl.dll như production; RDL.GerberStitch/GerberStitching.Core cố ý để Private=False (không copy local) nên phải tự resolve từ HALCONROOT lúc runtime.]
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
