using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Five precedence layers (code default < global file < recipe
    // override < ini < Master payload) are impossible to debug without this. Prints every value that differs from
    // the code default together with the layer that set it.]
    public static class ConfigLayerLog
    {
        /// <param name="sourceByKey">Key path -> layer name, filled in by whoever applied each layer.</param>
        public static void Dump(CoreConfig effective, IDictionary<string, string> sourceByKey, Action<string> log)
        {
            if (log == null) return;
            var defaults = new CoreConfig { ConfigVersion = 3 };

            log("[Gerber] --- effective config (values differing from code defaults) ---");
            log("[Gerber] matcher map: " + effective.DirectAlignment.CoarseMatcher +
                " -> DirectAlignment.CoarseMatcher; " + effective.DirectAlignment.RefinementMatcher +
                " -> DirectAlignment.RefinementMatcher; " + effective.NeighborAlignment.CoarseMatcher +
                " -> NeighborAlignment.CoarseMatcher + Recovery + PoseGraph");

            foreach (var line in Walk(string.Empty, effective, defaults, sourceByKey))
                log("[Gerber] " + line);
            log("[Gerber] --- end effective config ---");
        }

        private static IEnumerable<string> Walk(string prefix, object actual, object baseline,
                                                IDictionary<string, string> sourceByKey)
        {
            if (actual == null || baseline == null) yield break;
            foreach (var p in actual.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                object a, b;
                try { a = p.GetValue(actual, null); b = p.GetValue(baseline, null); }
                catch { continue; }

                if (a != null && !p.PropertyType.IsPrimitive && !p.PropertyType.IsEnum &&
                    p.PropertyType != typeof(string) && p.PropertyType != typeof(decimal) &&
                    p.PropertyType.Namespace != null && p.PropertyType.Namespace.StartsWith("GerberViewer"))
                {
                    foreach (var line in Walk(path, a, b, sourceByKey)) yield return line;
                    continue;
                }

                var actualText = Format(a);
                if (actualText == Format(b)) continue;

                string layer;
                if (sourceByKey == null || !sourceByKey.TryGetValue(path, out layer)) layer = "không rõ";
                yield return path.PadRight(56) + " = " + actualText + "   [" + layer + "]";
            }
        }

        private static string Format(object value)
        {
            if (value == null) return "null";
            var d = value as IFormattable;
            return d == null ? value.ToString() : d.ToString(null, CultureInfo.InvariantCulture);
        }
    }
}
