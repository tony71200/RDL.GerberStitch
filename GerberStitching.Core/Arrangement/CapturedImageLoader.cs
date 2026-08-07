using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GerberViewer.Stitching.Models;

namespace GerberViewer.Stitching.Arrangement
{
    public sealed class CapturedImageLoadResult
    {
        public bool Succeeded { get { return Errors.Count == 0; } }
        public IList<string> Errors { get; private set; } = new List<string>();
        public IList<CapturedImageInfo> Images { get; private set; } = new List<CapturedImageInfo>();
        public int ExpectedTileCount { get; set; }
        public int ExpectedImageWidth { get; set; }
        public int ExpectedImageHeight { get; set; }
    }

    public sealed class CapturedImageLoader
    {
        private readonly NaturalSortService _sort = new NaturalSortService();

        public CapturedImageLoadResult Load(
            string imageFolder, 
            string manifestPath, 
            int expectedImageWidth = 0, 
            int expectedImageHeight = 0)
        {
            var result = new CapturedImageLoadResult 
            { 
                ExpectedImageWidth = expectedImageWidth, 
                ExpectedImageHeight = expectedImageHeight 
            };
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) 
            { 
                result.Errors.Add("Manifest missing: " + (manifestPath ?? "-")); 
                return result; 
            }
            var manifest = ReadManifest(manifestPath, result.Errors);
            if (manifest == null || manifest.Tiles == null || manifest.Tiles.Count == 0) 
            { 
                result.Errors.Add("Manifest invalid or contains no tiles."); 
                return result; 
            }
            var manifestValidation = SampleManifestValidator.Validate(manifest, true);
            if (!manifestValidation.IsValid)
            {
                foreach (var error in manifestValidation.Errors)
                {
                    result.Errors.Add(error);
                    return result;
                }
            }
            var tileByOrder = manifest.Tiles.ToDictionary(t => t.OrderIndex);
            result.ExpectedTileCount = manifest.Tiles.Count;
            if (string.IsNullOrWhiteSpace(imageFolder) || !Directory.Exists(imageFolder)) 
            { 
                result.Errors.Add("Image folder missing: " + (imageFolder ?? "-")); 
                return result; 
            }

            var sorted = _sort.SortFiles(Directory.EnumerateFiles(imageFolder));
            var dupes = sorted.GroupBy(x => x.Key)
                              .Where(g => g.Count() > 1)
                              .Select(g => g.Key + ": " + string.Join(", ", g.Select(x => Path.GetFileName(x.FilePath))))
                              .ToList();
            if (dupes.Count > 0) 
                result.Errors.Add("Duplicate natural-sort keys: " + string.Join("; ", dupes));
            if (sorted.Count > result.ExpectedTileCount) 
                result.Errors.Add("Too many images. Extra files: " + string.Join(", ", sorted.Skip(result.ExpectedTileCount).Select(x => Path.GetFileName(x.FilePath))));
            if (sorted.Count < result.ExpectedTileCount) 
                result.Errors.Add("Not enough images. Expected " + result.ExpectedTileCount + ", found " + sorted.Count + ".");
            if (result.Errors.Count > 0) 
                return result;

            for (int i = 0; i < sorted.Count; i++)
            {
                var item = sorted[i];
                var tile = tileByOrder[i];
                try
                {
                    using (var bmp = new Bitmap(item.FilePath))
                    {
                        if (expectedImageWidth <= 0) 
                            result.ExpectedImageWidth = expectedImageWidth = bmp.Width;
                        if (expectedImageHeight <= 0) 
                            result.ExpectedImageHeight = expectedImageHeight = bmp.Height;
                        if (bmp.Width != expectedImageWidth || bmp.Height != expectedImageHeight) 
                            result.Errors.Add(Path.GetFileName(item.FilePath) + " dimensions " + bmp.Width + "x" + bmp.Height + " do not match expected " + expectedImageWidth + "x" + expectedImageHeight + ".");
                        result.Images.Add(
                            new CapturedImageInfo 
                            { 
                                FilePath = item.FilePath, 
                                Row = tile.Row, 
                                Column = tile.Column, 
                                OrderIndex = tile.OrderIndex, 
                                Width = bmp.Width, 
                                Height = bmp.Height, 
                                NaturalSortKey = item.Key, 
                                SourceMetadata = tile.ExpectedPath, 
                                RobotX = tile.ExpectedX, 
                                RobotY = tile.ExpectedY, 
                                CapturedUtc = File.GetLastWriteTimeUtc(item.FilePath), 
                                State = OrderNodeState.Pending 
                            });
                    }
                }
                catch (Exception ex) 
                { 
                    result.Errors.Add("Unreadable image " + Path.GetFileName(item.FilePath) + ": " + ex.Message); 
                }
            }
            if (result.Errors.Count > 0) 
                result.Images.Clear();
            return result;
        }

        private static SampleManifest ReadManifest(string path, IList<string> errors)
        {
            try { return SampleManifestSerializer.Read(path); }
            catch (Exception ex) { errors.Add("Manifest invalid: " + ex.Message); return null; }
        }
    }
}
