using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RDL.GerberStitch.Facade
{
    /// <summary>
    /// Read AlignStitchConfig from the RDL-style INI file section [GerberAlignStitch]. Use the minimal built-in
    /// Key=Value parser rather than adding an INI dependency.
    /// </summary>
    public static class AlignStitchConfigIniReader
    {
        // [Tony 20260813] Keys this section is allowed to contain. Anything else is a typo or a key that
        // belongs to another component; both used to be ignored without a word, so an ini could look
        // correct and do nothing. Update this set whenever a key is added below.
        private static readonly HashSet<string> KnownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Read by ReadEnableFlag / by the Worker's own ReadIniParms, not by ReadFromIni.
            "Enable",
            "GerberFilePath",
            "StitchFolder",
            "StitchOutputRoot",

            // Read below.
            "StitchingEngine",
            "EnableBlending",
            "CalculateTimeDetail",
            "NccMinScore",
            "EccMinCorrelation",
            "MaxTranslationPixels",
            "MaxAbsRotationDeg",
            "FallbackToLegacyMerge",
            "AllowCoarseOnlyAcceptance",
            "AllowRefinementFromExpectedWhenCoarseFails"
        };

        /// <summary>
        /// Read configuration from [GerberAlignStitch]. Missing keys retain defaults; a missing file or section
        /// returns the unchanged default configuration and is not an error.
        /// </summary>
        /// <param name="logWarning">
        /// Optional. Called once per key in the section that this reader does not understand. Passing null keeps
        /// the historical silent behaviour, which is why the single-argument overload still exists.
        /// </param>
        public static AlignStitchConfig ReadFromIni(string iniFilePath, Action<string> logWarning)
        {
            return ReadFromIni(iniFilePath, null, logWarning);
        }

        /// <param name="baseConfig">Values already applied by lower layers (settings files). Null starts from
        /// the code defaults. INI keys override whatever is here, matching the documented precedence: code
        /// default &lt; global settings file &lt; recipe override &lt; ini &lt; Master payload.</param>
        /// <param name="logWarning">
        /// Optional. Called once per key in the section that this reader does not understand. Passing null keeps
        /// the historical silent behaviour, which is why the two-argument overload still exists.
        /// </param>
        public static AlignStitchConfig ReadFromIni(string iniFilePath, AlignStitchConfig baseConfig,
                                                     Action<string> logWarning)
        {
            var config = baseConfig ?? new AlignStitchConfig();
            if (string.IsNullOrWhiteSpace(iniFilePath) || !File.Exists(iniFilePath))
                return config;

            var section = ReadSection(iniFilePath, "GerberAlignStitch");
            if (section == null)
                return config;

            if (logWarning != null)
            {
                foreach (var key in section.Keys.Where(k => !KnownKeys.Contains(k)))
                {
                    logWarning("[GerberAlignStitch] khoá không được dùng: " + key +
                               " (kiểm lại chính tả trong " + iniFilePath + ")");
                }
            }

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
            if (section.TryGetValue("FallbackToLegacyMerge", out value))
                config.FallbackToLegacyMerge = ParseBool(value, config.FallbackToLegacyMerge);

            // [Tony 20260813] Direct-pipeline policy. Both default to true in Core, so an absent key and
            // an explicit "true" behave identically - the point of reading them is to make "false" work.
            if (section.TryGetValue("AllowCoarseOnlyAcceptance", out value))
                config.AllowCoarseOnlyAcceptance = ParseBool(value, config.AllowCoarseOnlyAcceptance);
            if (section.TryGetValue("AllowRefinementFromExpectedWhenCoarseFails", out value))
                config.AllowRefinementFromExpectedWhenCoarseFails =
                    ParseBool(value, config.AllowRefinementFromExpectedWhenCoarseFails);

            return config;
        }

        /// <summary>
        /// Overload kept so existing callers compile unchanged; behaves exactly as before, without warnings.
        /// </summary>
        public static AlignStitchConfig ReadFromIni(string iniFilePath)
        {
            return ReadFromIni(iniFilePath, null);
        }

        /// <summary>
        /// Read Enable and Gerber path settings separately because they control the Master Gerber branch and are
        /// not part of AlignStitchConfig.
        /// </summary>
        public static bool ReadEnableFlag(string iniFilePath, out string gerberFilePath)
        {
            gerberFilePath = null;
            var section = ReadSection(iniFilePath, "GerberAlignStitch");
            if (section == null)
                return false;

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
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                    continue;

                if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']')
                {
                    inSection = string.Equals(line, target, StringComparison.OrdinalIgnoreCase);
                    if (inSection)
                        found = true;
                    continue;
                }
                if (!inSection)
                    continue;

                var eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
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
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed
                                                                                                        : fallback;
        }
    }
}
