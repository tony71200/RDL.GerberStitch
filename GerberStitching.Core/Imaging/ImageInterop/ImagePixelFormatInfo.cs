namespace GerberViewer.Stitching.Imaging.ImageInterop
{
    public enum InteropPixelFormat
    {
        Mono8,
        Mono16,
        Bgr8,
        Rgb8
    }

    public sealed class ImagePixelFormatInfo
    {
        public ImagePixelFormatInfo(InteropPixelFormat pixelFormat, int channelCount, int bitDepth, string channelOrder)
        {
            PixelFormat = pixelFormat;
            ChannelCount = channelCount;
            BitDepth = bitDepth;
            ChannelOrder = channelOrder;
        }

        public InteropPixelFormat PixelFormat { get; private set; }
        public int ChannelCount { get; private set; }
        public int BitDepth { get; private set; }
        public string ChannelOrder { get; private set; }
    }
}
