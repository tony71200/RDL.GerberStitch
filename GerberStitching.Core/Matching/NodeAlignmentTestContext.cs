using System;
using GerberViewer.Stitching.Configuration;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching
{
    public enum NodeAlignmentTestMode
    {
        ProductionPipeline,
        HalconNcc,
        HalconShapeModel,
        PyramidEcc,
        PyramidPhaseCorretion
    }

    /// <summary>Immutable input for a diagnostic one-node CapturedImage-to-SampleTile match.</summary>
    public sealed class NodeAlignmentTestContext
    {
        public NodeAlignmentTestContext(
            int orderIndex,
            string sampleImagePath,
            string capturedImagePath,
            string nccModelPath,
            string shapeModelPath,
            MatcherPreprocessOptions preprocessOptions,
            MatcherOptions matcherOptions,
            HalconShapeModelOptions shapeOptions)
        {
            if (orderIndex < 0) throw new ArgumentOutOfRangeException("orderIndex");
            OrderIndex = orderIndex;
            SampleImagePath = sampleImagePath ?? string.Empty;
            CapturedImagePath = capturedImagePath ?? string.Empty;
            NccModelPath = nccModelPath ?? string.Empty;
            ShapeModelPath = shapeModelPath ?? string.Empty;
            var preprocessing = preprocessOptions ?? new MatcherPreprocessOptions();
            PreprocessOptions = new MatcherPreprocessOptions { Variant = preprocessing.Variant, Contrast = preprocessing.Contrast };
            MatcherOptions = Clone(matcherOptions ?? new MatcherOptions());
            ShapeOptions = Clone(shapeOptions ?? new HalconShapeModelOptions());
        }

        public int OrderIndex { get; private set; }
        public string SampleImagePath { get; private set; }
        public string CapturedImagePath { get; private set; }
        public string NccModelPath { get; private set; }
        public Rect? NccModelRoi { get; set; }
        public double? NccReferenceOriginRow { get; set; }
        public double? NccReferenceOriginColumn { get; set; }
        public int NccModelSchemaVersion { get; set; }
        public string ShapeModelPath { get; private set; }
        public Rect? ShapeModelRoi { get; set; }
        public double? ShapeReferenceOriginRow { get; set; }
        public double? ShapeReferenceOriginColumn { get; set; }
        public int ShapeModelSchemaVersion { get; set; }
        public MatcherPreprocessOptions PreprocessOptions { get; private set; }
        public MatcherOptions MatcherOptions { get; private set; }
        public HalconShapeModelOptions ShapeOptions { get; private set; }
        public MatcherKind? ProductionCoarseMatcher { get; set; }
        public MatcherKind? ProductionRefinementMatcher { get; set; }
        public bool AllowCoarseOnlyAcceptance { get; set; }
        public bool AllowRefinementFromExpectedWhenCoarseFails { get; set; }

        private static MatcherOptions Clone(MatcherOptions source)
        {
            return new MatcherOptions
            {
                PhaseMinResponse = source.PhaseMinResponse, MinTextureStdDev = source.MinTextureStdDev,
                MinOverlapRatio = source.MinOverlapRatio, MaxTranslationPixels = source.MaxTranslationPixels,
                EccMotionModel = source.EccMotionModel, PyramidLevels = source.PyramidLevels,
                MaxIterations = source.MaxIterations, Epsilon = source.Epsilon,
                MinCorrelation = source.MinCorrelation, MaxAbsRotationDeg = source.MaxAbsRotationDeg,
                MinScale = source.MinScale, MaxScale = source.MaxScale,
                PreprocessingVariant = source.PreprocessingVariant, NccNumLevels = source.NccNumLevels,
                NccAngleStartRad = source.NccAngleStartRad, NccAngleExtentRad = source.NccAngleExtentRad,
                NccAngleStepRad = source.NccAngleStepRad, NccMetric = source.NccMetric,
                NccMinScore = source.NccMinScore, NccMaxMatches = source.NccMaxMatches,
                NccMaxOverlap = source.NccMaxOverlap, NccSubPixel = source.NccSubPixel,
                NccModelRoiMarginPixels = source.NccModelRoiMarginPixels,
                NccModelRoiMinWidth = source.NccModelRoiMinWidth,
                NccModelRoiMinHeight = source.NccModelRoiMinHeight,
                NccModelRoiMinStdDev = source.NccModelRoiMinStdDev
            };
        }

        private static HalconShapeModelOptions Clone(HalconShapeModelOptions source)
        {
            return new HalconShapeModelOptions
            {
                Mode = source.Mode, NumLevels = source.NumLevels,
                AngleStartRad = source.AngleStartRad, AngleExtentRad = source.AngleExtentRad,
                AngleStepRad = source.AngleStepRad, Optimization = source.Optimization,
                Metric = source.Metric, Contrast = source.Contrast, MinContrast = source.MinContrast,
                ScaleMin = source.ScaleMin, ScaleMax = source.ScaleMax, ScaleStep = source.ScaleStep,
                MinScore = source.MinScore, NumMatches = source.NumMatches,
                MaxOverlap = source.MaxOverlap, SubPixel = source.SubPixel,
                SearchNumLevels = source.SearchNumLevels, Greediness = source.Greediness,
                ModelRoiMarginPixels = source.ModelRoiMarginPixels,
                MinModelRoiWidth = source.MinModelRoiWidth,
                MinModelRoiHeight = source.MinModelRoiHeight
            };
        }
    }
}
