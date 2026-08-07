using System;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Alignment
{
    // [Codex] [Change time: 2026-08-01] [Purpose: Centralize phase-only neighbor expected/measured/residual transform semantics.]
    public static class NeighborTransformModel
    {
        public static Transform2D ExpectedTargetToAnchor(SampleTileInfo anchor, SampleTileInfo target)
        {
            if (anchor == null) throw new ArgumentNullException("anchor");
            if (target == null) throw new ArgumentNullException("target");
            if (Math.Abs(target.Row - anchor.Row) + Math.Abs(target.Column - anchor.Column) != 1)
                throw new ArgumentException("Anchor and target must be orthogonal grid neighbors.");
            return Transform2D.Translation(target.ExpectedX - anchor.ExpectedX, target.ExpectedY - anchor.ExpectedY);
        }

        public static Transform2D Residual(Transform2D expectedTargetToAnchor, Transform2D measuredTargetToAnchor)
        {
            if (expectedTargetToAnchor == null) throw new ArgumentNullException("expectedTargetToAnchor");
            if (measuredTargetToAnchor == null) throw new ArgumentNullException("measuredTargetToAnchor");
            return expectedTargetToAnchor.Invert().Multiply(measuredTargetToAnchor);
        }

        public static string Direction(SampleTileInfo anchor, SampleTileInfo target)
        {
            if (target.Column == anchor.Column + 1 && target.Row == anchor.Row) return "right";
            if (target.Column == anchor.Column - 1 && target.Row == anchor.Row) return "left";
            if (target.Row == anchor.Row + 1 && target.Column == anchor.Column) return "bottom";
            if (target.Row == anchor.Row - 1 && target.Column == anchor.Column) return "top";
            return null;
        }
    }
}
