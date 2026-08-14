using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Imaging;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment.Graph
{
    // [Tony] [Change time: 2026-08-04] [Purpose: Domain glue between AlignStitchWorkflowService state and the
    // pose-graph solver: builds the problem, gates edges, applies the guarded solution back onto TileWorkflowState.]
    public sealed class GlobalPoseGraphOptimizer
    {
        public PoseGraphReport Optimize(AlignStitchConfig config, IDictionary<int, SampleTileInfo> tiles,
                                        IList<TileWorkflowState> states, IList<RecoveryEdgeReport> edges)
        {
            var options = config == null ? new PoseGraphOptions() : config.PoseGraph ?? new PoseGraphOptions();
            var report = new PoseGraphReport { Enabled = options.Enabled };
            if (!options.Enabled)
                return report;
            states = states ?? new List<TileWorkflowState>();
            edges = edges ?? new List<RecoveryEdgeReport>();

            var ordered = states.Where(s => s.Source != PoseSource.Excluded).OrderBy(s => s.OrderIndex).ToList();
            var nodeIndexByOrder = new Dictionary<int, int>();
            for (var i = 0; i < ordered.Count; i++)
                nodeIndexByOrder[ordered[i].OrderIndex] = i;

            var nodes = new PoseGraphNode[ordered.Count];
            var originalTx = new double[ordered.Count];
            var originalTy = new double[ordered.Count];
            var originalTheta = new double[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                var state = ordered[i];
                var priorMatrix = state.OriginalDirectGlobalPose ?? state.GlobalPose;
                var hasPrior = Homography.IsFinite(priorMatrix);
                var initMatrix = Homography.IsFinite(state.GlobalPose) ? state.GlobalPose : priorMatrix;
                double tx = 0, ty = 0, theta = 0;
                if (Homography.IsFinite(initMatrix))
                {
                    tx = initMatrix[0, 2];
                    ty = initMatrix[1, 2];
                    theta = Math.Atan2(initMatrix[1, 0], initMatrix[0, 0]);
                }
                else
                {
                    SampleTileInfo tile;
                    if (tiles != null && tiles.TryGetValue(state.OrderIndex, out tile) && tile != null)
                    {
                        tx = tile.ExpectedX;
                        ty = tile.ExpectedY;
                    }
                }

                var node = new PoseGraphNode {
                    OrderIndex = state.OrderIndex,     Tx = tx, Ty = ty, Theta = theta, HasPrior = hasPrior,
                    Lambda = LambdaFor(state, options)
                };
                if (hasPrior)
                {
                    node.PriorDx = priorMatrix[0, 2];
                    node.PriorDy = priorMatrix[1, 2];
                    node.PriorTheta = Math.Atan2(priorMatrix[1, 0], priorMatrix[0, 0]);
                    var a = priorMatrix[0, 0];
                    var b = priorMatrix[1, 0];
                    var k = Math.Sqrt(a * a + b * b);
                    node.Scale = k > 1e-9 && !double.IsNaN(k) ? k : 1.0;
                }
                nodes[i] = node;
                originalTx[i] = tx;
                originalTy[i] = ty;
                originalTheta[i] = theta;
            }

            var minPhase = config != null && config.NeighborAlignment != null && config.NeighborAlignment.Phase != null
                               ? config.NeighborAlignment.Phase.MinResponse
                               : 0.0;
            var minOverlap =
                config != null && config.NeighborAlignment != null && config.NeighborAlignment.Acceptance != null
                    ? config.NeighborAlignment.Acceptance.MinOverlapRatio
                    : 0.0;

            var dedup = new Dictionary<long, RecoveryEdgeReport>();
            var fullEdges = edges.Where(e => e.Purpose == RecoveryEdgePurpose.FullPoseReconciliation).ToList();
            report.EdgesTotal = fullEdges.Count;
            foreach (var e in fullEdges)
            {
                var key = KeyFor(e.AnchorOrderIndex, e.TargetOrderIndex);
                RecoveryEdgeReport existing;
                if (!dedup.TryGetValue(key, out existing) || e.PhaseScore > existing.PhaseScore)
                    dedup[key] = e;
            }

            var graphEdges = new List<PoseGraphEdge>();
            var edgeDiagnostics = new List<PoseGraphEdgeEntry>();
            var edgeGateReasons = new List<string>();
            foreach (var kv in dedup.OrderBy(x => x.Key))
            {
                var e = kv.Value;
                var entry =
                    new PoseGraphEdgeEntry { AnchorOrderIndex = e.AnchorOrderIndex,
                                             TargetOrderIndex = e.TargetOrderIndex, Direction = e.Direction,
                                             PhaseScore = e.PhaseScore, WasRejectedByLegacyClosure = !e.Accepted };
                string gateReason = null;
                int anchorIndex, targetIndex;
                if (!options.UseRejectedEdges && !e.Accepted)
                    gateReason = "Not accepted by legacy pipeline and UseRejectedEdges=false.";
                else if (!nodeIndexByOrder.TryGetValue(e.AnchorOrderIndex, out anchorIndex) ||
                         !nodeIndexByOrder.TryGetValue(e.TargetOrderIndex, out targetIndex))
                    gateReason = "Anchor or target tile is excluded from the pose graph.";
                else if (e.MeasuredTargetToAnchorTransform == null ||
                         !Homography.IsFinite(e.MeasuredTargetToAnchorTransform))
                    gateReason = "Measured transform is not finite.";
                else if (e.PhaseScore < minPhase)
                    gateReason = "PhaseScore " + e.PhaseScore.ToString("0.###") + " below Phase.MinResponse " +
                                 minPhase.ToString("0.###") + ".";
                else if (e.OverlapRatio < minOverlap)
                    gateReason = "OverlapRatio " + e.OverlapRatio.ToString("0.###") +
                                 " below Acceptance.MinOverlapRatio " + minOverlap.ToString("0.###") + ".";
                else
                {
                    var residualNorm = !double.IsNaN(e.ResidualNorm) ? e.ResidualNorm : ResidualNormFrom(e);
                    if (residualNorm > options.MaxEdgeResidualPixels)
                        gateReason = "Residual vs expected grid " + residualNorm.ToString("0.###") +
                                     " px exceeds MaxEdgeResidualPixels " +
                                     options.MaxEdgeResidualPixels.ToString("0.###") + " (possible period slip).";
                    else if (Math.Abs(e.PairMeasuredRotationDeg) > options.MaxEdgeRotationDeg)
                        gateReason = "Rotation " + e.PairMeasuredRotationDeg.ToString("0.###") +
                                     " deg exceeds MaxEdgeRotationDeg " +
                                     options.MaxEdgeRotationDeg.ToString("0.###") + ".";
                }

                if (gateReason != null)
                {
                    entry.GateReason = gateReason;
                    edgeDiagnostics.Add(entry);
                    edgeGateReasons.Add("anchor=" + e.AnchorOrderIndex + ", target=" + e.TargetOrderIndex + ": " +
                                        gateReason);
                    continue;
                }

                nodeIndexByOrder.TryGetValue(e.AnchorOrderIndex, out anchorIndex);
                nodeIndexByOrder.TryGetValue(e.TargetOrderIndex, out targetIndex);
                var mTheta =
                    Math.Atan2(e.MeasuredTargetToAnchorTransform[1, 0], e.MeasuredTargetToAnchorTransform[0, 0]);
                var graphEdge = new PoseGraphEdge { AnchorIndex = anchorIndex,
                                                    TargetIndex = targetIndex,
                                                    Mx = e.MeasuredTargetToAnchorTransform[0, 2],
                                                    My = e.MeasuredTargetToAnchorTransform[1, 2],
                                                    MTheta = mTheta,
                                                    BaseWeight = Math.Sqrt(Math.Max(e.PhaseScore, 1e-3)) *
                                                                 Math.Sqrt(Math.Max(e.OverlapRatio, 1e-3)) };
                graphEdges.Add(graphEdge);
                entry.ResidualBefore = ResidualNormFrom(e);
                edgeDiagnostics.Add(entry);
            }
            report.EdgesUsed = graphEdges.Count;
            report.EdgesGatedOut = report.EdgesTotal - report.EdgesUsed;
            report.EdgeGateReasons = edgeGateReasons;

            var edgeCountByNode = new int[nodes.Length];
            var union
            = new int[nodes.Length];
            for (var i = 0; i < union.Length; i++) union[i] = i;
            foreach (var ge in graphEdges)
            {
                edgeCountByNode[ge.AnchorIndex]++;
                edgeCountByNode[ge.TargetIndex]++;
                Union(union, ge.AnchorIndex, ge.TargetIndex);
            }
            var componentSizes = new Dictionary<int, int>();
            for (var i = 0; i < union.Length; i++)
            {
                var root = Find(union, i);
                int size;
                componentSizes.TryGetValue(root, out size);
                componentSizes[root] = size + 1;
            }
            report.ComponentSizes = componentSizes.Values.OrderByDescending(x => x).ToList();
            var isolatedIndices = new List<int>();
            for (var i = 0; i < nodes.Length; i++)
                if (edgeCountByNode[i] == 0)
                    isolatedIndices.Add(i);
            report.IsolatedTiles = isolatedIndices.Select(i => nodes[i].OrderIndex).OrderBy(x => x).ToList();

            var problem = new PoseGraphProblem
            {
                Nodes = nodes,
                Edges = graphEdges.ToArray()
            };
            var solver = new PoseGraphSolver();
            var solveStats = solver.Solve(problem, options);

            report.Iterations = solveStats.Iterations;
            report.Converged = solveStats.Converged;
            report.GlobalScale = solveStats.GlobalScale;
            report.GlobalRotationDeg = solveStats.GlobalRotationDeg;
            report.GlobalOffsetX = solveStats.GlobalOffsetX;
            report.GlobalOffsetY = solveStats.GlobalOffsetY;
            report.GlobalScaleClamped = solveStats.GlobalScaleClamped;
            report.GlobalRotationClamped = solveStats.GlobalRotationClamped;
            AssignPercentiles(solveStats.BeforeEdgeResiduals, out var beforeMedian, out var beforeP90,
                              out var beforeMax);
            AssignPercentiles(solveStats.AfterEdgeResiduals, out var afterMedian, out var afterP90, out var afterMax);
            report.BeforeResidualMedian = beforeMedian;
            report.BeforeResidualP90 = beforeP90;
            report.BeforeResidualMax = beforeMax;
            report.AfterResidualMedian = afterMedian;
            report.AfterResidualP90 = afterP90;
            report.AfterResidualMax = afterMax;

            var deltas = new double[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                var dx = nodes[i].Tx - originalTx[i];
                var dy = nodes[i].Ty - originalTy[i];
                deltas[i] = Math.Sqrt(dx * dx + dy * dy);
            }
            AssignPercentiles(deltas, out var deltaMedian, out var deltaP90, out var deltaMax);
            report.PoseDeltaMedian = deltaMedian;
            report.PoseDeltaP90 = deltaP90;
            report.PoseDeltaMax = deltaMax;

            var applied = true;
            if (options.MaxPoseCorrectionPixels > 0)
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    if (deltas[i] <= options.MaxPoseCorrectionPixels)
                        continue;
                    applied = false;
                    report.Warnings.Add("Pose graph guard: OrderIndex " + nodes[i].OrderIndex + " would move " +
                                        deltas[i].ToString("0.###") + " px, exceeding MaxPoseCorrectionPixels " +
                                        options.MaxPoseCorrectionPixels.ToString("0.###") +
                                        ". Result discarded; legacy poses kept.");
                }
            }
            report.Applied = applied;

            for (var e = 0; e < edgeDiagnostics.Count; e++)
            {
                if (edgeDiagnostics[e].GateReason != null)
                    continue;
                var matchIndex =
                    graphEdges.FindIndex(g => g.AnchorIndex == nodeIndexByOrder[edgeDiagnostics[e].AnchorOrderIndex] &&
                                              g.TargetIndex == nodeIndexByOrder[edgeDiagnostics[e].TargetOrderIndex]);
                if (matchIndex < 0)
                    continue;
                edgeDiagnostics[e].ResidualAfter = EdgeResidualAt(nodes, graphEdges[matchIndex]);
                edgeDiagnostics[e].FinalHuberWeight = graphEdges[matchIndex].HuberWeight;

                if (options.EdgeResidualWarningPixels > 0 &&
                    edgeDiagnostics[e].ResidualAfter > options.EdgeResidualWarningPixels)
                {
                    report.Warnings.Add("Pose graph: edge OrderIndex " + edgeDiagnostics[e].AnchorOrderIndex + " <-> " +
                                        edgeDiagnostics[e].TargetOrderIndex + " still has a " +
                                        edgeDiagnostics[e].ResidualAfter.ToString("0.###") +
                                        " px residual after solving (Huber weight " +
                                        edgeDiagnostics[e].FinalHuberWeight.ToString("0.###") +
                                        "), exceeding EdgeResidualWarningPixels " +
                                        options.EdgeResidualWarningPixels.ToString("0.###") +
                                        ". The seam at this boundary may not be fully continuous.");
                }
            }
            report.EdgeDiagnostics = edgeDiagnostics;

            var tileEntries = new List<PoseGraphTileEntry>();
            if (applied)
            {
                for (var i = 0; i < nodes.Length; i++)
                {
                    var state = ordered[i];
                    var node = nodes[i];
                    var beforeSource = state.Source;
                    var cos = Math.Cos(node.Theta);
                    var sin = Math.Sin(node.Theta);
                    var k = node.Scale <= 0 || double.IsNaN(node.Scale) ? 1.0 : node.Scale;
                    var matrix =
                        new[,] { { k * cos, -k * sin, node.Tx }, { k * sin, k * cos, node.Ty }, { 0d, 0d, 1d } };
                    state.GlobalPose = matrix;
                    state.PoseGraphAdjusted = true;
                    state.PoseGraphDeltaPixels = deltas[i];
                    var afterSource = NextPoseSource(beforeSource, edgeCountByNode[i] > 0);
                    if (afterSource != beforeSource)
                    {
                        state.Source = afterSource;
                        state.Reason =
                            "Pose graph: jointly solved from " + edgeCountByNode[i] +
                            " neighbor edge(s); global similarity scale=" + report.GlobalScale.ToString("0.#####") +
                            ", rotation=" + report.GlobalRotationDeg.ToString("0.###") + " deg.";
                        state.IsStitchable = true;
                        state.AlignmentSucceeded = true;
                    }
                    tileEntries.Add(new PoseGraphTileEntry {
                        OrderIndex = node.OrderIndex, SourceBefore = beforeSource, SourceAfter = state.Source,
                        BeforeTx = originalTx[i], BeforeTy = originalTy[i],
                        BeforeRotationDeg = originalTheta[i] * 180.0 / Math.PI, AfterTx = node.Tx, AfterTy = node.Ty,
                        AfterRotationDeg = node.Theta * 180.0 / Math.PI, DeltaPixels = deltas[i],
                        DeltaRotationDeg = (node.Theta - originalTheta[i]) * 180.0 / Math.PI, Lambda = node.Lambda,
                        EdgeCount = edgeCountByNode[i]
                    });
                }
            }
            report.Tiles = tileEntries;
            return report;
        }

        private static PoseSource NextPoseSource(PoseSource before, bool hasEdges)
        {
            if (before == PoseSource.Failed)
                return hasEdges ? PoseSource.NeighborAlignment : PoseSource.Failed;
            return before;
        }

        private static double LambdaFor(TileWorkflowState state, PoseGraphOptions options)
        {
            switch (state.Source)
            {
            case PoseSource.SampleAlignment:
                var score = state.Alignment != null && !double.IsNaN(state.Alignment.SelectedCommonScore)
                                ? state.Alignment.SelectedCommonScore
                                : 1.0;
                return options.LambdaDirect * Clamp(score, 0.05, 1.0);
            case PoseSource.Manual:
                return options.LambdaPinned;
            case PoseSource.BlankSampleExpectedPose:
                return options.LambdaBlank;
            case PoseSource.AnchorAdjusted:
            case PoseSource.NeighborAlignment:
            case PoseSource.Interpolated:
                return options.LambdaWeak;
            case PoseSource.Failed:
                return options.LambdaWeak;
            default:
                return options.LambdaWeak;
            }
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static long KeyFor(int a, int b)
        {
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            return ((long)lo << 32) | (uint)hi;
        }

        private static double ResidualNormFrom(RecoveryEdgeReport e)
        {
            if (e.ExpectedTargetToAnchorTransform == null || e.MeasuredTargetToAnchorTransform == null)
                return 0.0;
            var dx = e.MeasuredTargetToAnchorTransform[0, 2] - e.ExpectedTargetToAnchorTransform[0, 2];
            var dy = e.MeasuredTargetToAnchorTransform[1, 2] - e.ExpectedTargetToAnchorTransform[1, 2];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double EdgeResidualAt(PoseGraphNode[] nodes, PoseGraphEdge edge)
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

        private static void AssignPercentiles(double[] values, out double median, out double p90, out double max)
        {
            if (values == null || values.Length == 0)
            {
                median = 0;
                p90 = 0;
                max = 0;
                return;
            }
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);
            median = Percentile(sorted, 0.5);
            p90 = Percentile(sorted, 0.9);
            max = sorted[sorted.Length - 1];
        }

        private static double Percentile(double[] sorted, double fraction)
        {
            if (sorted.Length == 1)
                return sorted[0];
            var position = fraction * (sorted.Length - 1);
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            if (lower == upper)
                return sorted[lower];
            var weight = position - lower;
            return sorted[lower] * (1 - weight) + sorted[upper] * weight;
        }

        private static int Find(int[] union, int i)
        {
            while (union[i] != i)
            {
                union[i] = union[union[i]];
                i = union[i];
            }
            return i;
        }
        private static void Union(int[] union, int a, int b)
        {
            var ra = Find(union, a);
            var rb = Find(union, b);
            if (ra != rb)
            {
                union[ra] = rb;
            }
        }
    }
}
