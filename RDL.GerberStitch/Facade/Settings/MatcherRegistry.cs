using System;
using System.Collections.Generic;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Matching.Halcon;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Core keeps one options slot per (stage, matcher kind), which
    // duplicates the same knobs across DirectAlignment and NeighborAlignment. The settings file instead declares
    // each matcher KIND once and lets stages reference it by name; this class copies one definition into every
    // matching slot. Entry names are the Core enum constant names, so a stage value needs no translation.]

    /// <summary>Matcher definitions keyed by matcher kind name, as they appear in the "Matchers" block.</summary>
    public sealed class MatcherRegistry
    {
        public HalconNccOptions HalconNcc { get; set; }
        public HalconShapeModelOptions HalconShapeModel { get; set; }
        public EccOptions PyramidEcc { get; set; }
        public PhaseCorrelationOptions PyramidPhaseCorrelation { get; set; }

        /// <summary>Kind names legal in DirectAlignment.CoarseMatcher.</summary>
        private static readonly HashSet<string> DirectCoarseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "HalconNcc", "HalconShapeModel", "PyramidEcc", "PyramidPhaseCorrelation", "None" };

        /// <summary>Kind names legal in DirectAlignment.RefinementMatcher.</summary>
        private static readonly HashSet<string> DirectRefinementKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PyramidEcc", "PyramidPhaseCorrelation", "None" };

        /// <summary>Kind names legal in NeighborAlignment.CoarseMatcher. Neighbor production is phase-only.</summary>
        private static readonly HashSet<string> NeighborCoarseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PyramidPhaseCorrelation", "None" };

        /// <summary>Copies each declared matcher definition into every Core slot that uses that kind.</summary>
        public void ApplyTo(CoreConfig config)
        {
            if (HalconNcc != null) Copy(HalconNcc, config.DirectAlignment.Ncc);
            if (HalconShapeModel != null) config.DirectAlignment.Shape = HalconShapeModel;
            if (PyramidEcc != null)
            {
                Copy(PyramidEcc, config.DirectAlignment.Ecc);
                Copy(PyramidEcc, config.NeighborAlignment.Ecc);
            }
            if (PyramidPhaseCorrelation != null)
                Copy(PyramidPhaseCorrelation, config.NeighborAlignment.Phase);
        }

        public void ValidateReference(string stageKeyPath, string kindName, MatcherSlot slot)
        {
            if (string.IsNullOrWhiteSpace(kindName))
                throw new ArgumentException(stageKeyPath + " is empty; name a matcher kind or \"None\".");

            HashSet<string> legal;
            switch (slot)
            {
            case MatcherSlot.DirectCoarse: legal = DirectCoarseKinds; break;
            case MatcherSlot.DirectRefinement: legal = DirectRefinementKinds; break;
            default: legal = NeighborCoarseKinds; break;
            }
            if (!legal.Contains(kindName))
                throw new ArgumentException(stageKeyPath + " = \"" + kindName + "\" is not valid here. Allowed: " +
                                            string.Join(", ", new List<string>(legal).ToArray()) + ".");
            if (string.Equals(kindName, "None", StringComparison.OrdinalIgnoreCase)) return;
            if (!IsDeclared(kindName))
                throw new ArgumentException(stageKeyPath + " references matcher \"" + kindName +
                                            "\", which is not declared in the Matchers block.");
        }

        private bool IsDeclared(string kindName)
        {
            if (string.Equals(kindName, "HalconNcc", StringComparison.OrdinalIgnoreCase)) return HalconNcc != null;
            if (string.Equals(kindName, "HalconShapeModel", StringComparison.OrdinalIgnoreCase)) return HalconShapeModel != null;
            if (string.Equals(kindName, "PyramidEcc", StringComparison.OrdinalIgnoreCase)) return PyramidEcc != null;
            if (string.Equals(kindName, "PyramidPhaseCorrelation", StringComparison.OrdinalIgnoreCase)) return PyramidPhaseCorrelation != null;
            return false;
        }

        private static void Copy(HalconNccOptions from, HalconNccOptions to)
        {
            to.MinScore = from.MinScore; to.NumLevels = from.NumLevels;
            to.AngleStartRad = from.AngleStartRad; to.AngleExtentRad = from.AngleExtentRad;
            to.AngleStepRad = from.AngleStepRad; to.Metric = from.Metric;
            to.MaxMatches = from.MaxMatches; to.MaxOverlap = from.MaxOverlap;
            to.SubPixel = from.SubPixel; to.ModelRoiMarginPixels = from.ModelRoiMarginPixels;
        }

        private static void Copy(EccOptions from, EccOptions to)
        {
            to.MotionModel = from.MotionModel; to.PyramidLevels = from.PyramidLevels;
            to.MaxIterations = from.MaxIterations; to.Epsilon = from.Epsilon;
            to.MinCorrelation = from.MinCorrelation;
        }

        private static void Copy(PhaseCorrelationOptions from, PhaseCorrelationOptions to)
        {
            to.MinResponse = from.MinResponse; to.PyramidLevels = from.PyramidLevels;
        }
    }

    public enum MatcherSlot
    {
        DirectCoarse,
        DirectRefinement,
        NeighborCoarse
    }
}
