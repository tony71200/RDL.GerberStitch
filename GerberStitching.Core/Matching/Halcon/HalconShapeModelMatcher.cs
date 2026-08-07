using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Matching.OpenCv;
using HalconDotNet;

namespace GerberViewer.Stitching.Matching.Halcon
{
    // [Codex] [Change time: 2026-07-27] [Purpose: Match AlignByShapeModel search behavior while preserving canonical Moving-to-Reference output.]
    public sealed class HalconShapeModelMatcher : IMatcher
    {
        private readonly MatchResultCompletion _completion = new MatchResultCompletion();
        public event EventHandler<MatchResultEventArgs> MatchResultAvailable;

        private readonly HalconMatchInputAdapter _adapter;
        private readonly HalconShapeModelProvider _provider;
        private readonly MatcherGeometryValidator _validator = new MatcherGeometryValidator();
        private bool _disposed;

        public HalconShapeModelMatcher() : this(new ImageInteropService()) { }

        public HalconShapeModelMatcher(IImageInteropService interop)
        {
            _adapter = new HalconMatchInputAdapter(interop);
            _provider = new HalconShapeModelProvider(interop);
        }

        public string MatcherName { get { return "HalconShapeModelMatcher"; } }
        public string ToString(MatchResult result) { if (result == null) throw new ArgumentNullException("result"); return result.ToString(); }
        public int CachedModelCount { get { return _provider.CachedModelCount; } }

        public MatchResult Match(MatchRequest request, CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            if (token.IsCancellationRequested)
                return Complete(request, Fail(MatchFailureReason.Cancelled, "HALCON Shape was cancelled before start."), stopwatch);

            var invalid = _validator.ValidateRequest(request, MatcherName);
            if (invalid != null) return Complete(request, invalid, stopwatch);

            var invalidShapeRoi = ValidateOptionalShapeRoi(request);
            if (invalidShapeRoi != null) return Complete(request, invalidShapeRoi, stopwatch);

            var options = request.ShapeOptions ?? new HalconShapeModelOptions();
            HTuple row = null;
            HTuple column = null;
            HTuple angle = null;
            HTuple score = null;

            try
            {
                using (var moving = _adapter.ToMono8HObjectCopy(request.MovingImage))
                {
                    var model = _provider.GetOrCreate(request, token);
                    token.ThrowIfCancellationRequested();

                    HOperatorSet.SetShapeModelParam(model.ModelId, "border_shape_models", "true");
                    HOperatorSet.FindShapeModel(
                        moving,
                        model.ModelId,
                        options.AngleStartRad,
                        options.AngleExtentRad,
                        options.MinScore,
                        options.NumMatches,
                        options.MaxOverlap,
                        options.SubPixel,
                        options.SearchNumLevels,
                        options.Greediness,
                        out row,
                        out column,
                        out angle,
                        out score);

                    token.ThrowIfCancellationRequested();
                    if (score == null || score.Length == 0)
                        return Complete(request, Fail(MatchFailureReason.ResponseBelowThreshold, "find_shape_model returned no result."), stopwatch);

                    var rawScore = score[0].D;
                    if (rawScore < options.MinScore)
                        return Complete(request, Fail(MatchFailureReason.ResponseBelowThreshold, "Shape score is below MinScore."), stopwatch);

                    var transform = HalconModelPoseConverter.ToMovingToReference(
                        column[0].D,
                        row[0].D,
                        angle[0].D,
                        1.0,
                        model.Origin.ReferenceOriginColumn,
                        model.Origin.ReferenceOriginRow);

                    var translationFailure = _validator.ValidateTranslation(
                        transform[0, 2],
                        transform[1, 2],
                        1.0,
                        request.Options,
                        MatcherName);
                    if (translationFailure != null) return Complete(request, translationFailure, stopwatch);

                    var rotationDeg = Math.Atan2(transform[1, 0], transform[0, 0]) * 180.0 / Math.PI;
                    if (Math.Abs(rotationDeg) > request.Options.MaxAbsRotationDeg)
                        return Complete(request, Fail(MatchFailureReason.GeometryRejected, "Shape rotation exceeds MaxAbsRotationDeg."), stopwatch);

                    var result = new MatchResult
                    {
                        Success = true,
                        MatcherName = MatcherName,
                        MatcherKind = MatcherKind.HalconShapeModel,
                        MovingToReferenceTransform = transform,
                        TranslationX = transform[0, 2],
                        TranslationY = transform[1, 2],
                        RotationDeg = rotationDeg,
                        Scale = 1.0,
                        RawScore = rawScore,
                        NormalizedConfidence = rawScore,
                        OverlapRatio = MatcherGeometryValidator.CalculateSameSizeOverlap(request.ReferenceImage, request.MovingImage),
                        FailureReason = MatchFailureReason.None
                    };

                    result.Diagnostics["ModelType"] = "Shape/Rigid";
                    result.Diagnostics["HalconCreateOperator"] = "create_shape_model";
                    result.Diagnostics["HalconFindOperator"] = "find_shape_model";
                    result.Diagnostics["HalconClearOperator"] = "clear_shape_model";
                    result.Diagnostics["ModelPath"] = request.ReferenceShapeModelPath ?? "runtime";
                    result.Diagnostics["ModelCacheKey"] = model.CacheKey;
                    result.Diagnostics["CacheHit"] = model.CacheHit.ToString();
                    result.Diagnostics["ModelSource"] = model.Source;
                    result.Diagnostics["ModelSchemaVersion"] = model.SchemaVersion.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["ModelRoi"] = model.ModelRoi.ToString();
                    result.Diagnostics["ReferenceOrigin"] = model.Origin.ReferenceOriginRow + "," + model.Origin.ReferenceOriginColumn;
                    result.Diagnostics["OriginConvention"] = model.Origin.Convention;
                    result.Diagnostics["HalconRow"] = row[0].D.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconColumn"] = column[0].D.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconAngleRad"] = angle[0].D.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconShapeScore"] = rawScore.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["DetectedDxLikeHDevelop"] = (column[0].D - model.Origin.ReferenceOriginColumn).ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["DetectedDyLikeHDevelop"] = (row[0].D - model.Origin.ReferenceOriginRow).ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["TransformDirection"] = "MovingImage -> ReferenceImage";
                    return Complete(request, result, stopwatch);
                }
            }
            catch (OperationCanceledException)
            {
                return Complete(request, Fail(MatchFailureReason.Cancelled, "HALCON Shape was cancelled."), stopwatch);
            }
            catch (NotSupportedException ex)
            {
                return Complete(request, Fail(MatchFailureReason.UnsupportedMatcher, ex.Message), stopwatch);
            }
            catch (HalconImageInteropException ex)
            {
                return Complete(request, Fail(MatchFailureReason.RuntimeFailure,
                    ex.Message + " Shape matching was rejected; no score or transform was published."), stopwatch);
            }
            catch (InvalidOperationException ex)
            {
                var reason = ex.Message.IndexOf("ROI", StringComparison.OrdinalIgnoreCase) >= 0
                    ? MatchFailureReason.InvalidRoi
                    : MatchFailureReason.LowTexture;
                return Complete(request, Fail(reason, ex.Message), stopwatch);
            }
            catch (Exception ex)
            {
                return Complete(request, Fail(MatchFailureReason.RuntimeFailure, ex.Message), stopwatch);
            }
            finally
            {
                DisposeTuple(row);
                DisposeTuple(column);
                DisposeTuple(angle);
                DisposeTuple(score);
            }
        }

        private MatchResult ValidateOptionalShapeRoi(MatchRequest request)
        {
            if (!request.ReferenceShapeModelRoi.HasValue) return null;
            var roi = request.ReferenceShapeModelRoi.Value;
            if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0 ||
                roi.Right > request.ReferenceImage.Cols || roi.Bottom > request.ReferenceImage.Rows)
                return Fail(MatchFailureReason.InvalidRoi, "Configured shape model ROI is outside reference image.");
            return null;
        }

        private MatchResult Fail(MatchFailureReason reason, string message)
        {
            var result = MatchResult.Failed(MatcherName, reason, message);
            result.MatcherKind = MatcherKind.HalconShapeModel;
            return result;
        }

        private MatchResult Complete(MatchRequest request, MatchResult result, Stopwatch stopwatch)
        {
            return _completion.Complete(this, MatchResultAvailable, request, result, stopwatch, MatcherName);
        }

        private static void DisposeTuple(HTuple tuple)
        {
            if (tuple != null) tuple.Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _provider.Dispose();
            _disposed = true;
        }
    }
}
