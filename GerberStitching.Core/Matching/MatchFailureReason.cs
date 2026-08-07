namespace GerberViewer.Stitching.Matching
{
    public enum MatchFailureReason
    {
        None,
        InvalidInput,
        InvalidRoi,
        SizeMismatch,
        LowTexture,
        ResponseBelowThreshold,
        CorrelationBelowThreshold,
        GeometryRejected,
        NonFiniteTransform,
        Cancelled,
        RuntimeFailure,
        UnsupportedMatcher
    }
}
