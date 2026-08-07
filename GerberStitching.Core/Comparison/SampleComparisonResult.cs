using System.Collections.Generic;

namespace GerberViewer.Stitching.Comparison
{
    public sealed class SampleComparisonResult
    {
        public bool IsAuthoritative { get; set; }
        public bool ProductsGenerated { get; set; }
        public string CoordinateSpace { get; set; }
        public string SamplePreviewPath { get; set; }
        public string StitchedPreviewPath { get; set; }
        public string AlphaOverlayPath { get; set; }
        public string AbsoluteDifferencePath { get; set; }
        public string EdgeOverlayPath { get; set; }
        public string MetadataPath { get; set; }
        public ComparisonMetrics Metrics { get; set; }
        public List<string> Warnings { get; private set; }
        public SampleComparisonResult()
        {
            Warnings = new List<string>();
            Metrics = new ComparisonMetrics();
            CoordinateSpace = "ProcessedSampleGlobalPixels";
        }
    }
}
