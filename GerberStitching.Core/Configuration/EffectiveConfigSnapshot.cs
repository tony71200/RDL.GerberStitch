using System.Collections.Generic;

namespace GerberViewer.Stitching.Configuration
{
    public sealed class EffectiveConfigEntry
    {
        public string Stage { get; internal set; }
        public string Group { get; internal set; }
        public string Parameter { get; internal set; }
        public string Configured { get; internal set; }
        public string Effective { get; internal set; }
        public string Source { get; internal set; }
        public string UsedBy { get; internal set; }
    }

    // Immutable-by-copy run audit. No worker reads the live PropertyGrid object.
    public sealed class EffectiveConfigSnapshot
    {
        private readonly IList<EffectiveConfigEntry> _entries;
        internal EffectiveConfigSnapshot(IList<EffectiveConfigEntry> entries) { _entries = new List<EffectiveConfigEntry>(entries).AsReadOnly(); }
        public IList<EffectiveConfigEntry> Entries { get { return _entries; } }
    }
}
