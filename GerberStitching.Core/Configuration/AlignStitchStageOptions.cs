using System.ComponentModel;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using GerberViewer.Stitching.Alignment.Evaluation;

namespace GerberViewer.Stitching.Configuration
{
    public enum DirectCoarseMatcherKind 
    { 
        HalconNcc, 
        HalconShapeModel, 
        PyramidEcc,
        PyramidPhaseCorrelation,
        None 
    }
    public enum DirectRefinementMatcherKind 
    { 
        PyramidEcc, 
        PyramidPhaseCorrelation,
        None 
    }
    public enum NeighborCoarseMatcherKind 
    { 
        PyramidPhaseCorrelation, 
        None 
    }
    public enum NeighborRefinementMatcherKind 
    { 
        [System.Obsolete("Neighbor production is phase-only; this value is deserialize-only and ignored.")]
        PyramidEcc, 
        None 
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class InputPathOptions 
    { 
        public string ManifestPath { get; set; } 
        public string CapturedFolderPath { get; set; } 
        public override string ToString() { return "Input paths"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class CommonGeometryOptions 
    { 
        public double MinTextureStdDev { get; set; } = 2;
        public double MinOverlapRatio { get; set; } = 0.01; 
        public double MaxTranslationPixels { get; set; } = 300; 
        public double MaxAbsRotationDeg { get; set; } = 0.5; 
        public double MinScale { get; set; } = 0.95; 
        public double MaxScale { get; set; } = 1.05; 
        public override string ToString() { return "Geometry"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class AlignmentPreprocessingOptions
    {
        [Category("Image")]
        [DisplayName("Contrast (%)")]
        [Description("Linear image contrast applied only to the Direct Alignment moving/request image before OpenCV or HALCON matching. 100 keeps the original contrast.")]
        public double Contrast { get; set; } = 100d;
        public override string ToString() { return "Contrast " + Contrast.ToString("0.###") + "%"; }
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class HalconNccOptions 
    { 
        public double MinScore { get; set; } = 0.10; 
        public int NumLevels { get; set; } = 3; 
        public double AngleStartRad { get; set; } = -.0034906585; 
        public double AngleExtentRad { get; set; } = .0034906585; 
        public double AngleStepRad { get; set; } = .00087266463; 
        public string Metric { get; set; } = "use_polarity"; 
        public int MaxMatches { get; set; } = 1; 
        public double MaxOverlap { get; set; } = 1; 
        public string SubPixel { get; set; } = "true"; 
        public int ModelRoiMarginPixels { get; set; } = 16; 
        public override string ToString() { return "HALCON NCC"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class EccOptions 
    { 
        public EccMotionModel MotionModel { get; set; } = EccMotionModel.Euclidean; 
        public int PyramidLevels { get; set; } = 3; 
        public int MaxIterations { get; set; } = 80; 
        public double Epsilon { get; set; } = 1e-5; 
        public double MinCorrelation { get; set; } = 0.13; 
        public override string ToString() { return "Pyramid ECC"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class PhaseCorrelationOptions 
    { 
        public double MinResponse { get; set; } = 0.15; 
        public int PyramidLevels { get; set; } = 3; 
        public override string ToString() { return "Phase correlation"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class DirectPipelinePolicy 
    {
        public bool AllowCoarseOnlyAcceptance { get; set; } = true;
        public bool AllowRefinementFromExpectedWhenCoarseFails { get; set; } = true; 
        public override string ToString() { return "Direct policy"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class NeighborAcceptanceOptions 
    { 
        public double MinOverlapRatio { get; set; } = .01; 
        public double MaxTranslationPixels { get; set; } = 500; 
        public double MaxAbsRotationDeg { get; set; } = 0.5; 
        public double MinScale { get; set; } = .95; 
        public double MaxScale { get; set; } = 1.05; 
        public override string ToString() { return "Acceptance"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class DirectAlignmentOptions 
    { 
        [Category("Pose correction")]
        public bool EnableDirectPoseOutlierCorrection { get; set; } = true;
        [Category("Pose correction")]
        public double RotationOutlierMadK { get; set; } = 3.0;
        [Category("Pose correction")]
        public double AngleBandFloorDeg { get; set; } = 0.02;
        [Category("Pose correction")]
        public double SignFlipGuardDeg { get; set; } = 0.01;
        public AlignmentPreprocessingOptions Preprocessing { get; set; } = new AlignmentPreprocessingOptions();
        [DisplayName("Coarse Matcher")]
        public DirectCoarseMatcherKind CoarseMatcher { get; set; } = DirectCoarseMatcherKind.HalconShapeModel;
        [DisplayName("Refinement Matcher")]
        public DirectRefinementMatcherKind RefinementMatcher { get; set; } = DirectRefinementMatcherKind.PyramidEcc;
        [Category("Pose")]
        [DisplayName("Manual Offset X")]
        [Description("X translation, in pixels, composed into each accepted direct alignment pose.")]
        public double ManualOffsetX { get; set; }
        [Category("Pose")]
        [DisplayName("Manual Offset Y")]
        [Description("Y translation, in pixels, composed into each accepted direct alignment pose.")]
        public double ManualOffsetY { get; set; }
        public CommonGeometryOptions Geometry { get; set; } = new CommonGeometryOptions(); 
        public HalconNccOptions Ncc { get; set; } = new HalconNccOptions(); 
        public HalconShapeModelOptions Shape { get; set; } = new HalconShapeModelOptions(); 
        public EccOptions Ecc { get; set; } = new EccOptions(); 
        public DirectPipelinePolicy Policy { get; set; } = new DirectPipelinePolicy(); 
        [DisplayName("Candidate Evaluation")]
        public DirectEvaluationOptions Evaluation { get; set; } = new DirectEvaluationOptions();
        public override string ToString() { return CoarseMatcher + " -> " + RefinementMatcher; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class NeighborAlignmentOptions 
    { 
        public NeighborCoarseMatcherKind CoarseMatcher { get; set; } = NeighborCoarseMatcherKind.PyramidPhaseCorrelation; 
        [Browsable(false)]
        [System.Obsolete("Neighbor production is phase-only. Retained only for config deserialization.")]
        public NeighborRefinementMatcherKind RefinementMatcher { get; set; } = NeighborRefinementMatcherKind.None;
        public PhaseCorrelationOptions Phase { get; set; } = new PhaseCorrelationOptions(); 
        [Browsable(false)]
        [System.Obsolete("Neighbor ECC settings are ignored. Retained only for config deserialization.")]
        public EccOptions Ecc { get; set; } = new EccOptions(); 
        public NeighborAcceptanceOptions Acceptance { get; set; } = new NeighborAcceptanceOptions(); 
        [Description("Maximum translation residual allowed when a graph pose adjusts an existing direct pose.")]
        public double MaxDirectPoseAdjustmentPixels { get; set; } = 8;
        [Description("Maximum rotation residual allowed when a graph pose adjusts an existing direct pose.")]
        public double MaxDirectPoseAdjustmentRotationDeg { get; set; } = 0.5;
        [Description("Maximum closure translation error for an edge joining two already posed nodes.")]
        public double MaxCycleClosureErrorPixels { get; set; } = 12;
        [Description("Diagnostic-only translation disagreement threshold between accepted recovery candidates; selection is never fused or changed.")]
        public double MaxCandidateDisagreementPixels { get; set; } = 12;
        [Description("Diagnostic-only rotation disagreement threshold between accepted recovery candidates; selection is never fused or changed.")]
        public double MaxCandidateDisagreementRotationDeg { get; set; } = 0.5;
        public override string ToString() { return CoarseMatcher + " (phase-only)"; }
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class RecoveryFallbackOptions
    { 
        [Category("Neighbor graph modes")]
        [DisplayName("Recover Failed Tiles")]
        [Description("Runs neighbor matching only for tiles whose direct match failed/rejected or that are not stitchable. This adds matching cost only for failed tiles and may replace their failed pose with an accepted neighbor pose.")]
        public bool RecoverFailedTiles { get; set; } = true;
        [Category("Neighbor graph modes")]
        [DisplayName("Reconcile All Matched Tiles")]
        [Description("Builds the full eligible captured-image neighbor graph and may adjust existing poses only when translation/rotation residual thresholds allow it. This is the more expensive mode.")]
        public bool ReconcileAllMatchedTiles { get; set; } = true;
        [Browsable(false)]
        public bool EnableNeighborRecovery { get; set; } = true;
        public bool EnableAnchorInterpolation { get; set; } = true;
        public bool EnableManualAlignment { get; set; } = true; 
        public bool AutoPlaceExactZeroSample { get; set; } = true; 
        public bool AutoPlaceLowTextureSample { get; set; } 
        public bool AllowExpectedGridFallback { get; set; } 
        public bool RequireManualConfirmationForExpectedGrid { get; set; } = true; 
        public bool RevalidateSampleContent { get; set; } = true; 
        public double LowTextureStdDevThreshold { get; set; } = 7; 
        public override string ToString() { return "Recovery policy"; } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class StitchingOptions 
    { 
        public StitchingEngine Engine { get; set; } = StitchingEngine.HalconProjectiveMosaicRebased;
        [Category("Safety")]
        [DisplayName("Allow unsafe legacy HALCON mosaic")]
        [Description("Explicitly permits the pre-Task-02 all-to-root HALCON mosaic. Its output coordinate contract is not verified and it must remain disabled for production.")]
        public bool AllowUnsafeLegacyHalconProjectiveMosaic { get; set; }
        [Browsable(false)]
        public double ManualOffsetX { get; set; }
        [Browsable(false)]
        public double ManualOffsetY { get; set; }
        public bool EnableBlending { get; set; } = true;
        public StitchBlendMode BlendMode { get; set; } = StitchBlendMode.Feather;
        public BlankFallbackOverlapPolicy BlankFallbackOverlapPolicy { get; set; } = BlankFallbackOverlapPolicy.PreserveExistingOverlap; 
        public bool ForceGray8Output { get; set; } = true; 
        public override string ToString() { return Engine.ToString(); } 
    }
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class OutputOptions 
    { 
        public string OutputPath { get; set; } 
        public TiffMode TiffMode { get; set; } = TiffMode.Auto; 
        public int BigTiffTileWidth { get; set; } = 512; 
        public int BigTiffTileHeight { get; set; } = 512; 
        public int PreviewUpdateInterval { get; set; } = 4; 
        public double MaxPreviewMegapixels { get; set; } = 32;
        public override string ToString() { return "Output"; } 
    }
}
