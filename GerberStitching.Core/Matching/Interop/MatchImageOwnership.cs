namespace GerberViewer.Stitching.Matching.Interop
{
    // [Codex] [Change time: 2026-07-26] [Purpose: Make source and returned image ownership explicit at adapter boundaries.]
    public enum MatchImageOwnership
    {
        BorrowedSource,
        CallerOwnsReturnedCopy
    }
}
