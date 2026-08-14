using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment
{
    /// <summary>
    /// Enforces the canonical pose contract: column-vector matrices map captured/source pixels
    /// to global/output pixels (X right, Y down).  Composition A.Multiply(B) means A * B and B
    /// is applied first.  Thus direct poses are TileTranslation * CapturedToTile and recovery is
    /// AnchorGlobal * TargetToAnchor.  With Y down, atan2(m10,m00) is positive clockwise.
    /// OpenCV angle APIs define positive angles counter-clockwise (therefore negative by this
    /// diagnostic convention); HALCON uses positive mathematical counter-clockwise angles and
    /// row/column adapters must perform the coordinate conversion.  Cv2.WarpAffine receives
    /// this source-to-destination matrix without inversion (no WARP_INVERSE_MAP flag).
    /// </summary>
    public sealed class GlobalPoseValidator
    {
        private const double DeterminantEpsilon = 1e-10;
        private const double MaxOrthogonalityResidual = 0.10;
        private const double MaxCanvasRatio = 10.0;

        public GlobalPoseValidationReport Validate(AlignStitchConfig config, SampleManifest manifest,
                                                   IList<CapturedImageInfo> captured, IList<TileWorkflowState> states,
                                                   IList<RecoveryEdgeReport> edges)
        {
            if (config == null)
                throw new ArgumentNullException("config");
            if (manifest == null)
                throw new ArgumentNullException("manifest");
            var result = new GlobalPoseValidationReport {
                TransformContract =
                    "CapturedImageLocalPixels -> ProcessedSampleGlobalPixels; X right; Y down; column vectors; " +
                    "direct=T(expected)*capturedToTile; recovery=anchorGlobal*targetToAnchor; diagnostic positive " +
                    "rotation is clockwise (OpenCV/HALCON positive angle parameters are counter-clockwise and " +
                    "converted at adapters); WarpAffine uses forward matrix without WARP_INVERSE_MAP."
            };
            var imageByOrder = (captured ?? new List<CapturedImageInfo>()).ToDictionary(x => x.OrderIndex);
            var tileByOrder = manifest.Tiles.ToDictionary(x => x.OrderIndex);

            foreach (var state in (states ?? new List<TileWorkflowState>())
                         .Where(x => x.IsStitchable)
                         .OrderBy(x => x.OrderIndex))
            {
                CapturedImageInfo image;
                SampleTileInfo tile;
                imageByOrder.TryGetValue(state.OrderIndex, out image);
                tileByOrder.TryGetValue(state.OrderIndex, out tile);
                var d = Inspect(state, image, tile, config);
                result.Tiles.Add(d);
                foreach (var violation in d.Violations)
                    result.Errors.Add("OrderIndex " + state.OrderIndex + ": " + violation);
            }
            ValidateGlobalDirections(result, states);
            ValidateRotationPopulation(result, config);
            ValidateCycles(result, states, edges, config);
            ValidateCanvas(result, manifest);
            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        public void ThrowIfInvalid(GlobalPoseValidationReport report)
        {
            if (report == null || !report.IsValid)
                throw new InvalidOperationException("GlobalPose validation blocked stitching: " +
                                                    string.Join(" | ", report == null
                                                                           ? new[] { "no diagnostic report" }
                                                                           : report.Errors.Take(12).ToArray()));
        }

        private static GlobalPoseTileDiagnostic Inspect(TileWorkflowState state, CapturedImageInfo image,
                                                        SampleTileInfo tile, AlignStitchConfig config)
        {
            var d =
                new GlobalPoseTileDiagnostic { OrderIndex = state.OrderIndex,
                                               Matrix = state.GlobalPose == null ? null
                                                                                 : (double[,])state.GlobalPose.Clone(),
                                               PoseSource = state.Source, RecoveryPath = state.Reason };
            var m = state.GlobalPose;
            if (!Homography.IsFinite(m))
            {
                d.Violations.Add("matrix is null, non-finite, or not 3x3");
                return d;
            }
            var w = image != null && image.Width > 0 ? image.Width : (tile == null ? 0 : tile.Width);
            var h = image != null && image.Height > 0 ? image.Height : (tile == null ? 0 : tile.Height);
            d.TranslationX = m[0, 2];
            d.TranslationY = m[1, 2];
            d.ScaleX = Math.Sqrt(m[0, 0] * m[0, 0] + m[1, 0] * m[1, 0]);
            d.ScaleY = Math.Sqrt(m[0, 1] * m[0, 1] + m[1, 1] * m[1, 1]);
            d.RotationDeg = Math.Atan2(m[1, 0], m[0, 0]) * 180 / Math.PI;
            d.Determinant = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
            d.OrthogonalityResidual = d.ScaleX * d.ScaleY < DeterminantEpsilon
                                          ? double.PositiveInfinity
                                          : Math.Abs((m[0, 0] * m[0, 1] + m[1, 0] * m[1, 1]) / (d.ScaleX * d.ScaleY));
            d.ExpectedDeltaX = tile == null ? double.NaN : d.TranslationX - tile.ExpectedX;
            d.ExpectedDeltaY = tile == null ? double.NaN : d.TranslationY - tile.ExpectedY;
            d.TransformedCorners = new[] { Point(m, 0, 0), Point(m, w, 0), Point(m, w, h), Point(m, 0, h) };
            d.BoundsLeft = d.TransformedCorners.Min(x => x[0]);
            d.BoundsTop = d.TransformedCorners.Min(x => x[1]);
            d.BoundsRight = d.TransformedCorners.Max(x => x[0]);
            d.BoundsBottom = d.TransformedCorners.Max(x => x[1]);
            var minScale =
                Math.Min(config.DirectAlignment.Geometry.MinScale, config.NeighborAlignment.Acceptance.MinScale);
            var maxScale =
                Math.Max(config.DirectAlignment.Geometry.MaxScale, config.NeighborAlignment.Acceptance.MaxScale);
            var maxRotation = Math.Max(config.DirectAlignment.Geometry.MaxAbsRotationDeg,
                                       config.NeighborAlignment.Acceptance.MaxAbsRotationDeg);
            if (Math.Abs(d.Determinant) < DeterminantEpsilon)
                d.Violations.Add("matrix is singular/determinant is near zero");
            if (d.Determinant < 0)
                d.Violations.Add("reflection/negative determinant is not allowed");
            if (d.ScaleX < minScale || d.ScaleX > maxScale || d.ScaleY < minScale || d.ScaleY > maxScale)
                d.Violations.Add("scale is outside configured limits");
            if (d.OrthogonalityResidual > MaxOrthogonalityResidual)
                d.Violations.Add("shear/orthogonality residual exceeds " + MaxOrthogonalityResidual);
            if (Math.Abs(d.RotationDeg) > maxRotation)
                d.Violations.Add("absolute rotation exceeds configured limit");
            var area = SignedArea(d.TransformedCorners);
            if (area <= 0)
                d.Violations.Add("corner polygon orientation is reversed or degenerate");
            if (!Finite(d.BoundsLeft) || !Finite(d.BoundsTop) || !Finite(d.BoundsRight) || !Finite(d.BoundsBottom) ||
                d.BoundsRight - d.BoundsLeft <= 0 || d.BoundsBottom - d.BoundsTop <= 0 ||
                Math.Abs(d.BoundsLeft) > 1e9 || Math.Abs(d.BoundsTop) > 1e9 || Math.Abs(d.BoundsRight) > 1e9 ||
                Math.Abs(d.BoundsBottom) > 1e9)
                d.Violations.Add("transformed bounds are physically implausible");
            return d;
        }

        private static double[] Point(double[,] m, double x, double y)
        {
            return new[] { m[0, 0] * x + m[0, 1] * y + m[0, 2], m[1, 0] * x + m[1, 1] * y + m[1, 2] };
        }
        private static double SignedArea(double[][] p)
        {
            double a = 0;
            for (var i = 0; i < p.Length; i++)
            {
                var n = p[(i + 1) % p.Length];
                a += p[i][0] * n[1] - n[0] * p[i][1];
            }
            return a / 2;
        }
        private static bool Finite(double x)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x);
        }

        private static void ValidateGlobalDirections(GlobalPoseValidationReport r, IList<TileWorkflowState> states)
        {
            foreach (var a in states.Where(x => x.IsStitchable))
                foreach (var b in states.Where(x => x.IsStitchable && x.OrderIndex > a.OrderIndex))
                {
                    if (a.Row == b.Row && a.Column < b.Column && b.GlobalPose[0, 2] <= a.GlobalPose[0, 2])
                        r.Errors.Add("Left-to-right direction is reversed for " + a.OrderIndex + " -> " + b.OrderIndex +
                                     ".");
                    if (a.Column == b.Column && a.Row < b.Row && b.GlobalPose[1, 2] <= a.GlobalPose[1, 2])
                        r.Errors.Add("Top-to-bottom direction is reversed for " + a.OrderIndex + " -> " + b.OrderIndex +
                                     ".");
                }
        }

        private static void ValidateRotationPopulation(GlobalPoseValidationReport r, AlignStitchConfig config)
        {
            if (r.Tiles.Count == 0)
                return;
            var sorted = r.Tiles.Select(x => x.RotationDeg).OrderBy(x => x).ToArray();
            r.MedianRotationDeg = sorted[sorted.Length / 2];
            var limit = Math.Max(0.5, Math.Max(config.DirectAlignment.Geometry.MaxAbsRotationDeg,
                                               config.NeighborAlignment.Acceptance.MaxAbsRotationDeg) /
                                          2);
            foreach (var d in r.Tiles.Where(x => Math.Abs(x.RotationDeg - r.MedianRotationDeg) > limit))
                r.Errors.Add("Rotation outlier OrderIndex " + d.OrderIndex + ": " + d.RotationDeg.ToString("R") +
                             " deg; median=" + r.MedianRotationDeg.ToString("R") + ".");
        }

        private static void ValidateCycles(GlobalPoseValidationReport r, IList<TileWorkflowState> states,
                                           IList<RecoveryEdgeReport> edges, AlignStitchConfig config)
        {
            var byOrder = states.Where(x => x.HasValidPose).ToDictionary(x => x.OrderIndex);
            foreach (var e in (edges ?? new List<RecoveryEdgeReport>())
                         .Where(x => x.Accepted && x.TargetToAnchorTransform != null))
            {
                TileWorkflowState a, t;
                if (!byOrder.TryGetValue(e.AnchorOrderIndex, out a) || !byOrder.TryGetValue(e.TargetOrderIndex, out t))
                    continue;
                var predicted = AlignStitchWorkflowService.Multiply(a.GlobalPose, e.TargetToAnchorTransform);
                var dx = predicted[0, 2] - t.GlobalPose[0, 2];
                var dy = predicted[1, 2] - t.GlobalPose[1, 2];
                var predictedAngle = Math.Atan2(predicted[1, 0], predicted[0, 0]) * 180 / Math.PI;
                var targetAngle = Math.Atan2(t.GlobalPose[1, 0], t.GlobalPose[0, 0]) * 180 / Math.PI;
                e.FinalTranslationResidual = Math.Sqrt(dx * dx + dy * dy);
                e.FinalRotationResidualDeg = Math.Abs(predictedAngle - targetAngle);
                if (e.FinalTranslationResidual <= config.NeighborAlignment.MaxCycleClosureErrorPixels)
                {
                    e.ResidualDisposition = "WithinLimit";
                    continue;
                }

                // [Tony] [Change time: 2026-08-01] [Purpose: A reconciliation edge is measurement evidence, not
                // necessarily the edge used to compose the final pose.] Reconciliation builds a deterministic
                // highest-quality tree and may retain an existing direct pose. Consequently a non-tree accepted edge
                // can disagree with the final tree without making any final matrix invalid. Only an edge still
                // authoritative for a neighbor-composed target is a publication invariant.
                var authoritativeComposition = e.ComposedIntoGlobalPose && t.Source == PoseSource.NeighborAlignment &&
                                               MatrixNear(e.CompositionAnchorGlobalPose, a.GlobalPose, 1e-9);
                if (authoritativeComposition)
                {
                    e.ResidualDisposition = "AuthoritativeCompositionError";
                    r.Errors.Add("Applied neighbor composition residual exceeds limit for " + e.AnchorOrderIndex +
                                 " -> " + e.TargetOrderIndex + ": " +
                                 e.FinalTranslationResidual.ToString(
                                     "0.###", System.Globalization.CultureInfo.InvariantCulture) +
                                 " px.");
                }
                else
                {
                    e.ResidualDisposition = "EvidenceOnlyWarning";
                    r.Warnings.Add("Neighbor evidence residual exceeds limit for " + e.AnchorOrderIndex + " -> " +
                                   e.TargetOrderIndex + ": " +
                                   e.FinalTranslationResidual.ToString(
                                       "0.###", System.Globalization.CultureInfo.InvariantCulture) +
                                   " px; edge was not authoritative for the final pose.");
                }
            }
        }

        private static bool MatrixNear(double[,] expected, double[,] actual, double tolerance)
        {
            if (!Homography.IsFinite(expected) || !Homography.IsFinite(actual))
                return false;
            for (var row = 0; row < 3; row++)
                for (var column = 0; column < 3; column++)
                    if (Math.Abs(expected[row, column] - actual[row, column]) > tolerance)
                        return false;
            return true;
        }

        private static void ValidateCanvas(GlobalPoseValidationReport r, SampleManifest manifest)
        {
            if (r.Tiles.Count == 0)
            {
                r.Errors.Add("No stitchable poses were available.");
                return;
            }
            r.CanvasLeft = r.Tiles.Min(x => x.BoundsLeft);
            r.CanvasTop = r.Tiles.Min(x => x.BoundsTop);
            r.CanvasRight = r.Tiles.Max(x => x.BoundsRight);
            r.CanvasBottom = r.Tiles.Max(x => x.BoundsBottom);
            r.CanvasWidthRatio = (r.CanvasRight - r.CanvasLeft) / manifest.ProcessedWidth;
            r.CanvasHeightRatio = (r.CanvasBottom - r.CanvasTop) / manifest.ProcessedHeight;
            if (!Finite(r.CanvasWidthRatio) || !Finite(r.CanvasHeightRatio) || r.CanvasWidthRatio <= 0 ||
                r.CanvasHeightRatio <= 0 || r.CanvasWidthRatio > MaxCanvasRatio || r.CanvasHeightRatio > MaxCanvasRatio)
                r.Errors.Add("Canvas bounds/processed-size ratio is physically implausible.");
        }
    }
}
