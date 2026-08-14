using System.Collections.Generic;
using System.Threading;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Configuration;

namespace GerberViewer.Stitching.Stitching
{
    public interface IWorkflowStitchingService
    {
        string Stitch(AlignStitchConfig config, IList<CapturedImageInfo> images, IList<TileWorkflowState> states,
                      CancellationToken cancellationToken);
    }

    // [Tony] [Change time: 2026-07-26] [Purpose: Isolate stitch implementation and output-path creation from stage
    // orchestration.]
    public sealed class WorkflowStitchingService : IWorkflowStitchingService
    {
        public StitchingExecutionReport LastExecutionReport { get; private set; }
        public string Stitch(AlignStitchConfig config, IList<CapturedImageInfo> images, IList<TileWorkflowState> states,
                             CancellationToken cancellationToken)
        {
            return Stitch(config, images, states, "Stitched.tiff", cancellationToken);
        }

        public string Stitch(AlignStitchConfig config, IList<CapturedImageInfo> images, IList<TileWorkflowState> states,
                             string outputFileName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = AlignStitchConfigMapper.ToStitchingOptions(config);
            if (string.IsNullOrWhiteSpace(options.OutputPath))
                return null;
            options.OutputPath =
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(options.OutputPath), outputFileName);
            try
            {
                return new GlobalTransformStitcher().StitchFromGlobalTransforms(images, states, options, null,
                                                                                cancellationToken);
            }
            finally
            {
                LastExecutionReport = options.ExecutionReport;
            }
        }
    }
}
