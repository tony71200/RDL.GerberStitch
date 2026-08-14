using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Serialization;
using GerberViewer.Stitching.RobotManager;

namespace GerberViewer.Stitching.Configuration
{
    [DataContract]
    public enum OverlapUnit
    {
        Pixel = 0,
        Percent = 1
    }
    [DataContract]
    public enum SamplePreprocessMode
    {
        None = 0,
        Resize = 1,
        FitPad = 2,
        CenterCrop = 3
    }
    [DataContract]
    public enum SampleOutputFormat
    {
        Tiff = 0,
        BigTiff = 1,
        Png = 2,
        Bmp = 3,
        Jpeg = 4,
    }

    // Let the user choose whether HALCON models are saved to disk in advance or created by the matcher at runtime;
    // record the choice in sample_manifest.json so the Worker knows the model source.
    [DataContract]
    public enum SampleModelGenerationMode
    {
        /// <summary>
        /// Do not write .ncm/.shm files. Leave model paths empty in the manifest; HalconNccModelProvider and
        /// HalconShapeModelProvider create models in memory at runtime. This is the default behavior validated in
        /// Phase 0.
        /// </summary>
        OnTheFly = 0,

        /// <summary>
        /// Analyze each tile. For matchable tiles, write adjacent .ncm and .shm files and store their paths, ROI,
        /// and origin in the manifest so the Worker reads rather than creates the models.
        /// </summary>
        Pregenerate = 1
    }

    [DataContract]
    public sealed class GerberSampleConfig
    {
        [DataMember]
        public string SourceRasterPath { get; set; }
        [DataMember]
        public string OutputDirectory { get; set; }
        [DataMember]
        public int Rows { get; set; } = 8;
        [DataMember]
        public int Columns { get; set; } = 10;
        [DataMember]
        public OrderMode CropOrder { get; set; } = OrderMode.Zigzag;
        [DataMember]
        public StartOrder StartOrder { get; set; } = StartOrder.TopLeftDown;
        [DataMember]
        public bool InvertImage { get; set; } = false;
        [DataMember]
        public double OverlapValue { get; set; } = 70;
        [DataMember]
        public OverlapUnit OverlapUnit { get; set; } = OverlapUnit.Pixel;
        [DataMember]
        public SamplePreprocessMode PreprocessMode { get; set; } = SamplePreprocessMode.None;
        [DataMember]
        public bool KeepAspectRatio { get; set; } = true;
        [DataMember]
        public SampleOutputFormat OutputFormat { get; set; } = SampleOutputFormat.Tiff;
        // Use the model-generation mode selected by the user; OnTheFly preserves the current default behavior.
        [DataMember]
        public SampleModelGenerationMode ModelGeneration { get; set; } = SampleModelGenerationMode.OnTheFly;
        [DataMember]
        public string TileNamePattern { get; set; } = "Sample_R{row:00}_C{col:00}_O{order:000}";
        [DataMember]
        public int ProcessedWidth { get; set; } = 4096;
        [DataMember]
        public int ProcessedHeight { get; set; } = 4096;
        [IgnoreDataMember]
        public Color PadColor { get; set; } = Color.White;
    }

    public sealed class SampleConfigValidationResult
    {
        public List<string> Errors { get; } = new List<string>();
        public bool IsValid
        {
            get {
                return Errors.Count == 0;
            }
        }
    }

    public static class GerberSampleConfigValidator
    {
        public static SampleConfigValidationResult Validate(GerberSampleConfig config, Size sourceSize)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            var result = new SampleConfigValidationResult();
            if (config.Rows < 1)
                result.Errors.Add("Rows must be >= 1.");
            if (config.Columns < 1)
                result.Errors.Add("Columns must be >= 1.");
            if (config.OverlapUnit == OverlapUnit.Percent && (config.OverlapValue < 0 || config.OverlapValue >= 100))
                result.Errors.Add("Percent overlap must satisfy 0 <= P < 100.");

            var fallbackTileWidth =
                CalculateFallbackTileSize(sourceSize.Width, config.Columns, config.OverlapValue, config.OverlapUnit);
            var fallbackTileHeight =
                CalculateFallbackTileSize(sourceSize.Height, config.Rows, config.OverlapValue, config.OverlapUnit);
            var tileWidth = config.ProcessedWidth > 0 ? config.ProcessedWidth : fallbackTileWidth;
            var tileHeight = config.ProcessedHeight > 0 ? config.ProcessedHeight : fallbackTileHeight;
            if (config.Rows >= 1 && config.Columns >= 1 && tileWidth > 0 && tileHeight > 0)
            {
                var overlapX = CalculateOverlap(tileWidth, config.OverlapValue, config.OverlapUnit);
                var overlapY = CalculateOverlap(tileHeight, config.OverlapValue, config.OverlapUnit);
                if (tileWidth <= overlapX)
                    result.Errors.Add("Overlap makes tileWidth <= overlap.");
                if (tileHeight <= overlapY)
                    result.Errors.Add("Overlap makes tileHeight <= overlap.");
            }
            return result;
        }

        private static double CalculateFallbackTileSize(int processedSize, int count, double overlapValue,
                                                        OverlapUnit overlapUnit)
        {
            if (count <= 1)
                return processedSize;
            var overlap = overlapUnit == OverlapUnit.Percent ? 0.0 : overlapValue;
            return (processedSize + Math.Max(0, count - 1) * overlap) / count;
        }

        private static double CalculateOverlap(double tileSize, double overlapValue, OverlapUnit overlapUnit)
        {
            return Math.Max(0, overlapUnit == OverlapUnit.Percent ? tileSize * overlapValue / 100.0 : overlapValue);
        }
    }
}
