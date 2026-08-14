using System;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.OpenCv
{
    public sealed class MatcherGeometryValidator
    {
        public MatchResult ValidateRequest(MatchRequest request, string matcherName)
        {
            if (request == null)
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidInput, "MatchRequest is required.");
            if (request.ReferenceImage == null || request.ReferenceImage.Empty())
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidInput, "ReferenceImage is required.");
            if (request.MovingImage == null || request.MovingImage.Empty())
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidInput, "MovingImage is required.");
            if (!IsRoiValid(request.ReferenceImage, request.ReferenceRoi))
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidRoi,
                                          "ReferenceRoi is outside ReferenceImage bounds.");
            if (!IsRoiValid(request.MovingImage, request.MovingRoi))
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidRoi,
                                          "MovingRoi is outside MovingImage bounds.");
            return null;
        }

        public MatchResult ValidatePreparedPair(Mat reference, Mat moving, MatcherOptions options, string matcherName)
        {
            if (reference == null || moving == null || reference.Empty() || moving.Empty())
                return MatchResult.Failed(matcherName, MatchFailureReason.InvalidInput,
                                          "Prepared images are required.");
            if (reference.Rows != moving.Rows || reference.Cols != moving.Cols)
                return MatchResult.Failed(
                    matcherName, MatchFailureReason.SizeMismatch,
                    "ReferenceImage and MovingImage ROI must have the same size for phase correlation.");
            options = options ?? new MatcherOptions();
            var overlap = CalculateSameSizeOverlap(reference, moving);
            if (overlap < options.MinOverlapRatio)
                return MatchResult.Failed(matcherName, MatchFailureReason.SizeMismatch,
                                          "Prepared image overlap ratio is below threshold.");
            if (CalculateStdDev(reference) < options.MinTextureStdDev ||
                CalculateStdDev(moving) < options.MinTextureStdDev)
                return MatchResult.Failed(matcherName, MatchFailureReason.LowTexture,
                                          "ReferenceImage or MovingImage texture is below MinTextureStdDev.");
            return null;
        }

        public MatchResult ValidateTranslation(double translationX, double translationY, double response,
                                               MatcherOptions options, string matcherName)
        {
            options = options ?? new MatcherOptions();
            if (double.IsNaN(translationX) || double.IsInfinity(translationX) || double.IsNaN(translationY) ||
                double.IsInfinity(translationY))
                return MatchResult.Failed(matcherName, MatchFailureReason.NonFiniteTransform,
                                          "Phase correlation returned a non-finite translation.");
            if (Math.Abs(translationX) > options.MaxTranslationPixels ||
                Math.Abs(translationY) > options.MaxTranslationPixels)
                return MatchResult.Failed(matcherName, MatchFailureReason.NonFiniteTransform,
                                          "Translation exceeds MaxTranslationPixels.");
            if (Math.Abs(response) < options.PhaseMinResponse)
                return MatchResult.Failed(matcherName, MatchFailureReason.ResponseBelowThreshold,
                                          "Phase correlation response is below PhaseMinResponse.");
            return null;
        }

        public static Rect NormalizeRoi(Mat image, Rect? roi)
        {
            if (roi.HasValue)
                return roi.Value;
            return new Rect(0, 0, image.Cols, image.Rows);
        }

        public static double CalculateSameSizeOverlap(Mat reference, Mat moving)
        {
            if (reference == null || moving == null || reference.Empty() || moving.Empty())
                return 0d;
            var width = Math.Min(reference.Cols, moving.Cols);
            var height = Math.Min(reference.Rows, moving.Rows);
            return (width * height) / Math.Max(1.0, reference.Cols * reference.Rows);
        }

        private static bool IsRoiValid(Mat image, Rect? roi)
        {
            if (!roi.HasValue)
                return true;
            var r = roi.Value;
            return r.Width > 0 && r.Height > 0 && r.X >= 0 && r.Y >= 0 && r.X + r.Width <= image.Cols &&
                   r.Y + r.Height <= image.Rows;
        }

        private static double CalculateStdDev(Mat image)
        {
            Scalar mean;
            Scalar stddev;
            Cv2.MeanStdDev(image, out mean, out stddev);
            return stddev.Val0;
        }
    }
}
