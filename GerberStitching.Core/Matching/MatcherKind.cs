namespace GerberViewer.Stitching.Matching
{
    // [Tony] [Change time: 2026-07-26] [Purpose: Provide a technology-neutral matcher selection contract.]
    public enum MatcherKind
    {
        HalconNcc,
        HalconShapeModel,
        PyramidEcc,
        PyramidPhaseCorrelation
    }
}
