using System;
using System.Collections.Generic;
using System.Linq;

namespace GerberViewer.Stitching.RobotManager
{
    public sealed class TraversalGraph
    {
        public OrderMode Mode { get; private set; }
        public IReadOnlyList<IReadOnlyList<ImageInfo>> Matrix { get; private set; }
        public Dictionary<int, NodeLinks> LinksById { get; } = new Dictionary<int, NodeLinks>();
        public Dictionary<int, GridCell> CellById { get; } = new Dictionary<int, GridCell>();
        public List<PathSegment> PathSegments { get; } = new List<PathSegment>();

        public static TraversalGraph Build(List<List<ImageInfo>> matrix, OrderOptions options)
        {
            if (matrix == null) 
                throw new ArgumentNullException(nameof(matrix));
            if (options == null) 
                throw new ArgumentNullException(nameof(options));
            var graph = new TraversalGraph 
            { 
                Mode = options.Mode, 
                Matrix = matrix.Select(r => (IReadOnlyList<ImageInfo>)r).ToList() 
            };
            graph.IndexCells(matrix);
            graph.BuildLinks(matrix);
            graph.BuildPathSegments();
            return graph;
        }

        private void BuildLinks(List<List<ImageInfo>> matrix)
        {
            if (Mode == OrderMode.BranchDown) BuildBranchDown(matrix);
            else BuildRows(matrix, Mode == OrderMode.Zigzag);
        }

        private void BuildRows(List<List<ImageInfo>> matrix, bool snake)
        {
            ImageInfo previous = null;
            for (int r = 0; r < matrix.Count; r++)
            {
                var row = snake && r % 2 == 1 ? matrix[r].AsEnumerable().Reverse().ToList() : matrix[r];
                for (int i = 0; i < row.Count; i++)
                {
                    var current = row[i]; if (current == null) continue;
                    Ensure(current.ImageId);
                    if (i + 1 < row.Count && row[i + 1] != null) 
                        LinksById[current.ImageId].HNext = row[i + 1].ImageId;
                    if (snake && previous != null) 
                    { 
                        LinksById[previous.ImageId].Next = current.ImageId; 
                        LinksById[current.ImageId].Prev = previous.ImageId; 
                    }
                    previous = current;
                }
                if (!snake && r + 1 < matrix.Count && 
                    row.FirstOrDefault() != null && 
                    matrix[r + 1].FirstOrDefault() != null)
                    LinksById[row.First().ImageId].VNext = matrix[r + 1].First().ImageId;
            }
        }

        private void BuildBranchDown(List<List<ImageInfo>> matrix)
        {
            var maxCols = matrix.Count == 0 ? 0 : matrix.Max(r => r.Count);
            for (int c = 0; c < maxCols; c++)
            {
                ImageInfo head = null, previous = null;
                for (int r = 0; r < matrix.Count; r++)
                {
                    if (c >= matrix[r].Count || matrix[r][c] == null) continue;
                    var current = matrix[r][c]; 
                    Ensure(current.ImageId); 
                    if (head == null) head = current;
                    if (previous != null) 
                        LinksById[previous.ImageId].VNext = current.ImageId;
                    previous = current;
                }
                if (head != null && c + 1 < maxCols)
                {
                    var nextHead = matrix.Select(row => c + 1 < row.Count ? row[c + 1] : null)
                        .FirstOrDefault(p => p != null);
                    if (nextHead != null) 
                        LinksById[head.ImageId].HNext = nextHead.ImageId;
                }
            }
        }

        public IEnumerable<Tuple<int, int, EdgeDir>> EnumerateEdges()
        {
            foreach (var link in LinksById.Values)
            {
                if (link.HNext.HasValue) 
                    yield return Tuple.Create(link.ImageId, link.HNext.Value, EdgeDir.Horizontal);
                if (link.VNext.HasValue) 
                    yield return Tuple.Create(link.ImageId, link.VNext.Value, EdgeDir.Vertical);
                if (Mode == OrderMode.Zigzag && link.Next.HasValue && !link.HNext.HasValue && !link.VNext.HasValue) 
                    yield return Tuple.Create(link.ImageId, link.Next.Value, EdgeDir.Horizontal);
            }
        }

        private void IndexCells(List<List<ImageInfo>> matrix)
        {
            for (int r = 0; r < matrix.Count; r++) 
                for (int c = 0; c < matrix[r].Count; c++) 
                    if (matrix[r][c] != null) 
                        CellById[matrix[r][c].ImageId] = new GridCell(r, c);
        }
        private void Ensure(int id) 
        { 
            if (!LinksById.ContainsKey(id)) 
                LinksById[id] = new NodeLinks { ImageId = id }; 
        }
        private void BuildPathSegments() 
        { 
            foreach (var e in EnumerateEdges()) 
                PathSegments.Add(new PathSegment 
                { 
                    FromId = e.Item1, 
                    ToId = e.Item2, 
                    Direction = e.Item3, 
                    FromCell = CellById[e.Item1], 
                    ToCell = CellById[e.Item2] 
                }); 
        }
    }
}
