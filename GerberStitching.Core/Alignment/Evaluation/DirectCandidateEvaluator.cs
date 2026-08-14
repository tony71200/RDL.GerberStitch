using System;
using System.Diagnostics;
using System.Threading;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Transforms;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment.Evaluation
{
    public enum DirectSelectionPolicy
    {
        WeightedCommonMetric = 0,
        [Obsolete("Compatibility rollback only. Use WeightedCommonMetric.")] LegacyAlwaysRefinementOnSuccess = 1
    }

    // [Tony] [Change time: 2026-08-01] [Purpose: Evaluate algorithm-independent Direct candidates on one shared
    // two-tier image domain without changing production selection.]
    public sealed class DirectEvaluationOptions
    {
        public bool ReportOnlyEnabled { get; set; } = true;
        public DirectSelectionPolicy SelectionPolicy { get; set; } = DirectSelectionPolicy.WeightedCommonMetric;
        public double MinRefinementGain { get; set; } = 0.01;
        public double MaxRefinementDeltaTranslationPixels { get; set; } = 20.0;
        public double MaxRefinementDeltaRotationDeg { get; set; } = 1.0;
        public int Tier1MaximumSide { get; set; } = 1024;
        public int Tier2MaximumSide { get; set; } = 2048;
        public double AmbiguousScoreGap { get; set; } = 0.03;
        public double EdgeWeight { get; set; } = 0.50;
        public double GradientNccWeight { get; set; } = 0.30;
        public double ValidOverlapWeight { get; set; } = 0.20;
        public double BorderPenaltyWeight { get; set; } = 0.10;
        public double EdgeDistanceTolerancePixels { get; set; } = 4.0;
        public double MinimumGradientStdDev { get; set; } = 1e-3;
        public override string ToString()
        {
            return ReportOnlyEnabled ? "Report-only, two-tier" : "Disabled";
        }
    }

    public sealed class DirectCandidateEvaluationRequest
    {
        public Mat ReferenceImage { get; set; }
        public Mat MovingImage { get; set; }
        public Mat ReferenceValidMask { get; set; }
        public Mat MovingValidMask { get; set; }
        public MatchResult CoarseCandidate { get; set; }
        public MatchResult RefinementCandidate { get; set; }
        public DirectEvaluationOptions Options { get; set; } = new DirectEvaluationOptions();
    }

    public sealed class DirectMetricBreakdown
    {
        public bool Success { get; set; }
        public double SymmetricEdgeProximity { get; set; }
        public double GradientNcc { get; set; }
        public double ValidOverlap { get; set; }
        public double BorderPenalty { get; set; }
        public double CombinedScore { get; set; }
        public string FailureReason { get; set; }
    }

    public sealed class DirectCandidateEvaluation
    {
        public bool Success { get; set; }
        public string FailureReason { get; set; }
        public int TierUsed { get; set; }
        public bool Escalated { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DirectMetricBreakdown Coarse { get; set; }
        public DirectMetricBreakdown Refinement { get; set; }
        public string RecommendedStage { get; set; }
        public double ScoreGap { get; set; }
        public double DeltaTranslationPixels { get; set; }
        public double DeltaRotationDeg { get; set; }
    }

    // [Tony] [Change time: 2026-08-01] [Purpose: Make the common metric and hard geometry gates authoritative for
    // Direct selection.]
    public static class DirectCandidateSelector
    {
        public static void Apply(MatcherPipelineResult pipeline, DirectCandidateEvaluation evaluation,
                                 DirectEvaluationOptions options)
        {
            if (pipeline == null)
                throw new ArgumentNullException("pipeline");
            options = options ?? new DirectEvaluationOptions();
            var coarse = pipeline.CoarseCandidate;
            var refinement = pipeline.RefinementCandidate;
            var coarseValid = IsCandidateGeometryValid(coarse);
            var refinementValid = IsCandidateGeometryValid(refinement);
            if (coarse != null && coarse.Success && !coarseValid)
            {
                if (refinementValid && pipeline.UsedRefinementFromExpected)
                    Select(pipeline, refinement, "REFINEMENT_FROM_EXPECTED_SELECTED",
                           "InvalidCoarseGeometryRefinementFromExpectedAccepted",
                           Score(evaluation == null ? null : evaluation.Refinement));
                else
                    Reject(pipeline, "InvalidCoarseCandidateGeometry");
                return;
            }
            if (coarse == null || !coarse.Success)
            {
                if (refinementValid && pipeline.UsedRefinementFromExpected)
                    Select(pipeline, refinement, "REFINEMENT_FROM_EXPECTED_SELECTED", "RefinementFromExpectedAccepted",
                           Score(evaluation == null ? null : evaluation.Refinement));
                return;
            }
            if (refinement == null || !refinement.Success || !refinementValid)
            {
                Select(pipeline, coarse,
                       refinement == null ? "COARSE_ONLY_CONFIGURED" : "COARSE_SELECTED_REFINEMENT_FAILED",
                       refinement == null ? "RefinementNotConfigured"
                                          : (!refinement.Success ? "RefinementFailedCoarseAccepted"
                                                                 : "InvalidRefinementGeometryCoarsePreserved"),
                       Score(evaluation == null ? null : evaluation.Coarse));
                return;
            }
#pragma warning disable 618
            if (options.SelectionPolicy == DirectSelectionPolicy.LegacyAlwaysRefinementOnSuccess)
            {
                Select(pipeline, refinement, "REFINEMENT_SELECTED_LEGACY_ROLLBACK", "LegacyAlwaysRefinementOnSuccess",
                       Score(evaluation == null ? null : evaluation.Refinement));
                return;
            }
#pragma warning restore 618
            double[,] delta;
            try
            {
                delta = coarse.MovingToReferenceTransform.Invert()
                            .Multiply(refinement.MovingToReferenceTransform)
                            .ToArray();
            }
            catch (Exception ex)
            {
                Select(pipeline, coarse, "COARSE_SELECTED_REFINEMENT_DELTA_REJECTED",
                       "RefinementTransformDeltaInvalid: " + ex.GetType().Name,
                       Score(evaluation == null ? null : evaluation.Coarse));
                return;
            }
            var translation = Math.Sqrt(delta[0, 2] * delta[0, 2] + delta[1, 2] * delta[1, 2]);
            var rotation = Math.Abs(Math.Atan2(delta[1, 0], delta[0, 0]) * 180d / Math.PI);
            if (evaluation != null)
            {
                evaluation.DeltaTranslationPixels = translation;
                evaluation.DeltaRotationDeg = rotation;
            }
            if (double.IsNaN(translation) || double.IsInfinity(translation) || double.IsNaN(rotation) ||
                double.IsInfinity(rotation) || translation > options.MaxRefinementDeltaTranslationPixels ||
                rotation > options.MaxRefinementDeltaRotationDeg)
            {
                Select(pipeline, coarse, "COARSE_SELECTED_REFINEMENT_DELTA_REJECTED",
                       "RefinementTransformDeltaExceeded", Score(evaluation == null ? null : evaluation.Coarse));
                return;
            }
            var coarseMetric = evaluation == null ? null : evaluation.Coarse;
            var refinementMetric = evaluation == null ? null : evaluation.Refinement;
            if (coarseMetric == null || !coarseMetric.Success || refinementMetric == null || !refinementMetric.Success)
            {
                Select(pipeline, coarse, "COARSE_SELECTED_REFINEMENT_NO_GAIN", "CommonMetricUnavailableCoarsePreserved",
                       Score(coarseMetric));
                return;
            }
            if (refinementMetric.CombinedScore - coarseMetric.CombinedScore < options.MinRefinementGain)
                Select(pipeline, coarse, "COARSE_SELECTED_REFINEMENT_NO_GAIN", "RefinementCommonMetricGainBelowMinimum",
                       coarseMetric.CombinedScore);
            else
                Select(pipeline, refinement, "REFINEMENT_SELECTED_COMMON_METRIC_GAIN",
                       "RefinementCommonMetricGainAccepted", refinementMetric.CombinedScore);
        }

        private static double Score(DirectMetricBreakdown metric)
        {
            return metric != null && metric.Success ? metric.CombinedScore : double.NaN;
        }
        private static bool IsCandidateGeometryValid(MatchResult candidate)
        {
            if (candidate == null || !candidate.Success)
                return false;
            if (candidate.MovingToReferenceTransform == null)
                return false;
            var matrix = candidate.MovingToReferenceTransform.ToArray();
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                    if (double.IsNaN(matrix[row, column]) || double.IsInfinity(matrix[row, column]))
                        return false;
            return true;
        }
        private static void Reject(MatcherPipelineResult pipeline, string reason)
        {
            pipeline.Success = false;
            pipeline.SelectedCandidate = null;
            pipeline.SelectedStage = "DIRECT_REJECTED";
            pipeline.SelectionReason = reason;
            pipeline.SelectedCommonScore = double.NaN;
            pipeline.FailureReason = reason;
        }
        private static void Select(MatcherPipelineResult pipeline, MatchResult candidate, string stage, string reason,
                                   double score)
        {
            pipeline.Success = candidate != null && candidate.Success;
            pipeline.SelectedCandidate = candidate;
            pipeline.SelectedStage = stage;
            pipeline.SelectionReason = reason;
            pipeline.SelectedCommonScore = score;
            if (candidate != null && candidate.Diagnostics != null)
            {
                candidate.Diagnostics["DirectPipelineStage"] = stage;
                candidate.Diagnostics["SelectionReason"] = reason;
                candidate.Diagnostics["SelectedCommonScore"] =
                    score.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class DirectCandidateEvaluator
    {
        public DirectCandidateEvaluation Evaluate(DirectCandidateEvaluationRequest request,
                                                  CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                Validate(request);
                cancellationToken.ThrowIfCancellationRequested();
                var result = EvaluateTier(request, request.Options.Tier1MaximumSide, 1, cancellationToken);
                if (result.Success && result.Coarse != null && result.Coarse.Success && result.Refinement != null &&
                    result.Refinement.Success && result.ScoreGap <= Clamp01(request.Options.AmbiguousScoreGap))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result = EvaluateTier(request, request.Options.Tier2MaximumSide, 2, cancellationToken);
                    result.Escalated = true;
                }
                result.ProcessingTime = stopwatch.Elapsed;
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DirectCandidateEvaluation { Success = false,
                                                       FailureReason = ex.GetType().Name + ": " + ex.Message,
                                                       ProcessingTime = stopwatch.Elapsed };
            }
        }

        public static Transform2D ScaleTransform(Transform2D fullTransform, double scaleX, double scaleY)
        {
            if (fullTransform == null)
                throw new ArgumentNullException("fullTransform");
            if (scaleX <= 0 || scaleY <= 0)
                throw new ArgumentOutOfRangeException("scaleX", "Scale must be positive.");
            var scale = new Transform2D(new[,] { { scaleX, 0d, 0d }, { 0d, scaleY, 0d }, { 0d, 0d, 1d } });
            return scale.Multiply(fullTransform).Multiply(scale.Invert());
        }

        private static DirectCandidateEvaluation EvaluateTier(DirectCandidateEvaluationRequest request, int maximumSide,
                                                              int tier, CancellationToken token)
        {
            var maxDimension = Math.Max(request.ReferenceImage.Cols, request.ReferenceImage.Rows);
            var uniformScale =
                maximumSide <= 0 || maxDimension <= maximumSide ? 1d : maximumSide / (double)maxDimension;
            var width = Math.Max(1, (int)Math.Round(request.ReferenceImage.Cols * uniformScale));
            var height = Math.Max(1, (int)Math.Round(request.ReferenceImage.Rows * uniformScale));
            var scaleX = width / (double)request.ReferenceImage.Cols;
            var scaleY = height / (double)request.ReferenceImage.Rows;
            var targetSize = new Size(width, height);
            var movingWidth = Math.Max(
                1,
                (int)Math.Round(request.MovingImage.Cols * scaleX));
            var movingHeight = Math.Max(
                1,
                (int)Math.Round(request.MovingImage.Rows * scaleY));
            var movingSize = new Size(movingWidth, movingHeight);
            var referenceImageSize = request.ReferenceImage.Size();
            var movingImageSize = request.MovingImage.Size();

            using (var reference = PrepareGray(
                request.ReferenceImage,
                targetSize))
            using (var moving = PrepareGray(
                request.MovingImage,
                movingSize))
            using (var referenceValid = PrepareMask(
                request.ReferenceValidMask,
                referenceImageSize,
                targetSize))
            using (var movingValid = PrepareMask(
                request.MovingValidMask,
                movingImageSize,
                moving.Size()))
            using (var contentMask = CreateContentMask(
                reference,
                referenceValid))
            {
                token.ThrowIfCancellationRequested();
                var coarse = EvaluateCandidate(reference, moving, referenceValid, movingValid, contentMask,
                                               request.CoarseCandidate, request.Options, scaleX, scaleY, token);
                var refinement = EvaluateCandidate(reference, moving, referenceValid, movingValid, contentMask,
                                                   request.RefinementCandidate, request.Options, scaleX, scaleY, token);
                var result =
                    new DirectCandidateEvaluation
                    {
                        TierUsed = tier,
                        Coarse = coarse,
                        Refinement = refinement
                    };
                result.Success = (coarse != null && coarse.Success) || (refinement != null && refinement.Success);
                if (!result.Success)
                    result.FailureReason = "No successful candidate metric: coarse=" + Failure(coarse) +
                                           "; refinement=" + Failure(refinement);
                if (coarse != null && coarse.Success && refinement != null && refinement.Success)
                {
                    result.ScoreGap = Math.Abs(coarse.CombinedScore - refinement.CombinedScore);
                    result.RecommendedStage = refinement.CombinedScore > coarse.CombinedScore ? "REFINEMENT" : "COARSE";
                }
                else if (coarse != null && coarse.Success)
                {
                    result.RecommendedStage = "COARSE";
                    result.ScoreGap = double.NaN;
                }
                else if (refinement != null && refinement.Success)
                {
                    result.RecommendedStage = "REFINEMENT";
                    result.ScoreGap = double.NaN;
                }
                return result;
            }
        }

        private static DirectMetricBreakdown EvaluateCandidate(Mat reference, Mat moving, Mat referenceValid,
                                                               Mat movingValid, Mat contentMask, MatchResult candidate,
                                                               DirectEvaluationOptions options, double scaleX,
                                                               double scaleY, CancellationToken token)
        {
            if (candidate == null)
                return new DirectMetricBreakdown { FailureReason = "CandidateNotAvailable" };
            if (!candidate.Success || candidate.MovingToReferenceTransform == null)
                return new DirectMetricBreakdown { FailureReason = "CandidateNotSuccessful" };
            try
            {
                token.ThrowIfCancellationRequested();
                using (var transform =
                           ScaleTransform(candidate.MovingToReferenceTransform, scaleX, scaleY)
                               .ToMatCv64FCopy()) using (var warped =
                                                             new Mat()) using (var warpedValid =
                                                                                   new Mat()) using (var overlap =
                                                                                                         new Mat())
                {
                    Cv2.WarpPerspective(moving, warped, transform, reference.Size(), InterpolationFlags.Linear,
                                        BorderTypes.Constant, Scalar.Black);
                    Cv2.WarpPerspective(movingValid, warpedValid, transform, reference.Size(),
                                        InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
                    Cv2.BitwiseAnd(referenceValid, warpedValid, overlap);
                    using (var scoredMask = new Mat())
                    {
                        Cv2.BitwiseAnd(overlap, contentMask, scoredMask);
                        var contentPixels = Math.Max(1, Cv2.CountNonZero(contentMask));
                        var overlapPixels = Cv2.CountNonZero(scoredMask);
                        if (overlapPixels < 16)
                            return new DirectMetricBreakdown { FailureReason = "InsufficientValidContentOverlap" };
                        var validOverlap = Clamp01(overlapPixels / (double)contentPixels);
                        var borderPenalty = Clamp01(1d - validOverlap);
                        var edge =
                            SymmetricEdgeScore(reference, warped, scoredMask, options.EdgeDistanceTolerancePixels);
                        var gradient = GradientNcc(reference, warped, scoredMask, options.MinimumGradientStdDev);
                        if (double.IsNaN(gradient))
                            return new DirectMetricBreakdown { FailureReason = "LowVarianceGradientDomain" };
                        var combined = options.EdgeWeight * edge + options.GradientNccWeight * gradient +
                                       options.ValidOverlapWeight * validOverlap -
                                       options.BorderPenaltyWeight * borderPenalty;
                        return new DirectMetricBreakdown { Success = true,
                                                           SymmetricEdgeProximity = edge,
                                                           GradientNcc = gradient,
                                                           ValidOverlap = validOverlap,
                                                           BorderPenalty = borderPenalty,
                                                           CombinedScore = Clamp01(combined) };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new DirectMetricBreakdown { FailureReason = ex.GetType().Name + ": " + ex.Message };
            }
        }

        private static double SymmetricEdgeScore(Mat reference, Mat warped, Mat mask, double tolerance)
        {
            using (var refEdges = new Mat()) using (var movingEdges = new Mat()) using (
                var maskedRef = new Mat()) using (var maskedMoving = new Mat())
            {
                Cv2.Canny(reference, refEdges, 40, 120);
                Cv2.Canny(warped, movingEdges, 40, 120);
                Cv2.BitwiseAnd(refEdges, mask, maskedRef);
                Cv2.BitwiseAnd(movingEdges, mask, maskedMoving);
                if (Cv2.CountNonZero(maskedRef) == 0 || Cv2.CountNonZero(maskedMoving) == 0)
                    return 0d;
                return 0.5 * (DirectedEdgeScore(maskedRef, maskedMoving, tolerance) +
                              DirectedEdgeScore(maskedMoving, maskedRef, tolerance));
            }
        }

        private static double DirectedEdgeScore(Mat sourceEdges, Mat targetEdges, double tolerance)
        {
            using (var inverted = new Mat()) using (var distance = new Mat())
            {
                Cv2.BitwiseNot(targetEdges, inverted);
                Cv2.DistanceTransform(inverted, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                var meanDistance = Cv2.Mean(distance, sourceEdges).Val0;
                return Clamp01(1d - meanDistance / Math.Max(0.01, tolerance));
            }
        }

        private static double GradientNcc(Mat reference, Mat warped, Mat mask, double minimumStdDev)
        {
            using (var refGradient = GradientMagnitude(reference)) using (var movingGradient =
                                                                              GradientMagnitude(warped))
            {
                Scalar refMean, refStd, movingMean, movingStd;
                Cv2.MeanStdDev(refGradient, out refMean, out refStd, mask);
                Cv2.MeanStdDev(movingGradient, out movingMean, out movingStd, mask);
                if (refStd.Val0 < minimumStdDev || movingStd.Val0 < minimumStdDev)
                    return double.NaN;
                using (var refCentered = new Mat()) using (var movingCentered = new Mat()) using (var product =
                                                                                                      new Mat())
                {
                    Cv2.Subtract(refGradient, new Scalar(refMean.Val0), refCentered);
                    Cv2.Subtract(movingGradient, new Scalar(movingMean.Val0), movingCentered);
                    Cv2.Multiply(refCentered, movingCentered, product);
                    var covariance = Cv2.Mean(product, mask).Val0;
                    return Clamp01((covariance / (refStd.Val0 * movingStd.Val0) + 1d) * 0.5d);
                }
            }
        }

        private static Mat GradientMagnitude(Mat gray)
        {
            var gx = new Mat();
            var gy = new Mat();
            var magnitude = new Mat();
            try
            {
                Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0);
                Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1);
                Cv2.Magnitude(gx, gy, magnitude);
                return magnitude;
            }
            catch
            {
                magnitude.Dispose();
                throw;
            }
            finally
            {
                gx.Dispose();
                gy.Dispose();
            }
        }

        private static Mat PrepareGray(Mat source, Size size)
        {
            if (source == null || source.Empty())
                throw new ArgumentException("Evaluation image is missing.");
            var gray = new Mat();
            if (source.Channels() == 1)
                source.CopyTo(gray);
            else
                Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            if (gray.Size() == size)
                return gray;
            var resized = new Mat();
            Cv2.Resize(gray, resized, size, 0, 0, InterpolationFlags.Area);
            gray.Dispose();
            return resized;
        }

        private static Mat PrepareMask(Mat source, Size originalSize, Size size)
        {
            var mask = source == null || source.Empty() ? new Mat(originalSize, MatType.CV_8UC1, Scalar.White)
                                                        : source.Clone();
            if (mask.Channels() != 1)
            {
                var mono = new Mat();
                Cv2.CvtColor(mask, mono, ColorConversionCodes.BGR2GRAY);
                mask.Dispose();
                mask = mono;
            }
            Cv2.Threshold(mask, mask, 0, 255, ThresholdTypes.Binary);
            if (mask.Size() == size)
                return mask;
            var resized = new Mat();
            Cv2.Resize(mask, resized, size, 0, 0, InterpolationFlags.Nearest);
            mask.Dispose();
            return resized;
        }

        private static Mat CreateContentMask(Mat reference, Mat valid)
        {
            using (var edges = new Mat()) using (var kernel =
                                                     Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7)))
            {
                Cv2.Canny(reference, edges, 20, 60);
                var content = new Mat();
                Cv2.Dilate(edges, content, kernel);
                Cv2.BitwiseAnd(content, valid, content);
                if (Cv2.CountNonZero(content) < 16)
                {
                    content.Dispose();
                    return valid.Clone();
                }
                return content;
            }
        }

        private static void Validate(DirectCandidateEvaluationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            if (request.Options == null)
                throw new ArgumentException("Evaluation options are missing.");
            if (request.ReferenceImage == null || request.ReferenceImage.Empty() || request.MovingImage == null ||
                request.MovingImage.Empty())
                throw new ArgumentException("Reference and moving images are required.");
            if (request.Options.Tier1MaximumSide <= 0 || request.Options.Tier2MaximumSide <= 0)
                throw new ArgumentException("Evaluation tier maximum sides must be positive.");
            if (request.Options.EdgeWeight < 0 || request.Options.GradientNccWeight < 0 ||
                request.Options.ValidOverlapWeight < 0 || request.Options.BorderPenaltyWeight < 0)
                throw new ArgumentException("Evaluation weights cannot be negative.");
        }
        private static string Failure(DirectMetricBreakdown value)
        {
            return value == null ? "not-evaluated" : value.FailureReason ?? "unknown";
        }
        private static double Clamp01(double value)
        {
            return Math.Max(0d, Math.Min(1d, value));
        }
    }
}
