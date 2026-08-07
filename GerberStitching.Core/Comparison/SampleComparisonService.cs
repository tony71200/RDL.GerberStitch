using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Matching.OpenCv;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Comparison
{
    /// <summary>
    /// Important code for get the sample gerber
    /// </summary>
    public sealed class SampleComparisonService
    {
        private const string CoordinateSpace = "ProcessedSampleGlobalPixels";
        private static readonly OpenCvMatchInputAdapter OpenCvInput = new OpenCvMatchInputAdapter();
        /// <summary>
        /// After stitching, compare image.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        public SampleComparisonResult Generate(SampleComparisonRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (request.Manifest == null) throw new ArgumentNullException("Manifest");
            if (string.IsNullOrWhiteSpace(request.StitchedImagePath)) throw new ArgumentException("StitchedImagePath is required.", "request");
            if (string.IsNullOrWhiteSpace(request.OutputDirectory)) throw new ArgumentException("OutputDirectory is required.", "request");
            cancellationToken.ThrowIfCancellationRequested();

            var result = new SampleComparisonResult();
            Directory.CreateDirectory(request.OutputDirectory);
            var mapping = ResolveMapping(request.Manifest, result.Warnings);
            result.IsAuthoritative = mapping.IsAuthoritative;
            if (!mapping.IsAuthoritative && !request.AllowNonAuthoritativeVisualPreview)
            {
                result.Warnings.Add("Authoritative comparison blocked: " + mapping.Reason + " Non-authoritative visual preview is disabled.");
                WriteMetadata(result, request, mapping, null);
                return result;
            }
            if (!mapping.IsAuthoritative) result.Warnings.Add("Authoritative comparison blocked: " + mapping.Reason + " Generated products are non-authoritative visual previews only.");

            try
            {
                using (var sample = LoadSampleInProcessedSpace(request.Manifest, mapping))
                using (var stitched = LoadColor(request.StitchedImagePath))
                using (var sampleForComparison = PrepareComparable(sample, stitched, mapping, result.Warnings))
                using (var stitchedForComparison = PrepareComparable(stitched, sampleForComparison, mapping, result.Warnings))
                using (var samplePreview = MakePreview(sampleForComparison, request.MaxPreviewMegapixels))
                using (var stitchedPreview = MakePreview(stitchedForComparison, request.MaxPreviewMegapixels))
                using (var overlay = AlphaOverlay(samplePreview, stitchedPreview, request.Alpha))
                using (var difference = AbsoluteDifference(samplePreview, stitchedPreview))
                using (var edge = EdgeOverlay(samplePreview, stitchedPreview))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.SamplePreviewPath = Path.Combine(request.OutputDirectory, "sample_reference_preview.png");
                    result.StitchedPreviewPath = Path.Combine(request.OutputDirectory, "stitched_preview.png");
                    result.AlphaOverlayPath = Path.Combine(request.OutputDirectory, "overlay_comparison.png");
                    result.AbsoluteDifferencePath = Path.Combine(request.OutputDirectory, "difference_comparison.png");
                    result.EdgeOverlayPath = Path.Combine(request.OutputDirectory, "edge_comparison.png");
                    SavePng(samplePreview, result.SamplePreviewPath);
                    SavePng(stitchedPreview, result.StitchedPreviewPath);
                    SavePng(overlay, result.AlphaOverlayPath);
                    SavePng(difference, result.AbsoluteDifferencePath);
                    SavePng(edge, result.EdgeOverlayPath);
                    result.Metrics = ComputeMetrics(sampleForComparison, stitchedForComparison);
                    result.ProductsGenerated = true;
                    WriteMetadata(result, request, mapping, result.Metrics);
                    return result;
                }
            }
            catch (OpenCVException ex)
            {
                return GenerateHalconPreviewOnly(request, mapping, result, "OpenCV comparison skipped for oversized image: " + ex.Message, cancellationToken);
            }
            catch (ArgumentException ex)
            {
                return GenerateHalconPreviewOnly(request, mapping, result, "Bitmap/OpenCV comparison skipped for oversized image: " + ex.Message, cancellationToken);
            }
        }

        private static ComparisonMapping ResolveMapping(SampleManifest manifest, List<string> warnings)
        {
            if (!string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath))
                return new ComparisonMapping(true, manifest.ProcessedSamplePath, null, "ProcessedSamplePath supplied by manifest.");
            if (manifest.SourceToProcessedTransform != null)
            {
                var matrix = MatrixFromJagged(manifest.SourceToProcessedTransform);
                if (IsFiniteAffine(matrix)) return new ComparisonMapping(true, manifest.SourceRasterPath, matrix, "SourceToProcessedTransform supplied by manifest.");
                warnings.Add("SourceToProcessedTransform is present but invalid/non-finite.");
            }
            if (manifest.ManifestVersion <= 1 && manifest.SourceWidth == manifest.ProcessedWidth && manifest.SourceHeight == manifest.ProcessedHeight)
                return new ComparisonMapping(true, manifest.SourceRasterPath, IdentityAffine(), "Manifest v1 source dimensions equal processed dimensions; identity mapping is authoritative.");
            return new ComparisonMapping(false, FirstAvailablePath(manifest), null, "manifest lacks ProcessedSamplePath or a valid SourceToProcessedTransform for ProcessedSampleGlobalPixels.");
        }

        private static string FirstAvailablePath(SampleManifest manifest)
        {
            if (!string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath)) return manifest.ProcessedSamplePath;
            return manifest.SourceRasterPath;
        }

        private static Mat LoadSampleInProcessedSpace(SampleManifest manifest, ComparisonMapping mapping)
        {
            using (var source = LoadColor(mapping.SamplePath))
            {
                if (!mapping.IsAuthoritative || !string.IsNullOrWhiteSpace(manifest.ProcessedSamplePath)) return source.Clone();
                if (mapping.SourceToProcessedTransform == null) return source.Clone();
                using (var warp = ToWarp(mapping.SourceToProcessedTransform))
                {
                    var result = new Mat();
                    Cv2.WarpAffine(source, result, warp, new Size(manifest.ProcessedWidth, manifest.ProcessedHeight), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                    return result;
                }
            }
        }

        private static Mat PrepareComparable(Mat image, Mat other, ComparisonMapping mapping, List<string> warnings)
        {
            if (image.Rows == other.Rows && image.Cols == other.Cols) return image.Clone();
            if (mapping.IsAuthoritative) throw new InvalidOperationException("Authoritative comparison requires sample and stitched images to share ProcessedSampleGlobalPixels dimensions.");
            warnings.Add("Non-authoritative preview resized an image for visual comparison only.");
            var resized = new Mat();
            Cv2.Resize(image, resized, new Size(other.Cols, other.Rows), 0, 0, InterpolationFlags.Area);
            return resized;
        }

        private static ComparisonMetrics ComputeMetrics(Mat sample, Mat stitched)
        {
            using (var sampleGray = ToGray(sample))
            using (var stitchedGray = ToGray(stitched))
            using (var sampleMask = Mask(sampleGray))
            using (var stitchedMask = Mask(stitchedGray))
            using (var overlap = new Mat())
            using (var union = new Mat())
            using (var sampleBinary = Binary(sampleGray))
            using (var stitchedBinary = Binary(stitchedGray))
            using (var binaryIntersect = new Mat())
            using (var binaryUnion = new Mat())
            using (var sampleEdges = Edges(sampleGray))
            using (var stitchedEdges = Edges(stitchedGray))
            using (var edgeIntersect = new Mat())
            using (var edgeUnion = new Mat())
            using (var sampleEdgeSupport = DilateEdges(sampleEdges))
            using (var stitchedEdgeSupport = DilateEdges(stitchedEdges))
            using (var matchedRealityEdges = new Mat())
            using (var matchedSampleEdges = new Mat())
            using (var absDiff = new Mat())
            {
                Cv2.BitwiseAnd(sampleMask, stitchedMask, overlap);
                Cv2.BitwiseOr(sampleMask, stitchedMask, union);
                Cv2.BitwiseAnd(sampleBinary, stitchedBinary, binaryIntersect);
                Cv2.BitwiseOr(sampleBinary, stitchedBinary, binaryUnion);
                Cv2.BitwiseAnd(sampleEdges, stitchedEdges, edgeIntersect);
                Cv2.BitwiseOr(sampleEdges, stitchedEdges, edgeUnion);
                Cv2.BitwiseAnd(stitchedEdges, sampleEdgeSupport, matchedRealityEdges);
                Cv2.BitwiseAnd(sampleEdges, stitchedEdgeSupport, matchedSampleEdges);
                Cv2.Absdiff(sampleGray, stitchedGray, absDiff);
                var edgePrecision = Ratio(Cv2.CountNonZero(matchedRealityEdges), Cv2.CountNonZero(stitchedEdges));
                var edgeRecall = Ratio(Cv2.CountNonZero(matchedSampleEdges), Cv2.CountNonZero(sampleEdges));
                var f1 = edgePrecision + edgeRecall <= 0 ? 0 : 2.0 * edgePrecision * edgeRecall / (edgePrecision + edgeRecall);
                var distance = DistanceStats(sampleEdges, stitchedEdges);
                var diff = AbsoluteDifferenceStats(absDiff, overlap);
                return new ComparisonMetrics
                {
                    ValidOverlapRatio = Ratio(Cv2.CountNonZero(overlap), Cv2.CountNonZero(union)),
                    NormalizedCrossCorrelation = NormalizedCrossCorrelation(sampleGray, stitchedGray, overlap),
                    BinaryMaskIoU = Ratio(Cv2.CountNonZero(binaryIntersect), Cv2.CountNonZero(binaryUnion)),
                    EdgeOverlap = Ratio(Cv2.CountNonZero(edgeIntersect), Cv2.CountNonZero(edgeUnion)),
                    DistanceTransformError = distance.Mean,
                    EdgePrecision = edgePrecision,
                    EdgeRecall = edgeRecall,
                    EdgeF1Score = f1,
                    MeanEdgeDistancePixels = distance.Mean,
                    P95EdgeDistancePixels = distance.P95,
                    AbsoluteDifferenceMean = diff.Mean,
                    AbsoluteDifferenceP95 = diff.P95,
                    AbsoluteDifferenceMax = diff.Max
                };
            }
        }

        private static double NormalizedCrossCorrelation(Mat a, Mat b, Mat mask)
        {
            if (Cv2.CountNonZero(mask) == 0) return 0;
            Scalar meanA, stdA, meanB, stdB;
            Cv2.MeanStdDev(a, out meanA, out stdA, mask);
            Cv2.MeanStdDev(b, out meanB, out stdB, mask);
            if (stdA.Val0 < 1e-6 || stdB.Val0 < 1e-6) return 0;
            using (var af = new Mat())
            using (var bf = new Mat())
            using (var da = new Mat())
            using (var db = new Mat())
            using (var prod = new Mat())
            using (var maskFloat = MaskToFloat(mask))
            using (var maskedProd = new Mat())
            {
                a.ConvertTo(af, MatType.CV_32FC1);
                b.ConvertTo(bf, MatType.CV_32FC1);
                Cv2.Subtract(af, Scalar.All(meanA.Val0), da);
                Cv2.Subtract(bf, Scalar.All(meanB.Val0), db);
                Cv2.Multiply(da, db, prod);
                Cv2.Multiply(prod, maskFloat, maskedProd);
                return Cv2.Sum(maskedProd).Val0 / (Cv2.CountNonZero(mask) * stdA.Val0 * stdB.Val0);
            }
        }

        private static Mat MaskToFloat(Mat mask)
        {
            var f = new Mat();
            mask.ConvertTo(f, MatType.CV_32FC1, 1.0 / 255.0);
            return f;
        }

        private static MetricDistribution DistanceStats(Mat referenceEdges, Mat stitchedEdges)
        {
            using (var inverted = new Mat())
            using (var distance = new Mat())
            {
                Cv2.BitwiseNot(referenceEdges, inverted);
                Cv2.DistanceTransform(inverted, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                var count = Cv2.CountNonZero(stitchedEdges);
                if (count == 0) return new MetricDistribution();
                using (var maskFloat = MaskToFloat(stitchedEdges))
                using (var maskedDistance = new Mat())
                {
                    Cv2.Multiply(distance, maskFloat, maskedDistance);
                    return new MetricDistribution { Mean = Cv2.Sum(maskedDistance).Val0 / count, P95 = MaskedPercentile(distance, stitchedEdges, 0.95), Max = MaskedMax(distance, stitchedEdges) };
                }
            }
        }

        private static MetricDistribution AbsoluteDifferenceStats(Mat absDiff, Mat overlapMask)
        {
            var count = Cv2.CountNonZero(overlapMask);
            if (count == 0) return new MetricDistribution();
            return new MetricDistribution { Mean = Cv2.Mean(absDiff, overlapMask).Val0, P95 = MaskedPercentile(absDiff, overlapMask, 0.95), Max = MaskedMax(absDiff, overlapMask) };
        }

        private static Mat DilateEdges(Mat edges)
        {
            var dilated = new Mat();
            using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5))) Cv2.Dilate(edges, dilated, kernel);
            return dilated;
        }

        private static double MaskedMax(Mat values, Mat mask)
        {
            double min, max;
            OpenCvSharp.Point minLoc, maxLoc;
            Cv2.MinMaxLoc(values, out min, out max, out minLoc, out maxLoc, mask);
            return max;
        }

        private static double MaskedPercentile(Mat values, Mat mask, double percentile)
        {
            var count = Cv2.CountNonZero(mask);
            if (count == 0) return 0;
            var max = MaskedMax(values, mask);
            var low = 0.0;
            var high = Math.Max(1.0, max);
            for (int i = 0; i < 16; i++)
            {
                var mid = (low + high) / 2.0;
                using (var threshold = new Mat())
                using (var above = new Mat())
                {
                    Cv2.Threshold(values, threshold, mid, 255, ThresholdTypes.Binary);
                    threshold.ConvertTo(threshold, MatType.CV_8UC1);
                    Cv2.BitwiseAnd(threshold, mask, above);
                    var leCount = count - Cv2.CountNonZero(above);
                    if (leCount >= count * percentile) high = mid; else low = mid;
                }
            }
            return high;
        }

        private static Mat ToGray(Mat image)
        {
            var gray = new Mat();
            if (image.Channels() == 1) image.CopyTo(gray);
            else Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
            return gray;
        }

        private static Mat Mask(Mat gray)
        {
            var mask = new Mat();
            Cv2.Threshold(gray, mask, 0, 255, ThresholdTypes.Binary);
            return mask;
        }

        private static Mat Binary(Mat gray)
        {
            var binary = new Mat();
            Cv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            return binary;
        }

        private static Mat Edges(Mat gray)
        {
            var edges = new Mat();
            Cv2.Canny(gray, edges, 50, 150);
            return edges;
        }

        private static Mat AlphaOverlay(Mat sample, Mat stitched, double alpha)
        {
            var result = new Mat();
            var a = Math.Max(0.0, Math.Min(1.0, alpha));
            Cv2.AddWeighted(sample, a, stitched, 1.0 - a, 0.0, result);
            return result;
        }

        private static Mat AbsoluteDifference(Mat sample, Mat stitched)
        {
            var result = new Mat();
            Cv2.Absdiff(sample, stitched, result);
            return result;
        }

        private static Mat EdgeOverlay(Mat sample, Mat stitched)
        {
            using (var sampleGray = ToGray(sample))
            using (var stitchedGray = ToGray(stitched))
            using (var sampleEdges = Edges(sampleGray))
            using (var stitchedEdges = Edges(stitchedGray))
            {
                var result = new Mat(sample.Rows, sample.Cols, MatType.CV_8UC3, Scalar.All(0));
                result.SetTo(new Scalar(0, 255, 0), sampleEdges);
                result.SetTo(new Scalar(0, 0, 255), stitchedEdges);
                return result;
            }
        }

        private static Mat MakePreview(Mat source, double maxPreviewMegapixels)
        {
            var maxPixels = Math.Max(1.0, maxPreviewMegapixels) * 1000000.0;
            var scale = Math.Min(1.0, Math.Sqrt(maxPixels / Math.Max(1.0, source.Rows * source.Cols)));
            var result = new Mat();
            if (scale >= 0.999) source.CopyTo(result);
            else Cv2.Resize(source, result, new Size(Math.Max(1, (int)(source.Cols * scale)), Math.Max(1, (int)(source.Rows * scale))), 0, 0, InterpolationFlags.Area);
            return result;
        }

        private static void SavePng(Mat image, string path)
        {
            if (!Cv2.ImWrite(path, image)) throw new IOException("Failed to write comparison product: " + path);
        }

        private static SampleComparisonResult GenerateHalconPreviewOnly(
            SampleComparisonRequest request, 
            ComparisonMapping mapping, 
            SampleComparisonResult result, 
            string reason, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.IsAuthoritative = false;
            result.ProductsGenerated = true;
            result.Warnings.Add(reason);
            result.Warnings.Add("Generated HALCON preview-only images because the source is too large for OpenCV full-image comparison.");
            result.SamplePreviewPath = Path.Combine(request.OutputDirectory, "sample_reference_preview.png");
            result.StitchedPreviewPath = Path.Combine(request.OutputDirectory, "stitched_preview.png");
            WriteHalconPreview(mapping.SamplePath, result.SamplePreviewPath, request.MaxPreviewMegapixels, cancellationToken);
            WriteHalconPreview(request.StitchedImagePath, result.StitchedPreviewPath, request.MaxPreviewMegapixels, cancellationToken);
            WriteMetadata(result, request, mapping, result.Metrics);
            return result;
        }

        private static void WriteHalconPreview(
            string sourcePath, 
            string outputPath, 
            double maxPreviewMegapixels, 
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) throw new FileNotFoundException("HALCON preview image not found.", sourcePath);
            HObject image = null;
            HObject preview = null;
            HTuple width = null;
            HTuple height = null;
            try
            {
                HOperatorSet.ReadImage(out image, sourcePath);
                cancellationToken.ThrowIfCancellationRequested();
                HOperatorSet.GetImageSize(image, out width, out height);
                var w = width.D;
                var h = height.D;
                var maxPixels = Math.Max(1.0, maxPreviewMegapixels) * 1000000.0;
                var scale = Math.Min(1.0, Math.Sqrt(maxPixels / Math.Max(1.0, w * h)));
                var previewWidth = Math.Max(1, (int)Math.Round(w * scale));
                var previewHeight = Math.Max(1, (int)Math.Round(h * scale));
                if (scale >= 0.999) HOperatorSet.CopyObj(image, out preview, 1, -1);
                else HOperatorSet.ZoomImageSize(image, out preview, previewWidth, previewHeight, "constant");
                HOperatorSet.WriteImage(preview, "png", 0, outputPath);
            }
            finally
            {
                if (image != null) image.Dispose();
                if (preview != null) preview.Dispose();
                if (width != null) width.Dispose();
                if (height != null) height.Dispose();
            }
        }

        private static Mat LoadColor(string path)
        {
            return OpenCvInput.DecodeBgr8Copy(path);
        }

        private static Mat ToWarp(double[,] h)
        {
            var warp = new Mat(2, 3, MatType.CV_64FC1);
            warp.Set<double>(0, 0, h[0, 0]); warp.Set<double>(0, 1, h[0, 1]); warp.Set<double>(0, 2, h[0, 2]);
            warp.Set<double>(1, 0, h[1, 0]); warp.Set<double>(1, 1, h[1, 1]); warp.Set<double>(1, 2, h[1, 2]);
            return warp;
        }

        private static double[,] MatrixFromJagged(double[][] source)
        {
            if (source == null || source.Length != 3 || source[0] == null || source[1] == null || source[2] == null || source[0].Length != 3 || source[1].Length != 3 || source[2].Length != 3) return null;
            return new[,] { { source[0][0], source[0][1], source[0][2] }, { source[1][0], source[1][1], source[1][2] }, { source[2][0], source[2][1], source[2][2] } };
        }

        private static double[,] IdentityAffine() { return new[,] { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } }; }
        private static bool IsFiniteAffine(double[,] h)
        {
            if (h == null || h.GetLength(0) != 3 || h.GetLength(1) != 3) return false;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) if (double.IsNaN(h[r, c]) || double.IsInfinity(h[r, c])) return false;
            return Math.Abs(h[2, 0]) < 1e-12 && Math.Abs(h[2, 1]) < 1e-12 && Math.Abs(h[2, 2] - 1.0) < 1e-12;
        }

        private static double Ratio(double numerator, double denominator) 
        { 
            return denominator <= 0 ? 0 : numerator / denominator; 
        }

        private static void WriteMetadata(SampleComparisonResult result, SampleComparisonRequest request, ComparisonMapping mapping, ComparisonMetrics metrics)
        {
            result.MetadataPath = Path.Combine(request.OutputDirectory, "comparison_metadata.json");
            var sb = new StringBuilder();
            sb.AppendLine("{");
            AppendJson(sb, "coordinateSpace", CoordinateSpace, true);
            AppendJson(sb, "requestedMode", request.Mode.ToString(), true);
            AppendJson(sb, "isAuthoritative", result.IsAuthoritative ? "true" : "false", true, true);
            AppendJson(sb, "mappingReason", mapping.Reason, true);
            AppendJson(sb, "productsGenerated", result.ProductsGenerated ? "true" : "false", true, true);
            sb.AppendLine("  \"warnings\": [");
            for (int i = 0; i < result.Warnings.Count; i++) sb.Append("    \"").Append(Escape(result.Warnings[i])).Append(i + 1 == result.Warnings.Count ? "\"\n" : "\",\n");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"metrics\": {");
            var m = metrics ?? new ComparisonMetrics();
            AppendJson(sb, "validOverlapRatio", m.ValidOverlapRatio, true);
            AppendJson(sb, "normalizedCrossCorrelation", m.NormalizedCrossCorrelation, true);
            AppendJson(sb, "binaryMaskIoU", m.BinaryMaskIoU, true);
            AppendJson(sb, "edgeOverlap", m.EdgeOverlap, true);
            AppendJson(sb, "distanceTransformError", m.DistanceTransformError, true);
            AppendJson(sb, "edgePrecision", m.EdgePrecision, true);
            AppendJson(sb, "edgeRecall", m.EdgeRecall, true);
            AppendJson(sb, "edgeF1Score", m.EdgeF1Score, true);
            AppendJson(sb, "meanEdgeDistancePixels", m.MeanEdgeDistancePixels, true);
            AppendJson(sb, "p95EdgeDistancePixels", m.P95EdgeDistancePixels, true);
            AppendJson(sb, "absoluteDifferenceMean", m.AbsoluteDifferenceMean, true);
            AppendJson(sb, "absoluteDifferenceP95", m.AbsoluteDifferenceP95, true);
            AppendJson(sb, "absoluteDifferenceMax", m.AbsoluteDifferenceMax, false);
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(result.MetadataPath, sb.ToString());
        }

        private static void AppendJson(StringBuilder sb, string name, string value, bool comma, bool raw = false)
        {
            sb.Append("  \"").Append(name).Append("\": ");
            if (raw) sb.Append(value); else sb.Append("\"").Append(Escape(value)).Append("\"");
            sb.AppendLine(comma ? "," : string.Empty);
        }

        private static void AppendJson(StringBuilder sb, string name, double value, bool comma)
        {
            sb.Append("    \"").Append(name).Append("\": ").Append(value.ToString("R", CultureInfo.InvariantCulture)).AppendLine(comma ? "," : string.Empty);
        }

        private static string Escape(string value) { return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\""); }

        private sealed class MetricDistribution
        {
            public double Mean { get; set; }
            public double P95 { get; set; }
            public double Max { get; set; }
        }

        private sealed class ComparisonMapping
        {
            public ComparisonMapping(bool isAuthoritative, string samplePath, double[,] sourceToProcessedTransform, string reason)
            {
                IsAuthoritative = isAuthoritative;
                SamplePath = samplePath;
                SourceToProcessedTransform = sourceToProcessedTransform;
                Reason = reason;
            }
            public bool IsAuthoritative { get; private set; }
            public string SamplePath { get; private set; }
            public double[,] SourceToProcessedTransform { get; private set; }
            public string Reason { get; private set; }
        }
    }
}
