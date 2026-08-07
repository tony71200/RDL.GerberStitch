using System.ComponentModel;

namespace GerberViewer.Stitching.Configuration
{
    public enum HalconShapeModelMode
    {
        Rigid,
        IsotropicScaled
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class HalconShapeModelOptions
    {
        // [Codex] [Change time: 2026-07-27] [Purpose: Match the proven AlignByShapeModel HDevelop parameter profile.]
        public HalconShapeModelMode Mode { get; set; } = HalconShapeModelMode.Rigid;

        // HALCON accepts 0 as the backwards-compatible representation of 'auto'.
        public int NumLevels { get; set; } = 0;
        public double AngleStartRad { get; set; } = -0.17453292519943295; // -10 deg
        public double AngleExtentRad { get; set; } = 0.3490658503988659;  // 20 deg
        public double AngleStepRad { get; set; } = 0.0;                  // 'auto'
        public string Optimization { get; set; } = "auto";
        public string Metric { get; set; } = "use_polarity";

        // A value <= 0 is mapped to the HALCON string value 'auto' by HalconShapeModelProvider.
        public int Contrast { get; set; } = 0;
        public int MinContrast { get; set; } = 0;

        public double ScaleMin { get; set; } = 0.95;
        public double ScaleMax { get; set; } = 1.05;
        public double ScaleStep { get; set; } = 0.01;

        public double MinScore { get; set; } = 0.10;
        public int NumMatches { get; set; } = 1;
        public double MaxOverlap { get; set; } = 1.0;
        public string SubPixel { get; set; } = "least_squares";
        public int SearchNumLevels { get; set; } = 5;
        public double Greediness { get; set; } = 0.95;

        // Retained for backward-compatible configuration loading. The HDevelop-compatible
        // rigid matcher now creates the model from the complete reference image domain.
        public int ModelRoiMarginPixels { get; set; } = 32;
        public int MinModelRoiWidth { get; set; } = 32;
        public int MinModelRoiHeight { get; set; } = 32;

        public override string ToString()
        {
            return "HALCON Shape " + Mode;
        }
    }
}
