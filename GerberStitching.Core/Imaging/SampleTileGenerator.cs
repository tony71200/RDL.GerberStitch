using System;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Models;
using ConfigGerberSampleConfig = GerberViewer.Stitching.Configuration.GerberSampleConfig;
using HalconDotNet;

namespace GerberViewer.Stitching.Imaging
{
    public sealed class SampleCropProgress { public int Completed { get; set; } public int Total { get; set; } public int OrderIndex { get; set; } public SampleTileState State { get; set; } public string Message { get; set; } }
    public sealed class SampleCropResult { public string OutputDirectory { get; set; } public string ManifestPath { get; set; } public bool Completed { get; set; } }

    public sealed class SampleTileGenerator
    {
        public Task<SampleCropResult> GenerateAsync(
            PreparedSampleRun preparedRun,
            string outputRoot,
            CancellationToken cancellationToken,
            IProgress<SampleCropProgress> progress)
        {
            return GenerateAsync(preparedRun, outputRoot, null, cancellationToken, progress);
        }

        // [Claude] [Change time: 2026-08-06] [Purpose: Allow Tab 2 to name the sample folder instead of always using GerberSample_<runId>.]
        public Task<SampleCropResult> GenerateAsync(
            PreparedSampleRun preparedRun,
            string outputRoot,
            string folderName,
            CancellationToken cancellationToken,
            IProgress<SampleCropProgress> progress)
        {
            return Task.Run(() =>
                Generate(preparedRun, outputRoot, folderName, cancellationToken, progress), cancellationToken);
        }

        private SampleCropResult Generate(PreparedSampleRun run, string outputRoot, string folderName, CancellationToken token, IProgress<SampleCropProgress> progress)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));
            if (string.IsNullOrWhiteSpace(outputRoot)) throw new ArgumentException("Output root is required.", nameof(outputRoot));
            var root = Path.GetFullPath(outputRoot);
            var runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var temp = Path.Combine(root, ".creating_" + runId);
            var final = Path.Combine(root, string.IsNullOrWhiteSpace(folderName) ? "GerberSample_" + runId : folderName);
            var marker = Path.Combine(temp, ".gerber_sample_run");
            try
            {
                Directory.CreateDirectory(root);
                Directory.CreateDirectory(temp);
                File.WriteAllText(marker, runId, Encoding.UTF8);
                var tilesDir = Path.Combine(temp, "tiles");
                Directory.CreateDirectory(tilesDir);
                var total = run.TilesByOrder.Count;
                var nccMetadata = new System.Collections.Generic.Dictionary<int, NccModelMetadata>();
                // [Claude] [Change time: 2026-08-07] [Purpose: Generate .ncm/.shm only when Pregenerate is selected; OnTheFly preserves the prior behavior of creating models in memory.]
                var pregenerateModels = run.ConfigSnapshot.ModelGeneration == SampleModelGenerationMode.Pregenerate;
                foreach (var tile in run.TilesByOrder)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new SampleCropProgress 
                        { 
                            Completed = Math.Max(0, tile.OrderIndex), 
                            Total = total, OrderIndex = tile.OrderIndex, 
                            State = SampleTileState.Processing, 
                            Message = "Cropping " + tile.OrderIndex 
                        }
                    );
                    var fileName = FormatName(
                        run.ConfigSnapshot.TileNamePattern, 
                        tile.Row, 
                        tile.Column, 
                        tile.OrderIndex) + ExtensionFor(run.ConfigSnapshot.OutputFormat);
                    var path = Path.Combine(tilesDir, fileName);
                    var baseName = Path.GetFileNameWithoutExtension(fileName);
                    var modelPath = Path.Combine(tilesDir, baseName + ".ncm");
                    var shapeModelPath = Path.Combine(tilesDir, baseName + ".shm");
                    using (var cropped = Crop(run.ProcessedImage, tile))
                    {
                        if (pregenerateModels)
                        {
                            var metadata = AnalyzeContent(cropped);
                            if (metadata.Metrics.ContentClass == SampleTileContentClass.Matchable)
                            {
                                var model = WriteNccModel(cropped, modelPath);
                                metadata.X = model.X; metadata.Y = model.Y; metadata.Width = model.Width; metadata.Height = model.Height; metadata.HasModel = true;
                                WriteShapeModel(cropped, shapeModelPath, metadata.X, metadata.Y, metadata.Width, metadata.Height); metadata.HasShapeModel = true;
                            }
                            nccMetadata[tile.OrderIndex] = metadata;
                        }
                        HOperatorSet.WriteImage(cropped, HalconFormatFor(run.ConfigSnapshot.OutputFormat), 0, path);
                    }
                    VerifyImageReadable(path);
                    if (pregenerateModels)
                    {
                        if (nccMetadata[tile.OrderIndex].HasModel) VerifyNccModelReadable(modelPath);
                        if (nccMetadata[tile.OrderIndex].HasShapeModel) VerifyShapeModelReadable(shapeModelPath);
                    }
                    progress?.Report(new SampleCropProgress 
                    { 
                        Completed = tile.OrderIndex + 1, 
                        Total = total, 
                        OrderIndex = tile.OrderIndex, 
                        State = SampleTileState.Completed, 
                        Message = fileName 
                    });
                }
                var processedSampleFileName = "processed_sample.tiff";
                WriteProcessedSample(Path.Combine(temp, processedSampleFileName), run.ProcessedImage);
                WriteConfig(Path.Combine(temp, "sample_config.json"), run.ConfigSnapshot);

                var manifest = BuildManifest(run, final, nccMetadata);
                ValidateTileFilesInTemp(run, temp);
                var manifestPathInTemp = Path.Combine(temp, "sample_manifest.json");
                SampleManifestSerializer.WriteValidated(manifestPathInTemp, manifest, false);
                if (Directory.Exists(final)) 
                    throw new IOException("Final run directory already exists: " + final);
                Directory.Move(temp, final);
                var manifestPath = Path.Combine(final, "sample_manifest.json");
                var finalValidation = SampleManifestValidator.Validate(SampleManifestSerializer.Read(manifestPath), true);
                if (!finalValidation.IsValid) 
                    throw new InvalidOperationException("Published manifest validation failed: " + string.Join(Environment.NewLine, finalValidation.Errors));
                return new SampleCropResult 
                { 
                    OutputDirectory = final, 
                    ManifestPath = manifestPath, 
                    Completed = true 
                };
            }
            catch
            {
                SafeCleanTemp(temp, marker);
                throw;
            }
        }

        private static HObject Crop(HObject source, SampleTileLayout tile)
        {
            HObject part = null;
            HOperatorSet.CropRectangle1(source, out part, tile.Rectangle.Top, tile.Rectangle.Left, tile.Rectangle.Bottom - 1, tile.Rectangle.Right - 1);
            return part;
        }

        private static void VerifyImageReadable(string path)
        {
            HObject read = null;
            try { HOperatorSet.ReadImage(out read, path); }
            finally { if (read != null && read.IsInitialized()) read.Dispose(); }
        }

        private static void ValidateTileFilesInTemp(PreparedSampleRun run, string tempRoot)
        {
            foreach (var tile in run.TilesByOrder)
            {
                var fileName = FormatName(
                    run.ConfigSnapshot.TileNamePattern, 
                    tile.Row, 
                    tile.Column, 
                    tile.OrderIndex) + ExtensionFor(run.ConfigSnapshot.OutputFormat);
                var path = Path.Combine(tempRoot, "tiles", fileName);
                if (!File.Exists(path)) 
                    throw new FileNotFoundException("Generated tile is missing before publication: " + path, path);
                //var //modelPath = Path.Combine(tempRoot, "tiles", Path.GetFileNameWithoutExtension(fileName) + ".ncm");
                // Exact-zero samples intentionally have no matcher model; manifest validation enforces model presence for other classes.
            }
        }

        private static SampleManifest BuildManifest(PreparedSampleRun run, string finalRoot, System.Collections.Generic.IDictionary<int, NccModelMetadata> nccMetadata)
        {
            return new SampleManifest
            {
                ManifestVersion = SampleManifest.CurrentVersion,
                RootDirectory = finalRoot,
                SourceRasterPath = run.ConfigSnapshot.SourceRasterPath,
                SourceWidth = run.SourceWidth,
                SourceHeight = run.SourceHeight,
                ProcessedWidth = run.ProcessedWidth,
                ProcessedHeight = run.ProcessedHeight,
                CropOrder = run.ConfigSnapshot.CropOrder.ToString(),
                StartOrder = run.ConfigSnapshot.StartOrder.ToString(),
                CreatedUtc = DateTime.Now,
                ProcessedSamplePath = Path.Combine(finalRoot, "processed_sample.tiff"),
                SourceToProcessedTransform = SourceToProcessedTransform(run),
                PreprocessMode = run.PreprocessMetadata == null ? null : run.PreprocessMetadata.Mode.ToString(),
                ProcessedChannelCount = CountChannels(run.ProcessedImage),
                ProcessedBitDepth = BitDepth(run.ProcessedImage),
                // [Claude] [Change time: 2026-08-07] [Purpose: Record model generation mode in the manifest so Worker knows whether .ncm/.shm files are included.]
                ModelGeneration = run.ConfigSnapshot.ModelGeneration.ToString(),
                Tiles = run.TilesByOrder.Select(t =>
                {
                    var baseName = FormatName(run.ConfigSnapshot.TileNamePattern, t.Row, t.Column, t.OrderIndex);
                    // [Claude] [Change time: 2026-08-07] [Purpose: OnTheFly has no metadata; use null-safe lookup instead of direct indexing.]
                    NccModelMetadata metadata;
                    if (nccMetadata == null || !nccMetadata.TryGetValue(t.OrderIndex, out metadata))
                        metadata = null;
                    var hasModel = metadata != null && metadata.HasModel;
                    var hasShapeModel = metadata != null && metadata.HasShapeModel;
                    return new SampleTileInfo
                    {
                        OrderIndex = t.OrderIndex,
                        Row = t.Row,
                        Column = t.Column,
                        ExpectedPath = Path.Combine(finalRoot, "tiles", baseName + ExtensionFor(run.ConfigSnapshot.OutputFormat)),
                        NccModelPath = hasModel ? Path.Combine(finalRoot, "tiles", baseName + ".ncm") : null,
                        NccModelRoiX = hasModel ? (int?)metadata.X : null, NccModelRoiY = hasModel ? (int?)metadata.Y : null,
                        NccModelRoiWidth = hasModel ? (int?)metadata.Width : null, NccModelRoiHeight = hasModel ? (int?)metadata.Height : null,
                        NccReferenceOriginRow = hasModel ? (double?)0.0 : null, NccReferenceOriginColumn = hasModel ? (double?)0.0 : null,
                        NccModelSchemaVersion = hasModel ? 1 : 0,
                        ShapeModelPath = hasShapeModel ? Path.Combine(finalRoot, "tiles", baseName + ".shm") : null,
                        ShapeModelRoiX = hasShapeModel ? (int?)metadata.X : null, ShapeModelRoiY = hasShapeModel ? (int?)metadata.Y : null, ShapeModelRoiWidth = hasShapeModel ? (int?)metadata.Width : null, ShapeModelRoiHeight = hasShapeModel ? (int?)metadata.Height : null,
                        ShapeReferenceOriginRow = hasShapeModel ? (double?)0 : null, ShapeReferenceOriginColumn = hasShapeModel ? (double?)0 : null, ShapeModelSchemaVersion = hasShapeModel ? 1 : 0,
                        SampleContentClass = metadata == null ? null : metadata.Metrics.ContentClass.ToString(),
                        SampleMinGray = metadata == null ? 0d : metadata.Metrics.MinGray,
                        SampleMaxGray = metadata == null ? 0d : metadata.Metrics.MaxGray,
                        SampleMeanGray = metadata == null ? 0d : metadata.Metrics.MeanGray,
                        SampleStdDevGray = metadata == null ? 0d : metadata.Metrics.StdDevGray,
                        SampleNonZeroPixelRatio = metadata == null ? 0d : metadata.Metrics.NonZeroPixelRatio,
                        ExpectedX = t.Rectangle.X,
                        ExpectedY = t.Rectangle.Y,
                        Width = t.Rectangle.Width,
                        Height = t.Rectangle.Height
                    };
                }).ToList()
            };
        }

        // [Codex] [Change time: 2026-07-27] [Purpose: Create a content ROI model with a verified origin representing full-tile (0,0).]
        private static NccModelMetadata WriteNccModel(HObject cropped, string modelPath)
        {
            HTuple width = null, height = null, row1 = null, column1 = null, row2 = null, column2 = null;
            HTuple area = null, centerRow = null, centerColumn = null, modelId = null, verifiedRow = null, verifiedColumn = null;
            HObject content = null, filteredContent = null, roiImage = null, domain = null;
            var options = new MatcherOptions();
            try
            {
                HOperatorSet.GetImageSize(cropped, out width, out height);
                HOperatorSet.Threshold(cropped, out content, 1, 255);
                HOperatorSet.OpeningCircle(content, out filteredContent, 1.5);
                HOperatorSet.SmallestRectangle1(filteredContent, out row1, out column1, out row2, out column2);
                var margin = Math.Max(0, options.NccModelRoiMarginPixels);
                var r1 = Math.Max(0, (int)Math.Floor(row1[0].D) - margin);
                var c1 = Math.Max(0, (int)Math.Floor(column1[0].D) - margin);
                var r2 = Math.Min(height[0].I - 1, (int)Math.Ceiling(row2[0].D) + margin);
                var c2 = Math.Min(width[0].I - 1, (int)Math.Ceiling(column2[0].D) + margin);
                if (r2 - r1 + 1 < options.NccModelRoiMinHeight || c2 - c1 + 1 < options.NccModelRoiMinWidth)
                    throw new InvalidOperationException("NCC content ROI is below configured minimum size.");
                if (r2 - r1 + 1 == height[0].I && c2 - c1 + 1 == width[0].I)
                    throw new InvalidOperationException("A full-size NCC model has no translation search margin in a same-size search image.");
                HOperatorSet.CropRectangle1(cropped, out roiImage, r1, c1, r2, c2);
                HOperatorSet.GetDomain(roiImage, out domain);
                HOperatorSet.AreaCenter(domain, out area, out centerRow, out centerColumn);
                HOperatorSet.CreateNccModel(roiImage, options.NccNumLevels, options.NccAngleStartRad, options.NccAngleExtentRad, options.NccAngleStepRad, options.NccMetric, out modelId);
                var offsetRow = -(r1 + centerRow[0].D);
                var offsetColumn = -(c1 + centerColumn[0].D);
                HOperatorSet.SetNccModelOrigin(modelId, offsetRow, offsetColumn);
                HOperatorSet.GetNccModelOrigin(modelId, out verifiedRow, out verifiedColumn);
                if (Math.Abs(verifiedRow[0].D - offsetRow) > 1e-9 || Math.Abs(verifiedColumn[0].D - offsetColumn) > 1e-9)
                    throw new InvalidOperationException("HALCON did not retain the requested NCC model origin.");
                HOperatorSet.WriteNccModel(modelId, modelPath);
                return new NccModelMetadata { X = c1, Y = r1, Width = c2 - c1 + 1, Height = r2 - r1 + 1 };
            }
            finally
            {
                if (modelId != null) { HOperatorSet.ClearNccModel(modelId); modelId.Dispose(); }
                if (content != null) content.Dispose(); if (filteredContent != null) filteredContent.Dispose(); if (roiImage != null) roiImage.Dispose(); if (domain != null) domain.Dispose();
                DisposeTuple(width); DisposeTuple(height); DisposeTuple(row1); DisposeTuple(column1); DisposeTuple(row2); DisposeTuple(column2);
                DisposeTuple(area); DisposeTuple(centerRow); DisposeTuple(centerColumn); DisposeTuple(verifiedRow); DisposeTuple(verifiedColumn);
            }
        }

        private static void DisposeTuple(HTuple tuple) { if (tuple != null) tuple.Dispose(); }
        private static void WriteShapeModel(HObject cropped,string path,int x,int y,int width,int height)
        {
            HObject roi=null,domain=null;HTuple id=null,area=null,row=null,column=null,vr=null,vc=null;var o=new HalconShapeModelOptions();try{HOperatorSet.CropRectangle1(cropped,out roi,y,x,y+height-1,x+width-1);HOperatorSet.GetDomain(roi,out domain);HOperatorSet.AreaCenter(domain,out area,out row,out column);HOperatorSet.CreateShapeModel(roi,o.NumLevels,o.AngleStartRad,o.AngleExtentRad,o.AngleStepRad,o.Optimization,o.Metric,o.Contrast,o.MinContrast,out id);var or=-(y+row[0].D);var oc=-(x+column[0].D);HOperatorSet.SetShapeModelOrigin(id,or,oc);HOperatorSet.GetShapeModelOrigin(id,out vr,out vc);if(Math.Abs(vr[0].D-or)>1e-9||Math.Abs(vc[0].D-oc)>1e-9)throw new InvalidOperationException("HALCON did not retain the requested shape model origin.");HOperatorSet.WriteShapeModel(id,path);}finally{if(id!=null){HOperatorSet.ClearShapeModel(id);id.Dispose();}if(roi!=null)roi.Dispose();if(domain!=null)domain.Dispose();DisposeTuple(area);DisposeTuple(row);DisposeTuple(column);DisposeTuple(vr);DisposeTuple(vc);}
        }
        private static NccModelMetadata AnalyzeContent(HObject cropped)
        {
            using (var mat = new ImageInteropService().ToMatCopy(cropped))
                return new NccModelMetadata { Metrics = new SampleTileContentAnalyzer().Analyze(mat, 2.0) };
        }

        private sealed class NccModelMetadata { public int X; public int Y; public int Width; public int Height; public bool HasModel; public bool HasShapeModel; public SampleTileContentMetrics Metrics; }

        private static void VerifyNccModelReadable(string path)
        {
            HTuple modelId = null;
            try { HOperatorSet.ReadNccModel(path, out modelId); }
            finally
            {
                if (modelId != null)
                {
                    HOperatorSet.ClearNccModel(modelId);
                    modelId.Dispose();
                }
            }
        }
        private static void VerifyShapeModelReadable(string path){HTuple id=null;try{HOperatorSet.ReadShapeModel(path,out id);}finally{if(id!=null){HOperatorSet.ClearShapeModel(id);id.Dispose();}}}

        private static void WriteProcessedSample(string path, HObject processedImage)
        {
            HOperatorSet.WriteImage(processedImage, "tiff", 0, path);
            VerifyImageReadable(path);
        }

        private static double[][] SourceToProcessedTransform(PreparedSampleRun run)
        {
            var sx = run.SourceWidth == 0 ? 1.0 : (double)run.ProcessedWidth / run.SourceWidth;
            var sy = run.SourceHeight == 0 ? 1.0 : (double)run.ProcessedHeight / run.SourceHeight;
            return new[] 
            { 
                new[] { sx, 0d, 0d }, 
                new[] { 0d, sy, 0d }, 
                new[] { 0d, 0d, 1d } 
            };
        }

        private static int CountChannels(HObject image)
        {
            HTuple channels = null;
            try 
            { 
                HOperatorSet.CountChannels(image, out channels); 
                return channels.I; 
            }
            finally 
            { 
                if (channels != null) 
                    channels.Dispose(); 
            }
        }

        private static int BitDepth(HObject image)
        {
            HTuple type = null;
            try
            {
                HOperatorSet.GetImageType(image, out type);
                var value = type.S;
                if (value == "byte") return 8;
                if (value == "uint2" || value == "int2") return 16;
                if (value == "int4" || value == "real") return 32;
                return 0;
            }
            finally { 
                if (type != null) 
                    type.Dispose(); 
            }
        }

        private static void WriteConfig(string path, ConfigGerberSampleConfig config)
        {
            IndentedJson.WriteDataContract(path, config, typeof(ConfigGerberSampleConfig));
        }

        private static void SafeCleanTemp(string temp, string marker)
        {
            if (string.IsNullOrWhiteSpace(temp) || !Directory.Exists(temp) || string.IsNullOrWhiteSpace(marker) || !File.Exists(marker)) return;
            Directory.Delete(temp, true);
        }

        private static string ExtensionFor(SampleOutputFormat format) 
        { 
            switch (format) 
            { 
                case SampleOutputFormat.BigTiff:
                case SampleOutputFormat.Tiff:
                    return ".tiff";
                case SampleOutputFormat.Png:
                    return ".png";
                case SampleOutputFormat.Bmp: return ".bmp"; 
                case SampleOutputFormat.Jpeg: return ".jpg"; 
                default: return ".png"; 
            } 
        }
        private static string HalconFormatFor(SampleOutputFormat format) 
        { 
            switch (format) 
            {
                case SampleOutputFormat.BigTiff: return "tiff none";
                case SampleOutputFormat.Tiff: return "tiff";
                case SampleOutputFormat.Bmp: return "bmp"; 
                case SampleOutputFormat.Jpeg: return "jpeg"; 
                default: return "png"; } }
        private static string FormatName(string p, int r, int c, int o) 
        { 
            return (p ?? "Sample_R{row:00}_C{col:00}_O{order:000}")
                .Replace("{row:00}", r.ToString("00")).
                Replace("{col:00}", c.ToString("00")).
                Replace("{order:000}", o.ToString("000")); 
        }
    }
}
