using System;
using System.Drawing;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Decouple the workflow from "a sample tile is a file on disk".
    // The disk implementation keeps today's behaviour; the in-memory one crops the large sample raster at the
    // start of a lot, which removes the whole prepare-to-disk stage.]

    /// <summary>Supplies the sample tile image for one OrderIndex.</summary>
    // Public because the facade assembly constructs the workflow with a chosen implementation.
    public interface ISampleTileSource : IDisposable
    {
        /// <summary>Mono8 sample tile. The returned Mat is owned by the source and must NOT be disposed
        /// by the caller; it stays valid until the source itself is disposed.</summary>
        Mat GetTile(int orderIndex);

        /// <summary>Sample tile as a freshly allocated Bitmap. The CALLER owns and must dispose it.
        /// Used only by the legacy ISampleAligner branch, which takes System.Drawing types.</summary>
        Bitmap GetTileBitmap(int orderIndex);
    }
}
