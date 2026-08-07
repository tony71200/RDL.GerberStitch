using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GerberViewer.Stitching.Utils
{
    public static class TiffBigWriter
    {
        public static Task SaveTiffGray8Async(string outputPath, int width, int height, byte[] pixels, CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(outputPath, width, height, pixels, 1); cancellationToken.ThrowIfCancellationRequested(); EnsureDir(outputPath);
            using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            { 
                for (int y = 0; y < height; y++) 
                    for (int x = 0; x < width; x++) 
                    { 
                        var v = pixels[y * width + x]; 
                        bmp.SetPixel(x, y, Color.FromArgb(v, v, v)); 
                    } 
                bmp.Save(outputPath, ImageFormat.Tiff); 
            }
            return Task.FromResult(0);
        }
        public static Task SaveTiffRgb24Async(string outputPath, int width, int height, byte[] pixelsRgb, CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(outputPath, width, height, pixelsRgb, 3); 
            cancellationToken.ThrowIfCancellationRequested(); 
            EnsureDir(outputPath);
            using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            { 
                int i = 0; 
                for (int y = 0; y < height; y++) 
                    for (int x = 0; x < width; x++) 
                        bmp.SetPixel(x, y, Color.FromArgb(pixelsRgb[i++], pixelsRgb[i++], pixelsRgb[i++])); 
                bmp.Save(outputPath, ImageFormat.Tiff); 
            }
            return Task.FromResult(0);
        }
        private static void EnsureDir(string path) 
        { 
            var dir = Path.GetDirectoryName(path); 
            if (!string.IsNullOrWhiteSpace(dir) && 
                !Directory.Exists(dir)) 
                Directory.CreateDirectory(dir); 
        }
        private static void Validate(string path, int w, int h, byte[] p, int bpp) 
        { 
            if (string.IsNullOrWhiteSpace(path)) 
                throw new ArgumentException("Output path is empty.", nameof(path)); 
            if (w <= 0 || h <= 0) 
                throw new ArgumentOutOfRangeException("Invalid image size."); 
            if (p == null) 
                throw new ArgumentNullException(nameof(p)); 
            if (p.LongLength != (long)w * h * bpp) 
                throw new ArgumentException("Pixel buffer length mismatch.", nameof(p)); 
        }
    }
}
