using System;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Alignment
{
    public sealed class HalconNccSampleAligner : ISampleAligner
    {
        private readonly NccThenPyramidEccSampleAligner _inner;

        public HalconNccSampleAligner()
        {
            _inner = new NccThenPyramidEccSampleAligner(
                new MatcherPipeline(
                    new NccOnlyFactory()
                    )
                );
        }

        public SampleAlignmentResult Align(SampleAlignmentContext context)
        {
            if (context == null) throw new ArgumentNullException("context");
            if (context.Options == null) context.Options = new SampleAlignmentOptions();
            var original = context.Options.AllowNccOnlyAcceptance;
            context.Options.AllowNccOnlyAcceptance = true;
            try
            {
                var result = _inner.Align(context);
                if (result != null)
                {
                    result.Method = SampleAlignmentMethod.HalconNcc;
                    if (result.Success)
                    {
                        result.PipelineStage = "HalconNccMatcher";
                        result.EccCorrelation = double.NaN;
                        result.EccFailureReason = null;
                        result.Warning = null;
                    }
                }
                return result;
            }
            finally { context.Options.AllowNccOnlyAcceptance = original; }
        }

        public void Dispose()
        {
            _inner.Dispose();
        }

        private sealed class NccOnlyFactory : IMatcherFactory
        {
            private readonly IMatcherFactory _factory = new MatcherFactory();
            public IMatcher Create(MatcherKind kind)
            {
                return kind == MatcherKind.HalconNcc ? _factory.Create(kind) : new RejectingMatcher("EccDisabledForNccOnlyAligner");
            }
        }
    }

    public sealed class PyramidEccSampleAligner : ISampleAligner
    {
        private readonly NccThenPyramidEccSampleAligner _inner;

        public PyramidEccSampleAligner()
        {
            _inner = new NccThenPyramidEccSampleAligner(
                new MatcherPipeline(
                    new EccOnlyFactory()));
        }

        public SampleAlignmentResult Align(SampleAlignmentContext context)
        {
            return _inner.Align(context);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }

        private sealed class EccOnlyFactory : IMatcherFactory
        {
            private readonly IMatcherFactory _factory = new MatcherFactory();
            public IMatcher Create(MatcherKind kind) { return kind == MatcherKind.PyramidEcc ? _factory.Create(kind) : new RejectingMatcher("NccDisabledForEccOnlyAligner"); }
        }
    }

    public sealed class NccThenPyramidEccSampleAligner : ISampleAligner
    {
        private readonly ModalityAwarePreprocessor _preprocessor;
        private readonly MatcherPipeline _pipeline;

        public NccThenPyramidEccSampleAligner() : this(new MatcherPipeline())
        {
        }

        public NccThenPyramidEccSampleAligner(MatcherPipeline pipeline) : this(pipeline, new ModalityAwarePreprocessor())
        {
        }

        public NccThenPyramidEccSampleAligner(MatcherPipeline pipeline, ModalityAwarePreprocessor preprocessor)
        {
            if (pipeline == null) throw new ArgumentNullException("pipeline");
            if (preprocessor == null) throw new ArgumentNullException("preprocessor");
            _pipeline = pipeline;
            _preprocessor = preprocessor;
        }

        public SampleAlignmentResult Align(SampleAlignmentContext context)
        {
            Validate(context);
            var options = context.Options ?? new SampleAlignmentOptions();
            var candidates = _preprocessor.PreprocessCandidates(context.SampleImage, context.CapturedImage, options.Preprocessing);
            SampleAlignmentResult bestRejected = null;
            try
            {
                foreach (var candidate in candidates)
                {
                    var request = new MatchRequest
                    {
                        ReferenceImage = candidate.Sample,
                        MovingImage = candidate.Captured,
                        InitialMovingToReferenceTransform = ToTransform(context.InitialCapturedToSampleTransform ?? context.ExpectedCapturedToSampleTransform),
                        Options = ToMatcherOptions(options, candidate.Variant),
                        Purpose = MatchPurpose.CapturedToSample,
                        SampleTileId = context.SampleTileId,
                        ReferenceNccModelPath = context.SampleNccModelPath,
                        ReferenceNccModelRoi = context.SampleNccModelRoi,
                        NccReferenceOriginRow = context.NccReferenceOriginRow,
                        NccReferenceOriginColumn = context.NccReferenceOriginColumn,
                        NccModelSchemaVersion = context.NccModelSchemaVersion,
                        OrderIndex = ParseOrderIndex(context.SampleTileId),
                        Context = "Direct camera-to-sample alignment"
                    };
                    var pipelineResult = _pipeline.MatchDirect(request, options.AllowNccOnlyAcceptance, options.AllowEccFromExpectedWhenNccFails, context.CancellationToken);
                    var alignment = ToAlignmentResult(pipelineResult, candidate.Variant, context);
                    if (candidate.SampleDiagnostic != null)
                    {
                        alignment.DiagnosticImages["sample_preprocessed"] = candidate.SampleDiagnostic;
                        candidate.SampleDiagnostic = null;
                    }
                    if (candidate.CapturedDiagnostic != null)
                    {
                        alignment.DiagnosticImages["captured_preprocessed"] = candidate.CapturedDiagnostic;
                        candidate.CapturedDiagnostic = null;
                    }
                    if (alignment.Success) return alignment;
                    bestRejected = PreferMoreInformative(bestRejected, alignment);
                }
            }
            finally
            {
                for (int i = 0; i < candidates.Count; i++) candidates[i].Dispose();
            }
            return bestRejected ?? SampleAlignmentResult.Rejected(SampleAlignmentMethod.NccThenPyramidEcc, "DirectRejected");
        }

        public void Dispose()
        {
        }

        private static SampleAlignmentResult ToAlignmentResult(MatcherPipelineResult pipelineResult, string variant, SampleAlignmentContext context)
        {
            if (pipelineResult == null || !pipelineResult.Success || pipelineResult.SelectedCandidate == null || !pipelineResult.SelectedCandidate.Success)
            {
                var rejected = SampleAlignmentResult.Rejected(SampleAlignmentMethod.NccThenPyramidEcc, pipelineResult == null ? "DirectRejected" : pipelineResult.FailureReason);
                FillScores(rejected, pipelineResult);
                rejected.PreprocessingVariant = variant;
                return rejected;
            }

            var final = pipelineResult.SelectedCandidate;
            var h = final.MovingToReferenceTransform.ToArray();
            var result = BuildResult(SampleAlignmentMethod.NccThenPyramidEcc, h, variant, context);
            FillScores(result, pipelineResult);
            result.Warning = pipelineResult.UsedCoarseFallback ? "NccOnlyAcceptedAfterEccFailure" : (pipelineResult.UsedRefinementFromExpected ? "EccAcceptedFromExpectedAfterNccFailure" : null);
            result.PipelineStage = pipelineResult.SelectedStage;
            result.SelectedMatcherName = final.MatcherName;
            result.SelectedRawScore = final.RawScore;
            result.SelectionReason = pipelineResult.SelectionReason;
            if (pipelineResult.CoarseCandidate != null && !pipelineResult.CoarseCandidate.Success)
                result.NccFailureReason = pipelineResult.CoarseCandidate.FailureReason + ": " + pipelineResult.CoarseCandidate.FailureMessage;
            if (pipelineResult.RefinementCandidate != null && !pipelineResult.RefinementCandidate.Success)
                result.EccFailureReason = pipelineResult.RefinementCandidate.FailureReason + ": " + pipelineResult.RefinementCandidate.FailureMessage;
            return result;
        }

        private static void FillScores(SampleAlignmentResult result, MatcherPipelineResult pipelineResult)
        {
            result.NccScore = pipelineResult != null && pipelineResult.CoarseCandidate != null ? pipelineResult.CoarseCandidate.RawScore : double.NaN;
            result.EccCorrelation = pipelineResult != null && pipelineResult.RefinementCandidate != null ? pipelineResult.RefinementCandidate.RawScore : double.NaN;
        }

        private static SampleAlignmentResult BuildResult(
            SampleAlignmentMethod method,
            double[,] h,
            string variant,
            SampleAlignmentContext ctx)
        {
            var r = new SampleAlignmentResult { Method = method, Success = true, CapturedToSampleTransform = h, PreprocessingVariant = variant, TranslationX = h[0, 2], TranslationY = h[1, 2], RotationDeg = Math.Atan2(h[1, 0], h[0, 0]) * 180 / Math.PI, Scale = Math.Sqrt(h[0, 0] * h[0, 0] + h[1, 0] * h[1, 0]) };
            r.OverlapRatio = EstimateOverlap(ctx, h);
            return r;
        }

        private static double EstimateOverlap(SampleAlignmentContext c, double[,] h)
        {
            var sx = c.SampleImage.Width; var sy = c.SampleImage.Height; var cx = c.CapturedImage.Width; var cy = c.CapturedImage.Height;
            var ox = Math.Max(0, Math.Min(sx, h[0, 2] + cx) - Math.Max(0, h[0, 2]));
            var oy = Math.Max(0, Math.Min(sy, h[1, 2] + cy) - Math.Max(0, h[1, 2]));
            return (ox * oy) / Math.Max(1.0, cx * cy);
        }

        private static MatcherOptions ToMatcherOptions(SampleAlignmentOptions options, string variant)
        {
            return new MatcherOptions
            {
                NccMinScore = options.NccMinScore,
                MinCorrelation = options.EccMinCorrelation,
                MinOverlapRatio = options.MinOverlapRatio,
                MaxTranslationPixels = options.MaxTranslationPixels,
                MaxAbsRotationDeg = options.MaxAbsRotationDeg,
                MinScale = options.MinScale,
                MaxScale = options.MaxScale,
                PyramidLevels = options.PyramidLevels,
                MaxIterations = options.EccIterations,
                Epsilon = options.EccEpsilon,
                PreprocessingVariant = variant
            };
        }

        private static Transform2D ToTransform(double[,] matrix)
        {
            return matrix == null ? null : new Transform2D(matrix);
        }

        private static int? ParseOrderIndex(string sampleTileId)
        {
            int value;
            return int.TryParse(sampleTileId, out value) ? (int?)value : null;
        }

        private static SampleAlignmentResult PreferMoreInformative(SampleAlignmentResult current, SampleAlignmentResult candidate)
        {
            if (current == null) return candidate;
            if (!double.IsNaN(candidate.NccScore) && double.IsNaN(current.NccScore)) return candidate;
            if (!double.IsNaN(candidate.EccCorrelation) && double.IsNaN(current.EccCorrelation)) return candidate;
            return current;
        }

        private static void Validate(SampleAlignmentContext c)
        {
            if (c == null) throw new ArgumentNullException("context");
            if (c.SampleImage == null) throw new ArgumentException("SampleImage is required");
            if (c.CapturedImage == null) throw new ArgumentException("CapturedImage is required");
        }
    }

    internal sealed class RejectingMatcher : IMatcher
    {
        private readonly string _reason;
        public RejectingMatcher(string reason) { _reason = reason; }
        public event EventHandler<MatchResultEventArgs> MatchResultAvailable; public string MatcherName { get { return "RejectingMatcher"; } }
        public MatchResult Match(
            MatchRequest request,
            System.Threading.CancellationToken cancellationToken)
        {
            return MatchResult.Failed(MatcherName, MatchFailureReason.RuntimeFailure, _reason);
        }
        public string ToString(MatchResult result) { return result.ToString(); }
        public void Dispose() { }
    }
}
