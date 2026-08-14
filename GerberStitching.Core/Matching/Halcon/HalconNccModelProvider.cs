using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using GerberViewer.Stitching.Imaging.ImageInterop;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Halcon
{
    // [Tony] [Change time: 2026-07-26] [Purpose: Give one IDisposable owner responsibility for every cached HALCON NCC
    // model ID.]
    public sealed class HalconNccModelProvider : IDisposable
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly HalconMatchInputAdapter _inputAdapter;
        private bool _disposed;

        public HalconNccModelProvider() : this(new ImageInteropService())
        {
        }
        public HalconNccModelProvider(IImageInteropService interop)
        {
            _inputAdapter = new HalconMatchInputAdapter(interop);
        }
        public int CachedModelCount
        {
            get {
                lock (_sync) return _cache.Count;
            }
        }

        public HalconNccModel GetOrCreate(MatchRequest request, MatcherOptions options, CancellationToken token)
        {
            if (request == null)
                throw new ArgumentNullException("request");
            if (options == null)
                throw new ArgumentNullException("options");
            var key = BuildCacheKey(request, options);
            lock (_sync)
            {
                ThrowIfDisposed();
                Entry found;
                if (_cache.TryGetValue(key, out found))
                {
                    found.UseCount++;
                    return found.ToModel(key, true);
                }
            }
            token.ThrowIfCancellationRequested();
            var created = CreateEntry(request, options);
            lock (_sync)
            {
                if (_disposed)
                {
                    created.Dispose();
                    throw new ObjectDisposedException(GetType().FullName);
                }
                Entry race;
                if (_cache.TryGetValue(key, out race))
                {
                    created.Dispose();
                    race.UseCount++;
                    return race.ToModel(key, true);
                }
                _cache.Add(key, created);
                return created.ToModel(key, false);
            }
        }

        private Entry CreateEntry(MatchRequest request, MatcherOptions options)
        {
            HTuple modelId = null;
            HObject modelImage = null;
            HObject domain = null;
            HTuple area = null, centerRow = null, centerColumn = null, verifiedRow = null, verifiedColumn = null;
            Mat roiMat = null;
            try
            {
                // Legacy .ncm files have no trustworthy origin/ROI contract. Regenerate instead of silently guessing.
                var roi = ResolveModelRoi(request, options);
                var hasVersionedDiskModel =
                    request.NccModelSchemaVersion == 1 && request.ReferenceNccModelRoi.HasValue &&
                    request.NccReferenceOriginRow.HasValue && request.NccReferenceOriginColumn.HasValue &&
                    !string.IsNullOrWhiteSpace(request.ReferenceNccModelPath) &&
                    File.Exists(request.ReferenceNccModelPath);
                double localRow;
                double localColumn;
                if (hasVersionedDiskModel)
                {
                    HOperatorSet.ReadNccModel(request.ReferenceNccModelPath, out modelId);
                    localRow = (roi.Height - 1) / 2.0;
                    localColumn = (roi.Width - 1) / 2.0;
                }
                else
                {
                    roiMat = new Mat(request.ReferenceImage, roi).Clone();
                    modelImage = _inputAdapter.ToMono8HObjectCopy(roiMat);
                    HOperatorSet.GetDomain(modelImage, out domain);
                    HOperatorSet.AreaCenter(domain, out area, out centerRow, out centerColumn);
                    localRow = centerRow[0].D;
                    localColumn = centerColumn[0].D;
                    HOperatorSet.CreateNccModel(modelImage, options.NccNumLevels, options.NccAngleStartRad,
                                                options.NccAngleExtentRad, options.NccAngleStepRad, options.NccMetric,
                                                out modelId);
                }
                var referenceOriginRow = request.NccReferenceOriginRow ?? 0.0;
                var referenceOriginColumn = request.NccReferenceOriginColumn ?? 0.0;
                var absoluteCenterRow = roi.Y + localRow;
                var absoluteCenterColumn = roi.X + localColumn;
                var offsetRow = referenceOriginRow - absoluteCenterRow;
                var offsetColumn = referenceOriginColumn - absoluteCenterColumn;
                HOperatorSet.SetNccModelOrigin(modelId, offsetRow, offsetColumn);
                HOperatorSet.GetNccModelOrigin(modelId, out verifiedRow, out verifiedColumn);
                var origin =
                    new HalconModelOrigin { DomainCenterRowLocal = localRow,
                                            DomainCenterColumnLocal = localColumn,
                                            ReferenceOriginRow = referenceOriginRow,
                                            ReferenceOriginColumn = referenceOriginColumn,
                                            ConfiguredOffsetRow = offsetRow,
                                            ConfiguredOffsetColumn = offsetColumn,
                                            VerifiedOffsetRow = verifiedRow[0].D,
                                            VerifiedOffsetColumn = verifiedColumn[0].D,
                                            Convention = "Model origin represents full reference tile top-left" };
                var entry = new Entry(modelId, roi, origin, 1,
                                      hasVersionedDiskModel ? "VersionedDiskModel" : "RegeneratedOrRuntimeModel");
                modelId = null;
                return entry;
            }
            finally
            {
                if (roiMat != null)
                    roiMat.Dispose();
                if (modelImage != null)
                    modelImage.Dispose();
                if (domain != null)
                    domain.Dispose();
                DisposeTuple(area);
                DisposeTuple(centerRow);
                DisposeTuple(centerColumn);
                DisposeTuple(verifiedRow);
                DisposeTuple(verifiedColumn);
                if (modelId != null)
                    modelId.Dispose();
            }
        }

        internal static Rect ResolveModelRoi(MatchRequest request, MatcherOptions options)
        {
            if (request.ReferenceNccModelRoi.HasValue)
                return ValidateRoi(request.ReferenceNccModelRoi.Value, request.ReferenceImage);
            using (var gray = new Mat()) using (var mask = new Mat()) using (var filteredMask = new Mat()) using (
                var nonZeroPoints = new Mat())
            {
                if (request.ReferenceImage.Channels() == 1)
                    request.ReferenceImage.CopyTo(gray);
                else
                    Cv2.CvtColor(request.ReferenceImage, gray, ColorConversionCodes.BGR2GRAY);
                // [Tony] [Change time: 2026-07-27] [Purpose: Use the OpenCvSharp API shape provided by the
                // repository's pinned package version.]
                Cv2.Compare(gray, Scalar.All(0), mask, CmpTypes.GT);
                using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
                    Cv2.MorphologyEx(mask, filteredMask, MorphTypes.Open, kernel);
                Cv2.FindNonZero(filteredMask, nonZeroPoints);
                Rect roi;
                if (nonZeroPoints.Empty())
                    throw new InvalidOperationException("Exact-zero reference cannot create an NCC content model.");
                roi = Cv2.BoundingRect(nonZeroPoints);
                var margin = Math.Max(0, options.NccModelRoiMarginPixels);
                roi = new Rect(Math.Max(0, roi.X - margin), Math.Max(0, roi.Y - margin),
                               Math.Min(gray.Cols, roi.Right + margin) - Math.Max(0, roi.X - margin),
                               Math.Min(gray.Rows, roi.Bottom + margin) - Math.Max(0, roi.Y - margin));
                if (roi.Width < options.NccModelRoiMinWidth || roi.Height < options.NccModelRoiMinHeight)
                    throw new InvalidOperationException("NCC content ROI is below configured minimum size.");
                if (roi.Width == gray.Cols && roi.Height == gray.Rows)
                    throw new InvalidOperationException(
                        "A full-size NCC model has no translation search margin in a same-size search image.");
                Scalar mean, stddev;
                using (var roiView = new Mat(gray, roi)) Cv2.MeanStdDev(roiView, out mean, out stddev);
                if (stddev.Val0 < options.NccModelRoiMinStdDev)
                    throw new InvalidOperationException("NCC content ROI has insufficient contrast.");
                return roi;
            }
        }

        private static Rect ValidateRoi(Rect roi, Mat image)
        {
            if (roi.X < 0 || roi.Y < 0 || roi.Width <= 0 || roi.Height <= 0 || roi.Right > image.Cols ||
                roi.Bottom > image.Rows)
                throw new InvalidOperationException("Configured NCC model ROI is outside the reference image.");
            if (roi.Width == image.Cols && roi.Height == image.Rows)
                throw new InvalidOperationException(
                    "A full-size NCC model has no translation search margin in a same-size search image.");
            return roi;
        }

        private static string BuildCacheKey(MatchRequest request, MatcherOptions options)
        {
            var path = NormalizePath(request.ReferenceNccModelPath);
            var tile =
                !string.IsNullOrWhiteSpace(request.SampleTileId)
                    ? request.SampleTileId
                    : (request.OrderIndex.HasValue ? request.OrderIndex.Value.ToString(CultureInfo.InvariantCulture)
                                                   : "__unspecified_tile__");
            return string.Join(
                "|", new[] { "Tile=" + tile, "Model=" + (path ?? "__none__"), "Stamp=" + ModelStamp(path),
                             "Variant=" + (options.PreprocessingVariant ?? "default"),
                             "Rows=" + request.ReferenceImage.Rows.ToString(CultureInfo.InvariantCulture),
                             "Cols=" + request.ReferenceImage.Cols.ToString(CultureInfo.InvariantCulture),
                             "Type=" + request.ReferenceImage.Type(), "Roi=" + RoiKey(request.ReferenceNccModelRoi),
                             "Schema=" + request.NccModelSchemaVersion.ToString(CultureInfo.InvariantCulture),
                             "Origin=" + NullableDouble(request.NccReferenceOriginRow) + "," +
                                 NullableDouble(request.NccReferenceOriginColumn),
                             "AngleStart=" + options.NccAngleStartRad.ToString("R", CultureInfo.InvariantCulture),
                             "AngleExtent=" + options.NccAngleExtentRad.ToString("R", CultureInfo.InvariantCulture),
                             "AngleStep=" + options.NccAngleStepRad.ToString("R", CultureInfo.InvariantCulture),
                             "Metric=" + options.NccMetric,
                             "Levels=" + options.NccNumLevels.ToString(CultureInfo.InvariantCulture) });
        }
        private static string RoiKey(Rect? roi)
        {
            return roi.HasValue ? roi.Value.X + "," + roi.Value.Y + "," + roi.Value.Width + "," + roi.Value.Height
                                : "auto";
        }
        private static string NullableDouble(double? value)
        {
            return value.HasValue ? value.Value.ToString("R", CultureInfo.InvariantCulture) : "auto";
        }
        private static void DisposeTuple(HTuple tuple)
        {
            if (tuple != null)
                tuple.Dispose();
        }
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
        private static string ModelStamp(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return "__not_available__";
            var i = new FileInfo(path);
            return i.LastWriteTimeUtc.Ticks + ":" + i.Length;
        }
        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }
        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                foreach (var e in _cache.Values)
                    e.Dispose();
                _cache.Clear();
                _disposed = true;
            }
        }

        private sealed class Entry : IDisposable
        {
            public Entry(HTuple modelId, Rect roi, HalconModelOrigin origin, int schemaVersion, string source)
            {
                ModelId = modelId ?? throw new ArgumentNullException("modelId");
                ModelRoi = roi;
                Origin = origin;
                SchemaVersion = schemaVersion;
                Source = source;
                UseCount = 1;
            }
            public HTuple ModelId { get; private set; }
            public int UseCount { get; set; }
            public Rect ModelRoi { get; private set; }
            public HalconModelOrigin Origin { get; private set; }
            public int SchemaVersion { get; private set; }
            public string Source { get; private set; }
            public HalconNccModel ToModel(string key, bool hit)
            {
                return new HalconNccModel(ModelId, key, hit, ModelRoi, Origin, SchemaVersion, Source);
            }
            public void Dispose()
            {
                if (ModelId == null)
                    return;
                HOperatorSet.ClearNccModel(ModelId);
                ModelId.Dispose();
                ModelId = null;
            }
        }
    }

    public sealed class HalconNccModel
    {
        internal HalconNccModel(HTuple modelId, string cacheKey, bool cacheHit, Rect roi, HalconModelOrigin origin,
                                int schemaVersion, string source)
        {
            ModelId = modelId;
            CacheKey = cacheKey;
            CacheHit = cacheHit;
            ModelRoi = roi;
            Origin = origin;
            SchemaVersion = schemaVersion;
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
