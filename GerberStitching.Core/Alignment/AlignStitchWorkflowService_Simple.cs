using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment
{
    /// <summary>
    /// Test workflow that performs direct alignment only. A rejected direct match keeps the
    /// captured image at its initial expected tile coordinate; no neighbor recovery, graph
    /// reconciliation, interpolation, manual alignment, or other fallback is executed.
    /// </summary>
    public sealed class AlignStitchWorkflowService_Simple
    {
        private readonly AlignStitchWorkflowService _workflow;

        public AlignStitchWorkflowService_Simple(IMatcherFactory matcherFactory = null)
        {
            _workflow = new AlignStitchWorkflowService(null, null, matcherFactory);
        }

        public event EventHandler<MatchResultEventArgs> MatcherResultAvailable
        {
            add { _workflow.MatcherResultAvailable += value; }
            remove { _workflow.MatcherResultAvailable -= value; }
        }

        public Task<AlignStitchWorkflowResult> RunAsync(
            AlignStitchConfig config,
            SampleManifest manifest,
            IList<CapturedImageInfo> captured,
            IProgress<WorkflowProgress> progress,
            CancellationToken cancellationToken)
        {
            return _workflow.RunSimpleAsync(config, manifest, captured, progress, cancellationToken);
        }
    }
}
