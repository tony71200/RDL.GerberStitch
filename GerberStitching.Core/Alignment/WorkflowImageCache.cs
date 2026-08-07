using System;
using System.Collections.Generic;
using GerberViewer.Stitching.Matching.OpenCv;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Codex] [Change time: 2026-07-26] [Purpose: Decode each direct/recovery image at most once per workflow run.]
    internal sealed class WorkflowImageCache : IDisposable
    {
        private readonly OpenCvMatchInputAdapter _adapter = new OpenCvMatchInputAdapter();
        private readonly Dictionary<string, Mat> _images = new Dictionary<string, Mat>(StringComparer.OrdinalIgnoreCase);

        public Mat GetMono8(string path) { return GetMono8Core(path, 100d); }

        public Mat GetDirectMovingMono8(string path, double contrastPercent)
        {
            return GetMono8Core(path, contrastPercent);
        }

        private Mat GetMono8Core(string path, double contrastPercent)
        {
            var key = path + "|contrast=" + contrastPercent.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            Mat image;
            if (!_images.TryGetValue(key, out image))
            {
                image = _adapter.DecodeMono8Copy(path);
                try { ModalityAwarePreprocessor.IncreaseContrast(image, contrastPercent); }
                catch { image.Dispose(); throw; }
                _images.Add(key, image);
            }
            return image;
        }

        public void Dispose()
        {
            foreach (var image in _images.Values) image.Dispose();
            _images.Clear();
        }
    }
}
