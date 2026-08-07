using System;
using System.Threading;

namespace GerberViewer.Stitching.Matching
{
    public interface IMatcher : IDisposable
    {
        string MatcherName { get; }
        event EventHandler<MatchResultEventArgs> MatchResultAvailable;
        MatchResult Match(MatchRequest request, CancellationToken cancellationToken);
        string ToString(MatchResult result);
    }
}
