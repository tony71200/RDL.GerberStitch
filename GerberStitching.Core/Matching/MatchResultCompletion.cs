using System;
using System.Diagnostics;

namespace GerberViewer.Stitching.Matching
{
    /// <summary>Shared terminal-result path: records elapsed time and publishes exactly once.</summary>
    internal sealed class MatchResultCompletion
    {
        public MatchResult Complete(IMatcher sender, EventHandler<MatchResultEventArgs> sink, MatchRequest request, MatchResult result, Stopwatch stopwatch, string stage)
        {
            if (result == null) throw new ArgumentNullException("result");
            if (stopwatch != null)
            {
                stopwatch.Stop();
                result.ProcessingTime = stopwatch.Elapsed;
            }
            var handler = sink;
            if (handler != null)
            {
                try { handler(sender, new MatchResultEventArgs(request, result, stage, DateTime.UtcNow)); }
                catch (Exception) { /* Diagnostics observers must never alter the matcher result. */ }
            }
            return result;
        }
    }
}
