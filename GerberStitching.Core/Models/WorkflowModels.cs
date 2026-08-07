using System;
using System.Collections.Generic;
using System.ComponentModel;
using GerberViewer.Stitching.RobotManager;
using GerberViewer.Stitching.Alignment;
using GerberViewer.Stitching.Configuration;

namespace GerberViewer.Stitching.Models
{
    public enum PoseSource 
    { 
        SampleAlignment, 
        NeighborAlignment, 
        AnchorAdjusted, 
        Interpolated, 
        ExpectedGridOffset, 
        Manual, 
        Excluded,
        Failed,
        BlankSampleExpectedPose
    }
    public enum AlignStitchRunStatus 
    { 
        NotStarted, 
        Completed, 
        CompletedWithFallback, 
        CompletedWithExcludedTiles, 
        Cancelled, 
        Failed 
    }
    public enum StitchingEngine 
    { 
        OpenCv, 
        HalconProjectiveMosaic, 
        HalconProjectiveMosaicRebased,
        HalconWarpThenTileOffsetExperimental,
        HalconThenOpenCvFallback 
    }
    public sealed class StitchingExecutionReport
    {
        public StitchingEngine ConfiguredEngine { get; set; }
        public StitchingEngine EffectiveEngine { get; set; }
        public string FallbackReason { get; set; }
        public string CoordinateContract { get; set; }
        public int? SelectedAnchorOrderIndex { get; set; }
        public double MaxMatrixResidual { get; set; }
        public IList<string> MatrixDiagnostics { get; set; } = new List<string>();
        public long ElapsedMilliseconds { get; set; }
        // [Claude] [Change time: 2026-08-06] [Purpose: Separate image-writing time (Save Image) from mosaic construction time (Stitching) on the Execute Time tab.]
        public long SaveElapsedMilliseconds { get; set; }
        public double EstimatedPeakPatchMegapixels { get; set; }
        public string OverlapPolicy { get; set; }
        public IList<int> DrawOrder { get; set; } = new List<int>();
    }
    // [Claude] [Change time: 2026-08-06] [Purpose: Measure each Tab 3 workflow stage for display on the Execute Time tab.]
    public sealed class StageTimingReport
    {
        public string Stage { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string Detail { get; set; }
    }
    public enum BlankFallbackOverlapPolicy { Normal, PreserveExistingOverlap, WeightedBlend }

    public sealed class GerberSampleConfig 
    { 
        public int Rows { get; set; } 
        public int Columns { get; set; } 
        public double TileWidth { get; set; } 
        public double TileHeight { get; set; } 
        public StartOrder StartOrder { get; set; } = StartOrder.TopLeftDown; 
    }
    public sealed class AlignStitchConfig 
    { 
        // [Codex] [Change time: 2026-07-27] [Purpose: Make runtime configuration stage-specific while retaining legacy flat fields for deserialization migration.]
        public int ConfigVersion { get; set; }
        public InputPathOptions Input { get; set; } = new InputPathOptions();
        public DirectAlignmentOptions DirectAlignment { get; set; } = new DirectAlignmentOptions();
        public NeighborAlignmentOptions NeighborAlignment { get; set; } = new NeighborAlignmentOptions();
        public RecoveryFallbackOptions Recovery { get; set; } = new RecoveryFallbackOptions();
        public GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions PoseGraph { get; set; } = new GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions();
        public StitchingOptions Stitching { get; set; } = new StitchingOptions();
        public OutputOptions Output { get; set; } = new OutputOptions();
        public string InputManifestPath { get; set; } 
        public string CapturedFolderPath { get; set; } 
        public string OutputPath { get; set; } 
        [Browsable(false)] 
        public SampleAlignmentMethod AlignmentMethod { get; set; } = SampleAlignmentMethod.HalconNcc; 
        [DisplayName("Alignment Matcher")] 
        [ReadOnly(true)] 
        public string AlignmentMatcher { get { return "HalconNccMatcher"; } }
        public double NccMinScore { get; set; } = 0.13; 
        public double EccMinCorrelation { get; set; } = 0.13; 
        public double MaxTranslationPixels { get; set; } = 300.0; 
        public double MaxAbsRotationDeg { get; set; } = 0.1; 
        public double MinScale { get; set; } = 0.95; 
        public double MaxScale { get; set; } = 1.05; 
        public double MinOverlapRatio { get; set; } = 0.01; 
        public bool AllowNccOnlyAcceptance { get; set; } 
        public bool AllowEccFromExpectedWhenNccFails { get; set; } = true; 
        public bool EnableNeighborRecovery { get; set; } = true; 
        public bool EnableAnchorInterpolation { get; set; } = true; 
        public bool EnableDirectPoseOutlierCorrection { get; set; } = true;
        public double RotationOutlierMadK { get; set; } = 3.0;
        public double AngleBandFloorDeg { get; set; } = 0.02;
        public double SignFlipGuardDeg { get; set; } = 0.01;
        public bool AllowExpectedGridFallback { get; set; } 
        public bool RequireManualConfirmationForExpectedGrid { get; set; } = true;
        public StitchingEngine StitchingEngine { get; set; } = StitchingEngine.HalconProjectiveMosaicRebased;
        [Browsable(false)]
        public bool AllowUnsafeLegacyHalconProjectiveMosaic { get; set; }
        [Browsable(false)]
        public double ManualOffsetX { get; set; } = 0d;
        [Browsable(false)]
        public double ManualOffsetY { get; set; } = 0d;
        public int PreviewUpdateInterval { get; set; } = 4; 
        public double MaxPreviewMegapixels { get; set; } = 32.0; 
        public TiffMode TiffMode { get; set; } = TiffMode.Auto; 
        public int BigTiffTileWidth { get; set; } = 512; 
        public int BigTiffTileHeight { get; set; } = 512; 
        public bool AutoPlaceExactZeroSample { get; set; } = true;
        public bool RevalidateSampleContent { get; set; } = true;
        public double LowTextureStdDevThreshold { get; set; } = 7.0;
        public bool AutoPlaceLowTextureSample { get; set; }
        public BlankFallbackOverlapPolicy BlankFallbackOverlapPolicy { get; set; } = BlankFallbackOverlapPolicy.PreserveExistingOverlap;
    }
    public sealed class GerberWorkflowConfig 
    { 
        public GerberSampleConfig Sample { get; set; } = new GerberSampleConfig(); 
        public AlignStitchConfig Alignment { get; set; } = new AlignStitchConfig(); 
    }
    public enum OrderNodeState
    {
        Pending,
        Processing,
        SampleAlignOk,
        NeighborAlignOk,
        AnchorAdjusted,
        Interpolated,
        ExpectedGridOffset,
        Manual,
        Failed,
        Excluded,
        ExpectedOffset
    }
    // [Claude] [Change time: 2026-08-06] [Purpose: Provide one source of truth for PoseSource-to-OrderNodeState mapping, which was previously duplicated in PathCanvasControl and AlignStitchingControl.]
    public static class OrderNodeStateMapper
    {
        public static OrderNodeState FromPoseSource(PoseSource source)
        {
            if (source == PoseSource.SampleAlignment) return OrderNodeState.SampleAlignOk;
            if (source == PoseSource.NeighborAlignment) return OrderNodeState.NeighborAlignOk;
            if (source == PoseSource.AnchorAdjusted) return OrderNodeState.AnchorAdjusted;
            if (source == PoseSource.Interpolated) return OrderNodeState.Interpolated;
            if (source == PoseSource.Manual) return OrderNodeState.Manual;
            if (source == PoseSource.Excluded) return OrderNodeState.Excluded;
            if (source == PoseSource.ExpectedGridOffset || source == PoseSource.BlankSampleExpectedPose) return OrderNodeState.ExpectedGridOffset;
            return OrderNodeState.Failed;
        }
    }
    public sealed class CapturedImageInfo
    { 
        public string FilePath { get; set; } 
        public int Row { get; set; } 
        public int Column { get; set; } 
        public int OrderIndex { get; set; } 
        public int Width { get; set; } 
        public int Height { get; set; } 
        public string NaturalSortKey { get; set; } 
        public string SourceMetadata { get; set; } 
        public double RobotX { get; set; } 
        public double RobotY { get; set; } 
        public DateTime CapturedUtc { get; set; } 
        public OrderNodeState State { get; set; } = OrderNodeState.Pending; 
    }
    public sealed class StitchImagePose 
    { 
        public int OrderIndex { get; set; } 
        public int Row { get; set; } 
        public int Column { get; set; } 
        public double X { get; set; } 
        public double Y { get; set; } 
        public double RotationDeg { get; set; } 
        public PoseSource Source { get; set; } 
    }
    public sealed class ProcessingReport 
    { 
        public bool Succeeded { get; set; } 
        public AlignStitchRunStatus RunStatus { get; set; } = AlignStitchRunStatus.NotStarted; 
        public AlignStitchConfig InputConfig { get; set; } 
        public EffectiveConfigSnapshot EffectiveConfig { get; set; }
        public SampleManifest InputManifest { get; set; } 
        public IList<string> Messages { get; set; } = new List<string>(); 
        public IList<string> Warnings { get; set; } = new List<string>(); 
        public IList<StitchImagePose> Poses { get; set; } = new List<StitchImagePose>(); 
        public IList<ProcessingTileReport> TileReports { get; set; } = new List<ProcessingTileReport>(); 
        public IList<RecoveryEdgeReport> RecoveryEdges { get; set; } = new List<RecoveryEdgeReport>(); 
        public DirectPoseCorrectionReport DirectPoseCorrection { get; set; }
        public GerberViewer.Stitching.Alignment.Graph.PoseGraphReport PoseGraph { get; set; }
        public GlobalPoseValidationReport GlobalPoseValidation { get; set; }
        public StitchingExecutionReport StitchingExecution { get; set; }
        // [Claude] [Change time: 2026-08-06] [Purpose: Measure each Tab 3 workflow stage for display on the Execute Time tab.]
        public IList<StageTimingReport> StageTimings { get; set; } = new List<StageTimingReport>();
        public string FinalOutputPath { get; set; }
        public static ProcessingReport Create(AlignStitchConfig config, SampleManifest manifest)
        { 
            return new ProcessingReport 
            { 
                InputConfig = config, 
                InputManifest = manifest 
            }; 
        } 
    }

    public enum TiffMode 
    { 
        Auto, 
        StandardTiff, 
        BigTiff 
    }
    public sealed class WorkflowProgress
    {
        public WorkflowProgress(int current, int total, CapturedImageInfo image, string stage)
            : this(current, total, image, stage, null) { }

        // [Claude] [Change time: 2026-08-06] [Purpose: Return each tile result to the UI so orderPathCanvas updates colors during execution, not only after completion.]
        public WorkflowProgress(int current, int total, CapturedImageInfo image, string stage, OrderNodeState? tileState)
        {
            Current = current;
            Total = total;
            Image = image;
            Stage = stage;
            TileState = tileState;
        }
        public int Current { get; private set; }
        public int Total { get; private set; }
        public CapturedImageInfo Image { get; private set; }
        public string Stage { get; private set; }
        public OrderNodeState? TileState { get; private set; }
    }

    public sealed class AlignStitchWorkflowResult 
    { 
        public ProcessingReport Report { get; set; } 
        public System.Collections.Generic.IList<TileWorkflowState> States { get; set; } = new System.Collections.Generic.List<TileWorkflowState>(); 
    }
    public sealed class TileWorkflowState
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public double[,] GlobalPose { get; set; }
        public PoseSource Source { get; set; }
        public string Reason { get; set; }
        public GerberViewer.Stitching.Alignment.SampleAlignmentResult Alignment { get; set; }
#if DEBUG
        // Kept separately because neighbor recovery replaces Alignment with its own local transform.
        internal double[,] DirectAlignmentTransform { get; set; }
#endif
        // [Codex] [Change time: 2026-08-04] [Purpose: Preserve the pre-recovery direct pose as the pose-graph prior; must be recorded in Release too, unlike DirectAlignmentTransform.]
        public double[,] OriginalDirectGlobalPose { get; set; }
        public bool PoseGraphAdjusted { get; set; }
        public double PoseGraphDeltaPixels { get; set; }
        public bool AlignmentSucceeded { get; set; }
        public bool IsFallbackPose { get; set; }
        public bool PoseCorrected { get; set; }
        public bool IsStitchable { get; set; }
        public GerberViewer.Stitching.Imaging.SampleTileContentMetrics SampleContent { get; set; }
        public bool HasValidPose { get { return GerberViewer.Stitching.Alignment.Homography.IsFinite(GlobalPose); } }
        public static TileWorkflowState From(CapturedImageInfo c, double[,] pose, PoseSource source, GerberViewer.Stitching.Alignment.SampleAlignmentResult result, string reason)
        {
            var hasPose = GerberViewer.Stitching.Alignment.Homography.IsFinite(pose);
            var alignmentSucceeded = hasPose && (source == PoseSource.SampleAlignment || source == PoseSource.NeighborAlignment || source == PoseSource.AnchorAdjusted || source == PoseSource.Manual);
            var isFallbackPose = hasPose && (source == PoseSource.ExpectedGridOffset || source == PoseSource.Interpolated);
            var isStitchable = alignmentSucceeded || (hasPose && source == PoseSource.Interpolated);
            return new TileWorkflowState 
            { 
                OrderIndex = c.OrderIndex, 
                Row = c.Row, 
                Column = c.Column, 
                GlobalPose = pose, 
                Source = source, 
                Alignment = result, 
                Reason = reason, 
                AlignmentSucceeded = alignmentSucceeded, 
                IsFallbackPose = isFallbackPose, 
                IsStitchable = isStitchable 
            };
        }
        public static TileWorkflowState FromBlankSampleExpectedPose(CapturedImageInfo captured, SampleTileInfo tile, GerberViewer.Stitching.Imaging.SampleTileContentMetrics metrics, string reason)
        {
            return new TileWorkflowState { OrderIndex = captured.OrderIndex, Row = captured.Row, Column = captured.Column,
                GlobalPose = new[,] { { 1d, 0d, (double)tile.ExpectedX }, { 0d, 1d, (double)tile.ExpectedY }, { 0d, 0d, 1d } },
                Source = PoseSource.BlankSampleExpectedPose, Reason = reason, AlignmentSucceeded = false,
                IsFallbackPose = true, IsStitchable = true, SampleContent = metrics };
        }
        public StitchImagePose ToPose() 
        { 
            return new StitchImagePose 
            { 
                OrderIndex = OrderIndex, 
                Row = Row, 
                Column = Column, 
                X = GlobalPose == null ? 0 : GlobalPose[0, 2], 
                Y = GlobalPose == null ? 0 : GlobalPose[1, 2], 
                RotationDeg = GlobalPose == null ? 0 : System.Math.Atan2(GlobalPose[1, 0], 
                GlobalPose[0, 0]) * 180 / System.Math.PI, 
                Source = Source 
            }; 
        }
    }
    public sealed class ManualAlignmentRequest 
    { 
        public CapturedImageInfo Captured { get; set; } 
        public SampleTileInfo SampleTile { get; set; } 
        public double[,] ExpectedGlobalPose { get; set; } 
        public double[,] BestAutomaticCandidate { get; set; } 
        public System.Collections.Generic.IList<string> Diagnostics { get; set; } = new System.Collections.Generic.List<string>(); 
    }
    public sealed class ManualAlignmentResult 
    { 
        public bool Accepted { get; set; } 
        public bool Skipped { get; set; } 
        public bool CancelRun { get; set; } 
        public double[,] GlobalPose { get; set; } 
        public string Notes { get; set; } 
    }
    public sealed class RecoveryEdgeReport 
    { 
        public RecoveryEdgePurpose Purpose { get; set; }
        public bool Accepted { get; set; }
        public int AnchorOrderIndex { get; set; } 
        public int TargetOrderIndex { get; set; } 
        public string Direction { get; set; } 
        public string Matcher { get; set; } 
        public string Reason { get; set; } 
        public double[,] TargetToAnchorTransform { get; set; } 
        public double[,] ExpectedTargetToAnchorTransform { get; set; }
        public double[,] MeasuredTargetToAnchorTransform { get; set; }
        public double[,] ResidualTransform { get; set; }
        public double ResidualDx { get; set; } = double.NaN;
        public double ResidualDy { get; set; } = double.NaN;
        public double PairMeasuredRotationDeg { get; set; }
        public double InheritedAnchorRotationDeg { get; set; } = double.NaN;
        public double PhaseScore { get; set; } 
        public double EccCorrelation { get; set; } 
        public double OverlapRatio { get; set; }
        public double Quality { get; set; }
        public double SampleForegroundRatio { get; set; } = double.NaN;
        public double SampleEdgeDensity { get; set; } = double.NaN;
        public bool TraversalDirectionPreferred { get; set; }
        public double ResidualNorm { get; set; } = double.NaN;
        public int Rank { get; set; }
        public bool Selected { get; set; }
        public string RankingReason { get; set; }
        public double[,] CandidateGlobalPose { get; set; }
        public double MaximumTranslationDisagreement { get; set; } = double.NaN;
        public double MaximumRotationDisagreementDeg { get; set; } = double.NaN;
        public string ConsistencyWarning { get; set; }
        public bool ComposedIntoGlobalPose { get; set; }
        public double[,] CompositionAnchorGlobalPose { get; set; }
        public double FinalTranslationResidual { get; set; } = double.NaN;
        public double FinalRotationResidualDeg { get; set; } = double.NaN;
        public string ResidualDisposition { get; set; }
    }
    public enum RecoveryEdgePurpose { FailureRecovery, FullPoseReconciliation }
    public sealed class ProcessingTileReport
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public PoseSource PoseSource { get; set; }
        public double NccScore { get; set; }
        public double EccCorrelation { get; set; }
        public string CoarseMatcherName { get; set; }
        public double CoarseRawScore { get; set; }
        public double CoarseNormalizedConfidence { get; set; }
        public string RefinementMatcherName { get; set; }
        public double RefinementRawScore { get; set; }
        public string SelectedMatcherName { get; set; }
        public double SelectedRawScore { get; set; }
        public double SelectedCommonScore { get; set; }
        public string SelectionReason { get; set; }
        public GerberViewer.Stitching.Alignment.Evaluation.DirectCandidateEvaluation CandidateEvaluation { get; set; }
        public string PipelineStage { get; set; }
        public string PreprocessingVariant { get; set; }
        public string NccFailureReason { get; set; }
        public string EccFailureReason { get; set; }
        public string FallbackReason { get; set; }
        public bool Manual { get; set; }
        public bool Excluded { get; set; }
        public string RejectionReason { get; set; }
        public string SampleContentClass { get; set; }
        public double SampleMin { get; set; }
        public double SampleMax { get; set; }
        public double SampleMean { get; set; }
        public double SampleStdDev { get; set; }
        public double NonZeroPixelRatio { get; set; }
        public bool AlignmentSucceeded { get; set; }
        public bool IsFallbackPose { get; set; }
        public bool PoseCorrected { get; set; }
        public bool IsStitchable { get; set; }
        public double LocalDx { get; set; }
        public double LocalDy { get; set; }
        public double LocalAngle { get; set; }
        public double GlobalX { get; set; }
        public double GlobalY { get; set; }
        public double GlobalAngle { get; set; }
        public double[,] OriginalGlobalPose { get; set; }
        public double[,] AdjustedGlobalPose { get; set; }
        public double AdjustmentTranslationResidual { get; set; }
        public double AdjustmentRotationResidualDeg { get; set; }
        public string AdjustmentReason { get; set; }
        public GlobalPoseTileDiagnostic GlobalPoseDiagnostic { get; set; }
        public static ProcessingTileReport From(CapturedImageInfo c, GerberViewer.Stitching.Alignment.SampleAlignmentResult r, string fallback)
        {
            return new ProcessingTileReport
            { 
                OrderIndex = c.OrderIndex, 
                Row = c.Row, 
                Column = c.Column, 
                NccScore = r == null ? double.NaN : r.NccScore, 
                EccCorrelation = r == null ? double.NaN : r.EccCorrelation, 
                CoarseMatcherName = r == null ? null : r.CoarseMatcherName, CoarseRawScore = r == null ? double.NaN : r.CoarseRawScore, CoarseNormalizedConfidence = r == null ? double.NaN : r.CoarseNormalizedConfidence, RefinementMatcherName = r == null ? null : r.RefinementMatcherName, RefinementRawScore = r == null ? double.NaN : r.RefinementRawScore,
                SelectedMatcherName = r == null ? null : r.SelectedMatcherName, SelectedRawScore = r == null ? double.NaN : r.SelectedRawScore, SelectedCommonScore = r == null ? double.NaN : r.SelectedCommonScore, SelectionReason = r == null ? null : r.SelectionReason,
                CandidateEvaluation = r == null ? null : r.CandidateEvaluation,
                PipelineStage = r == null ? null : r.PipelineStage, 
                PreprocessingVariant = r == null ? null : r.PreprocessingVariant, 
                NccFailureReason = r == null ? null : r.NccFailureReason, 
                EccFailureReason = r == null ? null : r.EccFailureReason, 
                FallbackReason = fallback, 
                RejectionReason = r == null ? null : r.RejectionReason 
            };
        }
    }

    public sealed class GlobalPoseValidationReport
    {
        public bool IsValid { get; set; }
        public string TransformContract { get; set; }
        public double MedianRotationDeg { get; set; }
        public double CanvasLeft { get; set; }
        public double CanvasTop { get; set; }
        public double CanvasRight { get; set; }
        public double CanvasBottom { get; set; }
        public double CanvasWidthRatio { get; set; }
        public double CanvasHeightRatio { get; set; }
        public IList<string> Errors { get; set; } = new List<string>();
        public IList<string> Warnings { get; set; } = new List<string>();
        public IList<GlobalPoseTileDiagnostic> Tiles { get; set; } = new List<GlobalPoseTileDiagnostic>();
    }

    public sealed class GlobalPoseTileDiagnostic
    {
        public int OrderIndex { get; set; }
        public double[,] Matrix { get; set; }
        public double TranslationX { get; set; }
        public double TranslationY { get; set; }
        public double RotationDeg { get; set; }
        public double ScaleX { get; set; }
        public double ScaleY { get; set; }
        public double Determinant { get; set; }
        public double OrthogonalityResidual { get; set; }
        public double[][] TransformedCorners { get; set; }
        public double BoundsLeft { get; set; }
        public double BoundsTop { get; set; }
        public double BoundsRight { get; set; }
        public double BoundsBottom { get; set; }
        public double ExpectedDeltaX { get; set; }
        public double ExpectedDeltaY { get; set; }
        public PoseSource PoseSource { get; set; }
        public string RecoveryPath { get; set; }
        public IList<string> Violations { get; set; } = new List<string>();
    }

    public partial class ProcessingReportExtensions { }
}
