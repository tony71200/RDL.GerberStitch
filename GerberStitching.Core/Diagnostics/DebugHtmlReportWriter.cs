#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using GerberViewer.Stitching.Alignment;
using GerberViewer.Stitching.Alignment.Graph;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Diagnostics
{
    /// <summary>Writes a DEBUG-only, grid-shaped snapshot of every alignment coordinate frame.</summary>
    internal static class DebugHtmlReportWriter
    {
        public static string Write(string outputDirectory, SampleManifest manifest,
            IList<TileWorkflowState> states, IList<RecoveryEdgeReport> neighborEdges, DirectPoseCorrectionReport correctionReport,
            PoseGraphReport poseGraphReport)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || manifest == null) return null;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, "Debug_" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".html");
            var stateByOrder = (states ?? new List<TileWorkflowState>()).ToDictionary(x => x.OrderIndex);
            var edgesByTarget = (neighborEdges ?? new List<RecoveryEdgeReport>())
                .Where(x => x.Accepted).GroupBy(x => x.TargetOrderIndex).ToDictionary(x => x.Key, x => x.ToList());
            var poseGraphByOrder = (poseGraphReport == null ? new List<PoseGraphTileEntry>() : poseGraphReport.Tiles).ToDictionary(x => x.OrderIndex);

            var html = new StringBuilder();
            html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Alignment debug</title>")
                .Append("<style>body{font:14px Segoe UI,Arial;margin:20px;color:#202124}.tabs{display:flex;gap:5px}.tab-button{padding:9px 14px;border:1px solid #aaa;background:#eee;cursor:pointer}.tab-button.active{background:#1769aa;color:white}.tab{display:none}.tab.active{display:block}table{border-collapse:collapse;margin-top:12px}td{border:1px solid #999;vertical-align:top;min-width:220px;padding:8px}.order{font-weight:700;color:#1769aa}.pose{margin-top:5px}.matrix{font:12px Consolas,monospace;white-space:pre;margin-top:5px}.missing{color:#888}h2{font-size:18px}.hot{background:#fff3cd}</style></head><body>")
                .Append("<h1>Alignment debug</h1><div class=\"tabs\">");
            var correctionByOrder = (correctionReport == null ? new List<CorrectedTile>() : correctionReport.Corrected).ToDictionary(x => x.OrderIndex);
            var titles = new[] { "1. Direct Alignment", "2. Neighbor Alignment", "3. Pose Correction", "4. Before Stitching", "5. Delta From Sample Origin", "6. Pose Graph" };
            for (var i = 0; i < titles.Length; i++) html.Append("<button class=\"tab-button").Append(i == 0 ? " active" : "").Append("\" data-tab=\"tab").Append(i).Append("\">").Append(titles[i]).Append("</button>");
            html.Append("</div>");
            for (var tab = 0; tab < titles.Length; tab++)
            {
                html.Append("<section id=\"tab").Append(tab).Append("\" class=\"tab").Append(tab == 0 ? " active" : "").Append("\"><h2>").Append(titles[tab]).Append("</h2>");
                if (tab == 2) AppendCorrectionSummary(html, correctionReport);
                if (tab == 5) AppendPoseGraphSummary(html, poseGraphReport);
                AppendGrid(html, manifest, delegate(SampleTileInfo tile)
                {
                    TileWorkflowState state; stateByOrder.TryGetValue(tile.OrderIndex, out state);
                    if (tab == 0) return DirectCell(tile, state);
                    if (tab == 1) { List<RecoveryEdgeReport> edges; edgesByTarget.TryGetValue(tile.OrderIndex, out edges); return NeighborCell(tile, edges); }
                    if (tab == 2) { CorrectedTile correction; correctionByOrder.TryGetValue(tile.OrderIndex, out correction); return CorrectionCell(tile, correction); }
                    if (tab == 3) return PoseCell(tile, state == null ? null : state.GlobalPose, state == null ? null : state.Source.ToString());
                    if (tab == 5) { PoseGraphTileEntry entry; poseGraphByOrder.TryGetValue(tile.OrderIndex, out entry); return PoseGraphCell(tile, entry); }
                    return DeltaCell(tile, state == null ? null : state.GlobalPose);
                });
                html.Append("</section>");
            }
            html.Append("<script>document.querySelectorAll('.tab-button').forEach(function(b){b.onclick=function(){document.querySelectorAll('.tab-button,.tab').forEach(function(x){x.classList.remove('active')});b.classList.add('active');document.getElementById(b.dataset.tab).classList.add('active')}});</script></body></html>");
            File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static void AppendCorrectionSummary(StringBuilder html, DirectPoseCorrectionReport report)
        {
            if (report == null) { html.Append(Missing("DirectPoseOutlierCorrector did not produce a report")); return; }
            html.Append("<div><strong>DirectPoseOutlierCorrector</strong>: enabled=").Append(report.Enabled)
                .Append(", valid poses=").Append(report.ValidPoseCount).Append(", outliers=").Append(report.OutlierCount)
                .Append(", corrected=").Append(report.Corrected.Count).Append(", uncorrectable=").Append(report.OutlierUncorrectable.Count).Append("</div>")
                .Append("<div>RotationOutlierMadK=").Append(Number(report.RotationOutlierMadK))
                .Append(", AngleBandFloorDeg=").Append(Number(report.AngleBandFloorDeg))
                .Append(", SignFlipGuardDeg=").Append(Number(report.SignFlipGuardDeg)).Append("</div>")
                .Append("<div>MedianRotationDeg=").Append(Number(report.MedianRotationDeg))
                .Append(", RotationMadDeg=").Append(Number(report.RotationMadDeg))
                .Append(", effective RotationBandDeg=").Append(Number(report.RotationBandDeg)).Append("</div>");
        }

        private static void AppendPoseGraphSummary(StringBuilder html, PoseGraphReport g)
        {
            if (g == null || !g.Enabled) { html.Append(Missing("Pose graph disabled")); return; }
            html.Append("<div><strong>PoseGraphOptimizer</strong>: applied=").Append(g.Applied).Append(", converged=").Append(g.Converged)
                .Append(", iterations=").Append(g.Iterations).Append(", edges=").Append(g.EdgesUsed).Append("/").Append(g.EdgesTotal).Append("</div>")
                .Append("<div>GlobalScale=").Append(Number(g.GlobalScale)).Append(", GlobalRotationDeg=").Append(Number(g.GlobalRotationDeg))
                .Append(", GlobalOffset=(").Append(Number(g.GlobalOffsetX)).Append(", ").Append(Number(g.GlobalOffsetY)).Append(")</div>")
                .Append("<div>Seam residual (px): before med=").Append(Number(g.BeforeResidualMedian)).Append(" p90=").Append(Number(g.BeforeResidualP90)).Append(" max=").Append(Number(g.BeforeResidualMax))
                .Append(" -&gt; after med=").Append(Number(g.AfterResidualMedian)).Append(" p90=").Append(Number(g.AfterResidualP90)).Append(" max=").Append(Number(g.AfterResidualMax)).Append("</div>")
                .Append("<div>Component sizes: [").Append(WebUtility.HtmlEncode(string.Join(", ", g.ComponentSizes.Select(x => x.ToString()).ToArray()))).Append("]</div>")
                .Append("<div>Isolated tiles: [").Append(WebUtility.HtmlEncode(string.Join(", ", g.IsolatedTiles.Select(x => x.ToString()).ToArray()))).Append("]</div>");
            foreach (var w in g.Warnings) html.Append("<div class=\"missing\">").Append(WebUtility.HtmlEncode(w)).Append("</div>");
        }

        private static string PoseGraphCell(SampleTileInfo tile, PoseGraphTileEntry entry)
        {
            if (entry == null) return Header(tile) + Missing("Not part of the pose graph (excluded)");
            return Header(tile) +
                "<div>Before: Tx=" + Number(entry.BeforeTx) + " Ty=" + Number(entry.BeforeTy) + " DAngle=" + Number(entry.BeforeRotationDeg) + "&deg;</div>" +
                "<div>After: Tx=" + Number(entry.AfterTx) + " Ty=" + Number(entry.AfterTy) + " DAngle=" + Number(entry.AfterRotationDeg) + "&deg;</div>" +
                "<div>Delta: " + Number(entry.DeltaPixels) + " px, " + Number(entry.DeltaRotationDeg) + " deg</div>" +
                "<div>Lambda=" + Number(entry.Lambda) + " Edges=" + entry.EdgeCount + "</div>" +
                "<div>Source: " + WebUtility.HtmlEncode(entry.SourceBefore.ToString()) + " -&gt; " + WebUtility.HtmlEncode(entry.SourceAfter.ToString()) + "</div>";
        }

        private static void AppendGrid(StringBuilder html, SampleManifest manifest, Func<SampleTileInfo, string> cell)
        {
            var byCell = manifest.Tiles.ToDictionary(x => x.Row + "," + x.Column);
            var rows = manifest.Tiles.Max(x => x.Row) + 1; var columns = manifest.Tiles.Max(x => x.Column) + 1;
            html.Append("<table>");
            for (var row = 0; row < rows; row++)
            {
                html.Append("<tr>");
                for (var column = 0; column < columns; column++)
                {
                    SampleTileInfo tile; byCell.TryGetValue(row + "," + column, out tile);
                    html.Append("<td>").Append(tile == null ? "<span class=\"missing\">—</span>" : cell(tile)).Append("</td>");
                }
                html.Append("</tr>");
            }
            html.Append("</table>");
        }

        private static string DirectCell(SampleTileInfo tile, TileWorkflowState state)
        {
            var transform = state == null ? null : state.DirectAlignmentTransform;
            if (state == null || state.Alignment == null) return Header(tile) + Missing("No direct alignment candidates");
            var alignment = state.Alignment;
            return Header(tile) + "<div><strong>CoarseCandidate</strong>: " + WebUtility.HtmlEncode(alignment.CoarseMatcherName ?? "-") +
                ", raw=" + Number(alignment.CoarseRawScore) + ", normalized=" + Number(alignment.CoarseNormalizedConfidence) + "</div>" +
                "<div><strong>RefinementCandidate</strong>: " + WebUtility.HtmlEncode(alignment.RefinementMatcherName ?? "-") +
                ", raw=" + Number(alignment.RefinementRawScore) + "</div>" +
                "<div><strong>SelectedCandidate</strong>: " + WebUtility.HtmlEncode(alignment.SelectedMatcherName ?? "-") +
                ", common score=" + Number(alignment.SelectedCommonScore) + "</div>" +
                "<div><strong>SelectionReason</strong>: " + WebUtility.HtmlEncode(alignment.SelectionReason ?? "-") + "</div>" +
                (transform == null ? Missing("No direct alignment transform") : Pose(transform, null));
        }

        private static string NeighborCell(SampleTileInfo tile, IList<RecoveryEdgeReport> edges)
        {
            var value = new StringBuilder(Header(tile));
            if (edges == null || edges.Count == 0) return value.Append(Missing("No accepted neighbor alignment")).ToString();
            foreach (var edge in edges)
                value.Append("<div><strong>Node ").Append(edge.TargetOrderIndex).Append(" aligned with node ").Append(edge.AnchorOrderIndex)
                    .Append("</strong> (").Append(WebUtility.HtmlEncode(edge.Direction ?? "-")).Append(")</div>")
                    .Append("<div>Residual Dx=").Append(Number(edge.ResidualDx)).Append(", Dy=").Append(Number(edge.ResidualDy)).Append("</div>")
                    .Append("<div>Accepted=").Append(edge.Accepted).Append(", selected=").Append(edge.Selected)
                    .Append(", rank=").Append(edge.Rank).Append(", sample foreground=").Append(Number(edge.SampleForegroundRatio))
                    .Append(", traversal preferred=").Append(edge.TraversalDirectionPreferred)
                    .Append(", sample edge density=").Append(Number(edge.SampleEdgeDensity))
                    .Append(", phase response=").Append(Number(edge.PhaseScore)).Append(", residual norm=").Append(Number(edge.ResidualNorm)).Append("</div>")
                    .Append("<div>").Append(WebUtility.HtmlEncode(edge.RankingReason ?? edge.Reason ?? string.Empty)).Append("</div>")
                    .Append(string.IsNullOrWhiteSpace(edge.ConsistencyWarning) ? string.Empty : "<div class=\"warning\">" + WebUtility.HtmlEncode(edge.ConsistencyWarning) + "</div>")
                    .Append(Pose(edge.MeasuredTargetToAnchorTransform ?? edge.TargetToAnchorTransform, null));
            return value.ToString();
        }


        private static string CorrectionCell(SampleTileInfo tile, CorrectedTile correction)
        {
            if (correction == null) return Header(tile) + Missing("Pose not corrected");
            return Header(tile) + "<div><strong>Corrected</strong></div>" +
                "<div>DAngle: " + Number(correction.BeforeRotationDeg) + "° → " + Number(correction.AfterRotationDeg) + "°</div>" +
                "<div>Dx: " + Number(correction.BeforeTranslationX) + " → " + Number(correction.AfterTranslationX) + "</div>" +
                "<div>Dy: " + Number(correction.BeforeTranslationY) + " → " + Number(correction.AfterTranslationY) + "</div>" +
                "<div><strong>Adjustment</strong>: ΔAngle=" + Number(correction.AdjustmentRotationDeg) + "°, ΔX=" + Number(correction.AdjustmentX) + ", ΔY=" + Number(correction.AdjustmentY) + "</div>" +
                "<div>Anchors: " + WebUtility.HtmlEncode(string.Join(",", correction.AnchorOrderIndices.Select(x => x.ToString()).ToArray())) + "</div>" +
                "<div>" + WebUtility.HtmlEncode(correction.Reason ?? string.Empty) + "</div>";
        }

        private static string PoseCell(SampleTileInfo tile, double[,] matrix, string note)
        {
            return Header(tile) + (matrix == null ? Missing("No transform before stitching") : Pose(matrix, note));
        }

        private static string DeltaCell(SampleTileInfo tile, double[,] globalPose)
        {
            if (globalPose == null) return Header(tile) + Missing("No transform before stitching");
            var originInverse = new[,] { { 1d, 0d, -tile.ExpectedX }, { 0d, 1d, -tile.ExpectedY }, { 0d, 0d, 1d } };
            return Header(tile) + Pose(AlignStitchWorkflowService.Multiply(originInverse, globalPose),
                "sample origin = (" + Number(tile.ExpectedX) + ", " + Number(tile.ExpectedY) + ")");
        }

        private static string Header(SampleTileInfo tile) { return "<div class=\"order\">#" + tile.OrderIndex + "</div><div>Row " + tile.Row + ", Column " + tile.Column + "</div>"; }
        private static string Missing(string text) { return "<div class=\"missing\">" + WebUtility.HtmlEncode(text) + "</div>"; }
        private static string Pose(double[,] matrix, string note)
        {
            if (matrix == null) return Missing("No transform matrix");
            var dx = matrix.GetLength(1) > 2 ? matrix[0, 2] : double.NaN;
            var dy = matrix.GetLength(1) > 2 ? matrix[1, 2] : double.NaN;
            var angle = matrix.GetLength(0) > 1 ? Math.Atan2(matrix[1, 0], matrix[0, 0]) * 180d / Math.PI : double.NaN;
            var value = new StringBuilder("<div class=\"pose\">Dx: ").Append(Number(dx)).Append("<br>Dy: ").Append(Number(dy)).Append("<br>DAngle: ").Append(Number(angle)).Append("°</div>");
            if (!string.IsNullOrWhiteSpace(note)) value.Append("<div>").Append(WebUtility.HtmlEncode(note)).Append("</div>");
            value.Append("<div class=\"matrix\">");
            for (var row = 0; row < matrix.GetLength(0); row++)
            { value.Append("["); for (var column = 0; column < matrix.GetLength(1); column++) { if (column > 0) value.Append(", "); value.Append(Number(matrix[row, column])); } value.Append("]"); if (row + 1 < matrix.GetLength(0)) value.Append("<br>"); }
            return value.Append("</div>").ToString();
        }
        private static string Number(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? "-" : value.ToString("0.######", CultureInfo.InvariantCulture); }

        public static string WriteStitchedPreview(string outputDirectory, string stitchedPath,
            SampleManifest manifest, IList<CapturedImageInfo> captured, IList<TileWorkflowState> states,
            double manualOffsetX, double manualOffsetY)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(stitchedPath) ||
                manifest == null || string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath) ||
                !File.Exists(stitchedPath) || !File.Exists(manifest.ProcessedSamplePath)) return null;

            var imageByOrder = (captured ?? new List<CapturedImageInfo>()).ToDictionary(x => x.OrderIndex);
            var boundsInput = new List<Tuple<CapturedImageInfo, double[,]>>();
            foreach (var state in states ?? new List<TileWorkflowState>())
            {
                CapturedImageInfo image;
                if (state != null && state.IsStitchable && state.HasValidPose && imageByOrder.TryGetValue(state.OrderIndex, out image))
                    boundsInput.Add(Tuple.Create(image, state.GlobalPose));
            }
            if (boundsInput.Count == 0) return null;

            const int maximumSide = 1280;
            using (var stitchedSource = ReadBoundedWithHalcon(stitchedPath, maximumSide))
            using (var sampleSource = ReadBoundedWithHalcon(manifest.ProcessedSamplePath, maximumSide))
            using (var stitched = ToBgr(stitchedSource.Image))
            using (var sample = ToGray(sampleSource.Image))
            {
                var previewSize = stitched.Size();
                var stitchedScaleX = stitched.Cols / (double)stitchedSource.OriginalWidth;
                var stitchedScaleY = stitched.Rows / (double)stitchedSource.OriginalHeight;
                var sampleScaleX = sample.Cols / (double)sampleSource.OriginalWidth;
                var sampleScaleY = sample.Rows / (double)sampleSource.OriginalHeight;
                using (var preview = new Mat())
                using (var mask = new Mat())
                using (var red = new Mat(previewSize, MatType.CV_8UC3, new Scalar(0, 0, 255)))
                using (var overlay = new Mat())
                using (var warp = new Mat(2, 3, MatType.CV_64FC1))
                {
                    stitched.CopyTo(preview);
                    var bounds = GlobalTransformStitcher.CalculateBounds(boundsInput);
                    warp.Set(0, 0, stitchedScaleX / sampleScaleX); warp.Set(0, 1, 0d); warp.Set(0, 2, (manualOffsetX - bounds.Left) * stitchedScaleX);
                    warp.Set(1, 0, 0d); warp.Set(1, 1, stitchedScaleY / sampleScaleY); warp.Set(1, 2, (manualOffsetY - bounds.Top) * stitchedScaleY);
                    Cv2.WarpAffine(sample, mask, warp, previewSize, InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                    Cv2.Threshold(mask, mask, 0, 255, ThresholdTypes.Binary);
                    Cv2.AddWeighted(preview, 0.45, red, 0.55, 0, overlay);
                    overlay.CopyTo(preview, mask);
                    Directory.CreateDirectory(outputDirectory);
                    var path = Path.Combine(outputDirectory, "DebugPreview_" + DateTime.Now.ToString("yyyyHHmm", CultureInfo.InvariantCulture) + ".jpg");
                    if (!Cv2.ImWrite(path, preview, new ImageEncodingParam(ImwriteFlags.JpegQuality, 60))) return null;
                    return path;
                }
            }
        }

        private static BoundedImage ReadBoundedWithHalcon(string path, int maximumSide)
        {
            HObject source = null;
            HObject resized = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.ReadImage(out source, path);
                HOperatorSet.GetImageSize(source, out width, out height);
                var originalWidth = width.I;
                var originalHeight = height.I;
                var scale = Math.Min(1d, maximumSide / (double)Math.Max(originalWidth, originalHeight));
                // [Claude] [Change time: 2026-08-06] [Purpose: DebugPreview_<time>.jpg không được nhỏ hơn 1/6 kích thước ảnh gốc, kể cả khi ảnh gốc vượt xa maximumSide.]
                const double minimumScale = 1d / 6d;
                if (scale < minimumScale) scale = minimumScale;
                var targetWidth = Math.Max(1, (int)Math.Round(originalWidth * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(originalHeight * scale));
                if (scale < 1d) HOperatorSet.ZoomImageSize(source, out resized, targetWidth, targetHeight, "constant");
                else HOperatorSet.CopyObj(source, out resized, 1, -1);
                var image = new ImageInteropService().ToMatCopy(resized);
                return new BoundedImage(image, originalWidth, originalHeight);
            }
            finally
            {
                if (source != null) source.Dispose();
                if (resized != null) resized.Dispose();
                if (width != null) width.Dispose();
                if (height != null) height.Dispose();
            }
        }

        private static Mat ToBgr(Mat source)
        {
            if (source.Channels() == 3) return source.Clone();
            var result = new Mat();
            Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }

        private static Mat ToGray(Mat source)
        {
            if (source.Channels() == 1) return source.Clone();
            var result = new Mat();
            Cv2.CvtColor(source, result, ColorConversionCodes.BGR2GRAY);
            return result;
        }

        private sealed class BoundedImage : IDisposable
        {
            public BoundedImage(Mat image, int originalWidth, int originalHeight)
            { Image = image; OriginalWidth = originalWidth; OriginalHeight = originalHeight; }
            public Mat Image { get; private set; }
            public int OriginalWidth { get; private set; }
            public int OriginalHeight { get; private set; }
            public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
        }
    }
}
#endif
