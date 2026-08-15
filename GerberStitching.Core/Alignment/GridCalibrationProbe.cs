using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Đo bước lưới THẬT từ ảnh chụp bằng matchTemplate trên dải
    // RỘNG, độc lập hoàn toàn với đường Neighbor Recovery (đường đó lấy bề rộng ROI từ hình học tile nên chỉ
    // 96 px và bị khoá nhầm chu kỳ trên board tuần hoàn -- xem spec §1.4).
    // Chỉ ĐO và CẢNH BÁO, không ghi đè lưới.]
    public sealed class GridCalibrationOptions
    {
        /// <summary>Số cặp lấy mẫu mỗi trục. 8+8 là đủ; không cần quét cả 142 cạnh.</summary>
        public int MaxPairsPerAxis { get; set; } = 8;
        /// <summary>Bề dày dải template, px. Phải nhỏ hơn CaptureOverlap thật.</summary>
        public int TemplateThicknessPx { get; set; } = 64;
        /// <summary>Biên tìm kiếm quanh bước khai, px.</summary>
        public int SearchMarginPx { get; set; } = 240;
        /// <summary>Biên tìm kiếm theo trục vuông góc, px.</summary>
        public int PerpendicularMarginPx { get; set; } = 80;
        public double MinProbeScore { get; set; } = 0.35;
        public double WarnPixels { get; set; } = 2.0;
        public double FailPixels { get; set; } = 16.0;
        public int MinSamplesPerAxis { get; set; } = 4;
    }

    // [Claude] [Change time: 2026-08-15] [Purpose: internal (không phải public) vì Run() nhận WorkflowImageCache
    // vốn internal -- tránh CS0051 inconsistent-accessibility. Probe chỉ được gọi nội bộ từ
    // AlignStitchWorkflowService trong cùng assembly, không cần expose qua façade.]
    internal static class GridCalibrationProbe
    {
        public static GridCalibrationReport Run(IList<CapturedImageInfo> ordered,
                                                IDictionary<int, SampleTileInfo> tileByOrder,
                                                WorkflowImageCache imageCache,
                                                GridCalibrationOptions options)
        {
            if (options == null) options = new GridCalibrationOptions();
            var report = new GridCalibrationReport();
            if (ordered == null || tileByOrder == null || imageCache == null || ordered.Count < 2)
            {
                report.Message = "Không đủ ảnh để đo.";
                return report;
            }

            var tileByRowCol = new Dictionary<string, SampleTileInfo>();
            var orderByRowCol = new Dictionary<string, int>();
            foreach (var cap in ordered)
            {
                SampleTileInfo tile;
                if (!tileByOrder.TryGetValue(cap.OrderIndex, out tile)) continue;
                var key = tile.Row + ":" + tile.Column;
                tileByRowCol[key] = tile;
                orderByRowCol[key] = cap.OrderIndex;
            }

            var capByOrder = ordered.ToDictionary(c => c.OrderIndex);
            var xs = new List<double>();
            var xp = new List<double>();
            var ys = new List<double>();
            var yp = new List<double>();
            var scores = new List<double>();

            foreach (var kv in tileByRowCol)
            {
                if (xs.Count >= options.MaxPairsPerAxis && ys.Count >= options.MaxPairsPerAxis) break;
                var anchorTile = kv.Value;

                if (xs.Count < options.MaxPairsPerAxis)
                    Measure(anchorTile, anchorTile.Row, anchorTile.Column + 1, true, tileByRowCol, orderByRowCol,
                            capByOrder, imageCache, options, xs, xp, scores);

                if (ys.Count < options.MaxPairsPerAxis)
                    Measure(anchorTile, anchorTile.Row + 1, anchorTile.Column, false, tileByRowCol, orderByRowCol,
                            capByOrder, imageCache, options, ys, yp, scores);
            }

            report.SampleCountX = xs.Count;
            report.SampleCountY = ys.Count;
            report.MedianScore = scores.Count == 0 ? double.NaN : Median(scores);

            if (xs.Count < options.MinSamplesPerAxis && ys.Count < options.MinSamplesPerAxis)
            {
                report.Status = GridCalibrationStatus.Inconclusive;
                report.Message = "Chỉ đo được " + xs.Count + " cặp ngang / " + ys.Count +
                                 " cặp dọc đạt ngưỡng điểm. Không kết luận (board có thể có vùng trống lớn).";
                return report;
            }

            if (xs.Count > 0) { report.MeasuredStepX = Median(xs); }
            if (ys.Count > 0) { report.MeasuredStepY = Median(ys); }

            var perp = new List<double>();
            perp.AddRange(xp);
            perp.AddRange(yp.Select(v => -v));
            if (perp.Count > 0 && !double.IsNaN(report.MeasuredStepX))
                report.MeasuredRotationDeg = Math.Atan2(Median(perp), report.MeasuredStepX) * 180d / Math.PI;

            report.DeclaredStepX = DeclaredStep(tileByRowCol, true);
            report.DeclaredStepY = DeclaredStep(tileByRowCol, false);
            report.DeltaX = report.MeasuredStepX - report.DeclaredStepX;
            report.DeltaY = report.MeasuredStepY - report.DeclaredStepY;

            double worst = 0d;
            if (!double.IsNaN(report.DeltaX)) worst = Math.Max(worst, Math.Abs(report.DeltaX));
            if (!double.IsNaN(report.DeltaY)) worst = Math.Max(worst, Math.Abs(report.DeltaY));

            if (worst > options.FailPixels)
                report.Status = GridCalibrationStatus.Mismatch;
            else if (worst > options.WarnPixels)
                report.Status = GridCalibrationStatus.Warning;
            else
                report.Status = GridCalibrationStatus.Ok;

            report.Message = "Bước khai X=" + Fmt(report.DeclaredStepX) + " Y=" + Fmt(report.DeclaredStepY) +
                             "; đo được X=" + Fmt(report.MeasuredStepX) + " Y=" + Fmt(report.MeasuredStepY) +
                             "; lệch X=" + Fmt(report.DeltaX) + " Y=" + Fmt(report.DeltaY) +
                             "; rotation=" + Fmt(report.MeasuredRotationDeg) + "°" +
                             "; n=" + report.SampleCountX + "/" + report.SampleCountY +
                             "; medianScore=" + Fmt(report.MedianScore) + ".";
            return report;
        }

        private static void Measure(SampleTileInfo anchorTile, int targetRow, int targetColumn, bool horizontal,
                                    IDictionary<string, SampleTileInfo> tileByRowCol,
                                    IDictionary<string, int> orderByRowCol,
                                    IDictionary<int, CapturedImageInfo> capByOrder,
                                    WorkflowImageCache imageCache, GridCalibrationOptions options,
                                    IList<double> steps, IList<double> perpendiculars, IList<double> scores)
        {
            var key = targetRow + ":" + targetColumn;
            int anchorOrder, targetOrder;
            if (!orderByRowCol.TryGetValue(anchorTile.Row + ":" + anchorTile.Column, out anchorOrder)) return;
            if (!orderByRowCol.TryGetValue(key, out targetOrder)) return;

            CapturedImageInfo anchorCap, targetCap;
            if (!capByOrder.TryGetValue(anchorOrder, out anchorCap)) return;
            if (!capByOrder.TryGetValue(targetOrder, out targetCap)) return;

            Mat anchor = imageCache.GetMono8(anchorCap.FilePath);
            Mat target = imageCache.GetMono8(targetCap.FilePath);
            if (anchor == null || target == null || anchor.Empty() || target.Empty()) return;

            var targetTile = tileByRowCol[key];
            double declared = horizontal
                                  ? targetTile.ExpectedX - anchorTile.ExpectedX
                                  : targetTile.ExpectedY - anchorTile.ExpectedY;
            if (declared <= 0) return;

            int thickness = options.TemplateThicknessPx;
            int pad = options.PerpendicularMarginPx;
            int lo = (int)Math.Max(0, declared - options.SearchMarginPx);
            int hi = (int)Math.Min(horizontal ? anchor.Cols : anchor.Rows, declared + options.SearchMarginPx);
            if (hi - lo < thickness) return;

            // Lấy dải dài theo trục vuông góc, chừa pad hai đầu để còn biên tìm kiếm.
            int longLo = pad * 2;
            int longHi = (horizontal ? target.Rows : target.Cols) - pad * 2;
            if (longHi - longLo < 256) return;

            Rect templateRect = horizontal
                                    ? new Rect(0, longLo, thickness, longHi - longLo)
                                    : new Rect(longLo, 0, longHi - longLo, thickness);
            Rect searchRect = horizontal
                                  ? new Rect(lo, longLo - pad, hi - lo, longHi - longLo + pad * 2)
                                  : new Rect(longLo - pad, lo, longHi - longLo + pad * 2, hi - lo);

            if (!Contains(target, templateRect) || !Contains(anchor, searchRect)) return;

            using (var template = new Mat(target, templateRect))
            using (var search = new Mat(anchor, searchRect))
            using (var response = new Mat())
            {
                if (search.Rows < template.Rows || search.Cols < template.Cols) return;
                Cv2.MatchTemplate(search, template, response, TemplateMatchModes.CCoeffNormed);
                double minVal, maxVal;
                Point minLoc, maxLoc;
                Cv2.MinMaxLoc(response, out minVal, out maxVal, out minLoc, out maxLoc);
                if (maxVal < options.MinProbeScore) return;

                scores.Add(maxVal);
                if (horizontal)
                {
                    steps.Add(lo + maxLoc.X);
                    perpendiculars.Add(maxLoc.Y - pad);
                }
                else
                {
                    steps.Add(lo + maxLoc.Y);
                    perpendiculars.Add(maxLoc.X - pad);
                }
            }
        }

        private static bool Contains(Mat image, Rect r)
        {
            return r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 &&
                   r.X + r.Width <= image.Cols && r.Y + r.Height <= image.Rows;
        }

        private static double DeclaredStep(IDictionary<string, SampleTileInfo> tileByRowCol, bool horizontal)
        {
            var deltas = new List<double>();
            foreach (var kv in tileByRowCol)
            {
                var a = kv.Value;
                var key = horizontal ? a.Row + ":" + (a.Column + 1) : (a.Row + 1) + ":" + a.Column;
                SampleTileInfo b;
                if (!tileByRowCol.TryGetValue(key, out b)) continue;
                deltas.Add(horizontal ? b.ExpectedX - a.ExpectedX : b.ExpectedY - a.ExpectedY);
            }
            return deltas.Count == 0 ? double.NaN : Median(deltas);
        }

        private static double Median(IList<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2d;
        }

        private static string Fmt(double v)
        {
            return double.IsNaN(v) ? "n/a" : v.ToString("0.##");
        }
    }
}
