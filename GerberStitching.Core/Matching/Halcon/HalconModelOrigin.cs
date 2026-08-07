namespace GerberViewer.Stitching.Matching.Halcon
{
    // [Codex] [Change time: 2026-07-27] [Purpose: Persist the measured NCC domain centre and explicit full-tile origin convention.]
    public sealed class HalconModelOrigin
    {
        public double DomainCenterRowLocal { get; set; }
        public double DomainCenterColumnLocal { get; set; }
        public double ReferenceOriginRow { get; set; }
        public double ReferenceOriginColumn { get; set; }
        public double ConfiguredOffsetRow { get; set; }
        public double ConfiguredOffsetColumn { get; set; }
        public double VerifiedOffsetRow { get; set; }
        public double VerifiedOffsetColumn { get; set; }
        public string Convention { get; set; }
    }
}
