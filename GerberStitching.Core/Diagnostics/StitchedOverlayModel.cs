using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Diagnostics
{
    public enum StitchedOverlayKind
    {
        SampleCell,
        ActualQuadrilateral,
        Label
    }

    public sealed class StitchedOverlay
    {
        public StitchedOverlayKind Kind { get; set; }
        public int OrderIndex { get; set; }
        public IList<PointF> Points { get; set; } = new List<PointF>();
        public string Color { get; set; }
        public string Text { get; set; }
    }

    // [Codex] [Change time: 2026-08-01] [Purpose: Keep manifest cells, captured geometry and labels in one canonical canvas contract.]
    public static class StitchedOverlayBuilder
    {
        public static IList<StitchedOverlay> Build(SampleManifest manifest, IList<CapturedImageInfo> captured,
            IList<TileWorkflowState> states, IList<RecoveryEdgeReport> recoveryEdges,
            double manualOffsetX, double manualOffsetY, bool includeActualGeometry)
        {
            var result = new List<StitchedOverlay>();
            if (manifest == null || manifest.Tiles == null || states == null || captured == null) return result;
            var imageByOrder = captured.ToDictionary(x => x.OrderIndex);
            var stateByOrder = states.ToDictionary(x => x.OrderIndex);
            var bounds = CalculateCanonicalBounds(states, imageByOrder);
            foreach (var tile in manifest.Tiles.OrderBy(x => x.OrderIndex))
            {
                TileWorkflowState state;
                CapturedImageInfo image;
                stateByOrder.TryGetValue(tile.OrderIndex, out state);
                imageByOrder.TryGetValue(tile.OrderIndex, out image);
                var left = (float)(tile.ExpectedX + manualOffsetX - bounds.Left);
                var top = (float)(tile.ExpectedY + manualOffsetY - bounds.Top);
                result.Add(new StitchedOverlay { Kind = StitchedOverlayKind.SampleCell, OrderIndex = tile.OrderIndex,
                    Color = "green", Points = RectanglePoints(left, top, tile.Width, tile.Height) });
                var actual = state != null && state.HasValidPose && image != null
                    ? TransformCorners(state.GlobalPose, image.Width, image.Height, bounds) : null;
                if (includeActualGeometry && actual != null)
                    result.Add(new StitchedOverlay { Kind = StitchedOverlayKind.ActualQuadrilateral, OrderIndex = tile.OrderIndex,
                        Color = state.AlignmentSucceeded ? "yellow" : "red", Points = actual });
                result.Add(new StitchedOverlay { Kind = StitchedOverlayKind.Label, OrderIndex = tile.OrderIndex,
                    Color = state != null && state.AlignmentSucceeded ? "white" : "yellow",
                    Points = actual ?? RectanglePoints(left, top, tile.Width, tile.Height),
                    Text = StitchedOverlayLabelFormatter.Format(tile, state, SelectedEdge(tile.OrderIndex, recoveryEdges)) });
            }
            return result;
        }

        public static RectangleF CalculateCanonicalBounds(IList<TileWorkflowState> states, IDictionary<int, CapturedImageInfo> images)
        {
            var points = new List<PointF>();
            foreach (var state in states ?? new List<TileWorkflowState>())
            {
                CapturedImageInfo image;
                if (state == null || !state.HasValidPose || !images.TryGetValue(state.OrderIndex, out image)) continue;
                points.AddRange(TransformCorners(state.GlobalPose, image.Width, image.Height, RectangleF.Empty));
            }
            if (points.Count == 0) return RectangleF.Empty;
            return RectangleF.FromLTRB(points.Min(x => x.X), points.Min(x => x.Y), points.Max(x => x.X), points.Max(x => x.Y));
        }

        public static IList<PointF> TransformCorners(double[,] transform, int width, int height, RectangleF canonicalBounds)
        {
            var source = new[] { new PointF(0, 0), new PointF(width, 0), new PointF(width, height), new PointF(0, height) };
            return source.Select(p => new PointF(
                (float)(transform[0, 0] * p.X + transform[0, 1] * p.Y + transform[0, 2] - canonicalBounds.Left),
                (float)(transform[1, 0] * p.X + transform[1, 1] * p.Y + transform[1, 2] - canonicalBounds.Top))).ToList();
        }

        private static IList<PointF> RectanglePoints(float left, float top, int width, int height)
        { return new[] { new PointF(left, top), new PointF(left + width, top), new PointF(left + width, top + height), new PointF(left, top + height) }; }

        private static RecoveryEdgeReport SelectedEdge(int orderIndex, IList<RecoveryEdgeReport> edges)
        { return (edges ?? new List<RecoveryEdgeReport>()).FirstOrDefault(x => x.TargetOrderIndex == orderIndex && x.Selected && x.Accepted); }
    }

    public static class StitchedOverlayLabelFormatter
    {
        public static string Format(SampleTileInfo tile, TileWorkflowState state, RecoveryEdgeReport selectedEdge)
        {
            var order = tile == null ? (state == null ? 0 : state.OrderIndex) : tile.OrderIndex;
            if (state == null) return "#" + order.ToString("000", CultureInfo.InvariantCulture) + "  S:--\nSource:Missing";
            if (state.Source == PoseSource.NeighborAlignment && selectedEdge != null)
                return Header(order, double.NaN) + "\nDx:" + Signed(selectedEdge.ResidualDx) + "  Dy:" + Signed(selectedEdge.ResidualDy) +
                    "\nA:" + Signed(selectedEdge.InheritedAnchorRotationDeg) + "°  Source:" + state.Source;
            if (state.Source == PoseSource.SampleAlignment && state.Alignment != null)
                return Header(order, state.Alignment.SelectedCommonScore) + "\nDx:" + Signed(state.Alignment.TranslationX) + "  Dy:" + Signed(state.Alignment.TranslationY) +
                    "\nA:" + Signed(state.Alignment.RotationDeg) + "°  Source:" + state.Source;
            return Header(order, double.NaN) + "\nSource:" + state.Source;
        }

        private static string Header(int orderIndex, double score)
        { return "#" + orderIndex.ToString("000", CultureInfo.InvariantCulture) + "  S:" + Number(score); }
        private static string Number(double value)
        { return Invalid(value) ? "--" : value.ToString("0.00", CultureInfo.InvariantCulture); }
        private static string Signed(double value)
        { return Invalid(value) ? "--" : value.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture); }
        private static bool Invalid(double value) { return double.IsNaN(value) || double.IsInfinity(value); }
    }
}
