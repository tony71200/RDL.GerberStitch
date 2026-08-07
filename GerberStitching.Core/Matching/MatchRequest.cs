using OpenCvSharp;
using GerberViewer.Stitching.Transforms;
using GerberViewer.Stitching.Configuration;

namespace GerberViewer.Stitching.Matching
{
    public enum MatchPurpose
    {
        CapturedToSample,
        TargetCapturedToAnchorCaptured,
        ManualPreview,
        SyntheticTest
    }

    public sealed class MatchRequest
    {
        public Mat ReferenceImage { get; set; }
        public Mat MovingImage { get; set; }
        public Mat ReferenceMask { get; set; }
        public Mat MovingMask { get; set; }
        public Rect? ReferenceRoi { get; set; }
        public Rect? MovingRoi { get; set; }
        public Transform2D InitialMovingToReferenceTransform { get; set; }
        public MatcherOptions Options { get; set; } = new MatcherOptions();
        public MatchPurpose Purpose { get; set; }
        public int? OrderIndex { get; set; }
        public string SampleTileId { get; set; }
        public string ReferenceNccModelPath { get; set; }
        public Rect? ReferenceNccModelRoi { get; set; }
        public double? NccReferenceOriginRow { get; set; }
        public double? NccReferenceOriginColumn { get; set; }
        public int NccModelSchemaVersion { get; set; }
        public string ReferenceShapeModelPath { get; set; }
        public Rect? ReferenceShapeModelRoi { get; set; }
        public double? ShapeReferenceOriginRow { get; set; }
        public double? ShapeReferenceOriginColumn { get; set; }
        public int ShapeModelSchemaVersion { get; set; }
        public HalconShapeModelOptions ShapeOptions { get; set; } = new HalconShapeModelOptions();
        public string Context { get; set; }
    }
}
