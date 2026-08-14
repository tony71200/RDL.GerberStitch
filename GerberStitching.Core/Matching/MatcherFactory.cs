using System;
using GerberViewer.Stitching.Matching.Halcon;
using GerberViewer.Stitching.Matching.OpenCv;

namespace GerberViewer.Stitching.Matching
{
    public interface IMatcherFactory
    {
        IMatcher Create(MatcherKind kind);
    }

    public static class MatcherFactoryExtensions
    {
        /// <summary>Creates a matcher and connects its terminal results to the caller's common sink.</summary>
        public static IMatcher Create(this IMatcherFactory factory, MatcherKind kind,
                                      EventHandler<MatchResultEventArgs> resultSink)
        {
            if (factory == null)
                throw new ArgumentNullException("factory");
            var matcher = factory.Create(kind);
            if (resultSink != null)
                matcher.MatchResultAvailable += resultSink;
            return matcher;
        }
    }

    public sealed class MatcherFactory : IMatcherFactory
    {
        private readonly EventHandler<MatchResultEventArgs> _resultSink;
        public MatcherFactory()
        {
        }
        public MatcherFactory(EventHandler<MatchResultEventArgs> resultSink)
        {
            _resultSink = resultSink;
        }

        public IMatcher Create(MatcherKind kind)
        {
            IMatcher matcher;
            switch (kind)
            {
            case MatcherKind.HalconNcc:
                matcher = new HalconNccMatcher();
                break;
            case MatcherKind.HalconShapeModel:
                matcher = new HalconShapeModelMatcher();
                break;
            case MatcherKind.PyramidEcc:
                matcher = new PyramidEccMatcher();
                break;
            case MatcherKind.PyramidPhaseCorrelation:
                matcher = new PyramidPhaseCorrelationMatcher();
                break;
            default:
                throw new NotSupportedException("Unsupported matcher kind: " + kind + ".");
            }
            if (_resultSink != null)
                matcher.MatchResultAvailable += _resultSink;
            return matcher;
        }
    }
}
