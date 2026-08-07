namespace GerberViewer.Stitching.Utils
{
    public enum StitchingMethod { CoarseFine, PhaseCorrelation, Manual }
    public sealed class StitchingConfig
    {
        public StitchingMethod Method { get; set; } = StitchingMethod.CoarseFine;
        public double WorkMegapix { get; set; } = 10.0;
        public double RoiMatchFraction { get; set; } = 0.03;
        public int RoiMinPx { get; set; } = 64;
        public double MinOverlapRatio { get; set; } = 0.01;
        public double MaxAbsRotationDeg { get; set; } = 8.0;
        public bool EnforceRobotDirection { get; set; } = true;
        public double ManualOffsetHorizontalTx { get; set; }
        public double ManualOffsetHorizontalTy { get; set; }
        public double ManualOffsetVerticalTx { get; set; }
        public double ManualOffsetVerticalTy { get; set; }
        public StitchingConfig Clone() { return (StitchingConfig)MemberwiseClone(); }
    }
}
