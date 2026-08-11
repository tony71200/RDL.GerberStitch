using System;

namespace RDL.GerberStitch.Facade
{
    /// <summary>
    /// Minimal alignment/stitching configuration for RDL Master/Worker. It exposes only settings
    /// normally tuned for a production line; all other pipeline defaults remain those verified in Phase 0.
    /// This name intentionally matches the Core AlignStitchConfig type while representing the façade layer.
    /// GerberStitchFacade.BuildCoreConfig uses the fully qualified Core type name when mapping it.
    /// </summary>
    public sealed class AlignStitchConfig
    {
        // ── Engine ──

        /// <summary>
        /// Stitching engine. Valid values: "OpenCv", "HalconProjectiveMosaic",
        /// "HalconProjectiveMosaicRebased" | "HalconWarpThenTileOffsetExperimental" |
        /// "HalconThenOpenCvFallback". The default HalconProjectiveMosaicRebased engine is
        /// 17 times faster than OpenCV for stitching on the reference 80-tile dataset (13.3s vs 231.7s).
        /// </summary>
        public string StitchingEngine { get; set; } = "HalconProjectiveMosaicRebased";

        /// <summary>
        /// Enables seam blending. The default is false because HOperatorSet.GenProjectiveMosaic
        /// has no blending parameter; enabling this with the HALCON engine only produces a warning
        /// and overlaps remain hard-overwritten. For real blending, also select the OpenCv engine,
        /// at the cost of approximately 17-times slower stitching.
        /// </summary>
        public bool EnableBlending { get; set; } = false;

        /// <summary>
        /// true (default) computes and reports per-tile alignment/recovery duration (Detail mode) —
        /// matches existing behavior. false skips the per-tile timing reads and only reports
        /// stage-level totals (Mapping, Direct Alignment, Failure Recovery, Neighbor Graph, Stitching)
        /// on ProcessingReport.StageTimings — use when per-image granularity is not needed.
        /// </summary>
        public bool CalculateTimeDetail { get; set; } = true;

        // ── Direct Alignment thresholds ──

        /// <summary>
        /// Minimum HALCON NCC match score. The 0.10 default matches
        /// GerberViewer.Stitching.Configuration.HalconNccOptions.MinScore in Core.
        /// Do not use 0.7; it is about 5–7 times the real default and rejects almost every tile.
        /// </summary>
        public double NccMinScore { get; set; } = 0.10;

        /// <summary>
        /// Minimum ECC correlation for the refinement matcher. The 0.13 default matches
        /// Core EccOptions.MinCorrelation.
        /// </summary>
        public double EccMinCorrelation { get; set; } = 0.13;

        /// <summary>Maximum translation in pixels allowed for direct matching.</summary>
        public double MaxTranslationPixels { get; set; } = 300;

        /// <summary>
        /// Maximum absolute rotation in degrees allowed for direct matching. RDL uses 0.1,
        /// tighter than the Core structured default of 0.5, because the mechanical RDL line
        /// places tiles with very little real rotational deviation.
        /// </summary>
        public double MaxAbsRotationDeg { get; set; } = 0.1;

        // ── Direct Alignment policy (Core DirectAlignmentOptions.Policy / DirectPipelinePolicy) ──

        /// <summary>
        /// true (default) accepts a direct-alignment pose from the coarse matcher alone when the
        /// refinement matcher is unavailable or skipped. Matches Core DirectPipelinePolicy default.
        /// </summary>
        public bool AllowCoarseOnlyAcceptance { get; set; } = true;

        /// <summary>
        /// true (default) lets the refinement matcher run from the tile's expected pose when the
        /// coarse matcher fails, instead of leaving the tile unaligned. Matches Core
        /// DirectPipelinePolicy default.
        /// </summary>
        public bool AllowRefinementFromExpectedWhenCoarseFails { get; set; } = true;

        // ── Fallback RDL ──

        /// <summary>
        /// true lets Worker fall back to the legacy NewMergeFunc after an align/stitch failure.
        /// false returns error -300 to Master. This flag is for the Worker caller only;
        /// Core has no NewMergeFunc concept.
        /// </summary>
        public bool FallbackToLegacyMerge { get; set; } = false;

        internal void Validate()
        {
            if (NccMinScore <= 0 || NccMinScore > 1)
                throw new ArgumentOutOfRangeException(nameof(NccMinScore), NccMinScore, "NccMinScore must be in (0, 1].");
            if (EccMinCorrelation <= 0 || EccMinCorrelation > 1)
                throw new ArgumentOutOfRangeException(nameof(EccMinCorrelation), EccMinCorrelation, "EccMinCorrelation must be in (0, 1].");
            if (MaxTranslationPixels <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTranslationPixels), MaxTranslationPixels, "MaxTranslationPixels must be greater than 0.");
            if (MaxAbsRotationDeg <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAbsRotationDeg), MaxAbsRotationDeg, "MaxAbsRotationDeg must be greater than 0.");
            if (ParseEngine(StitchingEngine) == null)
                throw new ArgumentException("Invalid StitchingEngine: " + StitchingEngine, nameof(StitchingEngine));
        }

        internal static GerberViewer.Stitching.Models.StitchingEngine? ParseEngine(string value)
        {
            GerberViewer.Stitching.Models.StitchingEngine parsed;
            if (Enum.TryParse(value, true, out parsed)) return parsed;
            return null;
        }
    }
}
