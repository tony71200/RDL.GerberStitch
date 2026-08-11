using System;
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

        // ── Helpers ────────────────────────────────────────────────────────────

        private static string GetArg(string[] args, string name, string defaultValue)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return defaultValue;
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
