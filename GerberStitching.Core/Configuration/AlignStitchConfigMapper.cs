using System;
using System.Collections.Generic;
using System.Globalization;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using System.IO;
using GerberViewer.Stitching.Alignment.Evaluation;

namespace GerberViewer.Stitching.Configuration
{
    // [Tony] [Change time: 2026-07-27] [Purpose: Centralize configured-to-effective runtime mapping and reporting.]
    public static class AlignStitchConfigMapper
    {
        public static void EnsureComposite(AlignStitchConfig c)
        {
            if (c == null)
                throw new ArgumentNullException("c");
            c.Input = c.Input ?? new InputPathOptions();
            c.DirectAlignment = c.DirectAlignment ?? new DirectAlignmentOptions();
            c.NeighborAlignment = c.NeighborAlignment ?? new NeighborAlignmentOptions();
            c.DirectAlignment.Preprocessing = c.DirectAlignment.Preprocessing ?? new AlignmentPreprocessingOptions();
            c.DirectAlignment.Evaluation = c.DirectAlignment.Evaluation ?? new DirectEvaluationOptions();
            c.Recovery = c.Recovery ?? new RecoveryFallbackOptions();
            c.PoseGraph = c.PoseGraph ?? new GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions();
            c.Stitching = c.Stitching ?? new StitchingOptions();
            c.Output = c.Output ?? new OutputOptions();
            // Preserve offsets saved by ConfigVersion 2 before they moved from Stitching to DirectAlignment.
            if (c.DirectAlignment.ManualOffsetX == 0d && c.DirectAlignment.ManualOffsetY == 0d &&
                (c.Stitching.ManualOffsetX != 0d || c.Stitching.ManualOffsetY != 0d))
            {
                c.DirectAlignment.ManualOffsetX = c.Stitching.ManualOffsetX;
                c.DirectAlignment.ManualOffsetY = c.Stitching.ManualOffsetY;
            }
            // Version 3 splits the old ambiguous switch. Version 2 always reconciled the
            // full graph, while EnableNeighborRecovery controlled failed-tile recovery.
            if (c.ConfigVersion == 2)
            {
                c.Recovery.RecoverFailedTiles = c.Recovery.EnableNeighborRecovery;
                c.Recovery.ReconcileAllMatchedTiles = true;
                c.ConfigVersion = 3;
                return;
            }
            // Version 0/1 is the explicit migration path for serialized flat configurations.
            if (c.ConfigVersion >= 3)
                return;
            c.Input.ManifestPath = c.InputManifestPath;
            c.Input.CapturedFolderPath = c.CapturedFolderPath;
            c.Output.OutputPath = c.OutputPath;
            c.DirectAlignment.EnableDirectPoseOutlierCorrection = c.EnableDirectPoseOutlierCorrection;
            c.DirectAlignment.RotationOutlierMadK = c.RotationOutlierMadK;
            c.DirectAlignment.AngleBandFloorDeg = c.AngleBandFloorDeg;
            c.DirectAlignment.SignFlipGuardDeg = c.SignFlipGuardDeg;
            c.DirectAlignment.Ncc.MinScore = c.NccMinScore;
            c.DirectAlignment.Ecc.MinCorrelation = c.EccMinCorrelation;
            c.DirectAlignment.Geometry.MaxTranslationPixels = c.MaxTranslationPixels;
            c.DirectAlignment.Geometry.MaxAbsRotationDeg = c.MaxAbsRotationDeg;
            c.DirectAlignment.Geometry.MinScale = c.MinScale;
            c.DirectAlignment.Geometry.MaxScale = c.MaxScale;
            c.DirectAlignment.Geometry.MinOverlapRatio = c.MinOverlapRatio;
            c.DirectAlignment.Policy.AllowCoarseOnlyAcceptance = c.AllowNccOnlyAcceptance;
            c.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails = c.AllowEccFromExpectedWhenNccFails;
            c.Recovery.RecoverFailedTiles = c.EnableNeighborRecovery;
            c.Recovery.ReconcileAllMatchedTiles = true;
            c.Recovery.EnableNeighborRecovery = c.EnableNeighborRecovery;
            c.Recovery.EnableAnchorInterpolation = c.EnableAnchorInterpolation;
            c.Recovery.AutoPlaceExactZeroSample = c.AutoPlaceExactZeroSample;
            c.Recovery.AutoPlaceLowTextureSample = c.AutoPlaceLowTextureSample;
            c.Recovery.AllowExpectedGridFallback = c.AllowExpectedGridFallback;
            c.Recovery.RequireManualConfirmationForExpectedGrid = c.RequireManualConfirmationForExpectedGrid;
            c.Stitching.Engine = c.StitchingEngine;
            c.Stitching.AllowUnsafeLegacyHalconProjectiveMosaic = c.AllowUnsafeLegacyHalconProjectiveMosaic;
            c.DirectAlignment.ManualOffsetX = c.ManualOffsetX;
            c.DirectAlignment.ManualOffsetY = c.ManualOffsetY;
            c.Stitching.BlankFallbackOverlapPolicy = c.BlankFallbackOverlapPolicy;
            c.Output.TiffMode = c.TiffMode;
            c.Output.BigTiffTileWidth = c.BigTiffTileWidth;
            c.Output.BigTiffTileHeight = c.BigTiffTileHeight;
            c.Output.PreviewUpdateInterval = c.PreviewUpdateInterval;
            c.Output.MaxPreviewMegapixels = c.MaxPreviewMegapixels;
            c.ConfigVersion = 3;
        }

        public static MatcherOptions ToDirectMatcherOptions(AlignStitchConfig c)
        {
            EnsureComposite(c);
            var d = c.DirectAlignment;
            return Map(d.Geometry, d.Ecc, d.Ncc, null);
        }
        public static MatcherOptions ToNeighborMatcherOptions(AlignStitchConfig c)
        {
            EnsureComposite(c);
            var n = c.NeighborAlignment;
            var g = new CommonGeometryOptions { MinOverlapRatio = n.Acceptance.MinOverlapRatio,
                                                MaxTranslationPixels = n.Acceptance.MaxTranslationPixels,
                                                MaxAbsRotationDeg = n.Acceptance.MaxAbsRotationDeg,
                                                MinScale = n.Acceptance.MinScale, MaxScale = n.Acceptance.MaxScale };
            return Map(g, n.Ecc, null, n.Phase);
        }
        public static MatcherKind? ToDirectCoarseMatcherKind(AlignStitchConfig c)
        {
            EnsureComposite(c);
            switch (c.DirectAlignment.CoarseMatcher)
            {
            case DirectCoarseMatcherKind.HalconNcc:
                return MatcherKind.HalconNcc;
            case DirectCoarseMatcherKind.HalconShapeModel:
                return MatcherKind.HalconShapeModel;
            case DirectCoarseMatcherKind.PyramidEcc:
                return MatcherKind.PyramidEcc;
            case DirectCoarseMatcherKind.PyramidPhaseCorrelation:
                return MatcherKind.PyramidPhaseCorrelation;
            case DirectCoarseMatcherKind.None:
                return null;
            default:
                throw new NotSupportedException(
                    "Unsupported direct coarse matcher: " + c.DirectAlignment.CoarseMatcher + ".");
            }
        }
        public static MatcherKind? ToDirectRefinementMatcherKind(AlignStitchConfig c)
        {
            EnsureComposite(c);
            switch (c.DirectAlignment.RefinementMatcher)
            {
            case DirectRefinementMatcherKind.PyramidEcc:
                return MatcherKind.PyramidEcc;
            case DirectRefinementMatcherKind.PyramidPhaseCorrelation:
                return MatcherKind.PyramidPhaseCorrelation;
            case DirectRefinementMatcherKind.None:
                return null;
            default:
                throw new NotSupportedException(
                    "Unsupported direct refinement matcher: " + c.DirectAlignment.RefinementMatcher + ".");
            }
        }
        public static MatcherKind? ToNeighborCoarseMatcherKind(AlignStitchConfig c)
        {
            EnsureComposite(c);
            return c.NeighborAlignment.CoarseMatcher == NeighborCoarseMatcherKind.None
                ? (MatcherKind ?) null: MatcherKind.PyramidPhaseCorrelation;
        }
        [Obsolete("Neighbor production is phase-only; legacy refinement config is ignored.")]
        public static MatcherKind? ToNeighborRefinementMatcherKind(AlignStitchConfig c)
        {
            EnsureComposite(c);
            return null;
        }
        public static StitchFromGlobalTransformsOptions ToStitchingOptions(AlignStitchConfig c)
        {
            EnsureComposite(c);
            return new StitchFromGlobalTransformsOptions {
                OutputPath = string.IsNullOrWhiteSpace(c.Output.OutputPath)
                                 ? null
                                 : Path.Combine(c.Output.OutputPath, "Stitched.tiff"),
                PreviewUpdateInterval = c.Output.PreviewUpdateInterval,
                MaxPreviewMegapixels = c.Output.MaxPreviewMegapixels,
                TiffMode = c.Output.TiffMode,
                BigTiffTileWidth = c.Output.BigTiffTileWidth,
                BigTiffTileHeight = c.Output.BigTiffTileHeight,
                StitchingEngine = c.Stitching.Engine,
                EnableBlending = c.Stitching.EnableBlending,
                BlendMode = c.Stitching.BlendMode,
                ForceGray8Output = c.Stitching.ForceGray8Output,
                BlankFallbackOverlapPolicy = c.Stitching.BlankFallbackOverlapPolicy,
                AllowUnsafeLegacyHalconProjectiveMosaic = c.Stitching.AllowUnsafeLegacyHalconProjectiveMosaic
            };
        }
        private static MatcherOptions Map(CommonGeometryOptions g, EccOptions e, HalconNccOptions n,
                                          PhaseCorrelationOptions p)
        {
            var o = new MatcherOptions { MinTextureStdDev = g.MinTextureStdDev,
                                         MinOverlapRatio = g.MinOverlapRatio,
                                         MaxTranslationPixels = g.MaxTranslationPixels,
                                         MaxAbsRotationDeg = g.MaxAbsRotationDeg,
                                         MinScale = g.MinScale,
                                         MaxScale = g.MaxScale,
                                         EccMotionModel = e.MotionModel,
                                         PyramidLevels = e.PyramidLevels,
                                         MaxIterations = e.MaxIterations,
                                         Epsilon = e.Epsilon,
                                         MinCorrelation = e.MinCorrelation };
            if (p != null)
            {
                o.PhaseMinResponse = p.MinResponse;
                o.PyramidLevels = p.PyramidLevels;
            }
            if (n != null)
            {
                o.NccMinScore = n.MinScore;
                o.NccNumLevels = n.NumLevels;
                o.NccAngleStartRad = n.AngleStartRad;
                o.NccAngleExtentRad = n.AngleExtentRad;
                o.NccAngleStepRad = n.AngleStepRad;
                o.NccMetric = n.Metric;
                o.NccMaxMatches = n.MaxMatches;
                o.NccMaxOverlap = n.MaxOverlap;
                o.NccSubPixel = n.SubPixel;
                o.NccModelRoiMarginPixels = n.ModelRoiMarginPixels;
            }
            return o;
        }

        public static AlignStitchConfig CloneForRun(AlignStitchConfig source, string outputPath)
        {
            EnsureComposite(source);
            var c = new AlignStitchConfig();
            c.Input = new InputPathOptions { ManifestPath = source.Input.ManifestPath,
                                             CapturedFolderPath = source.Input.CapturedFolderPath };
            c.DirectAlignment = Clone(source.DirectAlignment);
            c.NeighborAlignment = Clone(source.NeighborAlignment);
            c.PoseGraph = Clone(source.PoseGraph);
            c.Recovery =
                new RecoveryFallbackOptions { RecoverFailedTiles = source.Recovery.RecoverFailedTiles,
                                              ReconcileAllMatchedTiles = source.Recovery.ReconcileAllMatchedTiles,
                                              EnableNeighborRecovery = source.Recovery.RecoverFailedTiles,
                                              EnableAnchorInterpolation = source.Recovery.EnableAnchorInterpolation,
                                              EnableManualAlignment = source.Recovery.EnableManualAlignment,
                                              AutoPlaceExactZeroSample = source.Recovery.AutoPlaceExactZeroSample,
                                              AutoPlaceLowTextureSample = source.Recovery.AutoPlaceLowTextureSample,
                                              AllowExpectedGridFallback = source.Recovery.AllowExpectedGridFallback,
                                              RequireManualConfirmationForExpectedGrid =
                                                  source.Recovery.RequireManualConfirmationForExpectedGrid,
                                              RevalidateSampleContent = source.Recovery.RevalidateSampleContent,
                                              LowTextureStdDevThreshold = source.Recovery.LowTextureStdDevThreshold };
            c.Stitching =
                new StitchingOptions { Engine = source.Stitching.Engine,
                                       AllowUnsafeLegacyHalconProjectiveMosaic =
                                           source.Stitching.AllowUnsafeLegacyHalconProjectiveMosaic,
                                       EnableBlending = source.Stitching.EnableBlending,
                                       BlendMode = source.Stitching.BlendMode,
                                       BlankFallbackOverlapPolicy = source.Stitching.BlankFallbackOverlapPolicy,
                                       ForceGray8Output = source.Stitching.ForceGray8Output };
            c.Output = new OutputOptions { OutputPath = outputPath,
                                           TiffMode = source.Output.TiffMode,
                                           BigTiffTileWidth = source.Output.BigTiffTileWidth,
                                           BigTiffTileHeight = source.Output.BigTiffTileHeight,
                                           PreviewUpdateInterval = source.Output.PreviewUpdateInterval,
                                           MaxPreviewMegapixels = source.Output.MaxPreviewMegapixels };
            c.ConfigVersion = 3;
            // [Tony] [Change time: 2026-08-11] [Purpose: CloneForRun clones field-by-field, not by reflection;
            // EmitDebugPreview must be copied explicitly or it silently resets to its false default on every run.]
            c.EmitDebugPreview = source.EmitDebugPreview;
            c.CalculateTimeDetail = source.CalculateTimeDetail;
            SyncLegacy(c);
            return c;
        }
        private static DirectAlignmentOptions Clone(DirectAlignmentOptions d)
        {
            return new DirectAlignmentOptions {
                EnableDirectPoseOutlierCorrection = d.EnableDirectPoseOutlierCorrection,
                RotationOutlierMadK = d.RotationOutlierMadK,
                AngleBandFloorDeg = d.AngleBandFloorDeg,
                SignFlipGuardDeg = d.SignFlipGuardDeg,
                CoarseMatcher = d.CoarseMatcher,
                RefinementMatcher = d.RefinementMatcher,
                Preprocessing = new AlignmentPreprocessingOptions { Contrast = d.Preprocessing.Contrast },
                ManualOffsetX = d.ManualOffsetX,
                ManualOffsetY = d.ManualOffsetY,
                Geometry = new CommonGeometryOptions { MinTextureStdDev = d.Geometry.MinTextureStdDev,
                                                       MinOverlapRatio = d.Geometry.MinOverlapRatio,
                                                       MaxTranslationPixels = d.Geometry.MaxTranslationPixels,
                                                       MaxAbsRotationDeg = d.Geometry.MaxAbsRotationDeg,
                                                       MinScale = d.Geometry.MinScale, MaxScale = d.Geometry.MaxScale },
                Ncc = new HalconNccOptions { MinScore = d.Ncc.MinScore, NumLevels = d.Ncc.NumLevels,
                                             AngleStartRad = d.Ncc.AngleStartRad, AngleExtentRad = d.Ncc.AngleExtentRad,
                                             AngleStepRad = d.Ncc.AngleStepRad, Metric = d.Ncc.Metric,
                                             MaxMatches = d.Ncc.MaxMatches, MaxOverlap = d.Ncc.MaxOverlap,
                                             SubPixel = d.Ncc.SubPixel,
                                             ModelRoiMarginPixels = d.Ncc.ModelRoiMarginPixels },
                Shape = Clone(d.Shape),
                Ecc = Clone(d.Ecc),
                Policy = new DirectPipelinePolicy { AllowCoarseOnlyAcceptance = d.Policy.AllowCoarseOnlyAcceptance,
                                                    AllowRefinementFromExpectedWhenCoarseFails =
                                                        d.Policy.AllowRefinementFromExpectedWhenCoarseFails },
                Evaluation = Clone(d.Evaluation)
            };
        }
        private static DirectEvaluationOptions Clone(DirectEvaluationOptions value)
        {
            value = value ?? new DirectEvaluationOptions();
            return new DirectEvaluationOptions { ReportOnlyEnabled = value.ReportOnlyEnabled,
                                                 SelectionPolicy = value.SelectionPolicy,
                                                 MinRefinementGain = value.MinRefinementGain,
                                                 MaxRefinementDeltaTranslationPixels =
                                                     value.MaxRefinementDeltaTranslationPixels,
                                                 MaxRefinementDeltaRotationDeg = value.MaxRefinementDeltaRotationDeg,
                                                 Tier1MaximumSide = value.Tier1MaximumSide,
                                                 Tier2MaximumSide = value.Tier2MaximumSide,
                                                 AmbiguousScoreGap = value.AmbiguousScoreGap,
                                                 EdgeWeight = value.EdgeWeight,
                                                 GradientNccWeight = value.GradientNccWeight,
                                                 ValidOverlapWeight = value.ValidOverlapWeight,
                                                 BorderPenaltyWeight = value.BorderPenaltyWeight,
                                                 EdgeDistanceTolerancePixels = value.EdgeDistanceTolerancePixels,
                                                 MinimumGradientStdDev = value.MinimumGradientStdDev };
        }
        private static HalconShapeModelOptions Clone(HalconShapeModelOptions s)
        {
            return new HalconShapeModelOptions { Mode = s.Mode,
                                                 NumLevels = s.NumLevels,
                                                 AngleStartRad = s.AngleStartRad,
                                                 AngleExtentRad = s.AngleExtentRad,
                                                 AngleStepRad = s.AngleStepRad,
                                                 Optimization = s.Optimization,
                                                 Metric = s.Metric,
                                                 Contrast = s.Contrast,
                                                 MinContrast = s.MinContrast,
                                                 ScaleMin = s.ScaleMin,
                                                 ScaleMax = s.ScaleMax,
                                                 ScaleStep = s.ScaleStep,
                                                 MinScore = s.MinScore,
                                                 NumMatches = s.NumMatches,
                                                 MaxOverlap = s.MaxOverlap,
                                                 SubPixel = s.SubPixel,
                                                 SearchNumLevels = s.SearchNumLevels,
                                                 Greediness = s.Greediness,
                                                 ModelRoiMarginPixels = s.ModelRoiMarginPixels,
                                                 MinModelRoiWidth = s.MinModelRoiWidth,
                                                 MinModelRoiHeight = s.MinModelRoiHeight };
        }
        private static NeighborAlignmentOptions Clone(NeighborAlignmentOptions n)
        {
            return new NeighborAlignmentOptions {
                CoarseMatcher = n.CoarseMatcher,
                RefinementMatcher = n.RefinementMatcher,
                MaxDirectPoseAdjustmentPixels = n.MaxDirectPoseAdjustmentPixels,
                MaxDirectPoseAdjustmentRotationDeg = n.MaxDirectPoseAdjustmentRotationDeg,
                MaxCycleClosureErrorPixels = n.MaxCycleClosureErrorPixels,
                Phase = new PhaseCorrelationOptions { MinResponse = n.Phase.MinResponse,
                                                      PyramidLevels = n.Phase.PyramidLevels },
                Ecc = Clone(n.Ecc),
                Acceptance =
                    new NeighborAcceptanceOptions { MinOverlapRatio = n.Acceptance.MinOverlapRatio,
                                                    MaxTranslationPixels = n.Acceptance.MaxTranslationPixels,
                                                    MaxAbsRotationDeg = n.Acceptance.MaxAbsRotationDeg,
                                                    MinScale = n.Acceptance.MinScale, MaxScale = n.Acceptance.MaxScale }
            };
        }
        private static GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions Clone(
            GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions p)
        {
            p = p ?? new GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions();
            return new GerberViewer.Stitching.Alignment.Graph.PoseGraphOptions {
                Enabled = p.Enabled,
                LambdaDirect = p.LambdaDirect,
                LambdaWeak = p.LambdaWeak,
                LambdaBlank = p.LambdaBlank,
                LambdaPinned = p.LambdaPinned,
                RotationScalePixels = p.RotationScalePixels,
                HuberPixels = p.HuberPixels,
                MaxIterations = p.MaxIterations,
                MaxCgIterations = p.MaxCgIterations,
                CgTolerance = p.CgTolerance,
                EstimateGlobalScale = p.EstimateGlobalScale,
                MinGlobalScale = p.MinGlobalScale,
                MaxGlobalScale = p.MaxGlobalScale,
                MaxGlobalRotationDeg = p.MaxGlobalRotationDeg,
                UseRejectedEdges = p.UseRejectedEdges,
                MaxEdgeResidualPixels = p.MaxEdgeResidualPixels,
                MaxEdgeRotationDeg = p.MaxEdgeRotationDeg,
                MaxPoseCorrectionPixels = p.MaxPoseCorrectionPixels
            };
        }
        private static EccOptions Clone(EccOptions e)
        {
            return new EccOptions { MotionModel = e.MotionModel, PyramidLevels = e.PyramidLevels,
                                    MaxIterations = e.MaxIterations, Epsilon = e.Epsilon,
                                    MinCorrelation = e.MinCorrelation };
        }
        public static void SyncLegacy(AlignStitchConfig c)
        {
            c.InputManifestPath = c.Input.ManifestPath;
            c.CapturedFolderPath = c.Input.CapturedFolderPath;
            c.OutputPath = c.Output.OutputPath;
            c.EnableDirectPoseOutlierCorrection = c.DirectAlignment.EnableDirectPoseOutlierCorrection;
            c.RotationOutlierMadK = c.DirectAlignment.RotationOutlierMadK;
            c.AngleBandFloorDeg = c.DirectAlignment.AngleBandFloorDeg;
            c.SignFlipGuardDeg = c.DirectAlignment.SignFlipGuardDeg;
            c.NccMinScore = c.DirectAlignment.Ncc.MinScore;
            c.EccMinCorrelation = c.DirectAlignment.Ecc.MinCorrelation;
            c.MaxTranslationPixels = c.DirectAlignment.Geometry.MaxTranslationPixels;
            c.MaxAbsRotationDeg = c.DirectAlignment.Geometry.MaxAbsRotationDeg;
            c.MinScale = c.DirectAlignment.Geometry.MinScale;
            c.MaxScale = c.DirectAlignment.Geometry.MaxScale;
            c.MinOverlapRatio = c.DirectAlignment.Geometry.MinOverlapRatio;
            c.AllowNccOnlyAcceptance = c.DirectAlignment.Policy.AllowCoarseOnlyAcceptance;
            c.AllowEccFromExpectedWhenNccFails = c.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails;
            c.Recovery.EnableNeighborRecovery = c.Recovery.RecoverFailedTiles;
            c.EnableNeighborRecovery = c.Recovery.RecoverFailedTiles;
            c.EnableAnchorInterpolation = c.Recovery.EnableAnchorInterpolation;
            c.AutoPlaceExactZeroSample = c.Recovery.AutoPlaceExactZeroSample;
            c.AutoPlaceLowTextureSample = c.Recovery.AutoPlaceLowTextureSample;
            c.StitchingEngine = c.Stitching.Engine;
            c.AllowUnsafeLegacyHalconProjectiveMosaic = c.Stitching.AllowUnsafeLegacyHalconProjectiveMosaic;
            c.ManualOffsetX = c.DirectAlignment.ManualOffsetX;
            c.ManualOffsetY = c.DirectAlignment.ManualOffsetY;
            c.Stitching.ManualOffsetX = c.DirectAlignment.ManualOffsetX;
            c.Stitching.ManualOffsetY = c.DirectAlignment.ManualOffsetY;
            c.BlankFallbackOverlapPolicy = c.Stitching.BlankFallbackOverlapPolicy;
            c.TiffMode = c.Output.TiffMode;
            c.BigTiffTileWidth = c.Output.BigTiffTileWidth;
            c.BigTiffTileHeight = c.Output.BigTiffTileHeight;
            c.PreviewUpdateInterval = c.Output.PreviewUpdateInterval;
            c.MaxPreviewMegapixels = c.Output.MaxPreviewMegapixels;
        }

        public static EffectiveConfigSnapshot CreateSnapshot(AlignStitchConfig c)
        {
            EnsureComposite(c);
            var list = new List<EffectiveConfigEntry>();
            var d = ToDirectMatcherOptions(c);
            var n = ToNeighborMatcherOptions(c);
            var s = ToStitchingOptions(c);
            Add(list, "Direct", "Pose", "ManualOffsetX", c.DirectAlignment.ManualOffsetX,
                c.DirectAlignment.ManualOffsetX, "User config", "AlignStitchWorkflowService");
            Add(list, "Direct", "Pose", "ManualOffsetY", c.DirectAlignment.ManualOffsetY,
                c.DirectAlignment.ManualOffsetY, "User config", "AlignStitchWorkflowService");
            Add(list, "Direct", "Pipeline", "CoarseMatcher", c.DirectAlignment.CoarseMatcher,
                directMatcher(c.DirectAlignment.CoarseMatcher), "User config", "Direct matcher pipeline");
            Add(list, "Direct", "Pipeline", "RefinementMatcher", c.DirectAlignment.RefinementMatcher,
                c.DirectAlignment.RefinementMatcher, "User config", "Direct matcher pipeline");
            Add(list, "Direct", "NCC", "MinScore", c.DirectAlignment.Ncc.MinScore, d.NccMinScore, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "ECC", "MaxIterations", c.DirectAlignment.Ecc.MaxIterations, d.MaxIterations,
                "User config", "PyramidEccMatcher");
            Add(list, "Direct", "ECC", "Epsilon", c.DirectAlignment.Ecc.Epsilon, d.Epsilon, "User config",
                "PyramidEccMatcher");
            Add(list, "Direct", "NCC", "NumLevels", c.DirectAlignment.Ncc.NumLevels, d.NccNumLevels, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "NCC", "AngleStartRad", c.DirectAlignment.Ncc.AngleStartRad, d.NccAngleStartRad,
                "User config", "HalconNccMatcher");
            Add(list, "Direct", "NCC", "AngleExtentRad", c.DirectAlignment.Ncc.AngleExtentRad, d.NccAngleExtentRad,
                "User config", "HalconNccMatcher");
            Add(list, "Direct", "NCC", "AngleStepRad", c.DirectAlignment.Ncc.AngleStepRad, d.NccAngleStepRad,
                "User config", "HalconNccMatcher");
            Add(list, "Direct", "NCC", "Metric", c.DirectAlignment.Ncc.Metric, d.NccMetric, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "NCC", "MaxMatches", c.DirectAlignment.Ncc.MaxMatches, d.NccMaxMatches, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "NCC", "MaxOverlap", c.DirectAlignment.Ncc.MaxOverlap, d.NccMaxOverlap, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "NCC", "SubPixel", c.DirectAlignment.Ncc.SubPixel, d.NccSubPixel, "User config",
                "HalconNccMatcher");
            Add(list, "Direct", "NCC", "ModelRoiMarginPixels", c.DirectAlignment.Ncc.ModelRoiMarginPixels,
                d.NccModelRoiMarginPixels, "User config", "HalconNccModelProvider");
            Add(list, "Direct", "Shape", "MinScore", c.DirectAlignment.Shape.MinScore, c.DirectAlignment.Shape.MinScore,
                "User config", "HalconShapeModelMatcher");
            Add(list, "Direct", "Shape", "Greediness", c.DirectAlignment.Shape.Greediness,
                c.DirectAlignment.Shape.Greediness, "User config", "HalconShapeModelMatcher");
            Add(list, "Direct", "Shape", "Mode", c.DirectAlignment.Shape.Mode, c.DirectAlignment.Shape.Mode,
                "User config", "HalconShapeModelMatcher");
            Add(list, "Direct", "Shape", "NumLevels", c.DirectAlignment.Shape.NumLevels,
                c.DirectAlignment.Shape.NumLevels, "User config", "HalconShapeModelProvider");
            Add(list, "Direct", "Shape", "Contrast", c.DirectAlignment.Shape.Contrast, c.DirectAlignment.Shape.Contrast,
                "User config", "HalconShapeModelProvider");
            Add(list, "Direct", "Shape", "MinContrast", c.DirectAlignment.Shape.MinContrast,
                c.DirectAlignment.Shape.MinContrast, "User config", "HalconShapeModelProvider");
            Add(list, "Direct", "ECC", "MotionModel", c.DirectAlignment.Ecc.MotionModel, d.EccMotionModel,
                "User config", "PyramidEccMatcher");
            Add(list, "Direct", "ECC", "PyramidLevels", c.DirectAlignment.Ecc.PyramidLevels, d.PyramidLevels,
                "User config", "PyramidEccMatcher");
            Add(list, "Direct", "ECC", "MinCorrelation", c.DirectAlignment.Ecc.MinCorrelation, d.MinCorrelation,
                "User config", "PyramidEccMatcher");
            Add(list, "Direct", "Policy", "AllowCoarseOnlyAcceptance",
                c.DirectAlignment.Policy.AllowCoarseOnlyAcceptance, c.DirectAlignment.Policy.AllowCoarseOnlyAcceptance,
                "User config", "Direct matcher pipeline");
            Add(list, "Direct", "Policy", "AllowRefinementFromExpectedWhenCoarseFails",
                c.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails,
                c.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails, "User config",
                "Direct matcher pipeline");
            Add(list, "Neighbor", "Phase", "MinResponse", c.NeighborAlignment.Phase.MinResponse, n.PhaseMinResponse,
                "User config", "PyramidPhaseCorrelationMatcher");
            Add(list, "Neighbor", "ECC", "MinCorrelation", c.NeighborAlignment.Ecc.MinCorrelation, n.MinCorrelation,
                "User config", "PyramidEccMatcher");
            Add(list, "Neighbor", "Pipeline", "CoarseMatcher", c.NeighborAlignment.CoarseMatcher,
                c.NeighborAlignment.CoarseMatcher, "User config", "Neighbor recovery pipeline");
            Add(list, "Neighbor", "Pipeline", "RefinementMatcher", c.NeighborAlignment.RefinementMatcher,
                c.NeighborAlignment.RefinementMatcher, "User config", "Neighbor recovery pipeline");
            Add(list, "Neighbor", "Phase", "PyramidLevels", c.NeighborAlignment.Phase.PyramidLevels,
                c.NeighborAlignment.Phase.PyramidLevels, "User config", "PyramidPhaseCorrelationMatcher");
            Add(list, "Neighbor", "ECC", "MotionModel", c.NeighborAlignment.Ecc.MotionModel, n.EccMotionModel,
                "User config", "PyramidEccMatcher");
            Add(list, "Neighbor", "ECC", "PyramidLevels", c.NeighborAlignment.Ecc.PyramidLevels, n.PyramidLevels,
                "User config", "PyramidEccMatcher");
            Add(list, "Neighbor", "ECC", "MaxIterations", c.NeighborAlignment.Ecc.MaxIterations, n.MaxIterations,
                "User config", "PyramidEccMatcher");
            Add(list, "Neighbor", "ECC", "Epsilon", c.NeighborAlignment.Ecc.Epsilon, n.Epsilon, "User config",
                "PyramidEccMatcher");
            Add(list, "Input", "Paths", "ManifestPath", c.Input.ManifestPath, c.Input.ManifestPath,
                "User/workflow context", "Workflow mapping");
            Add(list, "Input", "Paths", "CapturedFolderPath", c.Input.CapturedFolderPath, c.Input.CapturedFolderPath,
                "User/workflow context", "CapturedImageLoader");
            Add(list, "Direct", "Preprocessing", "Contrast", c.DirectAlignment.Preprocessing.Contrast,
                c.DirectAlignment.Preprocessing.Contrast, "User config", "OpenCV/HALCON input preprocessing");
            Add(list, "Direct", "Geometry", "MinTextureStdDev", c.DirectAlignment.Geometry.MinTextureStdDev,
                d.MinTextureStdDev, "User config", "MatcherGeometryValidator");
            Add(list, "Direct", "Geometry", "MinOverlapRatio", c.DirectAlignment.Geometry.MinOverlapRatio,
                d.MinOverlapRatio, "User config", "MatcherGeometryValidator");
            Add(list, "Direct", "Geometry", "MaxTranslationPixels", c.DirectAlignment.Geometry.MaxTranslationPixels,
                d.MaxTranslationPixels, "User config", "MatcherGeometryValidator");
            Add(list, "Direct", "Geometry", "MaxAbsRotationDeg", c.DirectAlignment.Geometry.MaxAbsRotationDeg,
                d.MaxAbsRotationDeg, "User config", "MatcherGeometryValidator");
            Add(list, "Direct", "Geometry", "MinScale", c.DirectAlignment.Geometry.MinScale, d.MinScale, "User config",
                "MatcherGeometryValidator");
            Add(list, "Direct", "Geometry", "MaxScale", c.DirectAlignment.Geometry.MaxScale, d.MaxScale, "User config",
                "MatcherGeometryValidator");
            Add(list, "Neighbor", "Acceptance", "MinOverlapRatio", c.NeighborAlignment.Acceptance.MinOverlapRatio,
                n.MinOverlapRatio, "User config", "NeighborMatchAcceptance");
            Add(list, "Neighbor", "Acceptance", "MaxTranslationPixels",
                c.NeighborAlignment.Acceptance.MaxTranslationPixels, n.MaxTranslationPixels, "User config",
                "NeighborMatchAcceptance");
            Add(list, "Neighbor", "Acceptance", "MaxAbsRotationDeg", c.NeighborAlignment.Acceptance.MaxAbsRotationDeg,
                n.MaxAbsRotationDeg, "User config", "NeighborMatchAcceptance");
            Add(list, "Neighbor", "Acceptance", "MinScale", c.NeighborAlignment.Acceptance.MinScale, n.MinScale,
                "User config", "NeighborMatchAcceptance");
            Add(list, "Neighbor", "Acceptance", "MaxScale", c.NeighborAlignment.Acceptance.MaxScale, n.MaxScale,
                "User config", "NeighborMatchAcceptance");
            Add(list, "Recovery", "Fallback", "RecoverFailedTiles", c.Recovery.RecoverFailedTiles,
                c.Recovery.RecoverFailedTiles, "User config", "AlignStitchWorkflowService.Recover");
            Add(list, "Recovery", "Neighbor graph modes", "ReconcileAllMatchedTiles",
                c.Recovery.ReconcileAllMatchedTiles, c.Recovery.ReconcileAllMatchedTiles, "User config",
                "AlignStitchWorkflowService.ReconcileNeighborGraph");
            Add(list, "Recovery", "Fallback", "EnableAnchorInterpolation", c.Recovery.EnableAnchorInterpolation,
                c.Recovery.EnableAnchorInterpolation, "User config", "AlignmentPoseInterpolator");
            Add(list, "Recovery", "Fallback", "EnableManualAlignment", c.Recovery.EnableManualAlignment,
                c.Recovery.EnableManualAlignment, "User config", "AlignStitchWorkflowService");
            Add(list, "Recovery", "Fallback", "AutoPlaceExactZeroSample", c.Recovery.AutoPlaceExactZeroSample,
                c.Recovery.AutoPlaceExactZeroSample, "User config", "AlignStitchWorkflowService");
            Add(list, "Recovery", "Fallback", "AutoPlaceLowTextureSample", c.Recovery.AutoPlaceLowTextureSample,
                c.Recovery.AutoPlaceLowTextureSample, "User config", "AlignStitchWorkflowService");
            Add(list, "Recovery", "Fallback", "AllowExpectedGridFallback", c.Recovery.AllowExpectedGridFallback,
                c.Recovery.AllowExpectedGridFallback, "User config", "AlignStitchWorkflowService");
            Add(list, "Recovery", "Fallback", "RequireManualConfirmationForExpectedGrid",
                c.Recovery.RequireManualConfirmationForExpectedGrid,
                c.Recovery.RequireManualConfirmationForExpectedGrid, "User config", "AlignStitchWorkflowService");
            Add(list, "Recovery", "Content", "RevalidateSampleContent", c.Recovery.RevalidateSampleContent,
                c.Recovery.RevalidateSampleContent, "User config", "SampleTileContentAnalyzer");
            Add(list, "Recovery", "Content", "LowTextureStdDevThreshold", c.Recovery.LowTextureStdDevThreshold,
                c.Recovery.LowTextureStdDevThreshold, "User config", "SampleTileContentAnalyzer");
            Add(list, "Stitching", "Engine", "Engine", c.Stitching.Engine, s.StitchingEngine, "User config",
                "WorkflowStitchingService");
            Add(list, "Stitching", "Blend", "EnableBlending", c.Stitching.EnableBlending, s.EnableBlending,
                "User config", "GlobalTransformStitcher");
            Add(list, "Stitching", "Blend", "BlendMode", c.Stitching.BlendMode, s.EffectiveBlendMode,
                "User config + pixel format policy", "GlobalTransformStitcher");
            Add(list, "Stitching", "Output", "ForceGray8Output", c.Stitching.ForceGray8Output, s.ForceGray8Output,
                "User config", "GlobalTransformStitcher");
            Add(list, "Stitching", "Fallback", "BlankFallbackOverlapPolicy", c.Stitching.BlankFallbackOverlapPolicy,
                s.BlankFallbackOverlapPolicy, "User config", "GlobalTransformStitcher");
            Add(list, "Output", "Path", "OutputPath", c.Output.OutputPath, s.OutputPath, "Derived filename",
                "WorkflowStitchingService");
            Add(list, "Output", "TIFF", "TiffMode", c.Output.TiffMode, s.TiffMode, "User config",
                "GlobalTransformStitcher");
            Add(list, "Output", "TIFF", "BigTiffTileWidth", c.Output.BigTiffTileWidth, s.BigTiffTileWidth,
                "User config", "TIFF writer");
            Add(list, "Output", "TIFF", "BigTiffTileHeight", c.Output.BigTiffTileHeight, s.BigTiffTileHeight,
                "User config", "TIFF writer");
            Add(list, "Output", "Preview", "PreviewUpdateInterval", c.Output.PreviewUpdateInterval,
                s.PreviewUpdateInterval, "User config", "GlobalTransformStitcher");
            Add(list, "Output", "Preview", "MaxPreviewMegapixels", c.Output.MaxPreviewMegapixels,
                s.MaxPreviewMegapixels, "User config", "GlobalTransformStitcher");
            Add(list, "PoseGraph", "Solver", "Enabled", c.PoseGraph.Enabled, c.PoseGraph.Enabled, "User config",
                "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Prior weights", "LambdaDirect", c.PoseGraph.LambdaDirect, c.PoseGraph.LambdaDirect,
                "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Prior weights", "LambdaWeak", c.PoseGraph.LambdaWeak, c.PoseGraph.LambdaWeak,
                "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Prior weights", "LambdaBlank", c.PoseGraph.LambdaBlank, c.PoseGraph.LambdaBlank,
                "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Prior weights", "LambdaPinned", c.PoseGraph.LambdaPinned, c.PoseGraph.LambdaPinned,
                "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Solver", "RotationScalePixels", c.PoseGraph.RotationScalePixels,
                c.PoseGraph.RotationScalePixels, "User config", "PoseGraphSolver");
            Add(list, "PoseGraph", "Solver", "HuberPixels", c.PoseGraph.HuberPixels, c.PoseGraph.HuberPixels,
                "User config", "PoseGraphSolver");
            Add(list, "PoseGraph", "Solver", "MaxIterations", c.PoseGraph.MaxIterations, c.PoseGraph.MaxIterations,
                "User config", "PoseGraphSolver");
            Add(list, "PoseGraph", "Solver", "MaxCgIterations", c.PoseGraph.MaxCgIterations,
                c.PoseGraph.MaxCgIterations, "User config", "SparseNormalEquationCg");
            Add(list, "PoseGraph", "Solver", "CgTolerance", c.PoseGraph.CgTolerance, c.PoseGraph.CgTolerance,
                "User config", "SparseNormalEquationCg");
            Add(list, "PoseGraph", "Global similarity", "EstimateGlobalScale", c.PoseGraph.EstimateGlobalScale,
                c.PoseGraph.EstimateGlobalScale, "User config", "GlobalSimilarityFit");
            Add(list, "PoseGraph", "Global similarity", "MinGlobalScale", c.PoseGraph.MinGlobalScale,
                c.PoseGraph.MinGlobalScale, "User config", "GlobalSimilarityFit");
            Add(list, "PoseGraph", "Global similarity", "MaxGlobalScale", c.PoseGraph.MaxGlobalScale,
                c.PoseGraph.MaxGlobalScale, "User config", "GlobalSimilarityFit");
            Add(list, "PoseGraph", "Global similarity", "MaxGlobalRotationDeg", c.PoseGraph.MaxGlobalRotationDeg,
                c.PoseGraph.MaxGlobalRotationDeg, "User config", "GlobalSimilarityFit");
            Add(list, "PoseGraph", "Edge gate", "UseRejectedEdges", c.PoseGraph.UseRejectedEdges,
                c.PoseGraph.UseRejectedEdges, "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Edge gate", "MaxEdgeResidualPixels", c.PoseGraph.MaxEdgeResidualPixels,
                c.PoseGraph.MaxEdgeResidualPixels, "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Edge gate", "MaxEdgeRotationDeg", c.PoseGraph.MaxEdgeRotationDeg,
                c.PoseGraph.MaxEdgeRotationDeg, "User config", "GlobalPoseGraphOptimizer");
            Add(list, "PoseGraph", "Safety", "MaxPoseCorrectionPixels", c.PoseGraph.MaxPoseCorrectionPixels,
                c.PoseGraph.MaxPoseCorrectionPixels, "User config", "GlobalPoseGraphOptimizer");
            return new EffectiveConfigSnapshot(list);
        }
        private static object directMatcher(DirectCoarseMatcherKind k)
        {
            return k;
        }
        private static void Add(IList<EffectiveConfigEntry> l, string s, string g, string p, object c, object e,
                                string source, string used)
        {
            l.Add(new EffectiveConfigEntry { Stage = s, Group = g, Parameter = p, Configured = V(c), Effective = V(e),
                                             Source = source, UsedBy = used });
        }
        private static string V(object v)
        {
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
