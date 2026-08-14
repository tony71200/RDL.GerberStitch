using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment
{
    public static class AlignmentPoseInterpolator
    {
        public static bool TryInterpolate(SampleTileInfo target, IEnumerable<TileWorkflowState> states,
                                          IDictionary<int, SampleTileInfo> tiles, out double[,] globalPose,
                                          out string reason)
        {
            globalPose = null;
            reason = null;
            if (target == null || states == null || tiles == null)
                return false;

            var anchors =
                states
                    .Where(x => x != null && x.AlignmentSucceeded && Homography.IsFinite(x.GlobalPose) &&
                                tiles.ContainsKey(x.OrderIndex))
                    .Select(x => new Anchor(x, tiles[x.OrderIndex], DistanceSquared(target, tiles[x.OrderIndex])))
                    .OrderBy(x => x.DistanceSquared)
                    .ThenBy(x => x.State.OrderIndex)
                    .Take(4)
                    .ToList();
            if (anchors.Count == 0)
                return false;

            double weightTotal = 0;
            double a = 0, b = 0, d = 0, e = 0, offsetX = 0, offsetY = 0;
            foreach (var anchor in anchors)
            {
                var weight = 1.0 / Math.Max(1.0, Math.Sqrt(anchor.DistanceSquared));
                var pose = anchor.State.GlobalPose;
                weightTotal += weight;
                a += pose[0, 0] * weight;
                b += pose[0, 1] * weight;
                d += pose[1, 0] * weight;
                e += pose[1, 1] * weight;
                offsetX += (pose[0, 2] - anchor.Tile.ExpectedX) * weight;
                offsetY += (pose[1, 2] - anchor.Tile.ExpectedY) * weight;
            }

            globalPose = new[,] { { a / weightTotal, b / weightTotal, target.ExpectedX + offsetX / weightTotal },
                                  { d / weightTotal, e / weightTotal, target.ExpectedY + offsetY / weightTotal },
                                  { 0d, 0d, 1d } };
            if (!Homography.IsFinite(globalPose))
            {
                globalPose = null;
                return false;
            }
            reason = "Interpolated pose from " + anchors.Count + " measured anchor(s): " +
                     string.Join(",", anchors.Select(x => x.State.OrderIndex.ToString()).ToArray()) + ".";
            return true;
        }

        private static double DistanceSquared(SampleTileInfo a, SampleTileInfo b)
        {
            var dx = (double)a.ExpectedX - b.ExpectedX;
            var dy = (double)a.ExpectedY - b.ExpectedY;
            return dx * dx + dy * dy;
        }

        private sealed class Anchor
        {
            public Anchor(TileWorkflowState state, SampleTileInfo tile, double distanceSquared)
            {
                State = state;
                Tile = tile;
                DistanceSquared = distanceSquared;
            }
            public TileWorkflowState State { get; private set; }
            public SampleTileInfo Tile { get; private set; }
            public double DistanceSquared { get; private set; }
        }
    }
}
