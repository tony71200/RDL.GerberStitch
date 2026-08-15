using System.Collections.Generic;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Tony] [Change time: 2026-08-04] [Purpose: Report/diagnostics model for the global pose-graph optimizer, emitted
    // into processing_report.json and the DEBUG HTML report.]
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

        // [Claude] [Change time: 2026-08-15] [Purpose: EdgesTotal/EdgesUsed không phân biệt được "cạnh có phép đo
        // ảnh thật" với "cạnh rơi về lưới". Dòng log "rejected=120" của legacy closure đã bị đọc nhầm thành
        // "120 cạnh bị vứt", trong khi pose graph vẫn dùng đủ 142. Ba số dưới nói đúng chuyện gì xảy ra.]
        /// <summary>Cạnh có phép đo ảnh riêng (measured khác expected và hữu hạn).</summary>
        public int EdgesMeasured { get; set; }
        /// <summary>Cạnh không đo được, measured trùng expected — chỉ mang thông tin lưới.</summary>
        public int EdgesExpectedOnly { get; set; }
        /// <summary>Cạnh bị legacy cycle-closure đánh dấu không accepted. KHÔNG có nghĩa là bị loại khỏi
        /// pose graph: khi UseRejectedEdges=true chúng vẫn được dùng kèm phép đo của chính chúng.</summary>
        public int EdgesRejectedByLegacyClosure { get; set; }

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
        /// <summary>Số cạnh có phép đo ảnh thật nối vào đỉnh này.</summary>
        public int MeasuredEdgeCount { get; set; }
        /// <summary>Số neighbor trực giao tồn tại trong lưới (4 ở giữa, 3 ở cạnh, 2 ở góc).</summary>
        public int ExistingNeighborCount { get; set; }
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
