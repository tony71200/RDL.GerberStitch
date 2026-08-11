using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Allow testers to store paths in a file instead of entering arguments on every run; command-line arguments still take precedence.]
    [DataContract]
    internal sealed class AlignStitchTestConfig
    {
        [DataMember] public string ManifestPath { get; set; }
        [DataMember] public string ImagesPath { get; set; }
        [DataMember] public string OutputPath { get; set; }
    }

    [DataContract]
    internal sealed class CreateSampleTestConfig
    {
        [DataMember] public string RasterImagePath { get; set; }
        [DataMember] public string OutputPath { get; set; }
    }

    [DataContract]
    internal sealed class GlobalConfig
    {
        [DataMember] public AlignStitchTestConfig AlignStitch { get; set; }
        [DataMember] public CreateSampleTestConfig CreateSample { get; set; }

        /// <summary>Reads global_config.json when present; returns null when absent because callers may supply every value through command-line arguments.</summary>
        public static GlobalConfig ReadOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using (var stream = File.OpenRead(path))
                return (GlobalConfig)new DataContractJsonSerializer(typeof(GlobalConfig)).ReadObject(stream);
        }
    }
}
