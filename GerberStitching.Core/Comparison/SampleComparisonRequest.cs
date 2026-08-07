using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Comparison
{
    public sealed class SampleComparisonRequest
    {
        public SampleManifest Manifest { get; set; }
        public string StitchedImagePath { get; set; }
        public string OutputDirectory { get; set; }
        public ComparisonMode Mode { get; set; } = ComparisonMode.AlphaOverlay;
        public double Alpha { get; set; } = 0.5;
        public double MaxPreviewMegapixels { get; set; } = 4.0;
        public bool AllowNonAuthoritativeVisualPreview { get; set; }
    }
}
