using System;
using System.IO;
using HalconDotNet;

namespace GerberViewer.Stitching.Imaging
{
    public static class HalconImageFileValidator
    {
        public static void Validate(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new IOException("Image output was not written: " + (path ?? string.Empty));
            if (new FileInfo(path).Length <= 0)
                throw new IOException("Image output was empty: " + path);

            HObject image = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.ReadImage(out image, path);
                HOperatorSet.GetImageSize(image, out width, out height);
                var actualWidth = width == null || width.Length == 0 ? 0 : width.I;
                var actualHeight = height == null || height.Length == 0 ? 0 : height.I;
                if (actualWidth <= 0 || actualHeight <= 0)
                    throw new IOException("HALCON reopened the image but returned invalid dimensions: " + path);
            }
            catch (HOperatorException ex)
            {
                throw new IOException("HALCON could not reopen the image output: " + path, ex);
            }
            finally
            {
                if (height != null)
                    height.Dispose();
                if (width != null)
                    width.Dispose();
                if (image != null && image.IsInitialized())
                    image.Dispose();
            }
        }
    }
}
