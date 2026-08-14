using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Imaging.ImageInterop
{
    public sealed class ImageInteropService : IImageInteropService
    {
        public HObject ToHObjectCopy(Bitmap source, InteropPixelFormat targetFormat)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            using (var mat = ToMatCopy(source, targetFormat)) return ToHObjectCopy(mat, targetFormat);
        }

        public HObject ToHObjectCopy(Mat source, InteropPixelFormat targetFormat)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (source.Empty())
                throw new ArgumentException("Source Mat is empty.", "source");
            try
            {
                using (var prepared = ConvertMatCopy(source, targetFormat))
                {
                    if (targetFormat == InteropPixelFormat.Mono8)
                        return AllocateMonoImageCopy(prepared, "byte");
                    if (targetFormat == InteropPixelFormat.Mono16)
                        return AllocateMonoImageCopy(prepared, "uint2");
                    if (targetFormat == InteropPixelFormat.Bgr8)
                        return AllocateColorImageCopy(prepared, "bgr");
                    if (targetFormat == InteropPixelFormat.Rgb8)
                        return AllocateColorImageCopy(prepared, "rgb");
                    throw new NotSupportedException("Unsupported target pixel format: " + targetFormat);
                }
            }
            catch (HOperatorException ex)
            {
                var code = GetHalconErrorCode(ex);
                var expired = code == 2042;
                var guidance = expired ? "HALCON license feature has expired (error 2042). Renew/select a valid " +
                                         "HALCON 25.05 license or configure the Direct pipeline to use the OpenCV " +
                                         "refinement fallback; pixel conversion cannot repair an expired license."
                                       : "HALCON failed while allocating/copying an owned image (error " + code + ").";
                throw new HalconImageInteropException(guidance, code, expired, ex);
            }
        }

        public Mat ToMatCopy(Bitmap source, InteropPixelFormat targetFormat)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            using (var sourceBgr = ToBgr24MatCopy(source))
            {
                return ConvertMatCopy(sourceBgr, targetFormat);
            }
        }

        public Mat ToMatCopy(HObject source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            var info = Describe(source);
            if (info.PixelFormat == InteropPixelFormat.Mono8)
                return HObjectMonoToMatCopy(source, MatType.CV_8UC1, 1);
            if (info.PixelFormat == InteropPixelFormat.Mono16)
                return HObjectMonoToMatCopy(source, MatType.CV_16UC1, 2);
            if (info.PixelFormat == InteropPixelFormat.Rgb8)
                return HObjectColorToMatCopy(source, true);
            throw new NotSupportedException("Unsupported HObject format: " + info.PixelFormat);
        }

        public Bitmap ToBitmapCopy(Mat source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (source.Empty())
                throw new ArgumentException("Source Mat is empty.", "source");
            using (var bgr = ConvertMatCopy(source, InteropPixelFormat.Bgr8))
            {
                var bitmap = new Bitmap(bgr.Cols, bgr.Rows, PixelFormat.Format24bppRgb);
                var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    CopyMatToBitmapData(bgr, data);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
                return bitmap;
            }
        }

        public Bitmap ToBitmapCopy(HObject source)
        {
            using (var mat = ToMatCopy(source)) return ToBitmapCopy(mat);
        }

        public ImagePixelFormatInfo Describe(Mat source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            var channels = source.Channels();
            var depth = source.Depth();
            if (channels == 1 && depth == MatType.CV_8U)
                return new ImagePixelFormatInfo(InteropPixelFormat.Mono8, 1, 8, "Gray");
            if (channels == 1 && depth == MatType.CV_16U)
                return new ImagePixelFormatInfo(InteropPixelFormat.Mono16, 1, 16, "Gray");
            if (channels == 3 && depth == MatType.CV_8U)
                return new ImagePixelFormatInfo(InteropPixelFormat.Bgr8, 3, 8, "BGR");
            throw new NotSupportedException("Unsupported Mat type: " + source.Type());
        }

        public ImagePixelFormatInfo Describe(Bitmap source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            if (source.PixelFormat == PixelFormat.Format8bppIndexed)
                return new ImagePixelFormatInfo(InteropPixelFormat.Mono8, 1, 8, "Gray");
            if (source.PixelFormat == PixelFormat.Format16bppGrayScale)
                return new ImagePixelFormatInfo(InteropPixelFormat.Mono16, 1, 16, "Gray");
            if (source.PixelFormat == PixelFormat.Format24bppRgb)
                return new ImagePixelFormatInfo(InteropPixelFormat.Bgr8, 3, 8, "BGR memory / RGB logical Bitmap");
            if (source.PixelFormat == PixelFormat.Format32bppArgb || source.PixelFormat == PixelFormat.Format32bppRgb)
                return new ImagePixelFormatInfo(InteropPixelFormat.Bgr8, 3, 8, "BGR after alpha drop");
            throw new NotSupportedException("Unsupported Bitmap pixel format: " + source.PixelFormat);
        }

        public ImagePixelFormatInfo Describe(HObject source)
        {
            if (source == null)
                throw new ArgumentNullException("source");
            HTuple channels = null;
            HTuple pointer = null;
            HTuple type = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.CountChannels(source, out channels);
                if (channels.I == 1)
                {
                    HOperatorSet.GetImagePointer1(source, out pointer, out type, out width, out height);
                    var t = type.S;
                    if (string.Equals(t, "byte", StringComparison.OrdinalIgnoreCase))
                        return new ImagePixelFormatInfo(InteropPixelFormat.Mono8, 1, 8, "Gray");
                    if (string.Equals(t, "uint2", StringComparison.OrdinalIgnoreCase))
                        return new ImagePixelFormatInfo(InteropPixelFormat.Mono16, 1, 16, "Gray");
                }
                if (channels.I == 3)
                    return new ImagePixelFormatInfo(InteropPixelFormat.Rgb8, 3, 8, "RGB HALCON planes");
                throw new NotSupportedException("Unsupported HObject channel count: " + channels.I);
            }
            finally
            {
                DisposeTuple(channels);
                DisposeTuple(pointer);
                DisposeTuple(type);
                DisposeTuple(width);
                DisposeTuple(height);
            }
        }

        private static Mat ConvertMatCopy(Mat source, InteropPixelFormat targetFormat)
        {
            if (targetFormat == InteropPixelFormat.Mono8)
            {
                if (source.Type() == MatType.CV_8UC1)
                    return source.Clone();
                var gray = new Mat();
                if (source.Channels() == 3)
                    Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                else if (source.Type() == MatType.CV_16UC1)
                    source.ConvertTo(gray, MatType.CV_8UC1, 1.0 / 257.0);
                else
                    throw new NotSupportedException("Cannot convert Mat to Mono8: " + source.Type());
                return gray;
            }
            if (targetFormat == InteropPixelFormat.Mono16)
            {
                if (source.Type() == MatType.CV_16UC1)
                    return source.Clone();
                using (var gray8 = ConvertMatCopy(source, InteropPixelFormat.Mono8))
                {
                    var gray16 = new Mat();
                    gray8.ConvertTo(gray16, MatType.CV_16UC1, 257.0);
                    return gray16;
                }
            }
            if (targetFormat == InteropPixelFormat.Bgr8)
            {
                if (source.Type() == MatType.CV_8UC3)
                    return source.Clone();
                if (source.Type() == MatType.CV_8UC1)
                {
                    var bgr = new Mat();
                    Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
                    return bgr;
                }
                if (source.Type() == MatType.CV_16UC1)
                {
                    using (var gray8 = ConvertMatCopy(source, InteropPixelFormat.Mono8))
                    {
                        var bgr = new Mat();
                        Cv2.CvtColor(gray8, bgr, ColorConversionCodes.GRAY2BGR);
                        return bgr;
                    }
                }
                throw new NotSupportedException("Cannot convert Mat to BGR8: " + source.Type());
            }
            if (targetFormat == InteropPixelFormat.Rgb8)
            {
                using (var bgr = ConvertMatCopy(source, InteropPixelFormat.Bgr8))
                {
                    var rgb = new Mat();
                    Cv2.CvtColor(bgr, rgb, ColorConversionCodes.BGR2RGB);
                    return rgb;
                }
            }
            throw new NotSupportedException("Unsupported target pixel format: " + targetFormat);
        }

        private static Mat ToBgr24MatCopy(Bitmap source)
        {
            var rect = new Rectangle(0, 0, source.Width, source.Height);
            using (var clone = source.PixelFormat == PixelFormat.Format24bppRgb
                                   ? (Bitmap)source.Clone()
                                   : source.Clone(rect, PixelFormat.Format24bppRgb))
            {
                var mat = new Mat(clone.Height, clone.Width, MatType.CV_8UC3);
                var data = clone.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    CopyBitmapDataToMat(data, mat);
                }
                finally
                {
                    clone.UnlockBits(data);
                }
                return mat;
            }
        }

        private static HObject AllocateMonoImageCopy(Mat source, string halconType)
        {
            // [Tony] [Change time: 2026-08-02] [Purpose: Avoid gen_image1 feature-expiry failures and give HALCON
            // ownership of the copied pixel buffer.]
            using (var contiguous = source.Clone())
            {
                var bytes = new byte[checked((int)(contiguous.Rows * contiguous.Cols * contiguous.ElemSize()))];
                Marshal.Copy(contiguous.Data, bytes, 0, bytes.Length);
                HObject image = null;
                HTuple pointer = null;
                HTuple type = null;
                HTuple width = null;
                HTuple height = null;
                try
                {
                    HOperatorSet.GenImageConst(out image, halconType, contiguous.Cols, contiguous.Rows);
                    HOperatorSet.GetImagePointer1(image, out pointer, out type, out width, out height);
                    ValidateAllocatedImage(type, width, height, halconType, contiguous.Cols, contiguous.Rows);
                    Marshal.Copy(bytes, 0, pointer.IP, bytes.Length);
                    var result = image;
                    image = null;
                    return result;
                }
                finally
                {
                    if (image != null)
                        image.Dispose();
                    DisposeTuple(pointer);
                    DisposeTuple(type);
                    DisposeTuple(width);
                    DisposeTuple(height);
                }
            }
        }

        private static HObject AllocateColorImageCopy(Mat source, string colorFormat)
        {
            using (var contiguous = source.Clone())
            {
                var bytes = new byte[checked((int)(contiguous.Rows * contiguous.Cols * contiguous.ElemSize()))];
                Marshal.Copy(contiguous.Data, bytes, 0, bytes.Length);
                var count = contiguous.Rows * contiguous.Cols;
                var red = new byte[count];
                var green = new byte[count];
                var blue = new byte[count];
                var sourceIsBgr = string.Equals(colorFormat, "bgr", StringComparison.OrdinalIgnoreCase);
                for (var i = 0; i < count; i++)
                {
                    var o = i * 3;
                    red[i] = bytes[o + (sourceIsBgr ? 2 : 0)];
                    green[i] = bytes[o + 1];
                    blue[i] = bytes[o + (sourceIsBgr ? 0 : 2)];
                }
                HObject r = null;
                HObject g = null;
                HObject b = null;
                HObject rgb = null;
                try
                {
                    r = GenMono8FromManagedCopy(red, contiguous.Cols, contiguous.Rows);
                    g = GenMono8FromManagedCopy(green, contiguous.Cols, contiguous.Rows);
                    b = GenMono8FromManagedCopy(blue, contiguous.Cols, contiguous.Rows);
                    HOperatorSet.Compose3(r, g, b, out rgb);
                    var result = rgb;
                    rgb = null;
                    return result;
                }
                finally
                {
                    if (rgb != null)
                        rgb.Dispose();
                    if (b != null)
                        b.Dispose();
                    if (g != null)
                        g.Dispose();
                    if (r != null)
                        r.Dispose();
                }
            }
        }

        private static HObject GenMono8FromManagedCopy(byte[] bytes, int widthValue, int heightValue)
        {
            HObject image = null;
            HTuple pointer = null;
            HTuple type = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.GenImageConst(out image, "byte", widthValue, heightValue);
                HOperatorSet.GetImagePointer1(image, out pointer, out type, out width, out height);
                ValidateAllocatedImage(type, width, height, "byte", widthValue, heightValue);
                Marshal.Copy(bytes, 0, pointer.IP, bytes.Length);
                var result = image;
                image = null;
                return result;
            }
            finally
            {
                if (image != null)
                    image.Dispose();
                DisposeTuple(pointer);
                DisposeTuple(type);
                DisposeTuple(width);
                DisposeTuple(height);
            }
        }

        private static void ValidateAllocatedImage(HTuple type, HTuple width, HTuple height, string expectedType,
                                                   int expectedWidth, int expectedHeight)
        {
            if (type == null || width == null || height == null ||
                !string.Equals(type.S, expectedType, StringComparison.OrdinalIgnoreCase) || width.I != expectedWidth ||
                height.I != expectedHeight)
                throw new InvalidOperationException(
                    "HALCON-owned image allocation does not match the requested pixel contract.");
        }

        private static int GetHalconErrorCode(HOperatorException exception)
        {
            if (exception == null)
                return 0;
            var property = exception.GetType().GetProperty("ErrorCode");
            var propertyValue = property == null ? null : property.GetValue(exception, null);
            if (propertyValue is int)
                return (int)propertyValue;
            var method = exception.GetType().GetMethod("GetErrorCode", Type.EmptyTypes);
            var methodValue = method == null ? null : method.Invoke(exception, null);
            return methodValue is int ? (int)methodValue : 0;
        }

        private static Mat HObjectMonoToMatCopy(HObject source, MatType matType, int bytesPerPixel)
        {
            HTuple pointer = null;
            HTuple type = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.GetImagePointer1(source, out pointer, out type, out width, out height);
                var mat = new Mat(height.I, width.I, matType);
                var bytes = new byte[width.I * height.I * bytesPerPixel];
                Marshal.Copy(pointer.IP, bytes, 0, bytes.Length);
                Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
                return mat;
            }
            finally
            {
                DisposeTuple(pointer);
                DisposeTuple(type);
                DisposeTuple(width);
                DisposeTuple(height);
            }
        }

        private static Mat HObjectColorToMatCopy(HObject source, bool halconRgbToBgr)
        {
            HTuple red = null;
            HTuple green = null;
            HTuple blue = null;
            HTuple type = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.GetImagePointer3(source, out red, out green, out blue, out type, out width, out height);
                var count = width.I * height.I;
                var r = new byte[count];
                var g = new byte[count];
                var b = new byte[count];
                Marshal.Copy(red.IP, r, 0, count);
                Marshal.Copy(green.IP, g, 0, count);
                Marshal.Copy(blue.IP, b, 0, count);
                var interleaved = new byte[count * 3];
                for (int i = 0; i < count; i++)
                {
                    var o = i * 3;
                    if (halconRgbToBgr)
                    {
                        interleaved[o] = b[i];
                        interleaved[o + 1] = g[i];
                        interleaved[o + 2] = r[i];
                    }
                    else
                    {
                        interleaved[o] = r[i];
                        interleaved[o + 1] = g[i];
                        interleaved[o + 2] = b[i];
                    }
                }
                var mat = new Mat(height.I, width.I, MatType.CV_8UC3);
                Marshal.Copy(interleaved, 0, mat.Data, interleaved.Length);
                return mat;
            }
            finally
            {
                DisposeTuple(red);
                DisposeTuple(green);
                DisposeTuple(blue);
                DisposeTuple(type);
                DisposeTuple(width);
                DisposeTuple(height);
            }
        }

        private static void CopyBitmapDataToMat(BitmapData bitmapData, Mat mat)
        {
            var rowBytes = checked((int)(mat.Cols * mat.ElemSize()));
            var buffer = new byte[rowBytes];
            for (int y = 0; y < mat.Rows; y++)
            {
                var source = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                var target = IntPtr.Add(mat.Data, y * (int)mat.Step());
                Marshal.Copy(source, buffer, 0, buffer.Length);
                Marshal.Copy(buffer, 0, target, buffer.Length);
            }
        }

        private static void CopyMatToBitmapData(Mat mat, BitmapData bitmapData)
        {
            var rowBytes = checked((int)(mat.Cols * mat.ElemSize()));
            var buffer = new byte[rowBytes];
            for (int y = 0; y < mat.Rows; y++)
            {
                var source = IntPtr.Add(mat.Data, y * (int)mat.Step());
                var target = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride);
                Marshal.Copy(source, buffer, 0, buffer.Length);
                Marshal.Copy(buffer, 0, target, buffer.Length);
            }
        }

        private static void DisposeTuple(HTuple tuple)
        {
            if (tuple != null)
                tuple.Dispose();
        }
    }
}
