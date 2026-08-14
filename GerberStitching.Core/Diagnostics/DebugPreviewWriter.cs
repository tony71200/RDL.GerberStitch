using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Diagnostics
{
    // [Tony] [Change time: 2026-08-11] [Purpose: Extracted from DebugHtmlReportWriter.cs, which is compiled out
    // entirely in Release (see the #if DEBUG guard in that file and the matching .csproj comment: "Release builds
    // contain no diagnostic type or behavior"). WriteStitchedPreview is now needed at Worker production stations
    // in Release (AlignStitchConfig.EmitDebugPreview toggles it at runtime instead of compile time), so it - and
    // the 4 private helpers used only by it (ReadBoundedWithHalcon/ToBgr/ToGray/BoundedImage) - move here, always
    // compiled. Write() and the DEBUG-only HTML coordinate-frame dump stay untouched in DebugHtmlReportWriter.cs;
    // nothing there calls the members moved here, and nothing here calls back into that file.]
    internal static class DebugPreviewWriter
    {
        public static string WriteStitchedPreview(string outputDirectory, string stitchedPath, SampleManifest manifest,
                                                  IList<CapturedImageInfo> captured, IList<TileWorkflowState> states,
                                                  double manualOffsetX, double manualOffsetY)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory) || string.IsNullOrWhiteSpace(stitchedPath) ||
                manifest == null || string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath) ||
                !File.Exists(stitchedPath) || !File.Exists(manifest.ProcessedSamplePath))
                return null;

            var imageByOrder = (captured ?? new List<CapturedImageInfo>()).ToDictionary(x => x.OrderIndex);
            var boundsInput = new List<Tuple<CapturedImageInfo, double[,]>>();
            foreach (var state in states ?? new List<TileWorkflowState>())
            {
                CapturedImageInfo image;
                if (state != null && state.IsStitchable && state.HasValidPose &&
                    imageByOrder.TryGetValue(state.OrderIndex, out image))
                    boundsInput.Add(Tuple.Create(image, state.GlobalPose));
            }
            if (boundsInput.Count == 0)
                return null;

            const int maximumSide = 1280;
            using (var stitchedSource = ReadBoundedWithHalcon(stitchedPath, maximumSide)) using (
                var sampleSource = ReadBoundedWithHalcon(
                    manifest.ProcessedSamplePath,
                    maximumSide)) using (var stitched =
                                             ToBgr(stitchedSource.Image)) using (var sample =
                                                                                     ToGray(sampleSource.Image))
            {
                var previewSize = stitched.Size();
                var stitchedScaleX = stitched.Cols / (double)stitchedSource.OriginalWidth;
                var stitchedScaleY = stitched.Rows / (double)stitchedSource.OriginalHeight;
                var sampleScaleX = sample.Cols / (double)sampleSource.OriginalWidth;
                var sampleScaleY = sample.Rows / (double)sampleSource.OriginalHeight;
                using (var preview = new Mat()) using (var mask = new Mat()) using (
                    var red = new Mat(
                        previewSize, MatType.CV_8UC3,
                        new Scalar(0, 0, 255))) using (var overlay =
                                                           new Mat()) using (var warp = new Mat(2, 3, MatType.CV_64FC1))
                {
                    stitched.CopyTo(preview);
                    var bounds = GlobalTransformStitcher.CalculateBounds(boundsInput);
                    warp.Set(0, 0, stitchedScaleX / sampleScaleX);
                    warp.Set(0, 1, 0d);
                    warp.Set(0, 2, (manualOffsetX - bounds.Left) * stitchedScaleX);
                    warp.Set(1, 0, 0d);
                    warp.Set(1, 1, stitchedScaleY / sampleScaleY);
                    warp.Set(1, 2, (manualOffsetY - bounds.Top) * stitchedScaleY);
                    Cv2.WarpAffine(sample, mask, warp, previewSize, InterpolationFlags.Nearest, BorderTypes.Constant,
                                   Scalar.All(0));
                    Cv2.Threshold(mask, mask, 0, 255, ThresholdTypes.Binary);
                    Cv2.AddWeighted(preview, 0.45, red, 0.55, 0, overlay);
                    overlay.CopyTo(preview, mask);
                    Directory.CreateDirectory(outputDirectory);
                    var path = Path.Combine(
                        outputDirectory,
                        "DebugPreview_" + DateTime.Now.ToString("yyyyHHmm", CultureInfo.InvariantCulture) + ".jpg");
                    if (!Cv2.ImWrite(path, preview, new ImageEncodingParam(ImwriteFlags.JpegQuality, 60)))
                        return null;
                    return path;
                }
            }
        }

        private static BoundedImage ReadBoundedWithHalcon(string path, int maximumSide)
        {
            HObject source = null;
            HObject resized = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.ReadImage(out source, path);
                HOperatorSet.GetImageSize(source, out width, out height);
                var originalWidth = width.I;
                var originalHeight = height.I;
                var scale = Math.Min(1d, maximumSide / (double)Math.Max(originalWidth, originalHeight));
                // Keep DebugPreview_<time>.jpg at least one sixth of the original image dimensions, even when the
                // source greatly exceeds maximumSide.
                const double minimumScale = 1d / 6d;
                if (scale < minimumScale)
                    scale = minimumScale;
                var targetWidth = Math.Max(1, (int)Math.Round(originalWidth * scale));
                var targetHeight = Math.Max(1, (int)Math.Round(originalHeight * scale));
                if (scale < 1d)
                    HOperatorSet.ZoomImageSize(source, out resized, targetWidth, targetHeight, "constant");
                else
                    HOperatorSet.CopyObj(source, out resized, 1, -1);
                var image = new ImageInteropService().ToMatCopy(resized);
                return new BoundedImage(image, originalWidth, originalHeight);
            }
            finally
            {
                if (source != null)
                    source.Dispose();
                if (resized != null)
                    resized.Dispose();
                if (width != null)
                    width.Dispose();
                if (height != null)
                    height.Dispose();
            }
        }

        private static Mat ToBgr(Mat source)
        {
            if (source.Channels() == 3)
                return source.Clone();
            var result = new Mat();
            Cv2.CvtColor(source, result, ColorConversionCodes.GRAY2BGR);
            return result;
        }

        private static Mat ToGray(Mat source)
        {
            if (source.Channels() == 1)
                return source.Clone();
            var result = new Mat();
            Cv2.CvtColor(source, result, ColorConversionCodes.BGR2GRAY);
            return result;
        }

        private sealed class BoundedImage : IDisposable
        {
            public BoundedImage(Mat image, int originalWidth, int originalHeight)
            {
                Image = image;
                OriginalWidth = originalWidth;
                OriginalHeight = originalHeight;
            }
            public Mat Image { get; private set; }
            public int OriginalWidth { get; private set; }
            public int OriginalHeight { get; private set; }
            public void Dispose()
            {
                if (Image != null)
                {
                    Image.Dispose();
                    Image = null;
                }
            }
        }
    }
}
