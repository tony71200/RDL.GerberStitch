using System;

namespace RDL.GerberStitch.Facade
{
    /// <summary>
    /// Minimal align/stitch configuration for RDL Master and Worker. Expose only production-tunable values while
    /// retaining Phase 0 validated defaults for the rest of the pipeline.
    /// </summary>
    public sealed class AlignStitchConfig
    {
        // ── Engine ──

        /// <summary>
        /// Stitching engine. HalconProjectiveMosaicRebased is the default because reference runs measured it
        /// substantially faster than OpenCv.
        /// </summary>
        public string StitchingEngine { get; set; } = "HalconProjectiveMosaicRebased";

        /// <summary>
        /// Enable seam blending. The HALCON mosaic engine ignores this option and emits a warning; use OpenCv when
        /// actual blending is required.
        /// </summary>
        public bool EnableBlending { get; set; } = false;

        /// <summary>
        /// true (default) computes and reports per-tile alignment/recovery duration (Detail mode).
        /// false skips the per-tile timing reads and only reports stage-level totals
        /// (Mapping, Direct Alignment, Failure Recovery, Neighbor Graph, Stitching)
        /// on ProcessingReport.StageTimings.
        /// </summary>
        public bool CalculateTimeDetail { get; set; } = true;

        // Direct Alignment thresholds.

        /// <summary>
        /// Minimum HALCON NCC match score. Keep the Core default of 0.10; 0.7 rejects most tiles.
        /// </summary>
        public double NccMinScore { get; set; } = 0.10;

        /// <summary>
        /// Minimum ECC refinement correlation, matching the Core default of 0.13.
        /// </summary>
        public double EccMinCorrelation { get; set; } = 0.13;

        /// <summary>Maximum permitted translation in pixels for direct matching.</summary>
        public double MaxTranslationPixels { get; set; } = 300;

        /// <summary>
        /// Maximum absolute direct-match rotation in degrees. RDL uses 0.1 because mechanical placement has very
        /// small rotation error.
        /// </summary>
        public double MaxAbsRotationDeg { get; set; } = 0.1;

        // [Tony 20260813] Direct-pipeline acceptance policy, exposed so the station can tune it from
        // [GerberAlignStitch] in HandShake_Worker.ini. Both defaults mirror DirectPipelinePolicy in
        // GerberStitching.Core (AlignStitchStageOptions.cs:115-116); a different default here would
        // silently change how every existing lot is aligned.

        /// <summary>
        /// Accept a coarse-only (NCC) match when refinement does not reach its threshold. Core default: true.
        /// </summary>
        public bool AllowCoarseOnlyAcceptance { get; set; } = true;

        /// <summary>
        /// Run refinement from the expected position when the coarse stage fails. Core default: true.
        /// </summary>
        public bool AllowRefinementFromExpectedWhenCoarseFails { get; set; } = true;

        // ── Fallback RDL ──

        /// <summary>
        /// When true, the Worker falls back to legacy NewMergeFunc after align/stitch failure. When false, it
        /// returns -300 to the Master. Core does not know about NewMergeFunc.
        /// </summary>
        public bool FallbackToLegacyMerge { get; set; } = false;

        // [Tony] [Change time: 2026-08-11] [Purpose: Task07 truyền cờ DebugMode của Master xuống đây rồi
        // GerberStitchFacade.BuildCoreConfig map sang GerberViewer.Stitching.Models.AlignStitchConfig
        // (task04). Facade AlignStitchConfig này chưa có field này trước task07 - Runner cần property
        // để gán request.EmitDebugPreview vào.]
        /// <summary>Bật sinh DebugPreview_&lt;yyyyHHmm&gt;.jpg. Mặc định false.</summary>
        public bool EmitDebugPreview { get; set; } = false;

        // ── Settings file ──

        /// <summary>
        /// Station-wide GerberAlignStitch.settings.json. Null or a missing file is not an error: the
        /// pipeline then runs on code defaults plus whatever the INI and the Master payload set.
        /// </summary>
        public string SettingsFilePath { get; set; }

        /// <summary>
        /// Optional per-recipe override file holding only the keys to change. Applied after
        /// SettingsFilePath and before the INI.
        /// </summary>
        public string RecipeSettingsPath { get; set; }

        /// <summary>
        /// Sink for configuration warnings (unknown key, duplicated key, ...) and for the
        /// effective-config dump. Null silences both; the Worker always supplies its logger.
        /// </summary>
        public Action<string> LogWarning { get; set; }

        internal void Validate()
        {
            if (NccMinScore <= 0 || NccMinScore > 1)
                throw new ArgumentOutOfRangeException(nameof(NccMinScore), NccMinScore,
                                                      "NccMinScore phải trong (0, 1].");
            if (EccMinCorrelation <= 0 || EccMinCorrelation > 1)
                throw new ArgumentOutOfRangeException(nameof(EccMinCorrelation), EccMinCorrelation,
                                                      "EccMinCorrelation phải trong (0, 1].");
            if (MaxTranslationPixels <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTranslationPixels), MaxTranslationPixels,
                                                      "MaxTranslationPixels phải > 0.");
            if (MaxAbsRotationDeg <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAbsRotationDeg), MaxAbsRotationDeg,
                                                      "MaxAbsRotationDeg phải > 0.");
            if (ParseEngine(StitchingEngine) == null)
                throw new ArgumentException("StitchingEngine không hợp lệ: " + StitchingEngine,
                                            nameof(StitchingEngine));
        }

        internal static GerberViewer.Stitching.Models.StitchingEngine? ParseEngine(string value)
        {
            GerberViewer.Stitching.Models.StitchingEngine parsed;
            if (Enum.TryParse(value, true, out parsed))
                return parsed;
            return null;
        }
    }
}
