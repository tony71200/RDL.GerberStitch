namespace GerberViewer.Stitching.Comparison
{
    public sealed class ComparisonMetrics
    {
        public double ValidOverlapRatio { get; set; }
        public double NormalizedCrossCorrelation { get; set; }
        public double BinaryMaskIoU { get; set; }
        public double EdgeOverlap { get; set; }
        public double DistanceTransformError { get; set; }
        public double EdgePrecision { get; set; }
        public double EdgeRecall { get; set; }
        public double EdgeF1Score { get; set; }
        public double MeanEdgeDistancePixels { get; set; }
        public double P95EdgeDistancePixels { get; set; }
        public double AbsoluteDifferenceMean { get; set; }
        public double AbsoluteDifferenceP95 { get; set; }
        public double AbsoluteDifferenceMax { get; set; }
    }
}
