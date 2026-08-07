using System;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Codex] [Change time: 2026-08-04] [Purpose: Closed-form weighted 4-DOF similarity fit (Umeyama) between the sample-grid prior frame and the current pose-graph frame.]
    public static class GlobalSimilarityFit
    {
        public static bool TryFit(PoseGraphNode[] nodes, bool estimateScale,
            out double scale, out double rotationRad, out double offsetX, out double offsetY)
        {
            scale = 1.0;
            rotationRad = 0.0;
            offsetX = 0.0;
            offsetY = 0.0;

            double sumLambda = 0.0;
            double dSumX = 0.0, dSumY = 0.0, tSumX = 0.0, tSumY = 0.0;
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.Frozen || !n.HasPrior || n.Lambda <= 0) continue;
                sumLambda += n.Lambda;
                dSumX += n.Lambda * n.PriorDx;
                dSumY += n.Lambda * n.PriorDy;
                tSumX += n.Lambda * n.Tx;
                tSumY += n.Lambda * n.Ty;
            }
            if (sumLambda <= 1e-12) return false;

            var dBarX = dSumX / sumLambda;
            var dBarY = dSumY / sumLambda;
            var tBarX = tSumX / sumLambda;
            var tBarY = tSumY / sumLambda;

            double sxx = 0.0, sxy = 0.0, sdd = 0.0;
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.Frozen || !n.HasPrior || n.Lambda <= 0) continue;
                var dx = n.PriorDx - dBarX;
                var dy = n.PriorDy - dBarY;
                var tx = n.Tx - tBarX;
                var ty = n.Ty - tBarY;
                sxx += n.Lambda * (dx * tx + dy * ty);
                sxy += n.Lambda * (dx * ty - dy * tx);
                sdd += n.Lambda * (dx * dx + dy * dy);
            }
            if (sdd < 1e-9) return false;

            rotationRad = Math.Atan2(sxy, sxx);
            scale = estimateScale ? Math.Sqrt(sxx * sxx + sxy * sxy) / sdd : 1.0;
            var cos = Math.Cos(rotationRad);
            var sin = Math.Sin(rotationRad);
            offsetX = tBarX - scale * (cos * dBarX - sin * dBarY);
            offsetY = tBarY - scale * (sin * dBarX + cos * dBarY);
            return true;
        }
    }
}
