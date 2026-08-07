using System;
using System.Reflection;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Alignment
{
    public sealed class NeighborAcceptanceResult
    {
        public bool IsMatch { get; set; }
        public string Reason { get; set; }
    }

    public static class NeighborMatchAcceptance
    {
        public static NeighborAcceptanceResult Validate(
            MatchResult phase,
            Transform2D expectedTargetToAnchor,
            Transform2D measuredTargetToAnchor,
            Transform2D residualTransform,
            MatcherOptions options, 
            double overlapRatio)
        {
            options = options ?? new MatcherOptions();
            if (phase == null || !phase.Success) return Reject("No successful Pyramid Phase Correlation result.");
            if (expectedTargetToAnchor == null) return Reject("ExpectedTargetToAnchorTransform is missing.");
            if (measuredTargetToAnchor == null) return Reject("MeasuredTargetToAnchorTransform is missing.");
            if (residualTransform == null) return Reject("ResidualTransform is missing.");
            if (overlapRatio < options.MinOverlapRatio) 
                return Reject("OverlapRatio below MinOverlapRatio.");
            var matrix = measuredTargetToAnchor.ToArray();
            for (int r = 0; r < 3; r++) 
                for (int c = 0; c < 3; c++) 
                    if (double.IsNaN(matrix[r, c]) || double.IsInfinity(matrix[r, c])) 
                        return Reject("Pair transform contains non-finite values.");
            if (phase.RawScore < options.PhaseMinResponse)
                return Reject("Phase correlation score below threshold.");
            var rotationDeg = Math.Atan2(matrix[1, 0], matrix[0, 0]) * 180.0 / Math.PI;
            if (Math.Abs(rotationDeg) > 1e-9) return Reject("Phase-only pair transform must be translation-only.");
            var residual = residualTransform.ToArray();
            var canonicalResidual = NeighborTransformModel.Residual(expectedTargetToAnchor, measuredTargetToAnchor).ToArray();
            if (Math.Abs(residual[0, 2] - canonicalResidual[0, 2]) > 1e-6 || Math.Abs(residual[1, 2] - canonicalResidual[1, 2]) > 1e-6)
                return Reject("ResidualTransform is inconsistent with inverse(Expected) * Measured.");
            var dx = residual[0, 2];
            var dy = residual[1, 2];
            if (Math.Sqrt(dx * dx + dy * dy) > options.MaxTranslationPixels) 
                return Reject("Neighbor residual norm exceeds MaxTranslationPixels.");
            return new NeighborAcceptanceResult 
            { 
                IsMatch = true, 
                Reason = "Accepted image-based neighbor match." 
            };
        }

        public static bool IsAccepted(object pairResult)
        {
            if (pairResult == null) return false;
            var eval = pairResult.GetType().GetProperty("Eval", BindingFlags.Instance | BindingFlags.Public)?.GetValue(pairResult, null);
            if (eval == null) return false;
            var isMatch = eval.GetType().GetProperty("IsMatch", BindingFlags.Instance | BindingFlags.Public)?.GetValue(eval, null);
            return isMatch is bool && (bool)isMatch;
        }

        private static NeighborAcceptanceResult Reject(string reason)
        {
            return new NeighborAcceptanceResult { IsMatch = false, Reason = reason };
        }
    }
}
