using System;
using System.Collections.Generic;
using System.Linq;

namespace GerberViewer.Stitching.RobotManager
{
    public sealed class ArrangeComponent
    {
        public int Index { get; set; }
        public ImageInfo[] Items { get; set; }
        public List<List<ImageInfo>> Matrix { get; set; }
        public Bounds2D Bounds { get; set; }
    }

    public sealed class ArrangeBatchResult
    {
        public int GroupId { get; set; }
        public List<ArrangeComponent> Components { get; set; } = new List<ArrangeComponent>();
    }

    public static class RobotArrange
    {
        public static ArrangeBatchResult FromPhysicalMatrix(int groupId, ImageInfo[,] physicalMatrix, OrderOptions options)
        {
            if (physicalMatrix == null) 
                throw new ArgumentNullException(nameof(physicalMatrix));
            if (options == null) 
                throw new ArgumentNullException(nameof(options));

            var rows = physicalMatrix.GetLength(0);
            var cols = physicalMatrix.GetLength(1);
            var matrix = new List<List<ImageInfo>>(rows);
            var items = new List<ImageInfo>();
            var rowOrder = GetRowOrder(rows, options.StartCorner);
            var colOrder = GetColumnOrder(cols, options.StartCorner);

            foreach (var r in rowOrder)
            {
                var row = new List<ImageInfo>(cols);
                foreach (var c in colOrder)
                {
                    var item = physicalMatrix[r, c];
                    if (item != null) { row.Add(item); items.Add(item); }
                }
                if (row.Count > 0) 
                    matrix.Add(row);
            }

            return new ArrangeBatchResult
            {
                GroupId = groupId,
                Components = new List<ArrangeComponent> 
                { 
                    new ArrangeComponent 
                    { 
                        Index = 0, 
                        Items = items.ToArray(), 
                        Matrix = matrix, 
                        Bounds = Bounds2D.FromPoints(items) 
                    } 
                }
            };
        }

        private static IEnumerable<int> GetRowOrder(int rows, StartCorner corner)
        {
            var bottom = corner == StartCorner.BottomLeft || corner == StartCorner.BottomRight;
            return bottom ? Enumerable.Range(0, rows).Reverse() : Enumerable.Range(0, rows);
        }

        private static IEnumerable<int> GetColumnOrder(int cols, StartCorner corner)
        {
            var right = corner == StartCorner.TopRight || corner == StartCorner.BottomRight;
            return right ? Enumerable.Range(0, cols).Reverse() : Enumerable.Range(0, cols);
        }
    }
}
