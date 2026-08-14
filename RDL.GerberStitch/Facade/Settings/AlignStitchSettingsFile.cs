using Newtonsoft.Json.Linq;

namespace RDL.GerberStitch.Facade.Settings
{
    /// <summary>
    /// Shape of GerberAlignStitch.settings.json. Both blocks may carry the same sections; Advanced is merged
    /// first and CommonlyTuned is layered on top, so the short block at the top of the file always wins.
    /// Kept as JObject rather than typed properties because the merge happens before deserialization.
    /// </summary>
    public sealed class AlignStitchSettingsFile
    {
        /// <summary>Must be 3. Versions 0/1 make Core's EnsureComposite overwrite the structured groups
        /// with flat legacy defaults.</summary>
        public int ConfigVersion { get; set; }

        public JObject CommonlyTuned { get; set; }
        public JObject Advanced { get; set; }
    }
}
