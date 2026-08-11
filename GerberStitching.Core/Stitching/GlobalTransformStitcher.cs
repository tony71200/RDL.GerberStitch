using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Matching.Interop;
using GerberViewer.Stitching.Matching.OpenCv;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Imaging;
using OpenCvSharp;

namespace GerberViewer.Stitching.Stitching
{
    public enum StitchBlendMode 
    { 
        NoBlend, 
        WeightedAverage, 
        Feather 
    }
    public sealed class StitchFromGlobalTransformsOptions 
    { 
        public StitchingEngine StitchingEngine { get; set; } = StitchingEngine.OpenCv;
        public bool AllowUnsafeLegacyHalconProjectiveMosaic { get; set; }
        public StitchingExecutionReport ExecutionReport { get; set; }
        public int PreviewUpdateInterval { get; set; } = 4; 
        public double MaxPreviewMegapixels { get; set; } = 32; 
        public TiffMode TiffMode { get; set; } = TiffMode.BigTiff; 
        public StitchBlendMode BlendMode { get; set; } = StitchBlendMode.Feather; 
        public bool EnableBlending { get; set; } 
        public bool ForceGray8Output { get; set; } 
        public string OutputPath { get; set; } 
        public int BigTiffTileWidth { get; set; } = 512;
        public int BigTiffTileHeight { get; set; } = 512;
        public BlankFallbackOverlapPolicy BlankFallbackOverlapPolicy { get; set; } = BlankFallbackOverlapPolicy.PreserveExistingOverlap;
        public StitchBlendMode EffectiveBlendMode
        {
            get
            {
                var mode = EnableBlending && !ForceGray8Output ? BlendMode : StitchBlendMode.NoBlend;
                if (BlankFallbackOverlapPolicy == BlankFallbackOverlapPolicy.WeightedBlend && mode == StitchBlendMode.NoBlend && !ForceGray8Output) return StitchBlendMode.WeightedAverage;
                return mode;
            }
        }
    }
    public sealed class StitchPreview 
    { 
        public Bitmap Preview { get; set; } 
        public int PlacedCount { get; set; } 
        public int TotalCount { get; set; } 
    }

    public sealed class GlobalTransformStitcher
    {
        // ==================== STITCHING ====================
        // This class consumes completed alignment poses and composes the final TIFF.
        private static readonly OpenCvMatchInputAdapter OpenCvInput = new OpenCvMatchInputAdapter();
        private static readonly BitmapMatchImageAdapter BitmapInterop = new BitmapMatchImageAdapter();

        public string StitchFromGlobalTransforms(
            IList<CapturedImageInfo> images, 
            IList<TileWorkflowState> poses, 
            StitchFromGlobalTransformsOptions options, 
            IProgress<StitchPreview> preview, 
            CancellationToken cancellationToken)
        {
            if (images == null) 
                throw new ArgumentNullException("images");
            if (poses == null) 
                throw new ArgumentNullException("poses");
            options = options ?? new StitchFromGlobalTransformsOptions();
            options.ExecutionReport = new StitchingExecutionReport
            {
                ConfiguredEngine = options.StitchingEngine,
                EffectiveEngine = options.StitchingEngine,
                CoordinateContract = "CapturedImageLocalPixels -> ProcessedSampleGlobalPixels"
            };
            var output = NormalizeTiffPath(options.OutputPath);
            var creatingPath = ToCreatingPath(output);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = BuildItems(images, poses);
                if (items.Count == 0) 
                    throw new InvalidOperationException("No stitchable global transforms to stitch.");
                var bounds = CalculateBounds(items.Select(x => Tuple.Create(x.Image, x.Pose.GlobalPose)).ToList());
                var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                var bytesPerPixel = options.ForceGray8Output ? 1 : 3;
                var selection = SelectTiffOutput(options.TiffMode, width, height, bytesPerPixel);
                if (options.TiffMode == TiffMode.StandardTiff && 
                    selection.EstimatedBytes > 0xF0000000L) 
                    throw new InvalidOperationException("Standard TIFF selected for an output estimated beyond the standard TIFF limit.");
                if (selection.RequiresBigTiff) throw new NotSupportedException("BigTIFF output is required by size/configuration, but this writer does not claim BigTIFF support because it uses System.Drawing TIFF save.");
                EnsureDirectory(creatingPath);
                var effectiveEngine = options.StitchingEngine;
                // [Codex] [Change time: 2026-08-04] [Purpose: Stop silently ignoring the blending request on the HALCON mosaic path.]
                if (options.EnableBlending && (effectiveEngine == StitchingEngine.HalconProjectiveMosaic ||
                    effectiveEngine == StitchingEngine.HalconProjectiveMosaicRebased ||
                    effectiveEngine == StitchingEngine.HalconThenOpenCvFallback))
                {
                    const string blendingWarning = "EnableBlending was requested but HOperatorSet.GenProjectiveMosaic has no blending parameter; " +
                        "overlap uses hard overwrite. Select StitchingEngine.OpenCv for Feather or WeightedAverage.";
                    options.ExecutionReport.MatrixDiagnostics.Add(blendingWarning);
                }
                // Tony Assuming black spots are not considered.
                //var requiresBlankOverlapPolicy = items.Any(x => x.Pose.Source == PoseSource.BlankSampleExpectedPose) && options.BlankFallbackOverlapPolicy != BlankFallbackOverlapPolicy.Normal;
                //if (effectiveEngine == StitchingEngine.HalconProjectiveMosaic && requiresBlankOverlapPolicy)
                //    effectiveEngine = StitchingEngine.OpenCv;
                if (effectiveEngine == StitchingEngine.HalconProjectiveMosaic)
                {
                    LegacyHalconSafetyGate.ThrowIfBlocked(options);
                    options.ExecutionReport.CoordinateContract = "Legacy all-to-root HALCON output frame (unsafe opt-in; not authoritative)";
                    new HalconProjectiveMosaicEngine().StitchLegacy(ToHalconInputs(items), creatingPath, selection.RequiresBigTiff, cancellationToken);
                    ReopenAndValidate(creatingPath, width, height);
                }
                else if (effectiveEngine == StitchingEngine.HalconProjectiveMosaicRebased)
                {
                    try
                    {
                        new HalconProjectiveMosaicEngine().StitchRebased(ToHalconInputs(items), bounds, width, height,
                            creatingPath, selection.RequiresBigTiff, options.ExecutionReport, cancellationToken);
                        ReopenAndValidate(creatingPath, width, height);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception rebasedError)
                    {
                        options.ExecutionReport.EffectiveEngine = StitchingEngine.OpenCv;
                        options.ExecutionReport.FallbackReason = rebasedError.GetType().Name + ": " + rebasedError.Message;
                        options.ExecutionReport.CoordinateContract = "CapturedImageLocalPixels -> ProcessedSampleGlobalPixels";
                        CleanupCreating(creatingPath);
                        using (var canvas = StitchToMat(items, bounds, width, height, options, preview, cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            SaveStandardTiff(canvas, creatingPath, options.ExecutionReport);
                            ReopenAndValidate(creatingPath, width, height);
                        }
                    }
                }
                else if (effectiveEngine == StitchingEngine.HalconWarpThenTileOffsetExperimental)
                {
                    try
                    {
                        if (options.EffectiveBlendMode != StitchBlendMode.NoBlend)
                            throw new NotSupportedException("HalconWarpThenTileOffsetExperimental supports overwrite/NoBlend only; Feather and WeightedAverage are not silently downgraded.");
                        new HalconWarpThenTileOffsetEngine().Stitch(ToHalconInputs(items), bounds, width, height,
                            creatingPath, selection.RequiresBigTiff, options.BlankFallbackOverlapPolicy,
                            options.ExecutionReport, cancellationToken);
                        ReopenAndValidate(creatingPath, width, height);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception experimentalError)
                    {
                        options.ExecutionReport.EffectiveEngine = StitchingEngine.OpenCv;
                        options.ExecutionReport.FallbackReason = experimentalError.GetType().Name + ": " + experimentalError.Message;
                        options.ExecutionReport.CoordinateContract = "CapturedImageLocalPixels -> ProcessedSampleGlobalPixels";
                        CleanupCreating(creatingPath);
                        using (var canvas = StitchToMat(items, bounds, width, height, options, preview, cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            SaveStandardTiff(canvas, creatingPath, options.ExecutionReport);
                            ReopenAndValidate(creatingPath, width, height);
                        }
                    }
                }
                else if (effectiveEngine == StitchingEngine.HalconThenOpenCvFallback)
                {
                    try
                    {
                        LegacyHalconSafetyGate.ThrowIfBlocked(options);
                        options.ExecutionReport.CoordinateContract = "Legacy all-to-root HALCON output frame (unsafe opt-in; not authoritative)";
                        //if (requiresBlankOverlapPolicy) throw new InvalidOperationException("The OpenCV occupied-mask path is required for the configured blank-overlap policy.");
                        new HalconProjectiveMosaicEngine().StitchLegacy(ToHalconInputs(items), creatingPath, selection.RequiresBigTiff, cancellationToken);
                        ReopenAndValidate(creatingPath, width, height);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception legacyError)
                    {
                        options.ExecutionReport.EffectiveEngine = StitchingEngine.OpenCv;
                        options.ExecutionReport.FallbackReason = legacyError.GetType().Name + ": " + legacyError.Message;
                        options.ExecutionReport.CoordinateContract = "CapturedImageLocalPixels -> ProcessedSampleGlobalPixels";
                        CleanupCreating(creatingPath);
                        using (var canvas = StitchToMat(items, bounds, width, height, options, preview, cancellationToken))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            SaveStandardTiff(canvas, creatingPath, options.ExecutionReport);
                            ReopenAndValidate(creatingPath, width, height);
                        }
                    }
                }
                else
                {
                    options.ExecutionReport.EffectiveEngine = StitchingEngine.OpenCv;
                    using (var canvas = StitchToMat(items, bounds, width, height, options, preview, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        SaveStandardTiff(canvas, creatingPath, options.ExecutionReport);
                        ReopenAndValidate(creatingPath, width, height);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                // [Claude] [Change time: 2026-08-06] [Purpose: Treat the .creating-to-final publish step as part of Save Image from the user perspective.]
                var swPublish = System.Diagnostics.Stopwatch.StartNew();
                Publish(creatingPath, output);
                swPublish.Stop();
                if (options.ExecutionReport != null) options.ExecutionReport.SaveElapsedMilliseconds += swPublish.ElapsedMilliseconds;
                return output;
            }
            catch (OperationCanceledException)
            {
                CleanupCreating(creatingPath);
                throw;
            }
            catch
            {
                CleanupCreating(creatingPath);
                throw;
            }
            finally
            {
                // [Codex] [Change time: 2026-07-27] [Purpose: Cleanup must never replace a successful return or the original stitch exception.]
                TryCleanupCreating(creatingPath);
            }
        }


        private static IList<HalconMosaicInput> ToHalconInputs(IList<StitchItem> items)
        {
            return items.Select(x => new HalconMosaicInput
            {
                Image = x.Image,
                GlobalPose = x.Pose.GlobalPose,
                IsBlackPlaceholder = x.IsBlackPlaceholder
            }).ToList();
        }

        public static double[] ToHalconProjective(double[,] h)
        {
            return HalconProjectiveMosaicEngine.ToHalconRowColumn(h);
        }

        private Mat StitchToMat(IList<StitchItem> items, RectangleF bounds, int width, int height, StitchFromGlobalTransformsOptions options, IProgress<StitchPreview> preview, CancellationToken cancellationToken)
        {
            // Failed locations must remain unambiguously black, including overlap pixels.
            var blendMode = items.Any(x => x.IsBlackPlaceholder) ? StitchBlendMode.NoBlend : options.EffectiveBlendMode;
            var canvasType = options.ForceGray8Output ? MatType.CV_8UC1 : MatType.CV_8UC3;
            Mat canvas8 = blendMode == StitchBlendMode.NoBlend ? new Mat(height, width, canvasType, Scalar.All(0)) : null;
            Mat accum = blendMode == StitchBlendMode.NoBlend ? null : new Mat(height, width, MatType.CV_32FC3, Scalar.All(0));
            Mat weights = blendMode == StitchBlendMode.NoBlend ? null : new Mat(height, width, MatType.CV_32FC1, Scalar.All(0));
            Mat occupiedMask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
            try
            {
                for (int idx = 0; idx < items.Count; idx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var image = items[idx].IsBlackPlaceholder
                        ? CreateBlackTile(items[idx].Image, options.ForceGray8Output)
                        : LoadForStitch(items[idx].Image.FilePath, options.ForceGray8Output))
                    using (var mask = new Mat(image.Rows, image.Cols, MatType.CV_8UC1, Scalar.All(255)))
                    using (var warp = ToCanvasWarp(items[idx].Pose.GlobalPose, bounds))
                    using (var warped = new Mat())
                    using (var warpedMask = new Mat())
                    using (var effectiveMask = new Mat())
                    {
                        Cv2.WarpAffine(image, warped, warp, new OpenCvSharp.Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                        Cv2.WarpAffine(mask, warpedMask, warp, new OpenCvSharp.Size(width, height), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                        warpedMask.CopyTo(effectiveMask);
                        var preserve = items[idx].Pose.Source == PoseSource.BlankSampleExpectedPose && options.BlankFallbackOverlapPolicy == BlankFallbackOverlapPolicy.PreserveExistingOverlap;
                        if (preserve)
                        {
                            using (var unoccupied = new Mat()) { Cv2.BitwiseNot(occupiedMask, unoccupied); Cv2.BitwiseAnd(effectiveMask, unoccupied, effectiveMask); }
                        }
                        if (blendMode == StitchBlendMode.NoBlend) warped.CopyTo(canvas8, effectiveMask);
                        else AccumulateBlend(warped, effectiveMask, accum, weights, blendMode);
                        Cv2.BitwiseOr(occupiedMask, warpedMask, occupiedMask);
                    }
                    if (preview != null && options.PreviewUpdateInterval > 0 && ((idx + 1) % options.PreviewUpdateInterval == 0 || idx == items.Count - 1))
                    {
                        using (var current = blendMode == StitchBlendMode.NoBlend ? canvas8.Clone() : ResolveBlend(accum, weights))
                            preview.Report(new StitchPreview { Preview = MakePreview(current, options.MaxPreviewMegapixels), PlacedCount = idx + 1, TotalCount = items.Count });
                    }
                }
                if (blendMode == StitchBlendMode.NoBlend) { var result = canvas8; canvas8 = null; return result; }
                return ResolveBlend(accum, weights);
            }
            finally
            {
                if (canvas8 != null) canvas8.Dispose();
                if (accum != null) accum.Dispose();
                if (weights != null) weights.Dispose();
                if (occupiedMask != null) occupiedMask.Dispose();
            }
        }

        private static void AccumulateBlend(Mat warped, Mat warpedMask, Mat accum, Mat weights, StitchBlendMode blendMode)
        {
            using (var image32 = new Mat())
            using (var weight = BuildWeight(warpedMask, blendMode))
            using (var weight3 = To3Channels(weight))
            using (var weightedImage = new Mat())
            {
                warped.ConvertTo(image32, MatType.CV_32FC3);
                Cv2.Multiply(image32, weight3, weightedImage);
                Cv2.Add(accum, weightedImage, accum);
                Cv2.Add(weights, weight, weights);
            }
        }

        private static Mat BuildWeight(Mat mask, StitchBlendMode blendMode)
        {
            var weight = new Mat();
            if (blendMode == StitchBlendMode.Feather)
            {
                using (var binary = new Mat())
                {
                    Cv2.Threshold(mask, binary, 0, 255, ThresholdTypes.Binary);
                    Cv2.DistanceTransform(binary, weight, DistanceTypes.L2, DistanceTransformMasks.Mask3);
                    Cv2.Normalize(weight, weight, 0.0, 1.0, NormTypes.MinMax);
                    mask.ConvertTo(binary, MatType.CV_32FC1, 1.0 / 255.0);
                    Cv2.Multiply(weight, binary, weight);
                }
            }
            else mask.ConvertTo(weight, MatType.CV_32FC1, 1.0 / 255.0);
            return weight;
        }

        private static Mat To3Channels(Mat weight)
        {
            Mat[] channels = { weight, weight, weight };
            var result = new Mat();
            Cv2.Merge(channels, result);
            return result;
        }

        private static Mat ResolveBlend(Mat accum, Mat weights)
        {
            using (var safeWeights = new Mat())
            using (var weight3 = new Mat())
            using (var output32 = new Mat())
            {
                Cv2.Add(weights, Scalar.All(1e-6), safeWeights);
                Cv2.Merge(new[] { safeWeights, safeWeights, safeWeights }, weight3);
                Cv2.Divide(accum, weight3, output32);
                var output8 = new Mat();
                output32.ConvertTo(output8, MatType.CV_8UC3);
                return output8;
            }
        }

        private Mat LoadForStitch(string path, bool forceGray8)
        {
            return forceGray8 ? OpenCvInput.DecodeMono8Copy(path) : OpenCvInput.DecodeBgr8Copy(path);
        }

        private static Mat CreateBlackTile(CapturedImageInfo image, bool forceGray8)
        {
            var size = ResolveImageSize(image);
            return new Mat(size.Height, size.Width, forceGray8 ? MatType.CV_8UC1 : MatType.CV_8UC3, Scalar.All(0));
        }

        private static System.Drawing.Size ResolveImageSize(CapturedImageInfo image)
        {
            return image.Width > 0 && image.Height > 0
                ? new System.Drawing.Size(image.Width, image.Height)
                : ImageSize(image.FilePath);
        }

        private static Mat ToCanvasWarp(double[,] transform, RectangleF bounds)
        {
            // transform is captured/source -> global/output. WarpAffine is called without
            // WarpFlags.InverseMap, so OpenCV internally samples through the inverse; callers
            // must not invert this forward matrix. Canvas translation merely removes bounds origin.
            var warp = new Mat(2, 3, MatType.CV_64FC1);
            warp.Set<double>(0, 0, transform[0, 0]); warp.Set<double>(0, 1, transform[0, 1]); warp.Set<double>(0, 2, transform[0, 2] - bounds.Left);
            warp.Set<double>(1, 0, transform[1, 0]); warp.Set<double>(1, 1, transform[1, 1]); warp.Set<double>(1, 2, transform[1, 2] - bounds.Top);
            return warp;
        }

        private static IList<StitchItem> BuildItems(
            IList<CapturedImageInfo> images, 
            IList<TileWorkflowState> poses)
        {
            var duplicateImages = images.GroupBy(i => i.OrderIndex).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicateImages.Length != 0) throw new InvalidOperationException("Duplicate captured image OrderIndex: " + string.Join(",", duplicateImages) + ".");
            var duplicatePoses = poses.GroupBy(p => p.OrderIndex).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (duplicatePoses.Length != 0) throw new InvalidOperationException("Duplicate stitching pose OrderIndex: " + string.Join(",", duplicatePoses) + ".");
            var imageByOrder = images.ToDictionary(i => i.OrderIndex);
            var result = new List<StitchItem>();
            foreach (var pose in poses.Where(p => p.IsStitchable || (p.Source == PoseSource.Failed && p.HasValidPose))
                .OrderBy(p => p.Source == PoseSource.Failed ? 1 : 0).ThenBy(p => p.OrderIndex))
            {
                CapturedImageInfo image;
                if (imageByOrder.TryGetValue(pose.OrderIndex, out image)) result.Add(new StitchItem(image, pose));
            }
            return result;
        }

        public static RectangleF CalculateBounds(IList<Tuple<CapturedImageInfo, double[,]>> items)
        {
            if (items == null || items.Count == 0) throw new ArgumentException("At least one transformed image is required.", "items");
            double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
            foreach (var it in items)
            {
                var width = it.Item1.Width > 0 ? it.Item1.Width : ImageSize(it.Item1.FilePath).Width;
                var height = it.Item1.Height > 0 ? it.Item1.Height : ImageSize(it.Item1.FilePath).Height;
                foreach (var p in TransformCorners(it.Item2, width, height))
                {
                    left = Math.Min(left, p.X); top = Math.Min(top, p.Y); right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
                }
            }
            return RectangleF.FromLTRB((float)Math.Floor(left), (float)Math.Floor(top), (float)Math.Ceiling(right), (float)Math.Ceiling(bottom));
        }

        private static IEnumerable<PointF> TransformCorners(double[,] h, int width, int height)
        {
            var pts = new[] { new PointF(0, 0), new PointF(width, 0), new PointF(0, height), new PointF(width, height) };
            foreach (var p in pts) yield return new PointF((float)(h[0, 0] * p.X + h[0, 1] * p.Y + h[0, 2]), (float)(h[1, 0] * p.X + h[1, 1] * p.Y + h[1, 2]));
        }

        public static TiffOutputSelection SelectTiffOutput(TiffMode mode, int width, int height, int bytesPerPixel)
        {
            var bytes = EstimateByteCount(width, height, bytesPerPixel);
            if (mode == TiffMode.BigTiff) return new TiffOutputSelection(true, bytes, "Configured BigTIFF mode.");
            if (mode == TiffMode.StandardTiff) return new TiffOutputSelection(false, bytes, "Configured StandardTIFF mode.");
            return new TiffOutputSelection(bytes > 0xF0000000L || width > 65500 || height > 65500, bytes, "Auto selection from dimensions and byte count.");
        }

        public static bool SelectBigTiff(TiffMode mode, int width, int height, int bytesPerPixel) 
        { return SelectTiffOutput(mode, width, height, bytesPerPixel).RequiresBigTiff; 
        }
        public static long EstimateByteCount(int width, int height, int bytesPerPixel) 
        { 
            return (long)Math.Max(0, width) * Math.Max(0, height) * Math.Max(1, bytesPerPixel); 
        }
        public static string NormalizeTiffPath(string path) 
        { 
            if (string.IsNullOrWhiteSpace(path)) 
                path = Path.Combine(Environment.CurrentDirectory, "stitched.tiff"); 
            var ext = Path.GetExtension(path).ToLowerInvariant(); 
            if (ext != ".tif" && ext != ".tiff") 
                path = Path.ChangeExtension(path, ".tif"); 
            return path; 
        }

        // [Claude] [Change time: 2026-08-06] [Purpose: Measure TIFF writing separately as Save Image for the Execute Time tab.]
        private static void SaveStandardTiff(Mat image, string path, StitchingExecutionReport report)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!Cv2.ImWrite(path, image))
                throw new IOException("OpenCV failed to write stitched TIFF: " + path);
            sw.Stop();
            if (report != null) report.SaveElapsedMilliseconds += sw.ElapsedMilliseconds;
        }

        private static Bitmap MakePreview(Mat src, double maxPreviewMegapixels)
        {
            var scale = Math.Min(1.0, Math.Sqrt((maxPreviewMegapixels * 1000000.0) / Math.Max(1.0, src.Cols * src.Rows)));
            using (var previewMat = new Mat())
            {
                if (scale >= .999) src.CopyTo(previewMat);
                else Cv2.Resize(src, previewMat, new OpenCvSharp.Size(Math.Max(1, (int)(src.Cols * scale)), Math.Max(1, (int)(src.Rows * scale))), 0, 0, InterpolationFlags.Area);
                return BitmapInterop.ToBitmapCopy(previewMat);
            }
        }

        private static void ReopenAndValidate(string path, int width, int height)
        {
            HalconImageFileValidator.Validate(path);
        }

        private static void Publish(string creatingPath, string output)
        {
            EnsureDirectory(output);
            if (File.Exists(output))
                throw new IOException("Publish conflict: the stitched output already exists and will not be replaced: " + output);
            File.Move(creatingPath, output);
            ReopenAndValidate(output, 0, 0);
        }

        private static void CleanupCreating(string creatingPath) 
        { 
            if (!string.IsNullOrWhiteSpace(creatingPath) && File.Exists(creatingPath)) 
                File.Delete(creatingPath); 
        }

        private static void TryCleanupCreating(string creatingPath)
        {
            try
            {
                CleanupCreating(creatingPath);
                var directory = Path.GetDirectoryName(creatingPath);
                if (IsOwnedCreatingDirectory(directory) && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // Best-effort cleanup deliberately cannot hide the operation's original result.
            }
        }

        private static bool IsOwnedCreatingDirectory(string directory)
        {
            return !string.IsNullOrWhiteSpace(directory) && string.Equals(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), ".creating", StringComparison.OrdinalIgnoreCase);
        }
        private static void EnsureDirectory(string path) 
        { 
            var dir = Path.GetDirectoryName(path); 
            if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) 
                Directory.CreateDirectory(dir); }
        private static string ToCreatingPath(string output) 
        { 
            var dir = Path.GetDirectoryName(output); 
            var file = Path.GetFileName(output); 
            return Path.Combine(string.IsNullOrWhiteSpace(dir) ? Environment.CurrentDirectory : dir, ".creating", file); 
        }
        private static System.Drawing.Size ImageSize(string path) 
        { 
            using (var image = OpenCvInput.DecodeBgr8Copy(path))
                return new System.Drawing.Size(image.Cols, image.Rows);
        }

        private sealed class StitchItem
        {
            public StitchItem(CapturedImageInfo image, TileWorkflowState pose) 
            { 
                Image = image; 
                Pose = pose; 
            }
            public CapturedImageInfo Image { get; private set; }
            public TileWorkflowState Pose { get; private set; }
            public bool IsBlackPlaceholder { get { return Pose.Source == PoseSource.Failed; } }
        }
    }

    public sealed class TiffOutputSelection
    {
        public TiffOutputSelection(bool requiresBigTiff, long estimatedBytes, string reason) { RequiresBigTiff = requiresBigTiff; EstimatedBytes = estimatedBytes; Reason = reason; }
        public bool RequiresBigTiff { get; private set; }
        public long EstimatedBytes { get; private set; }
        public string Reason { get; private set; }
    }

    // [Codex] [Change time: 2026-08-01] [Purpose: Prevent the legacy all-to-root HALCON implementation from publishing an unverified coordinate frame.]
    public static class LegacyHalconSafetyGate
    {
        public const string BlockReason = "Legacy HALCON ProjectiveMosaic is blocked because its all-to-root output coordinate contract is not verified. Enable the unsafe opt-in only for controlled comparison runs.";

        public static void ThrowIfBlocked(StitchFromGlobalTransformsOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (!options.AllowUnsafeLegacyHalconProjectiveMosaic)
                throw new InvalidOperationException(BlockReason);
        }
    }
}
