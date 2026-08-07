using System;
using System.Drawing;
using GerberViewer.Stitching.Imaging.ImageInterop;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Interop
{
    /// <summary>Bitmap is borrowed for the call; every returned Mat is an independent caller-owned copy.</summary>
    public sealed class BitmapMatchImageAdapter
    {
        private readonly IImageInteropService _interop;
        public BitmapMatchImageAdapter() : this(new ImageInteropService()) { }
        public BitmapMatchImageAdapter(IImageInteropService interop) { _interop = interop ?? throw new ArgumentNullException("interop"); }
        public Mat ToMono8MatCopy(Bitmap source) { return _interop.ToMatCopy(source, InteropPixelFormat.Mono8); }
        public Mat ToBgr8MatCopy(Bitmap source) { return _interop.ToMatCopy(source, InteropPixelFormat.Bgr8); }
        public Bitmap ToBitmapCopy(Mat source) { return _interop.ToBitmapCopy(source); }
    }
}
