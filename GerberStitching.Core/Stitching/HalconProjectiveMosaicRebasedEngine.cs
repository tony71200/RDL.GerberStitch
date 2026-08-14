using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Models;
using HalconDotNet;

namespace GerberViewer.Stitching.Stitching
{
    public sealed class HalconMosaicInput
    {
        public CapturedImageInfo Image { get; set; }
        public double[,] GlobalPose { get; set; }
        public bool IsBlackPlaceholder { get; set; }
    }

    // [Tony] [Change time: 2026-08-01] [Purpose: Isolate legacy HALCON mosaic and implement pairwise graph plus
    // canonical canvas rebase.]
    public sealed class HalconProjectiveMosaicEngine
    {
        private const double MatrixTolerance = 1e-5;

        public void StitchLegacy(IList<HalconMosaicInput> inputs, string path, bool bigTiff,
                                 CancellationToken cancellationToken)
        {
            // Preserve the legacy caller order so the gated comparison engine remains a stable baseline.
            var ordered = Validate(inputs, false);
            using (var images = LoadImages(ordered, cancellationToken))
            {
                HObject mosaic = null;
                try
                {
                    if (ordered.Count == 1)
                        HOperatorSet.SelectObj(images, out mosaic, 1);
                    else
                    {
                        var source = new HTuple();
                        var destination = new HTuple();
                        var matrices = new HTuple();
                        var rootInverse = Invert(ordered[0].GlobalPose);
                        for (int i = 1; i < ordered.Count; i++)
                        {
                            source = source.TupleConcat(i + 1);
                            destination = destination.TupleConcat(1);
                            matrices = matrices.TupleConcat(
                                new HTuple(ToHalconRowColumn(Multiply(rootInverse, ordered[i].GlobalPose))));
                        }
                        HTuple ignored;
                        HOperatorSet.GenProjectiveMosaic(images, out mosaic, new HTuple(1), source, destination,
                                                         matrices, new HTuple("default"), new HTuple("false"),
                                                         out ignored);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Write(path, mosaic, bigTiff);
                }
                finally
                {
                    if (mosaic != null && mosaic.IsInitialized())
                        mosaic.Dispose();
                }
            }
        }

        public void StitchRebased(IList<HalconMosaicInput> inputs, RectangleF canonicalBounds, int width, int height,
                                  string path, bool bigTiff, StitchingExecutionReport report,
                                  CancellationToken cancellationToken)
        {
            if (report == null)
                throw new ArgumentNullException("report");
            var ordered = Validate(inputs, true);
            report.SelectedAnchorOrderIndex = ordered[0].Image.OrderIndex;
            report.CoordinateContract = "CapturedImageLocalPixels -> StitchedCanvasPixels (canonical rebased HALCON)";

            using (var images = LoadImages(ordered, cancellationToken))
            {
                HObject mosaic = null;
                HObject rebased = null;
                try
                {
                    HTuple mosaicMatrices;
                    if (ordered.Count == 1)
                    {
                        HOperatorSet.SelectObj(images, out mosaic, 1);
                        mosaicMatrices = new HTuple(ToHalconRowColumn(Identity()));
                    }
                    else
                    {
                        HTuple mappingSource;
                        HTuple mappingDestination;
                        HTuple pairwiseMatrices;
                        BuildPairwiseGraph(ordered, out mappingSource, out mappingDestination, out pairwiseMatrices);
                        HOperatorSet.GenProjectiveMosaic(images, out mosaic, new HTuple(1), mappingSource,
                                                         mappingDestination, pairwiseMatrices, new HTuple("default"),
                                                         new HTuple("false"), out mosaicMatrices);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var canvasTranslation = Translation(-canonicalBounds.Left, -canonicalBounds.Top);
                    var anchorPostTransform = default(double[,]);
                    var maximumResidual = 0d;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        var mosaicPose = FromHalconRowColumn(ReadMatrix(mosaicMatrices, i));
                        var desiredCanvasPose = Multiply(canvasTranslation, ordered[i].GlobalPose);
                        var postTransform = Multiply(desiredCanvasPose, Invert(mosaicPose));
                        if (i == 0)
                            anchorPostTransform = postTransform;
                        var equationResidual =
                            MaxResidual(Multiply(anchorPostTransform, mosaicPose), desiredCanvasPose);
                        var consensusResidual = MaxResidual(anchorPostTransform, postTransform);
                        var residual = Math.Max(equationResidual, consensusResidual);
                        maximumResidual = Math.Max(maximumResidual, residual);
                        report.MatrixDiagnostics.Add(
                            "OrderIndex=" + ordered[i].Image.OrderIndex + "; mosaicPose=" + Format(mosaicPose) +
                            "; desiredCanvasPose=" + Format(desiredCanvasPose) +
                            "; postTransform=" + Format(postTransform) + "; residual=" +
                            residual.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    report.MaxMatrixResidual = maximumResidual;
                    if (maximumResidual > MatrixTolerance)
                        throw new InvalidOperationException(
                            "HALCON canonical rebase matrix consensus failed. Maximum residual=" +
                            maximumResidual.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                            ", tolerance=" +
                            MatrixTolerance.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ".");

                    cancellationToken.ThrowIfCancellationRequested();
                    HOperatorSet.ProjectiveTransImageSize(
                        mosaic, out rebased, new HTuple(ToHalconRowColumn(anchorPostTransform)), new HTuple("bilinear"),
                        new HTuple(width), new HTuple(height), new HTuple("false"));
                    cancellationToken.ThrowIfCancellationRequested();
                    Write(path, rebased, bigTiff, report);
                }
                finally
                {
                    if (rebased != null && rebased.IsInitialized())
                        rebased.Dispose();
                    if (mosaic != null && mosaic.IsInitialized())
                        mosaic.Dispose();
                }
            }
        }

        public static double[,] RelativePose(double[,] sourceGlobalPose, double[,] destinationGlobalPose)
        {
            return Multiply(Invert(destinationGlobalPose), sourceGlobalPose);
        }

        public static double[] ToHalconRowColumn(double[,] xy)
        {
            if (xy == null || xy.GetLength(0) != 3 || xy.GetLength(1) != 3)
                throw new ArgumentException("Matrix must be 3x3.", "xy");
            return new[] { xy[1, 1], xy[1, 0], xy[1, 2], xy[0, 1], xy[0, 0], xy[0, 2], xy[2, 1], xy[2, 0], xy[2, 2] };
        }

        public static double[,] FromHalconRowColumn(double[] rowColumn)
        {
            if (rowColumn == null || rowColumn.Length != 9)
                throw new ArgumentException("HALCON projective matrix must contain 9 values.", "rowColumn");
            return new[,] { { rowColumn[4], rowColumn[3], rowColumn[5] },
                            { rowColumn[1], rowColumn[0], rowColumn[2] },
                            { rowColumn[7], rowColumn[6], rowColumn[8] } };
        }

        private static void BuildPairwiseGraph(IList<HalconMosaicInput> ordered, out HTuple source,
                                               out HTuple destination, out HTuple matrices)
        {
            source = new HTuple();
            destination = new HTuple();
            matrices = new HTuple();
            for (int i = 0; i < ordered.Count - 1; i++)
            {
                if (ordered[i + 1].Image.OrderIndex != ordered[i].Image.OrderIndex + 1)
                    throw new InvalidOperationException(
                        "HALCON pairwise graph contains a disconnected traversal sequence between OrderIndex " +
                        ordered[i].Image.OrderIndex + " and " + ordered[i + 1].Image.OrderIndex + ".");
                source = source.TupleConcat(i + 1);
                destination = destination.TupleConcat(i + 2);
                matrices = matrices.TupleConcat(
                    new HTuple(ToHalconRowColumn(RelativePose(ordered[i].GlobalPose, ordered[i + 1].GlobalPose))));
            }
        }

        private static IList<HalconMosaicInput> Validate(IList<HalconMosaicInput> inputs, bool orderByTraversal)
        {
            if (inputs == null || inputs.Count == 0)
                throw new ArgumentException("At least one HALCON mosaic input is required.", "inputs");
            if (inputs.Any(x => x == null || x.Image == null || x.GlobalPose == null))
                throw new ArgumentException("Every HALCON mosaic input requires image metadata and a global pose.",
                                            "inputs");
            var duplicates =
                inputs.GroupBy(x => x.Image.OrderIndex).Where(x => x.Count() > 1).Select(x => x.Key).ToArray();
            if (duplicates.Length != 0)
                throw new InvalidOperationException(
                    "Duplicate HALCON mosaic OrderIndex: " + string.Join(",", duplicates) + ".");
            return orderByTraversal ? inputs.OrderBy(x => x.Image.OrderIndex).ToList() : inputs.ToList();
        }

        private static HObject LoadImages(IList<HalconMosaicInput> inputs, CancellationToken cancellationToken)
        {
            HObject images;
            HOperatorSet.GenEmptyObj(out images);
            try
            {
                foreach (var input in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    HObject image = null;
                    HObject concatenated = null;
                    try
                    {
                        if (input.IsBlackPlaceholder)
                        {
                            if (input.Image.Width <= 0 || input.Image.Height <= 0)
                                throw new InvalidOperationException("Black placeholder OrderIndex " +
                                                                    input.Image.OrderIndex +
                                                                    " requires positive Width and Height.");
                            HOperatorSet.GenImageConst(out image, new HTuple("byte"), new HTuple(input.Image.Width),
                                                       new HTuple(input.Image.Height));
                        }
                        else
                            HOperatorSet.ReadImage(out image, input.Image.FilePath);
                        HOperatorSet.ConcatObj(images, image, out concatenated);
                        images.Dispose();
                        images = concatenated;
                        concatenated = null;
                    }
                    finally
                    {
                        if (image != null && image.IsInitialized())
                            image.Dispose();
                        if (concatenated != null && concatenated.IsInitialized())
                            concatenated.Dispose();
                    }
                }
                var result = images;
                images = null;
                return result;
            }
            finally
            {
                if (images != null && images.IsInitialized())
                    images.Dispose();
            }
        }

        private static double[] ReadMatrix(HTuple matrices, int index)
        {
            var result = new double[9];
            for (int i = 0; i < 9; i++)
                result[i] = matrices.TupleSelect(index * 9 + i).D;
            return result;
        }

        // Measure HALCON image-writing time separately for the Execute Time tab.
        private static void Write(string path, HObject image, bool bigTiff, StitchingExecutionReport report = null)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            HOperatorSet.WriteImage(image, new HTuple(bigTiff ? "bigtiff none" : "tiff none"), new HTuple(0),
                                    new HTuple(path));
            sw.Stop();
            if (report != null)
                report.SaveElapsedMilliseconds += sw.ElapsedMilliseconds;
        }

        private static double[,] Identity()
        {
            return new[,] { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };
        }
        private static double[,] Translation(double x, double y)
        {
            return new[,] { { 1d, 0d, x }, { 0d, 1d, y }, { 0d, 0d, 1d } };
        }

        private static double[,] Invert(double[,] h)
        {
            var det = h[0, 0] * h[1, 1] - h[0, 1] * h[1, 0];
            if (Math.Abs(det) < 1e-12)
                throw new InvalidOperationException("Cannot invert singular affine transform.");
            var a = h[1, 1] / det;
            var b = -h[0, 1] / det;
            var d = -h[1, 0] / det;
            var e = h[0, 0] / det;
            return new[,] { { a, b, -(a * h[0, 2] + b * h[1, 2]) },
                            { d, e, -(d * h[0, 2] + e * h[1, 2]) },
                            { 0d, 0d, 1d } };
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var result = new double[3, 3];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    for (int k = 0; k < 3; k++)
                        result[r, c] += a[r, k] * b[k, c];
            return result;
        }

        private static double MaxResidual(double[,] a, double[,] b)
        {
            var max = 0d;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    max = Math.Max(max, Math.Abs(a[r, c] - b[r, c]));
            return max;
        }

        private static string Format(double[,] value)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "[{0:R},{1:R},{2:R};{3:R},{4:R},{5:R};{6:R},{7:R},{8:R}]", value[0, 0], value[0, 1],
                                 value[0, 2], value[1, 0], value[1, 1], value[1, 2], value[2, 0], value[2, 1],
                                 value[2, 2]);
        }
    }
}
