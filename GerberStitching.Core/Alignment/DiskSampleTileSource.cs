using System;
using System.Collections.Generic;
using System.Drawing;
using GerberViewer.Stitching.Models;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Preserve the pre-existing behaviour exactly -- read
    // tile.ExpectedPath through WorkflowImageCache. GerberViewer Tab 2/3 and any caller that still supplies a
    // manifest with tile files on disk goes through this implementation.]

    internal sealed class DiskSampleTileSource : ISampleTileSource
    {
        private readonly IDictionary<int, SampleTileInfo> _tileByOrder;
        private readonly WorkflowImageCache _cache;

        /// <param name="cache">Not owned: the workflow owns and disposes it, as before.</param>
        public DiskSampleTileSource(IDictionary<int, SampleTileInfo> tileByOrder, WorkflowImageCache cache)
        {
            if (tileByOrder == null) throw new ArgumentNullException("tileByOrder");
            if (cache == null) throw new ArgumentNullException("cache");
            _tileByOrder = tileByOrder;
            _cache = cache;
        }

        public Mat GetTile(int orderIndex)
        {
            return _cache.GetMono8(PathFor(orderIndex));
        }

        public Bitmap GetTileBitmap(int orderIndex)
        {
            return new Bitmap(PathFor(orderIndex));
        }

        private string PathFor(int orderIndex)
        {
            SampleTileInfo tile;
            if (!_tileByOrder.TryGetValue(orderIndex, out tile))
                throw new KeyNotFoundException("No sample tile for OrderIndex " + orderIndex + ".");
            return tile.ExpectedPath;
        }

        // The cache belongs to the workflow, so this implementation owns nothing to release.
        public void Dispose()
        {
        }
    }
}
