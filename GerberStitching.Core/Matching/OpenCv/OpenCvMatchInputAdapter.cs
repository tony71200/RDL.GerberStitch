using System;
using System.IO;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.OpenCv
{
    /// <summary>Decodes matcher inputs as caller-owned Mat copies. OpenCV color images are BGR; grayscale output is
    /// Mono8.</summary>
    public sealed class OpenCvMatchInputAdapter
    {
        public Mat DecodeMono8Copy(string path)
        {
            return DecodeCopy(path, ImreadModes.Grayscale);
        }
        public Mat DecodeBgr8Copy(string path)
        {
            return DecodeCopy(path, ImreadModes.Color);
        }

        private static Mat DecodeCopy(string path, ImreadModes mode)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Image path is required.", "path");
            if (!File.Exists(path))
                throw new FileNotFoundException("Match image was not found.", path);
            var image = Cv2.ImRead(path, mode);
            if (image == null || image.Empty())
            {
                if (image != null)
                    image.Dispose();
                throw new InvalidDataException("OpenCV could not decode match image: " + path);
            }
            return image;
        }
    }
}
