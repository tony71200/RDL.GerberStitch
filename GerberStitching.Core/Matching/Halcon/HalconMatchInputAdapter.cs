using System;
using GerberViewer.Stitching.Imaging.ImageInterop;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Halcon
{
    /// <summary>Creates independent HALCON images; the caller owns and disposes every returned HObject.</summary>
    public sealed class HalconMatchInputAdapter
    {
        private readonly IImageInteropService _interop;
        public HalconMatchInputAdapter() : this(new ImageInteropService())
        {
        }
        public HalconMatchInputAdapter(IImageInteropService interop)
        {
            _interop = interop ?? throw new ArgumentNullException("interop");
        }
        public HObject ToMono8HObjectCopy(Mat source)
        {
            return _interop.ToHObjectCopy(source, InteropPixelFormat.Mono8);
        }
        public HObject ToMono16HObjectCopy(Mat source)
        {
            return _interop.ToHObjectCopy(source, InteropPixelFormat.Mono16);
        }
        public HObject ToBgr8HObjectCopy(Mat source)
        {
            return _interop.ToHObjectCopy(source, InteropPixelFormat.Bgr8);
        }
    }
}
