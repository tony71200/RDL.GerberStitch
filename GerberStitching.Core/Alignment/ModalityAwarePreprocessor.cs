using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Matching.Interop;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    public sealed class PreprocessedAlignmentImages : IDisposable
    {
        public Mat Sample { get; set; }
        public Mat Captured { get; set; }
        public Bitmap SampleDiagnostic { get; set; }
        public Bitmap CapturedDiagnostic { get; set; }
        public string Variant { get; set; }

        public void Dispose()
        {
            if (Sample != null) Sample.Dispose();
            if (Captured != null) Captured.Dispose();
            if (SampleDiagnostic != null) SampleDiagnostic.Dispose();
            if (CapturedDiagnostic != null) CapturedDiagnostic.Dispose();
        }
    }

    public sealed class ModalityAwarePreprocessor
    {
        // Temporary NCC investigation switch: keep normalized/polarity-adjusted Mono8 images
        // intact and skip threshold/edge preprocessing that can remove gray-level texture
        // needed by HALCON NCC.
        private const bool SkipThresholdAndEdgePreparation = true;

        private readonly BitmapMatchImageAdapter _imageAdapter;

        public ModalityAwarePreprocessor() : this(new ImageInteropService())
        {
        }

        public ModalityAwarePreprocessor(IImageInteropService imageInterop)
        {
            if (imageInterop == null) throw new ArgumentNullException("imageInterop");
            _imageAdapter = new BitmapMatchImageAdapter(imageInterop);
        }

        public IList<PreprocessedAlignmentImages> PreprocessCandidates(Bitmap sample, Bitmap captured, PreprocessingOptions options)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            if (captured == null) throw new ArgumentNullException("captured");
            options = options ?? new PreprocessingOptions();
            var candidates = new List<PreprocessedAlignmentImages>();
            //if (options.Polarity == PolarityMode.Auto)
            //{
            //    candidates.Add(CreateCandidate(sample, captured, options, PolarityMode.AsIs, "polarity:auto/as-is"));
            //    candidates.Add(CreateCandidate(sample, captured, options, PolarityMode.InvertCaptured, "polarity:auto/invert-captured"));
            //    return candidates;
            //}

            candidates.Add(CreateCandidate(sample, captured, options, options.Polarity, "polarity:" + options.Polarity));
            return candidates;
        }

        private PreprocessedAlignmentImages CreateCandidate(Bitmap sample, Bitmap captured, PreprocessingOptions options, PolarityMode polarity, string polarityVariant)
        {
            Mat sampleMat = null;
            Mat capturedMat = null;
            try
            {
                sampleMat = _imageAdapter.ToMono8MatCopy(sample);
                capturedMat = _imageAdapter.ToMono8MatCopy(captured);
                ResizeIfRequested(ref sampleMat, options.NormalizedWidth, options.NormalizedHeight);
                ResizeIfRequested(ref capturedMat, options.NormalizedWidth, options.NormalizedHeight);
                // Contrast belongs to the Direct Alignment moving/request image only.
                IncreaseContrast(capturedMat, options.Contrast);
                
                //Threshold(capturedMat, options);
                if (!SkipThresholdAndEdgePreparation)
                {
                    Normalize(sampleMat, options.ContrastNormalization);
                    Normalize(capturedMat, options.ContrastNormalization);
                    ApplyPolarity(sampleMat, capturedMat, polarity);
                    Threshold(sampleMat, options);
                    Threshold(capturedMat, options);
                    if (options.ApplyGerberContentMask) ApplyContentMask(sampleMat, capturedMat);
                    PrepareEdges(ref sampleMat, options.EdgePreparation);
                    PrepareEdges(ref capturedMat, options.EdgePreparation);
                }

                var result = new PreprocessedAlignmentImages
                {
                    Sample = sampleMat,
                    Captured = capturedMat,
                    Variant = BuildVariant(options, polarityVariant)
                };
                sampleMat = null;
                capturedMat = null;
                if (options.IncludeDiagnosticImages)
                {
                    result.SampleDiagnostic = _imageAdapter.ToBitmapCopy(result.Sample);
                    result.CapturedDiagnostic = _imageAdapter.ToBitmapCopy(result.Captured);
                }
                return result;
            }
            finally
            {
                if (sampleMat != null) sampleMat.Dispose();
                if (capturedMat != null) capturedMat.Dispose();
            }
        }

        private static string BuildVariant(PreprocessingOptions o, string polarityVariant)
        {
            var threshold = SkipThresholdAndEdgePreparation ? "disabled-temporary" : o.Threshold.ToString();
            var edge = SkipThresholdAndEdgePreparation ? "disabled-temporary" : o.EdgePreparation.ToString();
            var mask = SkipThresholdAndEdgePreparation ? "disabled-temporary" : o.ApplyGerberContentMask.ToString();
            return string.Format(CultureInfo.InvariantCulture, "opencv-gray+contrast:{0:0.###}%+{1}+{2}+threshold:{3}+edge:{4}+mask:{5}+size:{6}x{7}", o.Contrast, o.ContrastNormalization, polarityVariant, threshold, edge, mask, o.NormalizedWidth, o.NormalizedHeight);
        }

        private static void ResizeIfRequested(ref Mat image, int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            if (image.Cols == width && image.Rows == height) return;
            var resized = new Mat();
            Cv2.Resize(image, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
            image.Dispose();
            image = resized;
        }

        /// <summary>
        /// Applies contrast to a Mono8 OpenCV image in place. The same processed Mat is also
        /// used as the input for HALCON matchers, so both matcher implementations receive
        /// identical pixels. A value of 100 preserves the source image.
        /// </summary>
        public static void IncreaseContrast(Mat image, double contrastPercent = 100d)
        {
            if (image == null) throw new ArgumentNullException("image");
            ValidateContrast(contrastPercent);
            if (Math.Abs(contrastPercent - 100d) < 1e-9) return;

            double alpha;
            double beta;
            GetContrastScale(contrastPercent, out alpha, out beta);
            // ConvertTo uses saturating conversion, keeping the adjusted result in Mono8 range.
            image.ConvertTo(image, image.Type(), alpha, beta);
        }

        /// <summary>Creates an owned contrast-adjusted copy without changing the source image.</summary>
        public static Mat CreateContrastCopy(Mat source, double contrastPercent = 100d)
        {
            if (source == null) throw new ArgumentNullException("source");
            var result = source.Clone();
            try
            {
                IncreaseContrast(result, contrastPercent);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private static void ValidateContrast(double contrastPercent)
        {
            if (double.IsNaN(contrastPercent) || double.IsInfinity(contrastPercent) || contrastPercent < 0d)
                throw new ArgumentOutOfRangeException("contrastPercent", "Contrast must be a finite value greater than or equal to zero.");
        }

        private static void GetContrastScale(double contrastPercent, out double alpha, out double beta)
        {
            alpha = contrastPercent / 100d;
            beta = 127.5d * (1d - alpha);
        }

        private static void Normalize(Mat image, ContrastNormalizationMode mode)
        {
            if (mode == ContrastNormalizationMode.None) return;
            Cv2.Normalize(image, image, 0, 255, NormTypes.MinMax);
        }

        private static void ApplyPolarity(Mat sample, Mat captured, PolarityMode mode)
        {
            if (mode == PolarityMode.InvertSample || mode == PolarityMode.InvertBoth) Cv2.BitwiseNot(sample, sample);
            if (mode == PolarityMode.InvertCaptured || mode == PolarityMode.InvertBoth) Cv2.BitwiseNot(captured, captured);
        }

        private static void Threshold(Mat image, PreprocessingOptions options)
        {
            if (options.Threshold == ThresholdMode.None) return;
            if (options.Threshold == ThresholdMode.Fixed)
            {
                Cv2.Threshold(image, image, options.FixedThreshold, 255, ThresholdTypes.Binary);
                return;
            }
            if (options.Threshold == ThresholdMode.Adaptive)
            {
                var blockSize = Math.Max(3, options.AdaptiveRadius | 1);
                using (var adaptive = new Mat())
                {
                    Cv2.AdaptiveThreshold(image, adaptive, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, blockSize, 2);
                    adaptive.CopyTo(image);
                }
                return;
            }
            Cv2.Threshold(image, image, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        }

        private static void ApplyContentMask(Mat sample, Mat captured)
        {
            using (var sampleMask = new Mat())
            using (var capturedMask = new Mat())
            {
                Cv2.Threshold(sample, sampleMask, 0, 255, ThresholdTypes.Binary);
                Cv2.Threshold(captured, capturedMask, 0, 255, ThresholdTypes.Binary);
                Cv2.BitwiseAnd(sample, sampleMask, sample);
                Cv2.BitwiseAnd(captured, capturedMask, captured);
            }
        }

        private static void PrepareEdges(ref Mat image, EdgePreparationMode mode)
        {
            if (mode == EdgePreparationMode.None) return;
            if (mode == EdgePreparationMode.Canny)
            {
                var canny = new Mat();
                Cv2.Canny(image, canny, 50, 150);
                image.Dispose();
                image = canny;
                return;
            }

            var gradX = new Mat();
            var gradY = new Mat();
            var absX = new Mat();
            var absY = new Mat();
            var sobel = new Mat();
            try
            {
                Cv2.Sobel(image, gradX, MatType.CV_16SC1, 1, 0);
                Cv2.Sobel(image, gradY, MatType.CV_16SC1, 0, 1);
                Cv2.ConvertScaleAbs(gradX, absX);
                Cv2.ConvertScaleAbs(gradY, absY);
                Cv2.AddWeighted(absX, 0.5, absY, 0.5, 0, sobel);
                image.Dispose();
                image = sobel;
                sobel = null;
            }
            finally
            {
                gradX.Dispose();
                gradY.Dispose();
                absX.Dispose();
                absY.Dispose();
                if (sobel != null) sobel.Dispose();
            }
        }
    }
}
