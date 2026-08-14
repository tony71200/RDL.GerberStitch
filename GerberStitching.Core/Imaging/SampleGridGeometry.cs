using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.RobotManager;

namespace GerberViewer.Stitching.Imaging
{
    public enum SampleTileState
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3
    }
    public sealed class SampleTileLayout
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public int OrderIndex { get; set; }
        public Rectangle Rectangle { get; set; }
        public int? Predecessor { get; set; }
        public int? Successor { get; set; }
        public SampleTileState Status { get; set; }
    }
    public sealed class SampleGridLayout
    {
        public IList<SampleTileLayout> Tiles { get; set; } = new List<SampleTileLayout>();
        public int TileWidth { get; set; }
        public int TileHeight { get; set; }
        public int StepX { get; set; }
        public int StepY { get; set; }
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int ExpectedTileCount
        {
            get {
                return Rows * Columns;
            }
        }
        public IList<string> Warnings { get; set; } = new List<string>();
    }
    public static class SampleGeometryCalculator
    {
        public static SampleGridLayout Calculate(int processedWidth, int processedHeight, GerberSampleConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            var validation = GerberSampleConfigValidator.Validate(config, new Size(processedWidth, processedHeight));
            if (!validation.IsValid)
                throw new InvalidOperationException(string.Join(Environment.NewLine, validation.Errors));

            var fallbackTileWidth =
                CalculateFallbackTileSize(processedWidth, config.Columns, config.OverlapValue, config.OverlapUnit);
            var fallbackTileHeight =
                CalculateFallbackTileSize(processedHeight, config.Rows, config.OverlapValue, config.OverlapUnit);
            var tileWidth = config.ProcessedWidth > 0 ? config.ProcessedWidth : fallbackTileWidth;
            var tileHeight = config.ProcessedHeight > 0 ? config.ProcessedHeight : fallbackTileHeight;
            var overlapX = CalculateOverlap(tileWidth, config.OverlapValue, config.OverlapUnit);
            var overlapY = CalculateOverlap(tileHeight, config.OverlapValue, config.OverlapUnit);
            var stepX = Math.Max(1, tileWidth - overlapX);
            var stepY = Math.Max(1, tileHeight - overlapY);
            var requiredWidth = tileWidth + Math.Max(0, config.Columns - 1) * stepX;
            var requiredHeight = tileHeight + Math.Max(0, config.Rows - 1) * stepY;

            var coords = PhysicalOrder(config).ToList();
            var layout = new SampleGridLayout { Rows = config.Rows,    Columns = config.Columns,
                                                TileWidth = tileWidth, TileHeight = tileHeight,
                                                StepX = stepX,         StepY = stepY };
            AddCoverageWarning(layout.Warnings, "width", processedWidth, requiredWidth);
            AddCoverageWarning(layout.Warnings, "height", processedHeight, requiredHeight);

            for (int i = 0; i < coords.Count; i++)
            {
                var p = coords[i];
                var startX = p.Item2 * stepX;
                var startY = p.Item1 * stepY;
                var endX = startX + tileWidth;
                var endY = startY + tileHeight;
                var clampedLeft = Math.Max(0, Math.Min(processedWidth - 1, startX));
                var clampedTop = Math.Max(0, Math.Min(processedHeight - 1, startY));
                var clampedRight = Math.Max(clampedLeft + 1, Math.Min(processedWidth, endX));
                var clampedBottom = Math.Max(clampedTop + 1, Math.Min(processedHeight, endY));
                layout.Tiles.Add(new SampleTileLayout {
                    Row = p.Item1, Column = p.Item2, OrderIndex = i,
                    Rectangle = Rectangle.FromLTRB(clampedLeft, clampedTop, clampedRight, clampedBottom),
                    Predecessor = i == 0
                    ? (int                               ?) null: i - 1,
                       Successor = i + 1 == coords.Count ? (int ?) null: i + 1, Status = SampleTileState.Pending });
            }
            return layout;
        }

        // [Tony] [Change time: 2026-08-11] [Purpose: Dựng SampleGridLayout từ danh sách rect có sẵn thay vì
        // suy ra từ lưới đều. Lưới thật của Master ở chế độ EdgePath khởi động lại theo từng die nên không
        // biểu diễn được bằng Rows/Columns/Overlap - xem RDL_Master3/docs/implement_worker_phase4_20260811.md §3.2.
        // Calculate() giữ nguyên cho mọi đường gọi cũ.]
        public static SampleGridLayout FromExplicitRects(IList<SampleTileLayout> tiles,
                                                         int tileWidth, int tileHeight,
                                                         int rows, int columns)
        {
            if (tiles == null)
                throw new ArgumentNullException(nameof(tiles));
            if (tiles.Count == 0)
                throw new ArgumentException("Cần ít nhất một tile.", nameof(tiles));

            var ordered = tiles.OrderBy(t => t.OrderIndex).ToList();

            // OrderIndex phải liên tục 0..N-1: CapturedImageLoader map ảnh -> tile bằng tileByOrder[i]
            // với i chạy 0..N-1, thủng một số là ném KeyNotFoundException ở tận trong pipeline.
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].OrderIndex != i)
                    throw new ArgumentException(
                        "OrderIndex phải liên tục 0.." + (ordered.Count - 1) +
                        "; gặp " + ordered[i].OrderIndex + " ở vị trí " + i + ".", nameof(tiles));
            }

            var layout = new SampleGridLayout
            {
                Rows = rows,
                Columns = columns,
                TileWidth = tileWidth,
                TileHeight = tileHeight,
                // Lưới không đều nên Step chỉ mang tính tham khảo: lấy khoảng cách giữa 2 tile đầu
                // cùng hàng / cùng cột nếu có, không thì 0. Không có đường code nào của Core dùng
                // Step để cắt tile - Crop() đọc thẳng tile.Rectangle (SampleTileGenerator.cs:154-160).
                StepX = InferStepX(ordered),
                StepY = InferStepY(ordered)
            };

            foreach (SampleTileLayout t in ordered)
                layout.Tiles.Add(t);

            return layout;
        }

        private static int InferStepX(IList<SampleTileLayout> ordered)
        {
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].Row == ordered[0].Row && ordered[i].Column == ordered[0].Column + 1)
                    return ordered[i].Rectangle.Left - ordered[0].Rectangle.Left;
            return 0;
        }

        private static int InferStepY(IList<SampleTileLayout> ordered)
        {
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].Column == ordered[0].Column && ordered[i].Row == ordered[0].Row + 1)
                    return ordered[i].Rectangle.Top - ordered[0].Rectangle.Top;
            return 0;
        }

        private static int CalculateFallbackTileSize(int processedSize, int count, double overlapValue,
                                                     OverlapUnit overlapUnit)
        {
            if (count <= 1)
                return processedSize;
            var overlap = overlapUnit == OverlapUnit.Percent ? 0.0 : overlapValue;
            var tile = (processedSize + (count - 1) * overlap) / count;
            return Math.Max(1, (int)Math.Ceiling(tile));
        }

        private static int CalculateOverlap(int tileSize, double overlapValue, OverlapUnit overlapUnit)
        {
            var overlap = overlapUnit == OverlapUnit.Percent ? tileSize * overlapValue / 100.0 : overlapValue;
            return Math.Max(0, (int)Math.Round(overlap));
        }

        private static void AddCoverageWarning(IList<string> warnings, string axis, int processedSize, int requiredSize)
        {
            var delta = processedSize - requiredSize;
            if (delta > 0)
                warnings.Add("Unused processed " + axis + ": " + delta + " px");
            else if (delta < 0)
                warnings.Add("Tile grid exceeds processed " + axis + " by " + (-delta) + " px");
        }
        private static IEnumerable<Tuple<int, int>> PhysicalOrder(GerberSampleConfig c)
        {
            bool vertical = c.StartOrder == StartOrder.TopLeftDown || c.StartOrder == StartOrder.BottomRightUp;
            bool reverseRows = c.StartOrder == StartOrder.BottomRightLeft || c.StartOrder == StartOrder.BottomRightUp;
            bool reverseCols = c.StartOrder == StartOrder.BottomRightLeft || c.StartOrder == StartOrder.BottomRightUp;
            if (vertical)
            {
                for (int cc = 0; cc < c.Columns; cc++)
                {
                    int col = reverseCols ? c.Columns - 1 - cc : cc;
                    var rows = Enumerable.Range(0, c.Rows).Select(r => reverseRows ? c.Rows - 1 - r : r).ToList();
                    if (c.CropOrder == OrderMode.Zigzag && cc % 2 == 1)
                        rows.Reverse();
                    foreach (var row in rows)
                        yield return Tuple.Create(row, col);
                }
            }
            else
            {
                for (int rr = 0; rr < c.Rows; rr++)
                {
                    int row = reverseRows ? c.Rows - 1 - rr : rr;
                    var cols =
                        Enumerable.Range(0, c.Columns).Select(col => reverseCols ? c.Columns - 1 - col : col).ToList();
                    if (c.CropOrder == OrderMode.Zigzag && rr % 2 == 1)
                        cols.Reverse();
                    foreach (var col in cols)
                        yield return Tuple.Create(row, col);
                }
            }
        }
        private sealed class AxisSegmentsResult
        {
            public int[] Starts;
            public int[] Ends;
            public int TileSize;
            public int Step;
        }
        private static AxisSegmentsResult AxisSegments(int size, int count, double overlap)
        {
            var starts = new int[count];
            var ends = new int[count];
            if (count <= 1)
                return new AxisSegmentsResult { Starts = new[] { 0 }, Ends = new[] { size }, TileSize = size,
                                                Step = 0 };
            var tile = (size + (count - 1) * overlap) / count;
            var step = tile - overlap;
            var tileSize = Math.Max(1, (int)Math.Ceiling(tile));
            var stepSize = Math.Max(1, (int)Math.Round(step));
            for (int i = 0; i < count; i++)
            {
                var start = (int)Math.Round(i * step);
                var end = start + tileSize;
                if (i == count - 1)
                {
                    end = size;
                    start = Math.Max(0, end - tileSize);
                }
                starts[i] = Math.Max(0, Math.Min(size - 1, start));
                ends[i] = Math.Max(starts[i] + 1, Math.Min(size, end));
            }
            return new AxisSegmentsResult
            {
                Starts = starts,
                Ends = ends,
                TileSize = tileSize,
                Step = stepSize
            };
        }
    }
}
