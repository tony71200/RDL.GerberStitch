namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Tony] [Change time: 2026-08-04] [Purpose: Domain-free pose-graph data model so the solver is testable without
    // HALCON/OpenCV.]
    public sealed class PoseGraphNode
    {
        public int OrderIndex { get; set; }
        public double Tx { get; set; }
        public double Ty { get; set; }
        public double Theta { get; set; }
        public double PriorDx { get; set; }
        public double PriorDy { get; set; }
        public double PriorTheta { get; set; }
        public double Lambda { get; set; }
        public double Scale { get; set; } = 1.0;
        public bool HasPrior { get; set; }
        public bool Frozen { get; set; }
    }

    public sealed class PoseGraphEdge
    {
        public int AnchorIndex { get; set; }
        public int TargetIndex { get; set; }
        public double Mx { get; set; }
        public double My { get; set; }
        public double MTheta { get; set; }
        public double BaseWeight { get; set; }
        public double HuberWeight { get; set; } = 1.0;
    }

    public sealed class PoseGraphProblem
    {
        public PoseGraphNode[] Nodes { get; set; }
        public PoseGraphEdge[] Edges { get; set; }
        public double GlobalScale { get; set; } = 1.0;
        public double GlobalRotationRad { get; set; }
        public double GlobalOffsetX { get; set; }
        public double GlobalOffsetY { get; set; }
    }
}
