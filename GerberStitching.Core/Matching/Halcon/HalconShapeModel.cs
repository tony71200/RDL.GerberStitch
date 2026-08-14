using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Halcon
{
    public sealed class HalconShapeModel
    {
        internal HalconShapeModel(HTuple id, string key, bool hit, Rect roi, HalconModelOrigin origin, int schema,
                                  string source)
        {
            ModelId = id;
            CacheKey = key;
            CacheHit = hit;
            ModelRoi = roi;
            Origin = origin;
            SchemaVersion = schema;
            Source = source;
        }
        public HTuple ModelId { get; private set; }
        public string CacheKey { get; private set; }
        public bool CacheHit { get; private set; }
        public Rect ModelRoi { get; private set; }
        public HalconModelOrigin Origin { get; private set; }
        public int SchemaVersion { get; private set; }
        public string Source { get; private set; }
    }
}
