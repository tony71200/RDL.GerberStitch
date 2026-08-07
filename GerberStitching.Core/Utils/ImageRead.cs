using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace GerberViewer.Stitching.Utils
{
    public static class ImageRead
    {
        public sealed class ImageReadException : Exception { public ImageReadException(string message, Exception inner = null) : base(message, inner) { } }
        public static Bitmap ReadBitmap(string filename)
        {
            if (string.IsNullOrWhiteSpace(filename) || !File.Exists(filename)) throw new ImageReadException("Image not found: " + filename);
            try { using (var bmp = new Bitmap(filename)) return Ensure24bppBgr(bmp); }
            catch (Exception ex) { throw new ImageReadException("Failed to read image: " + filename, ex); }
        }
        public static Bitmap Ensure24bppBgr(Bitmap src)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.PixelFormat == PixelFormat.Format24bppRgb) return (Bitmap)src.Clone();
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(dst)) g.DrawImage(src, 0, 0, src.Width, src.Height);
            return dst;
        }
    }
}
