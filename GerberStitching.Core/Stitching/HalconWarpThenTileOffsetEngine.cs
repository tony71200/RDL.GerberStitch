using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Models;
using HalconDotNet;

namespace GerberViewer.Stitching.Stitching
{
    public sealed class HalconTileOffsetPatch
    {
        public int OrderIndex { get; set; }
        public int OffsetRow { get; set; }
        public int OffsetColumn { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double[,] LocalPatchTransform { get; set; }
    }

    // [Tony] [Change time: 2026-08-01] [Purpose: Bake full canonical poses into bounded HALCON patches before
    // experimental tile_images_offset composition.]
    public sealed class HalconWarpThenTileOffsetEngine
    {
        private const long MaximumPatchPixels = 512L * 1024L * 1024L;

        public void Stitch(IList<HalconMosaicInput> inputs, RectangleF canonicalBounds, int width, int height,
                           string path, bool bigTiff, BlankFallbackOverlapPolicy blankPolicy,
                           StitchingExecutionReport report, CancellationToken cancellationToken)
        {
            if (inputs == null || inputs.Count == 0)
                throw new ArgumentException("At least one tile-offset input is required.", "inputs");
            if (report == null)
                throw new ArgumentNullException("report");
            if (blankPolicy == BlankFallbackOverlapPolicy.WeightedBlend)
                throw new NotSupportedException(
                    "Experimental tile_images_offset does not support WeightedBlend blank overlap policy.");
            var timer = Stopwatch.StartNew();
            var ordered = (blankPolicy == BlankFallbackOverlapPolicy.PreserveExistingOverlap
                               ? inputs.OrderByDescending(x => x.IsBlackPlaceholder).ThenBy(x => x.Image.OrderIndex)
                               : inputs.OrderBy(x => x.Image.OrderIndex))
                              .ToList();
            var patches = ordered.Select(x => CalculatePatch(x, canonicalBounds, width, height)).ToList();
            var totalPixels = patches.Sum(x => (long)x.Width * x.Height);
            if (totalPixels > MaximumPatchPixels)
                throw new InvalidOperationException("Experimental bounded-patch memory guard exceeded: " + totalPixels +
                                                    " pixels.");
            report.CoordinateContract =
                "CapturedImageLocalPixels -> StitchedCanvasPixels (bounded local warp, integer patch origin)";
            report.OverlapPolicy =
                blankPolicy == BlankFallbackOverlapPolicy.PreserveExistingOverlap
                    ? "Overwrite; black placeholders first, then normal tiles by OrderIndex; valid warped domains only"
                    : "Overwrite; deterministic OrderIndex within placeholder group; valid warped domains only";
            foreach (var input in ordered)
                report.DrawOrder.Add(input.Image.OrderIndex);
            report.EstimatedPeakPatchMegapixels = totalPixels / 1000000d;

            HObject patchObjects = null;
            HObject tiled = null;
            HTuple rows = null;
            HTuple columns = null;
            HTuple row1 = null;
            HTuple column1 = null;
            HTuple row2 = null;
            HTuple column2 = null;
            HOperatorSet.GenEmptyObj(out patchObjects);
            try
            {
                rows = new HTuple();
                columns = new HTuple();
                row1 = new HTuple();
                column1 = new HTuple();
                row2 = new HTuple();
                column2 = new HTuple();
                for (var i = 0; i < ordered.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HObject source = null;
                    HObject mask = null;
                    HObject whiteMask = null;
                    HObject warped = null;
                    HObject warpedMask = null;
                    HObject validRegion = null;
                    HObject reduced = null;
                    HObject concatenated = null;
                    try
                    {
                        if (ordered[i].IsBlackPlaceholder)
                            HOperatorSet.GenImageConst(out source, new HTuple("byte"), ordered[i].Image.Width,
                                                       ordered[i].Image.Height);
                        else
                            HOperatorSet.ReadImage(out source, ordered[i].Image.FilePath);
                        HOperatorSet.GenImageConst(out mask, new HTuple("byte"), ordered[i].Image.Width,
                                                   ordered[i].Image.Height);
                        HOperatorSet.ScaleImage(mask, out whiteMask, new HTuple(0), new HTuple(255));
                        HOperatorSet.ProjectiveTransImageSize(
                            source, out warped,
                            new HTuple(HalconProjectiveMosaicEngine.ToHalconRowColumn(patches[i].LocalPatchTransform)),
                            new HTuple("bilinear"), patches[i].Width, patches[i].Height, new HTuple("true"));
                        HOperatorSet.ProjectiveTransImageSize(
                            whiteMask, out warpedMask,
                            new HTuple(HalconProjectiveMosaicEngine.ToHalconRowColumn(patches[i].LocalPatchTransform)),
                            new HTuple("nearest_neighbor"), patches[i].Width, patches[i].Height, new HTuple("true"));
                        HOperatorSet.Threshold(warpedMask, out validRegion, 1, 255);
                        HOperatorSet.ReduceDomain(warped, validRegion, out reduced);
                        HOperatorSet.ConcatObj(patchObjects, reduced, out concatenated);
                        patchObjects.Dispose();
                        patchObjects = concatenated;
                        concatenated = null;
                        Append(ref rows, patches[i].OffsetRow);
                        Append(ref columns, patches[i].OffsetColumn);
                        Append(ref row1, 0);
                        Append(ref column1, 0);
                        Append(ref row2, patches[i].Height - 1);
                        Append(ref column2, patches[i].Width - 1);
                    }
                    finally
                    {
                        Dispose(concatenated);
                        Dispose(reduced);
                        Dispose(validRegion);
                        Dispose(warpedMask);
                        Dispose(warped);
                        Dispose(whiteMask);
                        Dispose(mask);
                        Dispose(source);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                HOperatorSet.TileImagesOffset(patchObjects, out tiled, rows, columns, row1, column1, row2, column2,
                                              width, height);
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                // Measure image-writing time separately for the Execute Time tab.
                var swSave = Stopwatch.StartNew();
                HOperatorSet.WriteImage(tiled, new HTuple(bigTiff ? "bigtiff none" : "tiff none"), 0, path);
                swSave.Stop();
                report.SaveElapsedMilliseconds += swSave.ElapsedMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                timer.Stop();
                report.ElapsedMilliseconds = timer.ElapsedMilliseconds;
                Dispose(tiled);
                Dispose(patchObjects);
                Dispose(rows);
                Dispose(columns);
                Dispose(row1);
                Dispose(column1);
                Dispose(row2);
                Dispose(column2);
            }
        }

        public static HalconTileOffsetPatch CalculatePatch(HalconMosaicInput input, RectangleF canonicalBounds,
                                                           int canvasWidth, int canvasHeight)
        {
            if (input == null || input.Image == null || input.GlobalPose == null)
                throw new ArgumentException("Input, image, and pose are required.", "input");
            var desired = Multiply(Translation(-canonicalBounds.Left, -canonicalBounds.Top), input.GlobalPose);
            var corners =
                new[] { Point(desired, 0, 0), Point(desired, input.Image.Width, 0),
                        Point(desired, 0, input.Image.Height), Point(desired, input.Image.Width, input.Image.Height) };
            var left = Math.Max(0, (int)Math.Floor(corners.Min(x => x.Item1)));
            var top = Math.Max(0, (int)Math.Floor(corners.Min(x => x.Item2)));
            var right = Math.Min(canvasWidth, (int)Math.Ceiling(corners.Max(x => x.Item1)));
            var bottom = Math.Min(canvasHeight, (int)Math.Ceiling(corners.Max(x => x.Item2)));
            if (right <= left || bottom <= top)
                throw new InvalidOperationException("Tile has no bounded canonical canvas domain: OrderIndex " +
                                                    input.Image.OrderIndex + ".");
            return new HalconTileOffsetPatch { OrderIndex = input.Image.OrderIndex,
                                               OffsetRow = top,
                                               OffsetColumn = left,
                                               Width = right - left,
                                               Height = bottom - top,
                                               LocalPatchTransform = Multiply(Translation(-left, -top), desired) };
        }

        private static Tuple<double, double> Point(double[,] h, double x, double y)
        {
            return Tuple.Create(h[0, 0] * x + h[0, 1] * y + h[0, 2], h[1, 0] * x + h[1, 1] * y + h[1, 2]);
        }
        private static double[,] Translation(double x, double y)
        {
            return new[,] { { 1d, 0d, x }, { 0d, 1d, y }, { 0d, 0d, 1d } };
        }
        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (var y = 0; y < 3; y++)
                for (var x = 0; x < 3; x++)
                    for (var k = 0; k < 3; k++)
                        r[y, x] += a[y, k] * b[k, x];
            return r;
        }
        private static void Dispose(HObject value)
        {
            if (value != null && value.IsInitialized())
                value.Dispose();
        }
        private static void Dispose(HTuple value)
        {
            if (value != null)
                value.Dispose();
        }
        private static void Append(ref HTuple tuple, int value)
        {
            var next = tuple.TupleConcat(value);
            tuple.Dispose();
            tuple = next;
        }
    }
}
