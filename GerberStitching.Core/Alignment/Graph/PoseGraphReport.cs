using System.Collections.Generic;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Codex] [Change time: 2026-08-04] [Purpose: Report/diagnostics model for the global pose-graph optimizer, emitted into processing_report.json and the DEBUG HTML report.]
    public sealed class PoseGraphReport
    {
        public bool Enabled { get; set; }
        public bool Applied { get; set; }
        public int Iterations { get; set; }
        public bool Converged { get; set; }

        public int EdgesTotal { get; set; }
        public int EdgesUsed { get; set; }
        public int EdgesGatedOut { get; set; }
        public IList<string> EdgeGateReasons { get; set; } = new List<string>();

        public double GlobalScale { get; set; } = 1.0;
        public double GlobalRotationDeg { get; set; }
        public double GlobalOffsetX { get; set; }
        public double GlobalOffsetY { get; set; }
        public bool GlobalScaleClamped { get; set; }
        public bool GlobalRotationClamped { get; set; }

        public double BeforeResidualMedian { get; set; }
        public double BeforeResidualP90 { get; set; }
        public double BeforeResidualMax { get; set; }
        public double AfterResidualMedian { get; set; }
        public double AfterResidualP90 { get; set; }
        public double AfterResidualMax { get; set; }

        public double PoseDeltaMedian { get; set; }
        public double PoseDeltaP90 { get; set; }
        public double PoseDeltaMax { get; set; }

        public IList<int> ComponentSizes { get; set; } = new List<int>();
        public IList<int> IsolatedTiles { get; set; } = new List<int>();
        public IList<PoseGraphTileEntry> Tiles { get; set; } = new List<PoseGraphTileEntry>();
        public IList<PoseGraphEdgeEntry> EdgeDiagnostics { get; set; } = new List<PoseGraphEdgeEntry>();
        public IList<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class PoseGraphTileEntry
    {
        public int OrderIndex { get; set; }
        public PoseSource SourceBefore { get; set; }
        public PoseSource SourceAfter { get; set; }
        public double BeforeTx { get; set; }
        public double BeforeTy { get; set; }
        public double BeforeRotationDeg { get; set; }
        public double AfterTx { get; set; }
        public double AfterTy { get; set; }
        public double AfterRotationDeg { get; set; }
        public double DeltaPixels { get; set; }
        public double DeltaRotationDeg { get; set; }
        public double Lambda { get; set; }
        public int EdgeCount { get; set; }
    }

    public sealed class PoseGraphEdgeEntry
    {
        public int AnchorOrderIndex { get; set; }
        public int TargetOrderIndex { get; set; }
        public string Direction { get; set; }
        public double PhaseScore { get; set; }
        public bool WasRejectedByLegacyClosure { get; set; }
        public double ResidualBefore { get; set; }
        public double ResidualAfter { get; set; }
        public double FinalHuberWeight { get; set; }
        public string GateReason { get; set; }
    }
}
