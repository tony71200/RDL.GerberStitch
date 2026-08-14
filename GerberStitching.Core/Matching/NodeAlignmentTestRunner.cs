using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using GerberViewer.Stitching.Matching.Interop;
using GerberViewer.Stitching.Matching.OpenCv;
using GerberViewer.Stitching.Alignment;
using GerberViewer.Stitching.Transforms;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching
{
    /// <summary>Runs one diagnostic match and creates bounded UI copies without changing a workflow pose.</summary>
    public sealed class NodeAlignmentTestRunner
    {
        private const int MaximumPreviewSide = 1200;
        private readonly IMatcherFactory _factory;
        private readonly EventHandler<MatchResultEventArgs> _resultSink;
        private readonly OpenCvMatchInputAdapter _input = new OpenCvMatchInputAdapter();
        private readonly BitmapMatchImageAdapter _bitmap = new BitmapMatchImageAdapter();

        public NodeAlignmentTestRunner(IMatcherFactory factory, EventHandler<MatchResultEventArgs> resultSink = null)
        {
            _factory = factory ?? throw new ArgumentNullException("factory");
            _resultSink = resultSink;
        }

        // [Tony] [Change time: 2026-07-26] [Purpose: Isolate diagnostic matching, transform direction, mask generation
        // and preview ownership from WinForms.]
        public NodeAlignmentTestResult Run(NodeAlignmentTestContext context, NodeAlignmentTestMode testMode,
                                           CancellationToken cancellationToken)
        {
            Validate(context);
            cancellationToken.ThrowIfCancellationRequested();
            using (var reference = _input.DecodeMono8Copy(context.SampleImagePath)) using (
                var moving = _input.DecodeMono8Copy(context.CapturedImagePath))
            {
                // The test bench mirrors production Direct Alignment: adjust request.MovingImage only.
                ModalityAwarePreprocessor.IncreaseContrast(moving, context.PreprocessOptions.Contrast);
                var request = new MatchRequest {
                    ReferenceImage = reference,
                    MovingImage = moving,
                    InitialMovingToReferenceTransform = Transform2D.Identity,
                    Options = context.MatcherOptions,
                    Purpose = testMode == NodeAlignmentTestMode.ProductionPipeline ? MatchPurpose.CapturedToSample
                                                                                   : MatchPurpose.ManualPreview,
                    OrderIndex = context.OrderIndex,
                    SampleTileId = context.OrderIndex.ToString(),
                    ReferenceNccModelPath = context.NccModelPath,
                    ReferenceNccModelRoi = context.NccModelRoi,
                    NccReferenceOriginRow = context.NccReferenceOriginRow,
                    NccReferenceOriginColumn = context.NccReferenceOriginColumn,
                    NccModelSchemaVersion = context.NccModelSchemaVersion,
                    ReferenceShapeModelPath = context.ShapeModelPath,
                    ReferenceShapeModelRoi = context.ShapeModelRoi,
                    ShapeReferenceOriginRow = context.ShapeReferenceOriginRow,
                    ShapeReferenceOriginColumn = context.ShapeReferenceOriginColumn,
                    ShapeModelSchemaVersion = context.ShapeModelSchemaVersion,
                    ShapeOptions = context.ShapeOptions,
                    Context = "Node alignment test only; result is not applied to production pose."
                };
                var stopwatch = Stopwatch.StartNew();
                var match = RunMatch(request, context, testMode, cancellationToken);
                stopwatch.Stop();
                if (match == null)
                    match = MatchResult.Failed(testMode.ToString(), MatchFailureReason.RuntimeFailure,
                                               "Matcher returned no result.");
                if (match.ProcessingTime == TimeSpan.Zero)
                    match.ProcessingTime = stopwatch.Elapsed;
                cancellationToken.ThrowIfCancellationRequested();
                if (!match.Success || match.MovingToReferenceTransform == null)
                    return new NodeAlignmentTestResult { Match = match, FormattedMatch = FormatMatch(match, testMode),
                                                         SamplePreview = ToBoundedBitmap(reference),
                                                         TestPreview = ToBoundedBitmap(moving),
                                                         MaskSource = "Not generated because matching failed." };

                var referenceSize = reference.Size();
                var movingToReferenceTransform = match.MovingToReferenceTransform;

                using (var transformed = WarpMovingToReference(
                    moving,
                    referenceSize,
                    movingToReferenceTransform))
                using (var contentMask = GenerateContentMask(reference))
                using (var overlay = CreateRedMaskOverlay(
                    transformed,
                    contentMask))
                using (var sampleColor = ToBgr(reference))
                using (var movingColor = ToBgr(moving))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new NodeAlignmentTestResult {
                        Match = match,
                        FormattedMatch = FormatMatch(match, testMode),
                        SamplePreview = ToBoundedBitmap(sampleColor),
                        TestPreview = ToBoundedBitmap(moving),
                        TransformedCapturedPreview = ToBoundedBitmap(transformed),
                        OverlayPreview = ToBoundedBitmap(overlay),
                        MaskSource = "Generated sample content mask (Otsu threshold); red overlay opacity 0.50."
                    };
                }
            }
        }

        private MatchResult RunMatch(MatchRequest request, NodeAlignmentTestContext context,
                                     NodeAlignmentTestMode testMode, CancellationToken token)
        {
            if (testMode == NodeAlignmentTestMode.ProductionPipeline)
            {
                var pipeline = new MatcherPipeline(_factory, _resultSink);
                var result = pipeline.MatchDirect(
                    request, context.ProductionCoarseMatcher, context.ProductionRefinementMatcher,
                    context.AllowCoarseOnlyAcceptance, context.AllowRefinementFromExpectedWhenCoarseFails, token);
                return result.SelectedCandidate ??
                       MatchResult.Failed("ProductionPipeline", MatchFailureReason.ResponseBelowThreshold,
                                          result.FailureReason);
            }

            var matcherKind = ToMatcherKind(testMode);
            using (var matcher = _factory.Create(matcherKind, _resultSink)) return matcher.Match(request, token);
        }

        private static MatcherKind ToMatcherKind(NodeAlignmentTestMode testMode)
        {
            switch (testMode)
            {
            case NodeAlignmentTestMode.HalconNcc:
                return MatcherKind.HalconNcc;
            case NodeAlignmentTestMode.HalconShapeModel:
                return MatcherKind.HalconShapeModel;
            case NodeAlignmentTestMode.PyramidEcc:
                return MatcherKind.PyramidEcc;
            default:
                throw new ArgumentOutOfRangeException("testMode", testMode, "Unsupported single matcher test mode.");
            }
        }

        private static string FormatMatch(MatchResult match, NodeAlignmentTestMode testMode)
        {
            return testMode + ": " + (match == null ? "No result" : match.ToString());
        }

        private static void Validate(NodeAlignmentTestContext context)
        {
            if (context == null)
                throw new ArgumentNullException("context");
            if (!File.Exists(context.SampleImagePath))
                throw new FileNotFoundException("Sample tile image was not found.", context.SampleImagePath);
            if (!File.Exists(context.CapturedImagePath))
                throw new FileNotFoundException("Captured image was not found.", context.CapturedImagePath);
        }

        private static Mat WarpMovingToReference(Mat moving, OpenCvSharp.Size referenceSize,
                                                 GerberViewer.Stitching.Transforms.Transform2D transform)
        {
            var matrix = transform.ToArray();
            using (var affine = new Mat(2, 3, MatType.CV_64FC1))
            {
                for (var row = 0; row < 2; row++)
                    for (var column = 0; column < 3; column++)
                        affine.Set<double>(row, column, matrix[row, column]);
                var transformed = new Mat();
                Cv2.WarpAffine(moving, transformed, affine, referenceSize, InterpolationFlags.Linear,
                               BorderTypes.Constant, Scalar.Black);
                return transformed;
            }
        }

        private static Mat GenerateContentMask(Mat sample)
        {
            var mask = new Mat();
            Cv2.Threshold(sample, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            return mask;
        }

        private static Mat CreateRedMaskOverlay(Mat transformed, Mat mask)
        {
            using (var baseColor = ToBgr(transformed)) using (
                var red = new Mat(baseColor.Size(), baseColor.Type(), new Scalar(0, 0, 255))) using (var blended =
                                                                                                         new Mat())
            {
                Cv2.AddWeighted(baseColor, 0.5, red, 0.5, 0, blended);
                var output = baseColor.Clone();
                blended.CopyTo(output, mask);
                return output;
            }
        }

        private static Mat ToBgr(Mat source)
        {
            if (source.Channels() == 3)
                return source.Clone();
            var color = new Mat();
            Cv2.CvtColor(source, color, ColorConversionCodes.GRAY2BGR);
            return color;
        }

        private Bitmap ToBoundedBitmap(Mat source)
        {
            using (var bounded = ResizeBounded(source)) return _bitmap.ToBitmapCopy(bounded);
        }

        private static Mat ResizeBounded(Mat source)
        {
            var scale = Math.Min(1d, MaximumPreviewSide / (double)Math.Max(source.Width, source.Height));
            if (scale >= 1d)
                return source.Clone();
            var resized = new Mat();
            Cv2.Resize(source, resized,
                       new OpenCvSharp.Size(Math.Max(1, (int)Math.Round(source.Width * scale)),
                                            Math.Max(1, (int)Math.Round(source.Height * scale))),
                       0, 0, InterpolationFlags.Area);
            return resized;
        }
    }
}
