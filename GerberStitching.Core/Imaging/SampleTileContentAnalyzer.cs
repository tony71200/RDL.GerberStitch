using System;
using OpenCvSharp;

namespace GerberViewer.Stitching.Imaging
{
    public enum SampleTileContentClass { Matchable, ExactZero, UniformNonZero, LowTexture, Unsupported }

    public sealed class SampleTileContentMetrics
    {
        public SampleTileContentClass ContentClass { get; set; }
        public double MinGray { get; set; }
        public double MaxGray { get; set; }
        public double MeanGray { get; set; }
        public double StdDevGray { get; set; }
        public double NonZeroPixelRatio { get; set; }
    }

    // [Codex] [Change time: 2026-07-27] [Purpose: Distinguish exact-zero samples from uniform nonzero and merely low-texture samples before matching.]
    public sealed class SampleTileContentAnalyzer
    {
        public SampleTileContentMetrics Analyze(Mat source, double lowTextureStdDevThreshold)
        {
            if (source == null || source.Empty()) return Unsupported();
            using (var gray = ToCanonicalGray(source))
            {
                if (gray.Depth() != MatType.CV_8U && gray.Depth() != MatType.CV_16U) return Unsupported();
                double min, max;
                Cv2.MinMaxLoc(gray, out min, out max);
                Scalar mean, stddev;
                Cv2.MeanStdDev(gray, out mean, out stddev);
                var nonZero = Cv2.CountNonZero(gray);
                var ratio = nonZero / (double)(gray.Rows * gray.Cols);
                var contentClass = min == 0.0 && max == 0.0 && nonZero == 0
                    ? SampleTileContentClass.ExactZero
                    : (stddev.Val0 == 0.0
                        ? SampleTileContentClass.UniformNonZero
                        : ((stddev.Val0 < lowTextureStdDevThreshold || ratio < 0.001) ? SampleTileContentClass.LowTexture : SampleTileContentClass.Matchable));
                return new SampleTileContentMetrics { ContentClass = contentClass, MinGray = min, MaxGray = max, MeanGray = mean.Val0, StdDevGray = stddev.Val0, NonZeroPixelRatio = ratio };
            }
        }

        private static Mat ToCanonicalGray(Mat source)
        {
            if (source.Channels() == 1) return source.Clone();
            var gray = new Mat();
            if (source.Channels() == 3) Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
            else if (source.Channels() == 4) Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
            else { gray.Dispose(); return new Mat(); }
            return gray;
        }

        private static SampleTileContentMetrics Unsupported() { return new SampleTileContentMetrics { ContentClass = SampleTileContentClass.Unsupported }; }
    }
}
