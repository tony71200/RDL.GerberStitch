using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    /// <summary>
    /// Reads GerberAlignStitch.settings.json (comments allowed) and maps it onto the Core config tree.
    /// The file is READ ONLY -- never serialize back over it, because Newtonsoft drops comments on write.
    /// </summary>
    public static class AlignStitchSettingsReader
    {
        // Shared by every deserialize/populate call in this reader -- ALL of DirectAlignmentOptions,
        // NeighborAlignmentOptions, RecoveryFallbackOptions, StitchingOptions, OutputOptions, and their nested
        // sub-objects (CommonGeometryOptions, DirectPipelinePolicy, DirectEvaluationOptions, ...) carry the same
        // TypeConverter attribute as the Matchers option classes, for the same WinForms PropertyGrid reason, so
        // PopulateStage hits the identical "requires a JSON string value" failure without this resolver.
        private static readonly JsonSerializer StructuredConfigSerializer =
            new JsonSerializer { ContractResolver = new IgnoreTypeConverterResolver(),
                                ObjectCreationHandling = ObjectCreationHandling.Reuse };

        /// <summary>Sections the file is allowed to contain. Anything else is a typo.</summary>
        private static readonly HashSet<string> KnownSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Matchers", "DirectAlignment", "NeighborAlignment", "Recovery", "PoseGraph", "Stitching", "Output" };

        // [Claude] [Change time: 2026-08-14] [Purpose: Full flat legacy field list on
        // GerberViewer.Stitching.Models.AlignStitchConfig (Models/WorkflowModels.cs), verified against the live
        // file rather than copied from the task brief -- the brief's list was missing
        // AllowUnsafeLegacyHalconProjectiveMosaic, ManualOffsetX, ManualOffsetY, EmitDebugPreview and
        // CalculateTimeDetail. AlignmentMatcher is excluded: it is a read-only computed property and cannot be
        // set by deserialization, so banning it would be inert. ConfigVersion is handled separately below.]

        /// <summary>Flat legacy fields that must never appear: writing one into the settings file would either
        /// silently do nothing (Read() only populates the structured stage sub-objects, never the flat fields
        /// directly) or, if the reader is later extended to also populate the top-level config object, could let
        /// Core's migration logic overwrite the structured groups. Fail loud instead of failing silently.</summary>
        private static readonly HashSet<string> BannedFlatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "InputManifestPath", "CapturedFolderPath", "OutputPath", "AlignmentMethod",
            "NccMinScore", "EccMinCorrelation", "MaxTranslationPixels", "MaxAbsRotationDeg", "MinScale", "MaxScale",
            "MinOverlapRatio", "AllowNccOnlyAcceptance", "AllowEccFromExpectedWhenNccFails", "EnableNeighborRecovery",
            "EnableAnchorInterpolation", "EnableDirectPoseOutlierCorrection", "RotationOutlierMadK",
            "AngleBandFloorDeg", "SignFlipGuardDeg", "AllowExpectedGridFallback",
            "RequireManualConfirmationForExpectedGrid", "StitchingEngine",
            "AllowUnsafeLegacyHalconProjectiveMosaic", "ManualOffsetX", "ManualOffsetY",
            "PreviewUpdateInterval", "MaxPreviewMegapixels", "TiffMode", "BigTiffTileWidth", "BigTiffTileHeight",
            "AutoPlaceExactZeroSample", "RevalidateSampleContent", "LowTextureStdDevThreshold",
            "AutoPlaceLowTextureSample", "BlankFallbackOverlapPolicy", "EmitDebugPreview", "CalculateTimeDetail"
        };

        /// <param name="globalPath">Station-wide settings file. Missing file is not an error.</param>
        /// <param name="recipeOverridePath">Optional per-recipe file holding only the keys to override.</param>
        /// <param name="logWarning">Called once per unknown key. Never null-checked away silently.</param>
        public static CoreConfig Read(string globalPath, string recipeOverridePath, Action<string> logWarning)
        {
            IDictionary<string, string> sourceByKey;
            return Read(globalPath, recipeOverridePath, logWarning, out sourceByKey);
        }

        /// <param name="globalPath">Station-wide settings file. Missing file is not an error.</param>
        /// <param name="recipeOverridePath">Optional per-recipe file holding only the keys to override.</param>
        /// <param name="logWarning">Called once per unknown key. Never null-checked away silently.</param>
        /// <param name="sourceByKey">Filled in with, for every leaf key set by either file, the layer name that
        /// set it ("file chung" or "override recipe"). Keys never touched by a file are absent, not "code
        /// default" -- the caller (BuildCoreConfig) fills those in as it applies later layers.</param>
        public static CoreConfig Read(string globalPath, string recipeOverridePath, Action<string> logWarning,
                                      out IDictionary<string, string> sourceByKey)
        {
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sourceByKey = sources;

            var config = new CoreConfig { ConfigVersion = 3 };

            var merged = new JObject();
            MergeFile(merged, globalPath, "file chung", sources, logWarning);
            MergeFile(merged, recipeOverridePath, "override recipe", sources, logWarning);
            if (!merged.HasValues) return config;

            var registry = merged["Matchers"] == null
                               ? new MatcherRegistry()
                               : merged["Matchers"].ToObject<MatcherRegistry>(StructuredConfigSerializer);

            // Populate the stage trees first, so matcher references are readable, then overlay matcher params.
            PopulateStage(merged, "DirectAlignment", config.DirectAlignment);
            PopulateStage(merged, "NeighborAlignment", config.NeighborAlignment);
            PopulateStage(merged, "Recovery", config.Recovery);
            PopulateStage(merged, "PoseGraph", config.PoseGraph);
            PopulateStage(merged, "Stitching", config.Stitching);
            PopulateStage(merged, "Output", config.Output);

            registry.ValidateReference("DirectAlignment.CoarseMatcher",
                                       config.DirectAlignment.CoarseMatcher.ToString(), MatcherSlot.DirectCoarse);
            registry.ValidateReference("DirectAlignment.RefinementMatcher",
                                       config.DirectAlignment.RefinementMatcher.ToString(),
                                       MatcherSlot.DirectRefinement);
            registry.ValidateReference("NeighborAlignment.CoarseMatcher",
                                       config.NeighborAlignment.CoarseMatcher.ToString(),
                                       MatcherSlot.NeighborCoarse);

            registry.ApplyTo(config);
            return config;
        }

        private static void MergeFile(JObject target, string path, string layerName,
                                      IDictionary<string, string> sources, Action<string> logWarning)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            AlignStitchSettingsFile file;
            using (var reader = new JsonTextReader(new StreamReader(path)))
            {
                // JsonTextReader skips // and /* */ comments by default, which is what lets the file be annotated.
                file = new JsonSerializer().Deserialize<AlignStitchSettingsFile>(reader);
            }
            if (file == null) return;

            if (file.ConfigVersion != 0 && file.ConfigVersion != 3)
                throw new InvalidDataException(path + ": ConfigVersion must be 3, found " + file.ConfigVersion +
                                               ". Versions 0/1 trigger a legacy migration that overwrites the " +
                                               "structured option groups.");

            // Advanced first, CommonlyTuned on top: the short block at the top of the file wins.
            var settings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace };
            if (file.Advanced != null)
            {
                Check(file.Advanced, path, logWarning);
                target.Merge(file.Advanced, settings);
                RecordSources(file.Advanced, layerName, sources);
            }
            if (file.CommonlyTuned != null)
            {
                Check(file.CommonlyTuned, path, logWarning);
                WarnOnOverlap(file.Advanced, file.CommonlyTuned, path, logWarning);
                target.Merge(file.CommonlyTuned, settings);
                RecordSources(file.CommonlyTuned, layerName, sources);
            }
        }

        /// <summary>Records, for every leaf key in block, which layer set it. JValue.Path is
        /// "DirectAlignment.Geometry.MaxAbsRotationDeg" -- the same shape ConfigLayerLog.Walk builds, so the two
        /// line up without a translation table.</summary>
        private static void RecordSources(JObject block, string layerName, IDictionary<string, string> sources)
        {
            if (block == null) return;
            foreach (var descendant in block.Descendants())
            {
                var value = descendant as JValue;
                if (value == null) continue;
                sources[value.Path] = layerName;
            }
        }

        private static void Check(JObject block, string path, Action<string> logWarning)
        {
            foreach (var property in block.Properties())
            {
                if (BannedFlatKeys.Contains(property.Name))
                    throw new InvalidDataException(path + ": \"" + property.Name + "\" is a legacy flat field. " +
                                                   "Use the structured group instead; flat fields can overwrite it.");
                if (!KnownSections.Contains(property.Name) && logWarning != null)
                    logWarning(path + ": unknown section \"" + property.Name + "\" (check the spelling)");
            }
        }

        private static void WarnOnOverlap(JObject advanced, JObject common, string path, Action<string> logWarning)
        {
            if (advanced == null || logWarning == null) return;
            foreach (var leaf in common.Descendants())
            {
                var value = leaf as JValue;
                if (value == null) continue;
                if (advanced.SelectToken(value.Path) != null)
                    logWarning(path + ": \"" + value.Path + "\" is set in both CommonlyTuned and Advanced; " +
                               "CommonlyTuned wins.");
            }
        }

        private static void PopulateStage(JObject merged, string sectionName, object target)
        {
            var section = merged[sectionName] as JObject;
            if (section == null) return;
            using (var reader = section.CreateReader())
            {
                StructuredConfigSerializer.Populate(reader, target);
            }
        }
    }
}
