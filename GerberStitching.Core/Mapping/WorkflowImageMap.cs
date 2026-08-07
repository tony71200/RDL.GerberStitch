using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Mapping
{
    public sealed class WorkflowImageMap
    {
        public IList<CapturedImageInfo> OrderedCaptured { get; internal set; }
        public IDictionary<int, CapturedImageInfo> CapturedByOrder { get; internal set; }
        public IDictionary<int, SampleTileInfo> TileByOrder { get; internal set; }
    }

    public interface IWorkflowImageMappingService
    {
        WorkflowImageMap ValidateAndMap(SampleManifest manifest, IList<CapturedImageInfo> captured, CancellationToken cancellationToken);
    }

    // [Codex] [Change time: 2026-07-26] [Purpose: Keep validation and the canonical OrderIndex mapping out of workflow orchestration.]
    public sealed class WorkflowImageMappingService : IWorkflowImageMappingService
    {
        public WorkflowImageMap ValidateAndMap(SampleManifest manifest, IList<CapturedImageInfo> captured, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = SampleManifestValidator.Validate(manifest, true);
            if (!validation.IsValid)
                throw new InvalidOperationException("Invalid sample manifest: " + string.Join("; ", validation.Errors));
            if (captured == null || captured.Count != manifest.Tiles.Count)
                throw new InvalidOperationException("Captured image count must equal manifest tile count.");

            var tileByOrder = manifest.Tiles.ToDictionary(t => t.OrderIndex);
            var duplicate = captured.GroupBy(c => c.OrderIndex).FirstOrDefault(g => g.Count() > 1);
            if (duplicate != null)
                throw new InvalidOperationException("Duplicate captured image OrderIndex: " + duplicate.Key + ".");
            var capturedByOrder = captured.ToDictionary(c => c.OrderIndex);
            foreach (var tile in manifest.Tiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CapturedImageInfo image;
                if (!capturedByOrder.TryGetValue(tile.OrderIndex, out image))
                    throw new InvalidOperationException("Captured image missing for manifest OrderIndex: " + tile.OrderIndex + ".");
                if (string.IsNullOrWhiteSpace(image.FilePath) || !File.Exists(image.FilePath))
                    throw new FileNotFoundException("Captured image missing for OrderIndex " + image.OrderIndex, image.FilePath);
            }
            return new WorkflowImageMap
            {
                OrderedCaptured = captured.OrderBy(c => c.OrderIndex).ToList(),
                CapturedByOrder = capturedByOrder,
                TileByOrder = tileByOrder
            };
        }
    }
}
