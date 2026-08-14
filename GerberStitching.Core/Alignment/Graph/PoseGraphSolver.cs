using System;
using System.Collections.Generic;

namespace GerberViewer.Stitching.Alignment.Graph
{
    public sealed class PoseGraphSolveStats
    {
        public int Iterations { get; set; }
        public double[] BeforeEdgeResiduals { get; set; }
        public double[] AfterEdgeResiduals { get; set; }
        public double GlobalScale { get; set; } = 1.0;
        public double GlobalRotationDeg { get; set; }
        public double GlobalOffsetX { get; set; }
        public double GlobalOffsetY { get; set; }
        public bool GlobalScaleClamped { get; set; }
        public bool GlobalRotationClamped { get; set; }
        public bool Converged { get; set; }
    }

    // [Tony] [Change time: 2026-08-04] [Purpose: Solve every tile pose jointly from robust neighbor-edge evidence plus
    // a finite-weight direct-pose prior, replacing single-path spanning-tree propagation.]
    public sealed class PoseGraphSolver
    {
        private struct Row
        {
            public double Weight;
            public double Residual;
            public int[] Cols;
            public double[] Grad;
        }

        public PoseGraphSolveStats Solve(PoseGraphProblem problem, PoseGraphOptions options)
        {
            if (problem == null)
                throw new ArgumentNullException("problem");
            options = options ?? new PoseGraphOptions();
            var nodes = problem.Nodes ?? new PoseGraphNode[0];
            var edges = problem.Edges ?? new PoseGraphEdge[0];
            var unknownCount = nodes.Length * 3;

            var stats =
                new PoseGraphSolveStats { GlobalScale = problem.GlobalScale,
                                          GlobalRotationDeg = problem.GlobalRotationRad * 180.0 / Math.PI,
                                          GlobalOffsetX = problem.GlobalOffsetX, GlobalOffsetY = problem.GlobalOffsetY,
                                          BeforeEdgeResiduals = ComputeEdgeResiduals(nodes, edges) };

            var scale = 1.0;
            var rotationRad = 0.0;
            var offsetX = 0.0;
            var offsetY = 0.0;
            var lastStepNorm = double.PositiveInfinity;

            for (var it = 0; it < Math.Max(1, options.MaxIterations); it++)
            {
                double fitScale, fitRotation, fitOffsetX, fitOffsetY;
                if (GlobalSimilarityFit.TryFit(nodes, options.EstimateGlobalScale, out fitScale, out fitRotation,
                                               out fitOffsetX, out fitOffsetY))
                {
                    scale = fitScale;
                    rotationRad = fitRotation;
                    offsetX = fitOffsetX;
                    offsetY = fitOffsetY;
                    if (scale < options.MinGlobalScale)
                    {
                        scale = options.MinGlobalScale;
                        stats.GlobalScaleClamped = true;
                    }
                    else if (scale > options.MaxGlobalScale)
                    {
                        scale = options.MaxGlobalScale;
                        stats.GlobalScaleClamped = true;
                    }
                    var maxRotationRad = options.MaxGlobalRotationDeg * Math.PI / 180.0;
                    if (rotationRad < -maxRotationRad)
                    {
                        rotationRad = -maxRotationRad;
                        stats.GlobalRotationClamped = true;
                    }
                    else if (rotationRad > maxRotationRad)
                    {
                        rotationRad = maxRotationRad;
                        stats.GlobalRotationClamped = true;
                    }
                }

                var rows = BuildRows(nodes, edges, options, scale, rotationRad, offsetX, offsetY);
                var delta =
                    SparseNormalEquationCg.Solve(unknownCount, delegate(Action<double, double, int[], double[]> visit) {
                        for (var i = 0; i < rows.Count; i++)
                        {
                            var row = rows[i];
                            visit(row.Weight, row.Residual, row.Cols, row.Grad);
                        }
                    }, Math.Max(1, options.MaxCgIterations), options.CgTolerance <= 0 ? 1e-10 : options.CgTolerance);

                var stepSumSquares = 0.0;
                for (var i = 0; i < nodes.Length; i++)
                {
                    var n = nodes[i];
                    var dtx = delta[i * 3 + 0];
                    var dty = delta[i * 3 + 1];
                    var dth = delta[i * 3 + 2];
                    n.Tx += dtx;
                    n.Ty += dty;
                    n.Theta = WrapAngle(n.Theta + dth);
                    stepSumSquares += dtx * dtx + dty * dty;
                }
                lastStepNorm = nodes.Length > 0 ? Math.Sqrt(stepSumSquares / nodes.Length) : 0.0;

                UpdateHuberWeights(nodes, edges, options.HuberPixels);
                stats.Iterations = it + 1;
            }

            stats.GlobalScale = scale;
            stats.GlobalRotationDeg = rotationRad * 180.0 / Math.PI;
            stats.GlobalOffsetX = offsetX;
            stats.GlobalOffsetY = offsetY;
            stats.AfterEdgeResiduals = ComputeEdgeResiduals(nodes, edges);
            stats.Converged = lastStepNorm < 1e-3;

            problem.GlobalScale = scale;
            problem.GlobalRotationRad = rotationRad;
            problem.GlobalOffsetX = offsetX;
            problem.GlobalOffsetY = offsetY;
            return stats;
        }

        private static List<Row> BuildRows(PoseGraphNode[] nodes, PoseGraphEdge[] edges, PoseGraphOptions options,
                                           double scale, double rotationRad, double offsetX, double offsetY)
        {
            var rows = new List<Row>(edges.Length * 3 + nodes.Length * 3);
            var cosPhi = Math.Cos(rotationRad);
            var sinPhi = Math.Sin(rotationRad);

            for (var e = 0; e < edges.Length; e++)
            {
                var edge = edges[e];
                var a = nodes[edge.AnchorIndex];
                var b = nodes[edge.TargetIndex];
                var ca = Math.Cos(a.Theta);
                var sa = Math.Sin(a.Theta);
                var dtx = b.Tx - a.Tx;
                var dty = b.Ty - a.Ty;
                var rtx = ca * dtx + sa * dty - edge.Mx;
                var rty = -sa * dtx + ca * dty - edge.My;
                var rtheta = WrapAngle(b.Theta - a.Theta - edge.MTheta);
                var w = edge.BaseWeight * edge.HuberWeight;

                var aTx = edge.AnchorIndex * 3 + 0;
                var aTy = edge.AnchorIndex * 3 + 1;
                var aTh = edge.AnchorIndex * 3 + 2;
                var bTx = edge.TargetIndex * 3 + 0;
                var bTy = edge.TargetIndex * 3 + 1;
                var bTh = edge.TargetIndex * 3 + 2;

                var thetaGradX = -sa * dtx + ca * dty;
                var thetaGradY = -ca * dtx - sa * dty;

                var translationXRow = new Row
                {
                    Weight = w,
                    Residual = rtx,
                    Cols = new[] { aTx, aTy, aTh, bTx, bTy },
                    Grad = new[] { -ca, -sa, thetaGradX, ca, sa }
                };
                rows.Add(translationXRow);

                var translationYRow = new Row
                {
                    Weight = w,
                    Residual = rty,
                    Cols = new[] { aTx, aTy, aTh, bTx, bTy },
                    Grad = new[] { sa, -ca, thetaGradY, -sa, ca }
                };
                rows.Add(translationYRow);

                var rotationRow = new Row
                {
                    Weight = w * options.RotationScalePixels,
                    Residual = rtheta,
                    Cols = new[] { aTh, bTh },
                    Grad = new double[] { -1.0, 1.0 }
                };
                rows.Add(rotationRow);
            }

            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (n.Frozen || !n.HasPrior || n.Lambda <= 0)
                    continue;
                var predX = scale * (cosPhi * n.PriorDx - sinPhi * n.PriorDy) + offsetX;
                var predY = scale * (sinPhi * n.PriorDx + cosPhi * n.PriorDy) + offsetY;
                var rpx = n.Tx - predX;
                var rpy = n.Ty - predY;
                var rptheta = WrapAngle(n.Theta - (n.PriorTheta + rotationRad));
                var txCol = i * 3 + 0;
                var tyCol = i * 3 + 1;
                var thCol = i * 3 + 2;

                var priorTranslationXRow = new Row
                {
                    Weight = n.Lambda,
                    Residual = rpx,
                    Cols = new[] { txCol },
                    Grad = new[] { 1.0 }
                };
                rows.Add(priorTranslationXRow);

                var priorTranslationYRow = new Row
                {
                    Weight = n.Lambda,
                    Residual = rpy,
                    Cols = new[] { tyCol },
                    Grad = new[] { 1.0 }
                };
                rows.Add(priorTranslationYRow);

                var priorRotationRow = new Row
                {
                    Weight = n.Lambda * options.RotationScalePixels,
                    Residual = rptheta,
                    Cols = new[] { thCol },
                    Grad = new[] { 1.0 }
                };
                rows.Add(priorRotationRow);
            }
            return rows;
        }

        private static void UpdateHuberWeights(PoseGraphNode[] nodes, PoseGraphEdge[] edges, double huberPixels)
        {
            if (huberPixels <= 0)
                return;
            for (var e = 0; e < edges.Length; e++)
            {
                var edge = edges[e];
                var er = EdgeTranslationResidualNorm(nodes, edge);
                edge.HuberWeight = er <= huberPixels ? 1.0 : huberPixels / er;
            }
        }

        private static double[] ComputeEdgeResiduals(PoseGraphNode[] nodes, PoseGraphEdge[] edges)
        {
            var result = new double[edges.Length];
            for (var e = 0; e < edges.Length; e++)
                result[e] = EdgeTranslationResidualNorm(nodes, edges[e]);
            return result;
        }

        private static double EdgeTranslationResidualNorm(PoseGraphNode[] nodes, PoseGraphEdge edge)
        {
            var a = nodes[edge.AnchorIndex];
            var b = nodes[edge.TargetIndex];
            var ca = Math.Cos(a.Theta);
            var sa = Math.Sin(a.Theta);
            var dtx = b.Tx - a.Tx;
            var dty = b.Ty - a.Ty;
            var rtx = ca * dtx + sa * dty - edge.Mx;
            var rty = -sa * dtx + ca * dty - edge.My;
            return Math.Sqrt(rtx * rtx + rty * rty);
        }

        private static double WrapAngle(double radians)
        {
            while (radians <= -Math.PI)
                radians += 2 * Math.PI;
            while (radians > Math.PI)
                radians -= 2 * Math.PI;
            return radians;
        }
    }
}
