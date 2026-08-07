using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment
{
    // [Codex] [Change time: 2026-08-01] [Purpose: Apply the locked lexicographic multi-neighbor selection policy independently of pair measurement.]
    public static class NeighborCandidateRanker
    {
        public static IList<RecoveryEdgeReport> Rank(IEnumerable<RecoveryEdgeReport> candidates)
        {
            if (candidates == null) throw new ArgumentNullException("candidates");
            var accepted = candidates.Where(x => x != null && x.Accepted)
                .OrderByDescending(x => FiniteOrMinimum(x.SampleForegroundRatio))
                .ThenByDescending(x => x.TraversalDirectionPreferred)
                .ThenByDescending(x => FiniteOrMinimum(x.SampleEdgeDensity))
                .ThenByDescending(x => FiniteOrMinimum(x.PhaseScore))
                .ThenBy(x => FiniteOrMaximum(x.ResidualNorm))
                .ThenBy(x => x.AnchorOrderIndex)
                .ToList();
            for (var i = 0; i < accepted.Count; i++) accepted[i].Rank = i + 1;
            return accepted;
        }

        public static string RankingTuple(RecoveryEdgeReport candidate)
        {
            if (candidate == null) throw new ArgumentNullException("candidate");
            return "foreground=" + Format(candidate.SampleForegroundRatio) +
                ", traversalPreferred=" + candidate.TraversalDirectionPreferred +
                ", edgeDensity=" + Format(candidate.SampleEdgeDensity) +
                ", phaseResponse=" + Format(candidate.PhaseScore) +
                ", residualNorm=" + Format(candidate.ResidualNorm) +
                ", anchorOrderIndex=" + candidate.AnchorOrderIndex;
        }

        private static double FiniteOrMinimum(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? double.MinValue : value; }
        private static double FiniteOrMaximum(double value) { return double.IsNaN(value) || double.IsInfinity(value) ? double.MaxValue : value; }
        private static string Format(double value) { return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture); }
    }
}
