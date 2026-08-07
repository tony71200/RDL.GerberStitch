using System.ComponentModel;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Codex] [Change time: 2026-08-04] [Purpose: Configure the global pose-graph least squares optimizer that replaces single-path neighbor propagation.]
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class PoseGraphOptions
    {
        [Category("Pose graph")]
        [Description("Solves all tile poses jointly by robust least squares over neighbor edges. Supersedes single-path propagation and DirectPoseOutlierCorrector.")]
        public bool Enabled { get; set; } = true;

        [Category("Prior weights")]
        [Description("Prior weight pulling a SampleAlignment tile toward its direct pose, scaled by the direct selected common score.")]
        public double LambdaDirect { get; set; } = 0.30;

        [Category("Prior weights")]
        public double LambdaWeak { get; set; } = 0.02;

        [Category("Prior weights")]
        [Description("Prior weight for exact-zero sample tiles. Non-zero so isolated blank tiles still land on the corrected grid.")]
        public double LambdaBlank { get; set; } = 0.02;

        [Category("Prior weights")]
        [Description("Prior weight for manually accepted poses. Large enough to pin them.")]
        public double LambdaPinned { get; set; } = 100.0;

        [Category("Solver")]
        [Description("Radian-to-pixel conversion for rotation residuals. Use about half the tile side.")]
        public double RotationScalePixels { get; set; } = 4000.0;

        [Category("Solver")]
        [Description("Huber threshold in pixels for edge translation residuals.")]
        public double HuberPixels { get; set; } = 8.0;

        [Category("Solver")]
        public int MaxIterations { get; set; } = 8;

        [Category("Solver")]
        public int MaxCgIterations { get; set; } = 500;

        [Category("Solver")]
        public double CgTolerance { get; set; } = 1e-10;

        [Category("Global similarity")]
        [Description("Estimate one shared scale between the sample grid frame and the measured mosaic frame.")]
        public bool EstimateGlobalScale { get; set; } = true;

        [Category("Global similarity")]
        public double MinGlobalScale { get; set; } = 0.99;

        [Category("Global similarity")]
        public double MaxGlobalScale { get; set; } = 1.01;

        [Category("Global similarity")]
        public double MaxGlobalRotationDeg { get; set; } = 0.5;

        [Category("Edge gate")]
        [Description("Loads neighbor edges that the legacy cycle-closure check rejected. Robust loss handles genuine outliers.")]
        public bool UseRejectedEdges { get; set; } = true;

        [Category("Edge gate")]
        [Description("Maximum translation deviation from the expected grid step before an edge is discarded as a period slip. Must stay below the capture overlap of 60 pixels.")]
        public double MaxEdgeResidualPixels { get; set; } = 40.0;

        [Category("Edge gate")]
        public double MaxEdgeRotationDeg { get; set; } = 0.5;

        [Category("Safety")]
        [Description("Aborts the pose-graph result and keeps legacy poses when any tile would move more than this. Zero disables the guard.")]
        public double MaxPoseCorrectionPixels { get; set; } = 120.0;

        [Category("Diagnostics")]
        [Description("Emits a report warning for any used edge whose post-solve translation residual still exceeds this many pixels, so an unresolved seam gap is flagged instead of only visible in the stitched image. Zero disables the check.")]
        public double EdgeResidualWarningPixels { get; set; } = 5.0;

        public override string ToString()
        {
            return Enabled ? "Enabled (Huber " + HuberPixels + " px)" : "Disabled";
        }
    }
}
