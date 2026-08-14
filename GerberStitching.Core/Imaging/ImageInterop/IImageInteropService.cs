using System.Drawing;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Imaging.ImageInterop
{
    public sealed class HalconImageInteropException : System.InvalidOperationException
    {
        public HalconImageInteropException(string message, int errorCode, bool licenseExpired,
                                           System.Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            LicenseExpired = licenseExpired;
        }
        public int ErrorCode { get; private set; }
        public bool LicenseExpired { get; private set; }
    }

    /// <summary>
    /// Copy-only image interop boundary. Every returned Bitmap, Mat, or HObject is owned by the caller
    /// and must not reference temporary source buffers that were disposed inside the conversion method.
    /// Bitmap Format24bppRgb stores bytes in BGR order in memory; OpenCV color Mats are canonical BGR8;
    /// HALCON three-channel HObject images are treated as RGB planes unless a target format explicitly says BGR.
    /// </summary>
    public interface IImageInteropService
    {
        HObject ToHObjectCopy(Bitmap source, InteropPixelFormat targetFormat);
        HObject ToHObjectCopy(Mat source, InteropPixelFormat targetFormat);
        Mat ToMatCopy(Bitmap source, InteropPixelFormat targetFormat);
        Mat ToMatCopy(HObject source);
        Bitmap ToBitmapCopy(Mat source);
        Bitmap ToBitmapCopy(HObject source);
        ImagePixelFormatInfo Describe(Mat source);
        ImagePixelFormatInfo Describe(Bitmap source);
        ImagePixelFormatInfo Describe(HObject source);
    }
}
