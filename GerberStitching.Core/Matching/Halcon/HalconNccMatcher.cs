using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Transforms;
using GerberViewer.Stitching.Matching.OpenCv;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Halcon
{
    public sealed class HalconNccMatcher : IMatcher
    {
        private readonly MatchResultCompletion _completion = new MatchResultCompletion();
        public event EventHandler<MatchResultEventArgs> MatchResultAvailable;

        private readonly MatcherGeometryValidator _validator = new MatcherGeometryValidator();
        private readonly HalconMatchInputAdapter _inputAdapter;
        private readonly HalconNccModelProvider _modelProvider;
        private bool _disposed;

        public HalconNccMatcher() : this(new ImageInteropService())
        {
        }

        public HalconNccMatcher(IImageInteropService imageInterop)
        {
            if (imageInterop == null) throw new ArgumentNullException("imageInterop");
            _inputAdapter = new HalconMatchInputAdapter(imageInterop);
            _modelProvider = new HalconNccModelProvider(imageInterop);
        }

        public string MatcherName { get { return "HalconNccMatcher"; } }

        public string ToString(MatchResult result)
        {
            if (result == null) throw new ArgumentNullException("result");
            return result.ToString();
        }

        public MatchResult Match(MatchRequest request, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            if (cancellationToken.IsCancellationRequested)
                return Complete(request, MatchResult.Failed(MatcherName, MatchFailureReason.Cancelled, "HALCON NCC was cancelled before start."), sw);
            var invalid = _validator.ValidateRequest(request, MatcherName);
            if (invalid != null) return Complete(request, invalid, sw);
            var options = request.Options ?? new MatcherOptions();
            HTuple row = null;
            HTuple column = null;
            HTuple angle = null;
            HTuple score = null;
            try
            {
                using (var movingHObject = _inputAdapter.ToMono8HObjectCopy(request.MovingImage))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var model = _modelProvider.GetOrCreate(request, options, cancellationToken);
                    var key = model.CacheKey;
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    HOperatorSet.FindNccModel(movingHObject, model.ModelId, options.NccAngleStartRad, options.NccAngleExtentRad, options.NccMinScore, options.NccMaxMatches, options.NccMaxOverlap, options.NccSubPixel,
                        options.NccNumLevels, out row, out column, out angle, out score);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (score == null || score.Length <= 0)
                        return Complete(request, CreateCenterFallback(request, options, "HALCON find_ncc_model returned no match above NccMinScore."), sw);

                    var bestRow = row[0].D;
                    var bestColumn = column[0].D;
                    var bestAngle = angle[0].D;
                    var bestScore = score[0].D;
                    if (bestScore < options.NccMinScore)
                        return Complete(request, CreateCenterFallback(request, options, "HALCON NCC score is below NccMinScore."), sw);

                    var movingToReference = HalconModelPoseConverter.ToMovingToReference(bestColumn, bestRow, bestAngle, model.Origin.ReferenceOriginColumn, model.Origin.ReferenceOriginRow);
                    var geometryFailure = ValidateNccGeometry(movingToReference, request.ReferenceImage, request.MovingImage, options);
                    if (geometryFailure != null) return Complete(request, CreateCenterFallback(request, options, geometryFailure.FailureMessage), sw);
                    var result = new MatchResult
                    {
                        Success = true,
                        MatcherName = MatcherName,
                        MovingToReferenceTransform = movingToReference,
                        TranslationX = movingToReference[0, 2],
                        TranslationY = movingToReference[1, 2],
                        RotationDeg = Math.Atan2(movingToReference[1, 0], movingToReference[0, 0]) * 180.0 / Math.PI,
                        Scale = 1d,
                        RawScore = bestScore,
                        NormalizedConfidence = bestScore,
                        OverlapRatio = 1d,
                        FailureReason = MatchFailureReason.None
                    };
                    result.Diagnostics["HalconCreateOperator"] = "create_ncc_model";
                    result.Diagnostics["HalconFindOperator"] = "find_ncc_model";
                    result.Diagnostics["HalconClearOperator"] = "clear_ncc_model";
                    result.Diagnostics["ModelCacheKey"] = key;
                    result.Diagnostics["CacheHit"] = model.CacheHit ? "true" : "false";
                    result.Diagnostics["ModelType"] = "NCC";
                    result.Diagnostics["ModelSchemaVersion"] = model.SchemaVersion.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["ModelSource"] = model.Source;
                    result.Diagnostics["ModelRoi"] = model.ModelRoi.X + "," + model.ModelRoi.Y + "," + model.ModelRoi.Width + "," + model.ModelRoi.Height;
                    result.Diagnostics["DomainCenterLocal"] = model.Origin.DomainCenterRowLocal.ToString("R", CultureInfo.InvariantCulture) + "," + model.Origin.DomainCenterColumnLocal.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["ReferenceOrigin"] = model.Origin.ReferenceOriginRow.ToString("R", CultureInfo.InvariantCulture) + "," + model.Origin.ReferenceOriginColumn.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["ConfiguredOriginOffset"] = model.Origin.ConfiguredOffsetRow.ToString("R", CultureInfo.InvariantCulture) + "," + model.Origin.ConfiguredOffsetColumn.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["VerifiedOriginOffset"] = model.Origin.VerifiedOffsetRow.ToString("R", CultureInfo.InvariantCulture) + "," + model.Origin.VerifiedOffsetColumn.ToString("R", CultureInfo.InvariantCulture);
                    result.Diagnostics["ModelOrigin"] = model.Origin.Convention;
                    result.Diagnostics["HalconRow"] = bestRow.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconColumn"] = bestColumn.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconAngleRad"] = bestAngle.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["HalconNccScore"] = bestScore.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics["ReferenceToMovingTransform"] = HalconModelPoseConverter.Format(movingToReference.Invert());
                    result.Diagnostics["MovingToReferenceTransform"] = HalconModelPoseConverter.Format(movingToReference);
                    result.Diagnostics["TransformDirection"] = "MovingImage -> ReferenceImage";
                    return Complete(request, result, sw);
                }
            }
            catch (OperationCanceledException)
            {
                return Complete(request, MatchResult.Failed(MatcherName, MatchFailureReason.Cancelled, "HALCON NCC was cancelled."), sw);
            }
            catch (Exception ex)
            {
                return Complete(request, CreateCenterFallback(request, request == null ? null : request.Options, ex.Message), sw);
            }
            finally
            {
                DisposeTuple(row); DisposeTuple(column); DisposeTuple(angle); DisposeTuple(score);
            }
        }

        public int CachedModelCount { get { return _modelProvider.CachedModelCount; } }

        public void Dispose()
        {
            if (_disposed) return;
            _modelProvider.Dispose();
            _disposed = true;
        }


        private MatchResult CreateCenterFallback(MatchRequest request, MatcherOptions options, string reason)
        {
            // [Codex] [Change time: 2026-07-26] [Purpose: A missing NCC match is failure; an expected/center pose must never become measured success.]
            var result = MatchResult.Failed(MatcherName, MatchFailureReason.ResponseBelowThreshold, reason);
            result.OverlapRatio = request == null ? 0d : MatcherGeometryValidator.CalculateSameSizeOverlap(request.ReferenceImage, request.MovingImage);
            result.Diagnostics["Fallback"] = "false";
            result.Diagnostics["FailureReason"] = reason ?? string.Empty;
            return result;
        }

        private static Mat WarpMovingToReference(Mat movingImage, Transform2D movingToReference, OpenCvSharp.Size referenceSize)
        {
            var warp = new Mat(2, 3, MatType.CV_64FC1);
            try
            {
                warp.Set<double>(0, 0, movingToReference[0, 0]);
                warp.Set<double>(0, 1, movingToReference[0, 1]);
                warp.Set<double>(0, 2, movingToReference[0, 2]);
                warp.Set<double>(1, 0, movingToReference[1, 0]);
                warp.Set<double>(1, 1, movingToReference[1, 1]);
                warp.Set<double>(1, 2, movingToReference[1, 2]);
                var aligned = new Mat();
                Cv2.WarpAffine(movingImage, aligned, warp, referenceSize, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
                return aligned;
            }
            finally
            {
                warp.Dispose();
            }
        }

        private MatchResult ValidateNccGeometry(Transform2D movingToReference, Mat referenceImage, Mat movingImage, MatcherOptions options)
        {
            if (movingToReference == null) return MatchResult.Failed(MatcherName, MatchFailureReason.NonFiniteTransform, "HALCON NCC did not produce a transform.");
            var translationFailure = _validator.ValidateTranslation(movingToReference[0, 2], movingToReference[1, 2], 1.0, options, MatcherName);
            if (translationFailure != null) return translationFailure;
            var rotationDeg = Math.Atan2(movingToReference[1, 0], movingToReference[0, 0]) * 180.0 / Math.PI;
            if (Math.Abs(rotationDeg) > options.MaxAbsRotationDeg) return MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected, "HALCON NCC rotation exceeds MaxAbsRotationDeg.");
            var scaleX = Math.Sqrt(movingToReference[0, 0] * movingToReference[0, 0] + movingToReference[1, 0] * movingToReference[1, 0]);
            var scaleY = Math.Sqrt(movingToReference[0, 1] * movingToReference[0, 1] + movingToReference[1, 1] * movingToReference[1, 1]);
            if (scaleX < options.MinScale || scaleX > options.MaxScale || scaleY < options.MinScale || scaleY > options.MaxScale) return MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected, "HALCON NCC scale is outside configured bounds.");
            var overlap = MatcherGeometryValidator.CalculateSameSizeOverlap(referenceImage, movingImage);
            if (overlap < options.MinOverlapRatio) return MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected, "HALCON NCC image overlap is below MinOverlapRatio.");
            return null;
        }

        private static void DisposeTuple(HTuple tuple) { if (tuple != null) tuple.Dispose(); }

        private MatchResult Complete(MatchRequest request, MatchResult result, Stopwatch stopwatch)
        {
            return _completion.Complete(this, MatchResultAvailable, request, result, stopwatch, MatcherName);
        }

    }
}
