using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-14] [Purpose: The "alignstitchmem" mode exercises the new in-memory
    // RunAlignStitch(sampleImagePath, tiles, ...) overload from Task 4 of the settings-file plan. Rather than
    // invent a new file format, this reads the SAME shape as the real Master payload (GerberCommonFileInfo)
    // that docs/GerberCommonFileInfo.json and docs/sample_prepare.json already contain -- ExpectedX/ExpectedY
    // match the Master's field names, not the facade's TileRect.X/Y.]
    [DataContract]
    internal sealed class TileRectPayload
    {
        [DataMember] public int OrderIndex { get; set; }
        [DataMember] public int Row { get; set; }
        [DataMember] public int Column { get; set; }
        [DataMember] public int ExpectedX { get; set; }
        [DataMember] public int ExpectedY { get; set; }
        [DataMember] public int Width { get; set; }
        [DataMember] public int Height { get; set; }
    }

    [DataContract]
    internal sealed class BatchPayload
    {
        [DataMember] public string GerberSampleImagePath { get; set; }
        [DataMember] public TileRectPayload[] GerberTiles { get; set; }
        [DataMember] public string Folder_CaptureImages { get; set; }
        [DataMember] public string GerberManifestOutputDir { get; set; }
        [DataMember] public bool DebugMode { get; set; }

        /// <summary>Reads a GerberCommonFileInfo-shaped JSON file. Unknown extra fields in the file
        /// (JobName, workmode, resolution_umPx_X, ...) are ignored by DataContractJsonSerializer.</summary>
        public static BatchPayload ReadOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            // [Claude] [Change time: 2026-08-15] [Purpose: Real payload files (docs/sample_prepare.json,
            // docs/GerberCommonFileInfo.json) are saved with a UTF-8 BOM. DataContractJsonSerializer reads the
            // BOM bytes as the start of JSON content and throws XmlException("Encountered unexpected character
            // 'ï'") instead of skipping them, unlike a text reader configured with encoding detection. Read the
            // bytes through Encoding.UTF8.GetString first so the BOM is stripped before the JSON parser sees it.]
            var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            using (var stream = new MemoryStream(bytes))
                return (BatchPayload)new DataContractJsonSerializer(typeof(BatchPayload)).ReadObject(stream);
        }
    }
}
