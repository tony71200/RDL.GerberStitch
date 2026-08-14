using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Alignment
{
    public sealed class DirectPoseOutlierCorrector
    {
        private const double MadConsistencyFactor = 1.4826;

        // [Tony] [Change time: 2026-08-03] [Purpose: Replace anisotropic direct-pose rotation outliers with a robust
        // global angle and neighbor-interpolated translation.]
        public DirectPoseCorrectionReport Correct(AlignStitchConfig config, IDictionary<int, SampleTileInfo> tiles,
                                                  IList<TileWorkflowState> states)
        {
            if (config == null)
                throw new ArgumentNullException("config");
            if (tiles == null)
                throw new ArgumentNullException("tiles");
            if (states == null)
                throw new ArgumentNullException("states");

            var report = new DirectPoseCorrectionReport();
            report.RotationOutlierMadK = config.DirectAlignment.RotationOutlierMadK;
            report.AngleBandFloorDeg = config.DirectAlignment.AngleBandFloorDeg;
            report.SignFlipGuardDeg = config.DirectAlignment.SignFlipGuardDeg;
            if (!config.DirectAlignment.EnableDirectPoseOutlierCorrection)
            {
                report.Enabled = false;
                return report;
            }
            report.Enabled = true;

            var validStates =
                states.Where(x => x != null && x.HasValidPose && tiles.ContainsKey(x.OrderIndex)).ToList();
            report.ValidPoseCount = validStates.Count;
            if (validStates.Count == 0)
                return report;

            var angles = validStates.Select(RotationDeg).OrderBy(x => x).ToArray();
            report.MedianRotationDeg = Median(angles);
            report.RotationMadDeg =
                Median(angles.Select(x => Math.Abs(x - report.MedianRotationDeg)).OrderBy(x => x).ToArray());
            report.RotationBandDeg =
                Math.Max(config.DirectAlignment.AngleBandFloorDeg,
                         config.DirectAlignment.RotationOutlierMadK * MadConsistencyFactor * report.RotationMadDeg);

            var outliers = validStates
                               .Where(x => IsOutlier(RotationDeg(x), report.MedianRotationDeg, report.RotationBandDeg,
                                                     config.DirectAlignment.SignFlipGuardDeg))
                               .ToList();
            report.OutlierCount = outliers.Count;
            var outlierOrders = new HashSet<int>(outliers.Select(x => x.OrderIndex));
            var anchors =
                validStates.Where(x => !outlierOrders.Contains(x.OrderIndex) && x.AlignmentSucceeded).ToList();

            foreach (var state in outliers.OrderBy(x => x.OrderIndex))
            {
                var before = (double[,])state.GlobalPose.Clone();
                double[,] interpolated;
                string interpolationReason;
                if (!AlignmentPoseInterpolator.TryInterpolate(tiles[state.OrderIndex], anchors, tiles, out interpolated,
                                                              out interpolationReason))
                {
                    report.OutlierUncorrectable.Add(state.OrderIndex);
                    report.Warnings.Add("OrderIndex " + state.OrderIndex +
                                        (" is a rotation outlier but no valid non-outlier anchor was available; " +
                                         "original pose retained."));
                    continue;
                }

                var radians = report.MedianRotationDeg * Math.PI / 180d;
                var cosine = Math.Cos(radians);
                var sine = Math.Sin(radians);
                state.GlobalPose = new[,] { { cosine, -sine, interpolated[0, 2] },
                                            { sine, cosine, interpolated[1, 2] },
                                            { 0d, 0d, 1d } };
                state.Source = PoseSource.Interpolated;
                state.PoseCorrected = true;
                state.IsFallbackPose = true;
                state.IsStitchable = true;
                state.Reason = "Direct pose corrected: median rotation + neighbor-interpolated translation. " +
                               interpolationReason;

                report.Corrected.Add(
                    new CorrectedTile { OrderIndex = state.OrderIndex, BeforeRotationDeg = RotationDeg(before),
                                        AfterRotationDeg = report.MedianRotationDeg, BeforeTranslationX = before[0, 2],
                                        BeforeTranslationY = before[1, 2], AfterTranslationX = state.GlobalPose[0, 2],
                                        AfterTranslationY = state.GlobalPose[1, 2],
                                        AnchorOrderIndices =
                                            NearestAnchorOrders(tiles[state.OrderIndex], anchors, tiles),
                                        Reason = state.Reason });
            }
            return report;
        }

        private static bool IsOutlier(double angle, double median, double band, double signFlipGuard)
        {
            return Math.Abs(angle - median) > band ||
                   (Math.Abs(median) > signFlipGuard && Math.Sign(angle) != 0 && Math.Sign(angle) != Math.Sign(median));
        }

        private static IList<int> NearestAnchorOrders(SampleTileInfo target, IEnumerable<TileWorkflowState> anchors,
                                                      IDictionary<int, SampleTileInfo> tiles)
        {
            return anchors.OrderBy(x => DistanceSquared(target, tiles[x.OrderIndex]))
                .ThenBy(x => x.OrderIndex)
                .Take(4)
                .Select(x => x.OrderIndex)
                .ToList();
        }

        private static double DistanceSquared(SampleTileInfo a, SampleTileInfo b)
        {
            var dx = (double)a.ExpectedX - b.ExpectedX;
            var dy = (double)a.ExpectedY - b.ExpectedY;
            return dx * dx + dy * dy;
        }

        private static double RotationDeg(TileWorkflowState state)
        {
            return RotationDeg(state.GlobalPose);
        }
        private static double RotationDeg(double[,] pose)
        {
            return Math.Atan2(pose[1, 0], pose[0, 0]) * 180d / Math.PI;
        }
        private static double Median(double[] sorted)
        {
            if (sorted.Length == 0)
                return double.NaN;
            var middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2d : sorted[middle];
        }
    }

    public sealed class DirectPoseCorrectionReport
    {
        public bool Enabled { get; set; }
        public double MedianRotationDeg { get; set; }
        public double RotationMadDeg { get; set; }
        public double RotationBandDeg { get; set; }
        public double RotationOutlierMadK { get; set; }
        public double AngleBandFloorDeg { get; set; }
        public double SignFlipGuardDeg { get; set; }
        public int ValidPoseCount { get; set; }
        public int OutlierCount { get; set; }
        public IList<CorrectedTile> Corrected { get; set; } = new List<CorrectedTile>();
        public IList<int> OutlierUncorrectable { get; set; } = new List<int>();
        public IList<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class CorrectedTile
    {
        public int OrderIndex { get; set; }
        public double BeforeRotationDeg { get; set; }
        public double AfterRotationDeg { get; set; }
        public double BeforeTranslationX { get; set; }
        public double BeforeTranslationY { get; set; }
        public double AfterTranslationX { get; set; }
        public double AfterTranslationY { get; set; }
        public IList<int> AnchorOrderIndices { get; set; } = new List<int>();
        public string Reason { get; set; }
        public double AdjustmentRotationDeg
        {
            get {
                return AfterRotationDeg - BeforeRotationDeg;
            }
        }
        public double AdjustmentX
        {
            get {
                return AfterTranslationX - BeforeTranslationX;
            }
        }
        public double AdjustmentY
        {
            get {
                return AfterTranslationY - BeforeTranslationY;
            }
        }
    }
}
