using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GerberViewer.Stitching.Transforms;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.OpenCv
{
    public sealed class PyramidEccMatcher : IMatcher
    {
        private readonly MatchResultCompletion _completion = new MatchResultCompletion();
        public event EventHandler<MatchResultEventArgs> MatchResultAvailable;

        private readonly MatcherGeometryValidator _validator = new MatcherGeometryValidator();
        public string MatcherName
        {
            get {
                return "PyramidEccMatcher";
            }
        }
        public string ToString(MatchResult result)
        {
            if (result == null)
                throw new ArgumentNullException("result");
            return result.ToString();
        }

        public MatchResult Match(MatchRequest request, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            if (cancellationToken.IsCancellationRequested)
                return Complete(
                    request,
                    MatchResult.Failed(MatcherName, MatchFailureReason.Cancelled, "ECC was cancelled before start."),
                    sw);

            var invalid = _validator.ValidateRequest(request, MatcherName);
            if (invalid != null)
                return Complete(request, invalid, sw);
            var options = request.Options ?? new MatcherOptions();

            var referencePyramid = new List<Mat>();
            var movingPyramid = new List<Mat>();
            Mat currentWarp = null;
            try
            {
                var referenceRoiBounds = MatcherGeometryValidator.NormalizeRoi(
                    request.ReferenceImage,
                    request.ReferenceRoi);
                var movingRoiBounds = MatcherGeometryValidator.NormalizeRoi(
                    request.MovingImage,
                    request.MovingRoi);

                using (var referenceRoi = CropCopy(
                    request.ReferenceImage,
                    referenceRoiBounds))
                using (var movingRoi = CropCopy(
                    request.MovingImage,
                    movingRoiBounds))
                using (var reference32 = ToGray32FCopy(referenceRoi))
                using (var moving32 = ToGray32FCopy(movingRoi))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var preparedInvalid = _validator.ValidatePreparedPair(reference32, moving32, options, MatcherName);
                    if (preparedInvalid != null)
                        return Complete(request, preparedInvalid, sw);

                    BuildPyramids(reference32, moving32, Math.Max(1, options.PyramidLevels), referencePyramid,
                                  movingPyramid);
                    var fullReferenceToMoving =
                        InitialReferenceToMoving(request.InitialMovingToReferenceTransform, options.EccMotionModel);
                    double finalCorrelation = double.NaN;
                    var motionType = ToOpenCvMotionType(options.EccMotionModel);

                    for (int level = referencePyramid.Count - 1; level >= 0; level--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (currentWarp != null)
                            currentWarp.Dispose();
                        var scale = referencePyramid[level].Cols / (double)Math.Max(1, reference32.Cols);
                        currentWarp = ToWarpMatAtScale(fullReferenceToMoving, scale, options.EccMotionModel);
                        var criteria = new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps,
                                                        Math.Max(1, options.MaxIterations), options.Epsilon);
                        finalCorrelation = Cv2.FindTransformECC(referencePyramid[level], movingPyramid[level],
                                                                currentWarp, motionType, criteria);
                        fullReferenceToMoving = FromWarpMatAtScale(currentWarp, scale);
                    }

                    var movingToReferenceMatrix = InvertAffine(fullReferenceToMoving);
                    var resultInvalid = ValidateMovingToReference(movingToReferenceMatrix, finalCorrelation, options);
                    if (resultInvalid != null)
                        return Complete(request, resultInvalid, sw);

                    var transform = new Transform2D(movingToReferenceMatrix);
                    var translationX = movingToReferenceMatrix[0, 2];
                    var translationY = movingToReferenceMatrix[1, 2];
                    var rotationDeg =
                        Math.Atan2(movingToReferenceMatrix[1, 0], movingToReferenceMatrix[0, 0]) * 180.0 / Math.PI;
                    if (Math.Abs(rotationDeg) > request.Options.MaxAbsRotationDeg)
                        return Complete(request,
                                        MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected,
                                                           "Shape rotation exceeds MaxAbsRotationDeg."),
                                        sw);
                    var scaleValue = Math.Sqrt(movingToReferenceMatrix[0, 0] * movingToReferenceMatrix[0, 0] +
                                               movingToReferenceMatrix[1, 0] * movingToReferenceMatrix[1, 0]);
                    var result = new MatchResult { Success = true,
                                                   MatcherName = MatcherName,
                                                   MovingToReferenceTransform = transform,
                                                   TranslationX = translationX,
                                                   TranslationY = translationY,
                                                   RotationDeg = rotationDeg,
                                                   Scale = scaleValue,
                                                   RawScore = finalCorrelation,
                                                   NormalizedConfidence = NormalizeCorrelation(finalCorrelation),
                                                   OverlapRatio = MatcherGeometryValidator.CalculateSameSizeOverlap(
                                                       reference32, moving32),
                                                   FailureReason = MatchFailureReason.None };
                    result.Diagnostics["OpenCvFunction"] = "Cv2.FindTransformECC";
                    result.Diagnostics["MotionModel"] = options.EccMotionModel.ToString();
                    result.Diagnostics["PyramidLevels"] = referencePyramid.Count.ToString();
                    result.Diagnostics["TransformDirection"] = "MovingImage -> ReferenceImage";
                    return Complete(request, result, sw);
                }
            }
            catch (OperationCanceledException)
            {
                return Complete(
                    request, MatchResult.Failed(MatcherName, MatchFailureReason.Cancelled, "ECC was cancelled."), sw);
            }
            catch (OpenCVException ex)
            {
                return Complete(request,
                                MatchResult.Failed(MatcherName, MatchFailureReason.RuntimeFailure,
                                                   "ECC did not converge: " + ex.Message),
                                sw);
            }
            catch (Exception ex)
            {
                return Complete(request, MatchResult.Failed(MatcherName, MatchFailureReason.RuntimeFailure, ex.Message),
                                sw);
            }
            finally
            {
                if (currentWarp != null)
                    currentWarp.Dispose();
                DisposeAll(referencePyramid);
                DisposeAll(movingPyramid);
            }
        }

        public void Dispose()
        {
        }

        private MatchResult Complete(MatchRequest request, MatchResult result, Stopwatch stopwatch)
        {
            return _completion.Complete(this, MatchResultAvailable, request, result, stopwatch, MatcherName);
        }

        private MatchResult ValidateMovingToReference(double[,] matrix, double correlation, MatcherOptions options)
        {
            var translationX = matrix[0, 2];
            var translationY = matrix[1, 2];
            var rotationDeg = Math.Atan2(matrix[1, 0], matrix[0, 0]) * 180.0 / Math.PI;
            var scaleValue = Math.Sqrt(matrix[0, 0] * matrix[0, 0] + matrix[1, 0] * matrix[1, 0]);
            var translationInvalid = _validator.ValidateTranslation(translationX, translationY,
                                                                    Math.Max(correlation, 0d), options, MatcherName);
            if (translationInvalid != null)
                return translationInvalid;
            if (double.IsNaN(correlation) || double.IsInfinity(correlation) || correlation < options.MinCorrelation)
                return MatchResult.Failed(MatcherName, MatchFailureReason.CorrelationBelowThreshold,
                                          "ECC correlation is below MinCorrelation.");
            if (Math.Abs(rotationDeg) > options.MaxAbsRotationDeg)
                return MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected,
                                          "ECC rotation exceeds MaxAbsRotationDeg.");
            if (scaleValue < options.MinScale || scaleValue > options.MaxScale)
                return MatchResult.Failed(MatcherName, MatchFailureReason.GeometryRejected,
                                          "ECC scale is outside configured range.");
            return null;
        }

        private static Mat CropCopy(Mat image, Rect roi)
        {
            using (var view = new Mat(image, roi)) return view.Clone();
        }

        private static Mat ToGray32FCopy(Mat source)
        {
            Mat gray = null;
            try
            {
                if (source.Channels() == 1)
                    gray = source.Clone();
                else if (source.Channels() == 3)
                {
                    gray = new Mat();
                    Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
                }
                else if (source.Channels() == 4)
                {
                    gray = new Mat();
                    Cv2.CvtColor(source, gray, ColorConversionCodes.BGRA2GRAY);
                }
                else
                    throw new NotSupportedException("Unsupported channel count for ECC: " + source.Channels());

                var gray32 = new Mat();
                gray.ConvertTo(gray32, MatType.CV_32F);
                return gray32;
            }
            finally
            {
                if (gray != null)
                    gray.Dispose();
            }
        }

        private static void BuildPyramids(Mat reference, Mat moving, int levels, IList<Mat> referencePyramid,
                                          IList<Mat> movingPyramid)
        {
            referencePyramid.Add(reference.Clone());
            movingPyramid.Add(moving.Clone());
            for (int level = 1; level < levels; level++)
            {
                var prevReference = referencePyramid[level - 1];
                var prevMoving = movingPyramid[level - 1];
                if (prevReference.Cols < 32 || prevReference.Rows < 32)
                    break;
                var nextReference = new Mat();
                var nextMoving = new Mat();
                Cv2.PyrDown(prevReference, nextReference);
                Cv2.PyrDown(prevMoving, nextMoving);
                referencePyramid.Add(nextReference);
                movingPyramid.Add(nextMoving);
            }
        }

        private static double[,] InitialReferenceToMoving(Transform2D initialMovingToReference,
                                                          EccMotionModel motionModel)
        {
            var matrix = initialMovingToReference == null ? Transform2D.Identity.ToArray()
                                                          : initialMovingToReference.Invert().ToArray();
            return RestrictMotion(matrix, motionModel);
        }

        private static MotionTypes ToOpenCvMotionType(EccMotionModel motionModel)
        {
            if (motionModel == EccMotionModel.Translation)
                return MotionTypes.Translation;
            if (motionModel == EccMotionModel.Affine)
                return MotionTypes.Affine;
            return MotionTypes.Euclidean;
        }

        private static Mat ToWarpMatAtScale(double[,] fullReferenceToMoving, double scale, EccMotionModel motionModel)
        {
            var restricted = RestrictMotion(fullReferenceToMoving, motionModel);
            var warp = new Mat(2, 3, MatType.CV_32FC1);
            warp.Set<float>(0, 0, (float)restricted[0, 0]);
            warp.Set<float>(0, 1, (float)restricted[0, 1]);
            warp.Set<float>(0, 2, (float)(restricted[0, 2] * scale));
            warp.Set<float>(1, 0, (float)restricted[1, 0]);
            warp.Set<float>(1, 1, (float)restricted[1, 1]);
            warp.Set<float>(1, 2, (float)(restricted[1, 2] * scale));
            return warp;
        }

        private static double[,] FromWarpMatAtScale(Mat warp, double scale)
        {
            var s = Math.Abs(scale) < 1e-12 ? 1d : scale;
            return new[,] { { (double)warp.At<float>(0, 0), (double)warp.At<float>(0, 1), warp.At<float>(0, 2) / s },
                            { (double)warp.At<float>(1, 0), (double)warp.At<float>(1, 1), warp.At<float>(1, 2) / s },
                            { 0d, 0d, 1d } };
        }

        private static double[,] RestrictMotion(double[,] matrix, EccMotionModel motionModel)
        {
            if (motionModel == EccMotionModel.Translation)
                return new[,] { { 1d, 0d, matrix[0, 2] }, { 0d, 1d, matrix[1, 2] }, { 0d, 0d, 1d } };
            if (motionModel == EccMotionModel.Euclidean)
            {
                var angle = Math.Atan2(matrix[1, 0], matrix[0, 0]);
                var c = Math.Cos(angle);
                var s = Math.Sin(angle);
                return new[,] { { c, -s, matrix[0, 2] }, { s, c, matrix[1, 2] }, { 0d, 0d, 1d } };
            }
            return new[,] { { matrix[0, 0], matrix[0, 1], matrix[0, 2] },
                            { matrix[1, 0], matrix[1, 1], matrix[1, 2] },
                            { 0d, 0d, 1d } };
        }

        private static double[,] InvertAffine(double[,] matrix)
        {
            return new Transform2D(matrix).Invert().ToArray();
        }

        private static double NormalizeCorrelation(double correlation)
        {
            return Math.Max(0d, Math.Min(1d, (correlation + 1d) / 2d));
        }

        private static void DisposeAll(IList<Mat> mats)
        {
            foreach (var mat in mats)
                mat.Dispose();
        }
    }
}
