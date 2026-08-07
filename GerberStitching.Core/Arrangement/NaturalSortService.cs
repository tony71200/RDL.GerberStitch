using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GerberViewer.Stitching.Arrangement
{
    public sealed class NaturalSortItem
    {
        public NaturalSortItem(string filePath, string key) { FilePath = filePath; Key = key; }
        public string FilePath { get; private set; }
        public string Key { get; private set; }
    }

    public sealed class NaturalSortService : IComparer<string>
    {
        public static readonly string[] SupportedExtensions = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
        public bool IsSupportedImage(string path) 
        { 
            return SupportedExtensions.Contains(Path.GetExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase); 
        }
        public string GetNaturalKey(string filePath)
        {
            var name = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
            return Regex.Replace(name, "\\d+", m => long.Parse(m.Value)
                .ToString("D19"))
                .ToLowerInvariant();
        }
        public IList<NaturalSortItem> SortFiles(IEnumerable<string> files)
        {
            return (files ?? Enumerable.Empty<string>())
                .Where(IsSupportedImage)
                .Select(f => new NaturalSortItem(f, GetNaturalKey(f)))
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ThenBy(x => Path.GetFileName(x.FilePath), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        public int Compare(string x, string y) { return StringComparer.Ordinal.Compare(GetNaturalKey(x), GetNaturalKey(y)); }
    }
}
