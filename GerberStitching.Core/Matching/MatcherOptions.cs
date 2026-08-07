namespace GerberViewer.Stitching.Matching
{
    public enum EccMotionModel
    {
        Translation,
        Euclidean,
        Affine
    }

    public sealed class MatcherOptions
    {
        public double PhaseMinResponse { get; set; } = 0.002;
        public double MinTextureStdDev { get; set; } = 2.0;
        public double MinOverlapRatio { get; set; } = 0.10;
        public double MaxTranslationPixels { get; set; } = 10000.0;
        public EccMotionModel EccMotionModel { get; set; } = EccMotionModel.Euclidean;
        public int PyramidLevels { get; set; } = 5;
        public int MaxIterations { get; set; } = 100;
        public double Epsilon { get; set; } = 1e-5;
        public double MinCorrelation { get; set; } = 0.70;
        public double MaxAbsRotationDeg { get; set; } = 0.1;
        public double MinScale { get; set; } = 0.90;
        public double MaxScale { get; set; } = 1.10;
        public string PreprocessingVariant { get; set; } = "default";
        public int NccNumLevels { get; set; } = 3;
        public double NccAngleStartRad { get; set; } = -0.0034906585;
        public double NccAngleExtentRad { get; set; } = 0.0034906585;
        public double NccAngleStepRad { get; set; } = 0.00087266463;
        public string NccMetric { get; set; } = "use_polarity";
        public double NccMinScore { get; set; } = 0.10;
        public int NccMaxMatches { get; set; } = 1;
        public double NccMaxOverlap { get; set; } = 0.1;
        public string NccSubPixel { get; set; } = "true";
        public int NccModelRoiMarginPixels { get; set; } = 16;
        public int NccModelRoiMinWidth { get; set; } = 16;
        public int NccModelRoiMinHeight { get; set; } = 16;
        public double NccModelRoiMinStdDev { get; set; } = 2.0;
    }
}
