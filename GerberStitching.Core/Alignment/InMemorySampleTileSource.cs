using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GerberViewer.Stitching.Imaging.ImageInterop;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    /// <summary>One sample tile rectangle, in pixels on the sample raster.</summary>
    public sealed class SampleTileRect
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // [Claude] [Change time: 2026-08-14] [Purpose: Crop the sample tiles straight out of the large sample raster
    // instead of writing 80 TIFFs and reading them back. HALCON does the reading and cropping because OpenCV's
    // Cv2.ImRead refuses images above 2^30 pixels and the reference sample is 40418 x 32364 = 1.31e9 pixels.
    // Crops are stored as Mat because every consumer (MatchRequest, SampleTileContentAnalyzer,
    // CalculateSampleOverlapMetrics) takes Mat -- keeping HImage would force a conversion at each use.
    // The large image is disposed as soon as the crop loop ends, before alignment starts.]
    public sealed class InMemorySampleTileSource : ISampleTileSource
    {
        private readonly Dictionary<int, Mat> _tiles = new Dictionary<int, Mat>();

        private InMemorySampleTileSource()
        {
        }

        public static InMemorySampleTileSource CreateFromRaster(string rasterPath, IList<SampleTileRect> rects,
                                                                string debugTileDirectory,
                                                                System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
                throw new FileNotFoundException("Sample raster not found.", rasterPath);
            if (rects == null || rects.Count == 0)
                throw new ArgumentException("Tile rect list is empty.", "rects");

            var source = new InMemorySampleTileSource();
            var interop = new ImageInteropService();
            HObject large = null;
            try
            {
                HOperatorSet.ReadImage(out large, rasterPath);
                if (large == null || !large.IsInitialized())
                    throw new InvalidDataException("HALCON did not return a valid image: " + rasterPath);

                HTuple w = null, h = null;
                int sourceWidth, sourceHeight;
                try
                {
                    HOperatorSet.GetImageSize(large, out w, out h);
                    sourceWidth = w.I;
                    sourceHeight = h.I;
                }
                finally
                {
                    if (w != null) w.Dispose();
                    if (h != null) h.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(debugTileDirectory))
                    Directory.CreateDirectory(debugTileDirectory);

                foreach (var rect in rects)
                {
                    ct.ThrowIfCancellationRequested();
                    Validate(rect, sourceWidth, sourceHeight);

                    HObject cropped = null;
                    try
                    {
                        // crop_rectangle1 takes an inclusive bottom-right corner.
                        HOperatorSet.CropRectangle1(large, out cropped, rect.Y, rect.X,
                                                    rect.Y + rect.Height - 1, rect.X + rect.Width - 1);
                        var mat = interop.ToMatCopy(cropped);
                        if (source._tiles.ContainsKey(rect.OrderIndex))
                        {
                            mat.Dispose();
                            throw new ArgumentException("Duplicate OrderIndex in tile rect list: " +
                                                        rect.OrderIndex, "rects");
                        }
                        source._tiles.Add(rect.OrderIndex, mat);

                        if (!string.IsNullOrWhiteSpace(debugTileDirectory))
                        {
                            var name = string.Format("Sample_R{0:00}_C{1:00}_O{2:000}.tiff",
                                                     rect.Row, rect.Column, rect.OrderIndex);
                            HOperatorSet.WriteImage(cropped, "tiff", 0, Path.Combine(debugTileDirectory, name));
                        }
                    }
                    finally
                    {
                        if (cropped != null && cropped.IsInitialized()) cropped.Dispose();
                    }
                }
            }
            catch
            {
                source.Dispose();
                throw;
            }
            finally
            {
                // Release the ~1.3 GB raster before alignment begins; nothing downstream needs it.
                if (large != null && large.IsInitialized()) large.Dispose();
            }
            return source;
        }

        private static void Validate(SampleTileRect r, int sourceWidth, int sourceHeight)
        {
            if (r.Width <= 0 || r.Height <= 0)
                throw new ArgumentOutOfRangeException("rects", "Tile " + r.OrderIndex + " has a non-positive size.");
            if (r.X < 0 || r.Y < 0 || r.X + r.Width > sourceWidth || r.Y + r.Height > sourceHeight)
                throw new ArgumentOutOfRangeException(
                    "rects", "Tile " + r.OrderIndex + " (" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height +
                             ") falls outside the sample raster " + sourceWidth + "x" + sourceHeight + ".");
        }

        public Mat GetTile(int orderIndex)
        {
            Mat tile;
            if (!_tiles.TryGetValue(orderIndex, out tile))
                throw new KeyNotFoundException("No sample tile for OrderIndex " + orderIndex + ".");
            return tile;
        }

        public Bitmap GetTileBitmap(int orderIndex)
        {
            return new ImageInteropService().ToBitmapCopy(GetTile(orderIndex));
        }

        public void Dispose()
        {
            foreach (var tile in _tiles.Values)
                tile.Dispose();
            _tiles.Clear();
        }
    }
}
