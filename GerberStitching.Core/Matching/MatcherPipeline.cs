using System;
using System.Threading;
using GerberViewer.Stitching.Transforms;

namespace GerberViewer.Stitching.Matching
{
    public sealed class MatcherPipelineResult
    {
        public bool Success { get; set; }
        public MatcherKind? CoarseMatcherKind { get; set; }
        public MatcherKind? RefinementMatcherKind { get; set; }
        public MatchResult CoarseCandidate { get; set; }
        public MatchResult RefinementCandidate { get; set; }
        public MatchResult SelectedCandidate { get; set; }
        public string SelectedStage { get; set; }
        public string SelectionReason { get; set; }
        public double SelectedCommonScore { get; set; } = double.NaN;
        public bool UsedCoarseFallback { get; set; }
        public bool UsedRefinementFromExpected { get; set; }
        public string FailureReason { get; set; }

        // [Tony] [Change time: 2026-08-01] [Purpose: Retain one-release source compatibility while production
        // decisions use generic candidate names.]
        [Obsolete("Use SelectedCandidate.")]
        public MatchResult FinalResult
        {
            get {
                return SelectedCandidate;
            }
            set {
                SelectedCandidate = value;
            }
        }
        [Obsolete("Use CoarseCandidate.")]
        public MatchResult NccResult
        {
            get {
                return CoarseCandidate;
            }
            set {
                CoarseCandidate = value;
            }
        }
        [Obsolete("Use RefinementCandidate.")]
        public MatchResult EccResult
        {
            get {
                return RefinementCandidate;
            }
            set {
                RefinementCandidate = value;
            }
        }
        [Obsolete("Use UsedCoarseFallback.")]
        public bool UsedNccOnlyFallback
        {
            get {
                return UsedCoarseFallback;
            }
            set {
                UsedCoarseFallback = value;
            }
        }
        [Obsolete("Use UsedRefinementFromExpected.")]
        public bool UsedExpectedEccInitialization
        {
            get {
                return UsedRefinementFromExpected;
            }
            set {
                UsedRefinementFromExpected = value;
            }
        }
    }

    public sealed class MatcherPipeline
    {
        private readonly IMatcherFactory _factory;
        private readonly EventHandler<MatchResultEventArgs> _resultSink;

        public MatcherPipeline() : this(new MatcherFactory())
        {
        }

        public MatcherPipeline(IMatcherFactory factory) : this(factory, null)
        {
        }

        public MatcherPipeline(IMatcherFactory factory, EventHandler<MatchResultEventArgs> resultSink)
        {
            if (factory == null)
                throw new ArgumentNullException("factory");
            _factory = factory;
            _resultSink = resultSink;
        }

        public MatcherPipelineResult MatchDirect(MatchRequest request, bool allowNccOnlyAcceptance,
                                                 bool allowEccFromExpectedWhenNccFails,
                                                 CancellationToken cancellationToken)
        {
            return MatchDirect(request, MatcherKind.HalconNcc, MatcherKind.PyramidEcc, allowNccOnlyAcceptance,
                               allowEccFromExpectedWhenNccFails, cancellationToken);
        }

        public MatcherPipelineResult MatchDirect(MatchRequest request, MatcherKind? coarseKind,
                                                 MatcherKind? refinementKind, bool allowCoarseOnlyAcceptance,
                                                 bool allowRefinementFromExpectedWhenCoarseFails,
                                                 CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            cancellationToken.ThrowIfCancellationRequested();

            MatchResult coarseCandidate = null;
            if (coarseKind.HasValue)
            {
                using (var coarse = _factory.Create(coarseKind.Value, _resultSink)) coarseCandidate =
                    coarse.Match(CloneRequest(request, request.InitialMovingToReferenceTransform), cancellationToken);
            }

            if (coarseCandidate != null && coarseCandidate.Success)
            {
                if (!refinementKind.HasValue)
                    return Success("COARSE_ONLY", "RefinementNotConfigured", coarseKind, refinementKind,
                                   coarseCandidate, coarseCandidate, null, true, false);
                MatchResult refinementCandidate;
                using (var refinement = _factory.Create(refinementKind.Value, _resultSink))
                {
                    refinementCandidate = refinement.Match(
                        CloneRequest(request, coarseCandidate.MovingToReferenceTransform), cancellationToken);
                }

                if (refinementCandidate.Success)
                    return Success("REFINEMENT", "RefinementSucceededAfterCoarse", coarseKind, refinementKind,
                                   refinementCandidate, coarseCandidate, refinementCandidate, false, false);

                if (allowCoarseOnlyAcceptance)
                    return Success("COARSE_FALLBACK", "RefinementFailedCoarseAccepted", coarseKind, refinementKind,
                                   coarseCandidate, coarseCandidate, refinementCandidate, true, false);

                return Failed("Refinement failed after coarse pass and coarse-only acceptance is disabled.", coarseKind,
                              refinementKind, coarseCandidate, refinementCandidate);
            }

            if (refinementKind.HasValue && allowRefinementFromExpectedWhenCoarseFails &&
                request.InitialMovingToReferenceTransform != null)
            {
                MatchResult refinementFromExpected;
                using (var refinement = _factory.Create(refinementKind.Value, _resultSink))
                {
                    refinementFromExpected = refinement.Match(
                        CloneRequest(request, request.InitialMovingToReferenceTransform), cancellationToken);
                }

                if (refinementFromExpected.Success)
                    return Success("REFINEMENT_FROM_EXPECTED", "CoarseFailedRefinementSucceededFromExpected",
                                   coarseKind, refinementKind, refinementFromExpected, coarseCandidate,
                                   refinementFromExpected, false, true);

                return Failed("Coarse failed and refinement from expected transform also failed.", coarseKind,
                              refinementKind, coarseCandidate, refinementFromExpected);
            }

            return Failed(
                "Coarse failed and refinement-from-expected policy is disabled or no expected transform exists.",
                coarseKind, refinementKind, coarseCandidate, null);
        }

        private static MatcherPipelineResult Success(string stage, string reason, MatcherKind? coarseKind,
                                                     MatcherKind? refinementKind, MatchResult selected,
                                                     MatchResult coarse, MatchResult refinement, bool coarseFallback,
                                                     bool refinementFromExpected)
        {
            if (selected.Diagnostics != null)
            {
                selected.Diagnostics["DirectPipelineStage"] = stage;
                selected.Diagnostics["SelectionReason"] = reason;
                AddAlgorithmScore(selected, coarse);
                AddAlgorithmScore(selected, refinement);
            }
            return new MatcherPipelineResult { Success = true,
                                               CoarseMatcherKind = coarseKind,
                                               RefinementMatcherKind = refinementKind,
                                               SelectedStage = stage,
                                               SelectionReason = reason,
                                               SelectedCandidate = selected,
                                               CoarseCandidate = coarse,
                                               RefinementCandidate = refinement,
                                               UsedCoarseFallback = coarseFallback,
                                               UsedRefinementFromExpected = refinementFromExpected };
        }

        private static MatcherPipelineResult Failed(string reason, MatcherKind? coarseKind, MatcherKind? refinementKind,
                                                    MatchResult coarse, MatchResult refinement)
        {
            return new MatcherPipelineResult { Success = false,
                                               CoarseMatcherKind = coarseKind,
                                               RefinementMatcherKind = refinementKind,
                                               SelectedStage = "DIRECT_REJECTED",
                                               SelectionReason = "NoCandidateAccepted",
                                               CoarseCandidate = coarse,
                                               RefinementCandidate = refinement,
                                               FailureReason = reason,
                                               SelectedCandidate = null };
        }

        private static void AddAlgorithmScore(MatchResult selected, MatchResult candidate)
        {
            if (candidate == null || double.IsNaN(candidate.RawScore))
                return;
            string key;
            switch (candidate.MatcherKind)
            {
            case MatcherKind.HalconShapeModel:
                key = "HalconShapeScore";
                break;
            case MatcherKind.HalconNcc:
                key = "HalconNccScore";
                break;
            case MatcherKind.PyramidEcc:
                key = "EccCorrelation";
                break;
            case MatcherKind.PyramidPhaseCorrelation:
                key = "PhaseResponse";
                break;
            default:
                return;
            }
            selected.Diagnostics[key] = candidate.RawScore.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static MatchRequest CloneRequest(MatchRequest source, Transform2D initial)
        {
            return new MatchRequest { ReferenceImage = source.ReferenceImage,
                                      MovingImage = source.MovingImage,
                                      ReferenceMask = source.ReferenceMask,
                                      MovingMask = source.MovingMask,
                                      ReferenceRoi = source.ReferenceRoi,
                                      MovingRoi = source.MovingRoi,
                                      InitialMovingToReferenceTransform = initial,
                                      Options = source.Options,
                                      Purpose = source.Purpose,
                                      OrderIndex = source.OrderIndex,
                                      SampleTileId = source.SampleTileId,
                                      Context = source.Context,
                                      ReferenceNccModelPath = source.ReferenceNccModelPath,
                                      ReferenceNccModelRoi = source.ReferenceNccModelRoi,
                                      NccReferenceOriginRow = source.NccReferenceOriginRow,
                                      NccReferenceOriginColumn = source.NccReferenceOriginColumn,
                                      NccModelSchemaVersion = source.NccModelSchemaVersion,
                                      ReferenceShapeModelPath = source.ReferenceShapeModelPath,
                                      ReferenceShapeModelRoi = source.ReferenceShapeModelRoi,
                                      ShapeReferenceOriginRow = source.ShapeReferenceOriginRow,
                                      ShapeReferenceOriginColumn = source.ShapeReferenceOriginColumn,
                                      ShapeModelSchemaVersion = source.ShapeModelSchemaVersion,
                                      ShapeOptions = source.ShapeOptions };
        }
    }
}
