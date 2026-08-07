using System;

namespace GerberViewer.Stitching.Matching
{
    /// <summary>Describes the single final result produced by one matcher invocation.</summary>
    public sealed class MatchResultEventArgs : EventArgs
    {
        public MatchResultEventArgs(MatchRequest request, MatchResult result, string stage, DateTime completedAtUtc)
        {
            Request = request;
            Result = result ?? throw new ArgumentNullException("result");
            Stage = stage;
            Purpose = request == null ? default(MatchPurpose) : request.Purpose;
            CompletedAtUtc = completedAtUtc;
        }

        public MatchRequest Request { get; private set; }
        public MatchResult Result { get; private set; }
        public string Stage { get; private set; }
        public MatchPurpose Purpose { get; private set; }
        public DateTime CompletedAtUtc { get; private set; }
    }
}
