using System;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Transforms;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment.Neighbor
{
    public enum NeighborDirection
    {
        Left,
        Right,
        Up,
        Down
    }

    public sealed class StitchingOffsetTestContext
    {
        public int AnchorOrderIndex { get; set; }
        public int TargetOrderIndex { get; set; }
        public NeighborDirection Direction { get; set; }
        public string AnchorCapturedPath { get; set; }
        public string TargetCapturedPath { get; set; }
        public Rect AnchorOverlapRoi { get; set; }
        public Rect TargetOverlapRoi { get; set; }
        public Transform2D ExpectedTargetToAnchor { get; set; }
    }

    public sealed class StitchingOffsetTestResult
    {
        public MatchResult Match { get; set; }
        public string FormattedMatch { get; set; }
        public Transform2D MeasuredTargetToAnchor { get; set; }
        public double ResidualX { get; set; }
        public double ResidualY { get; set; }
        public double ResidualAngleDegrees { get; set; }
    }

    // [Tony] [Change time: 2026-07-26] [Purpose: Isolate neighbor validation, manifest ROI derivation and canonical
    // Target-to-Anchor composition from the test UI.]
    public sealed class StitchingOffsetTestRunner
    {
        private readonly IMatcherFactory _factory;
        private readonly EventHandler<MatchResultEventArgs> _resultSink;
        public StitchingOffsetTestRunner(IMatcherFactory factory, EventHandler<MatchResultEventArgs> resultSink = null)
        {
            _factory = factory ?? throw new ArgumentNullException("factory");
            _resultSink = resultSink;
        }

        public static StitchingOffsetTestContext CreateContext(SampleManifest manifest, int anchorOrder,
                                                               int targetOrder, string anchorPath, string targetPath)
        {
            if (manifest == null || manifest.Tiles == null)
                throw new ArgumentException("A manifest with tiles is required.", "manifest");
            var anchor = manifest.Tiles.SingleOrDefault(t => t.OrderIndex == anchorOrder);
            var target = manifest.Tiles.SingleOrDefault(t => t.OrderIndex == targetOrder);
            if (anchor == null || target == null)
                throw new ArgumentException("Anchor or target OrderIndex is absent from the manifest.");
            var rowDelta = target.Row - anchor.Row;
            var columnDelta = target.Column - anchor.Column;
            if (Math.Abs(rowDelta) + Math.Abs(columnDelta) != 1)
                throw new ArgumentException("Selected nodes are not orthogonal neighbors.");
            var left = Math.Max(anchor.ExpectedX, target.ExpectedX);
            var top = Math.Max(anchor.ExpectedY, target.ExpectedY);
            var right = Math.Min(anchor.ExpectedX + anchor.Width, target.ExpectedX + target.Width);
            var bottom = Math.Min(anchor.ExpectedY + anchor.Height, target.ExpectedY + target.Height);
            if (right <= left || bottom <= top)
                throw new ArgumentException("Neighbor tiles have no manifest overlap.");
            return new StitchingOffsetTestContext {
                AnchorOrderIndex = anchorOrder,
                TargetOrderIndex = targetOrder,
                Direction = columnDelta > 0   ? NeighborDirection.Right
                            : columnDelta < 0 ? NeighborDirection.Left
                            : rowDelta > 0    ? NeighborDirection.Down
                                              : NeighborDirection.Up,
                AnchorCapturedPath = anchorPath,
                TargetCapturedPath = targetPath,
                AnchorOverlapRoi =
                    new Rect(left - anchor.ExpectedX, top - anchor.ExpectedY, right - left, bottom - top),
                TargetOverlapRoi =
                    new Rect(left - target.ExpectedX, top - target.ExpectedY, right - left, bottom - top),
                ExpectedTargetToAnchor =
                    Transform2D.Translation(target.ExpectedX - anchor.ExpectedX, target.ExpectedY - anchor.ExpectedY)
            };
        }

        public StitchingOffsetTestResult Run(StitchingOffsetTestContext context, MatcherKind kind,
                                             CancellationToken token)
        {
            Validate(context);
            token.ThrowIfCancellationRequested();
            using (var anchor = Cv2.ImRead(context.AnchorCapturedPath, ImreadModes.Color)) using (
                var target = Cv2.ImRead(context.TargetCapturedPath,
                                        ImreadModes.Color)) using (var matcher = _factory.Create(kind, _resultSink))
            {
                if (anchor.Empty() || target.Empty())
                    throw new InvalidOperationException("A captured image could not be decoded.");
                var request = new MatchRequest { ReferenceImage = anchor,
                                                 MovingImage = target,
                                                 ReferenceRoi = context.AnchorOverlapRoi,
                                                 MovingRoi = context.TargetOverlapRoi,
                                                 InitialMovingToReferenceTransform = Transform2D.Identity,
                                                 Purpose = MatchPurpose.TargetCapturedToAnchorCaptured,
                                                 Context = context.AnchorOrderIndex + "->" + context.TargetOrderIndex };
                var match = matcher.Match(request, token);
                var result = new StitchingOffsetTestResult
                {
                    Match = match,
                    FormattedMatch = matcher.ToString(match)
                };
                if (!match.Success)
                    return result;
                result.MeasuredTargetToAnchor =
                    context.ExpectedTargetToAnchor.Multiply(match.MovingToReferenceTransform);
                result.ResidualX = result.MeasuredTargetToAnchor[0, 2] - context.ExpectedTargetToAnchor[0, 2];
                result.ResidualY = result.MeasuredTargetToAnchor[1, 2] - context.ExpectedTargetToAnchor[1, 2];
                result.ResidualAngleDegrees = match.RotationDeg;
                return result;
            }
        }

        private static void Validate(StitchingOffsetTestContext context)
        {
            if (context == null)
                throw new ArgumentNullException("context");
            if (context.AnchorOrderIndex == context.TargetOrderIndex)
                throw new ArgumentException("Anchor and target must differ.");
            if (string.IsNullOrWhiteSpace(context.AnchorCapturedPath) ||
                string.IsNullOrWhiteSpace(context.TargetCapturedPath))
                throw new ArgumentException("Both captured paths are required.");
            if (context.AnchorOverlapRoi.Width <= 0 || context.AnchorOverlapRoi.Height <= 0 ||
                (context.TargetOverlapRoi.Width != context.AnchorOverlapRoi.Width ||
                 context.TargetOverlapRoi.Height != context.AnchorOverlapRoi.Height))
                throw new ArgumentException("Overlap ROIs must be positive and equally sized.");
            if (context.ExpectedTargetToAnchor == null)
                throw new ArgumentException("ExpectedTargetToAnchor is required.");
        }
    }
}
