using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using GerberViewer.Stitching.Alignment.Evaluation;

namespace GerberViewer.Stitching.Alignment
{
    /// <summary>Aligns a captured tile to its Gerber sample tile. All transforms are 3x3 CV_64F-compatible homographies
    /// in Captured -> Sample direction.</summary>
    public interface ISampleAligner : IDisposable
    {
        SampleAlignmentResult Align(SampleAlignmentContext context);
    }

    public enum SampleAlignmentMethod
    {
        HalconNcc,
        PyramidEcc,
        PyramidPhaseCorrelation,
        NccThenPyramidEcc,
        HalconShapeModel,
        ShapeThenPyramidEcc
    }
    public enum PolarityMode
    {
        AsIs,
        InvertSample,
        InvertCaptured,
        InvertBoth,
        Auto
    }
    public enum ThresholdMode
    {
        None,
        Fixed,
        Otsu,
        Adaptive
    }
    public enum EdgePreparationMode
    {
        None,
        Sobel,
        Canny,
        HalconEquivalent
    }
    public enum ContrastNormalizationMode
    {
        None,
        MinMax,
        HistogramStretch
    }

    public sealed class SampleAlignmentOptions
    {
        public double NccMinScore { get; set; } = 0.10;
        public double EccMinCorrelation { get; set; } = 0.23;
        public double MinOverlapRatio { get; set; } = 0.01;
        public double MaxTranslationPixels { get; set; } = 500.0;
        public double MaxAbsRotationDeg { get; set; } = 8.0;
        public double MinScale { get; set; } = 0.95;
        public double MaxScale { get; set; } = 1.05;
        public bool AllowNccOnlyAcceptance { get; set; }
        public bool AllowEccFromExpectedWhenNccFails { get; set; } = true;
        public int PyramidLevels { get; set; } = 4;
        public int EccIterations { get; set; } = 80;
        public double EccEpsilon { get; set; } = 1e-5;
        public PreprocessingOptions Preprocessing { get; set; } = new PreprocessingOptions();
    }

    public sealed class PreprocessingOptions
    {
        public double Contrast { get; set; } = 100d;
        public ContrastNormalizationMode ContrastNormalization { get; set; } = ContrastNormalizationMode.MinMax;
        public PolarityMode Polarity { get; set; } = PolarityMode.Auto;
        public ThresholdMode Threshold { get; set; } = ThresholdMode.Fixed;
        public byte FixedThreshold { get; set; } = 180;
        public int AdaptiveRadius { get; set; } = 15;
        public EdgePreparationMode EdgePreparation { get; set; } = EdgePreparationMode.Sobel;
        public bool ApplyGerberContentMask { get; set; } = true;
        public int NormalizedWidth { get; set; }
        public int NormalizedHeight { get; set; }
        public bool IncludeDiagnosticImages { get; set; }
    }

    public sealed class SampleAlignmentContext
    {
        public string SampleTileId { get; set; }
        public string SampleNccModelPath { get; set; }
        public OpenCvSharp.Rect? SampleNccModelRoi { get; set; }
        public double? NccReferenceOriginRow { get; set; }
        public double? NccReferenceOriginColumn { get; set; }
        public int NccModelSchemaVersion { get; set; }
        public Bitmap SampleImage { get; set; }
        public Bitmap CapturedImage { get; set; }
        public double[,] ExpectedCapturedToSampleTransform { get; set; }
        public double[,] InitialCapturedToSampleTransform { get; set; }
        public SampleAlignmentOptions Options { get; set; } = new SampleAlignmentOptions();
        public CancellationToken CancellationToken { get; set; }
    }

    public sealed class SampleAlignmentResult
    {
        public bool Success { get; set; }
        public double[,] CapturedToSampleTransform { get; set; }
        public double NccScore { get; set; } = double.NaN;
        public double EccCorrelation { get; set; } = double.NaN;
        public string CoarseMatcherName { get; set; }
        public double CoarseRawScore { get; set; } = double.NaN;
        public double CoarseNormalizedConfidence { get; set; } = double.NaN;
        public string RefinementMatcherName { get; set; }
        public double RefinementRawScore { get; set; } = double.NaN;
        public string SelectedMatcherName { get; set; }
        public double SelectedRawScore { get; set; } = double.NaN;
        public double SelectedCommonScore { get; set; } = double.NaN;
        public string SelectionReason { get; set; }
        public DirectCandidateEvaluation CandidateEvaluation { get; set; }
        public double RotationDeg { get; set; } = double.NaN;
        public double TranslationX { get; set; } = double.NaN;
        public double TranslationY { get; set; } = double.NaN;
        public double Scale { get; set; } = double.NaN;
        public double OverlapRatio { get; set; } = double.NaN;
        public SampleAlignmentMethod Method { get; set; }
        public string PreprocessingVariant { get; set; }
        public string PipelineStage { get; set; }
        public string NccFailureReason { get; set; }
        public string EccFailureReason { get; set; }
        public string RejectionReason { get; set; }
        public string Warning { get; set; }
        public IDictionary<string, Bitmap> DiagnosticImages { get; private set; } = new Dictionary<string, Bitmap>();

        public static SampleAlignmentResult Rejected(SampleAlignmentMethod method, string reason)
        {
            return new SampleAlignmentResult { Method = method, Success = false, RejectionReason = reason,
                                               CapturedToSampleTransform = Homography.Identity() };
        }
    }

    public static class Homography
    {
        public static double[,] Identity()
        {
            return new[,] { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };
        }
        public static double[,] FromPose(double tx, double ty, double angleRad, double scale)
        {
            var c = Math.Cos(angleRad) * scale;
            var s = Math.Sin(angleRad) * scale;
            return new[,] { { c, -s, tx }, { s, c, ty }, { 0d, 0d, 1d } };
        }
        public static bool IsFinite(double[,] h)
        {
            if (h == null || h.GetLength(0) != 3 || h.GetLength(1) != 3)
                return false;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (double.IsNaN(h[r, c]) || double.IsInfinity(h[r, c]))
                        return false;
            return Math.Abs(h[2, 2]) > 1e-12;
        }
    }
}
