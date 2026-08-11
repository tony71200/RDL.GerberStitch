using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RDL.GerberStitch.Facade
{
    /// <summary>
    /// Reads <see cref="AlignStitchConfig"/> from the [GerberAlignStitch] section of an RDL-style INI file.
    /// This minimal parser avoids an external INI dependency and supports one-line Key=Value entries
    /// with semicolon-prefixed comments, matching the simple RDL Master/Worker convention.
    /// </summary>
    public static class AlignStitchConfigIniReader
    {
        /// <summary>
        /// Reads configuration from [GerberAlignStitch]. Missing keys retain AlignStitchConfig defaults.
        /// A missing file or section returns the unchanged default configuration because Gerber may not be enabled.
        /// </summary>
        public static AlignStitchConfig ReadFromIni(string iniFilePath)
        {
            var config = new AlignStitchConfig();
            if (string.IsNullOrWhiteSpace(iniFilePath) || !File.Exists(iniFilePath))
                return config;

            var section = ReadSection(iniFilePath, "GerberAlignStitch");
            if (section == null)
                return config;

            string value;
            if (section.TryGetValue("StitchingEngine", out value) && !string.IsNullOrWhiteSpace(value))
                config.StitchingEngine = value.Trim();
            if (section.TryGetValue("EnableBlending", out value))
                config.EnableBlending = ParseBool(value, config.EnableBlending);
            if (section.TryGetValue("CalculateTimeDetail", out value))
                config.CalculateTimeDetail = ParseBool(value, config.CalculateTimeDetail);
            if (section.TryGetValue("NccMinScore", out value))
                config.NccMinScore = ParseDouble(value, config.NccMinScore);
            if (section.TryGetValue("EccMinCorrelation", out value))
                config.EccMinCorrelation = ParseDouble(value, config.EccMinCorrelation);
            if (section.TryGetValue("MaxTranslationPixels", out value))
                config.MaxTranslationPixels = ParseDouble(value, config.MaxTranslationPixels);
            if (section.TryGetValue("MaxAbsRotationDeg", out value))
                config.MaxAbsRotationDeg = ParseDouble(value, config.MaxAbsRotationDeg);
            if (section.TryGetValue("AllowCoarseOnlyAcceptance", out value))
                config.AllowCoarseOnlyAcceptance = ParseBool(value, config.AllowCoarseOnlyAcceptance);
            if (section.TryGetValue("AllowRefinementFromExpectedWhenCoarseFails", out value))
                config.AllowRefinementFromExpectedWhenCoarseFails = ParseBool(value, config.AllowRefinementFromExpectedWhenCoarseFails);
            if (section.TryGetValue("FallbackToLegacyMerge", out value))
                config.FallbackToLegacyMerge = ParseBool(value, config.FallbackToLegacyMerge);

            return config;
        }

        /// <summary>
        /// Reads the Enable flag and Gerber path separately. These Master-only keys determine whether
        /// the Gerber branch runs and do not belong in AlignStitchConfig.
        /// </summary>
        public static bool ReadEnableFlag(string iniFilePath, out string gerberFilePath)
        {
            gerberFilePath = null;
            var section = ReadSection(iniFilePath, "GerberAlignStitch");
            if (section == null) return false;

            string value;
            section.TryGetValue("GerberFilePath", out gerberFilePath);
            return section.TryGetValue("Enable", out value) && ParseBool(value, false);
        }

        private static IDictionary<string, string> ReadSection(string iniFilePath, string sectionName)
        {
            if (string.IsNullOrWhiteSpace(iniFilePath) || !File.Exists(iniFilePath))
                return null;

            var target = "[" + sectionName + "]";
            var inSection = false;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var found = false;

            foreach (var rawLine in File.ReadAllLines(iniFilePath, Encoding.UTF8))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']')
                {
                    inSection = string.Equals(line, target, StringComparison.OrdinalIgnoreCase);
                    if (inSection) found = true;
                    continue;
                }
                if (!inSection) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                result[key] = value;
            }

            return found ? result : null;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            bool parsed;
            return bool.TryParse(value, out parsed) ? parsed : fallback;
        }

        private static double ParseDouble(string value, double fallback)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }
    }
}
