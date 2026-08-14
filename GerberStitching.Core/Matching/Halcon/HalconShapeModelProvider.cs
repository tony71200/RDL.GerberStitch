using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Imaging.ImageInterop;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Matching.Halcon
{
    // [Tony] [Change time: 2026-07-27] [Purpose: Make model creation equivalent to AlignByShapeModel in AlignNCC.txt.]
    public sealed class HalconShapeModelProvider : IDisposable
    {
        private const int HDevelopCompatibleSchemaVersion = 2;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _cache = new Dictionary<string, Entry>();
        private readonly HalconMatchInputAdapter _adapter;
        private bool _disposed;

        public HalconShapeModelProvider() : this(new ImageInteropService())
        {
        }

        public HalconShapeModelProvider(IImageInteropService interop)
        {
            _adapter = new HalconMatchInputAdapter(interop);
        }

        public int CachedModelCount
        {
            get {
                lock (_sync) return _cache.Count;
            }
        }

        public HalconShapeModel GetOrCreate(MatchRequest request, CancellationToken token)
        {
            var options = request.ShapeOptions ?? new HalconShapeModelOptions();
            if (options.Mode != HalconShapeModelMode.Rigid)
                throw new NotSupportedException("Scaled shape models are not enabled without real-data validation.");

            var key = Key(request, options);
            lock (_sync)
            {
                ThrowIfDisposed();
                Entry cached;
                if (_cache.TryGetValue(key, out cached))
                    return cached.Model(key, true);
            }

            token.ThrowIfCancellationRequested();
            var created = Create(request, options);
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
                    return race.Model(key, true);
                }

                _cache.Add(key, created);
                return created.Model(key, false);
            }
        }

        private Entry Create(MatchRequest request, HalconShapeModelOptions options)
        {
            HTuple modelId = null;
            HTuple area = null;
            HTuple centerRow = null;
            HTuple centerColumn = null;
            HTuple numLevels = null;
            HTuple angleStep = null;
            HTuple contrast = null;
            HTuple minContrast = null;
            HObject reference = null;
            HObject domain = null;
            try
            {
                reference = _adapter.ToMono8HObjectCopy(request.ReferenceImage);
                HOperatorSet.GetDomain(reference, out domain);
                HOperatorSet.AreaCenter(domain, out area, out centerRow, out centerColumn);

                var referenceOriginRow = centerRow[0].D;
                var referenceOriginColumn = centerColumn[0].D;
                var useCompatibleDiskModel = request.ShapeModelSchemaVersion >= HDevelopCompatibleSchemaVersion &&
                                             !string.IsNullOrWhiteSpace(request.ReferenceShapeModelPath) &&
                                             File.Exists(request.ReferenceShapeModelPath);

                if (useCompatibleDiskModel)
                {
                    HOperatorSet.ReadShapeModel(request.ReferenceShapeModelPath, out modelId);
                }
                else
                {
                    numLevels = options.NumLevels <= 0 ? new HTuple("auto") : new HTuple(options.NumLevels);
                    angleStep = options.AngleStepRad <= 0 ? new HTuple("auto") : new HTuple(options.AngleStepRad);
                    contrast = options.Contrast <= 0 ? new HTuple("auto") : new HTuple(options.Contrast);
                    minContrast = options.MinContrast <= 0 ? new HTuple("auto") : new HTuple(options.MinContrast);

                    HOperatorSet.CreateShapeModel(reference, numLevels, options.AngleStartRad, options.AngleExtentRad,
                                                  angleStep, options.Optimization, options.Metric, contrast,
                                                  minContrast, out modelId);
                }

                // AlignByShapeModel deliberately keeps HALCON's default model origin:
                // the center of gravity of the complete reference-image domain.
                var fullReferenceRoi = new Rect(0, 0, request.ReferenceImage.Cols, request.ReferenceImage.Rows);
                var origin = new HalconModelOrigin {
                    DomainCenterRowLocal = referenceOriginRow,
                    DomainCenterColumnLocal = referenceOriginColumn,
                    ReferenceOriginRow = referenceOriginRow,
                    ReferenceOriginColumn = referenceOriginColumn,
                    ConfiguredOffsetRow = 0.0,
                    ConfiguredOffsetColumn = 0.0,
                    VerifiedOffsetRow = 0.0,
                    VerifiedOffsetColumn = 0.0,
                    Convention = "HALCON default shape origin: center of gravity of full reference domain"
                };

                var entry =
                    new Entry(modelId, fullReferenceRoi, origin,
                              useCompatibleDiskModel ? "HDevelopCompatibleDiskModel" : "RuntimeFullReferenceModel",
                              HDevelopCompatibleSchemaVersion);
                modelId = null;
                return entry;
            }
            finally
            {
                if (reference != null)
                    reference.Dispose();
                if (domain != null)
                    domain.Dispose();
                DisposeTuple(area);
                DisposeTuple(centerRow);
                DisposeTuple(centerColumn);
                DisposeTuple(numLevels);
                DisposeTuple(angleStep);
                DisposeTuple(contrast);
                DisposeTuple(minContrast);
                if (modelId != null)
                    modelId.Dispose();
            }
        }

        internal static Rect ResolveRoi(MatchRequest request, HalconShapeModelOptions options)
        {
            if (request == null || request.ReferenceImage == null)
                throw new InvalidOperationException("Reference image is required for shape-model creation.");
            return new Rect(0, 0, request.ReferenceImage.Cols, request.ReferenceImage.Rows);
        }

        private static string Key(MatchRequest request, HalconShapeModelOptions options)
        {
            var path = string.IsNullOrWhiteSpace(request.ReferenceShapeModelPath)
                           ? string.Empty
                           : Path.GetFullPath(request.ReferenceShapeModelPath);
            var stamp = File.Exists(path) ? new FileInfo(path).LastWriteTimeUtc.Ticks + ":" + new FileInfo(path).Length
                                          : "none";

            return string.Join(
                "|", new[] { request.SampleTileId ?? "none", path, stamp,
                             request.ShapeModelSchemaVersion.ToString(CultureInfo.InvariantCulture),
                             request.ReferenceImage.Rows + "x" + request.ReferenceImage.Cols, options.Mode.ToString(),
                             options.NumLevels.ToString(CultureInfo.InvariantCulture),
                             options.AngleStartRad.ToString("R", CultureInfo.InvariantCulture),
                             options.AngleExtentRad.ToString("R", CultureInfo.InvariantCulture),
                             options.AngleStepRad.ToString("R", CultureInfo.InvariantCulture), options.Optimization,
                             options.Metric, options.Contrast.ToString(CultureInfo.InvariantCulture),
                             options.MinContrast.ToString(CultureInfo.InvariantCulture) });
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);
        }

        private static void DisposeTuple(HTuple tuple)
        {
            if (tuple != null)
                tuple.Dispose();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                foreach (var entry in _cache.Values)
                    entry.Dispose();
                _cache.Clear();
                _disposed = true;
            }
        }

        private sealed class Entry : IDisposable
        {
            private HTuple _id;
            private readonly Rect _roi;
            private readonly HalconModelOrigin _origin;
            private readonly string _source;
            private readonly int _schemaVersion;

            public Entry(HTuple id, Rect roi, HalconModelOrigin origin, string source, int schemaVersion)
            {
                _id = id;
                _roi = roi;
                _origin = origin;
                _source = source;
                _schemaVersion = schemaVersion;
            }

            public HalconShapeModel Model(string key, bool hit)
            {
                return new HalconShapeModel(_id, key, hit, _roi, _origin, _schemaVersion, _source);
            }

            public void Dispose()
            {
                if (_id == null)
                    return;
                HOperatorSet.ClearShapeModel(_id);
                _id.Dispose();
                _id = null;
            }
        }
    }
}
