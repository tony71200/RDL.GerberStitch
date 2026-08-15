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

    // [Claude] [Change time: 2026-08-14] [Purpose: Config for the new "alignstitchmem" mode, which exercises
    // RunAlignStitch(sampleImagePath, tiles, ...) instead of the manifest-path overload. PayloadPath points at
    // a GerberCommonFileInfo-shaped JSON (see BatchPayload.cs); ImagesPath/OutputPath override the payload's
    // own Folder_CaptureImages/GerberManifestOutputDir when set, matching how the Worker's drive-remap works.]
    [DataContract]
    internal sealed class AlignStitchMemTestConfig
    {
        [DataMember] public string PayloadPath { get; set; }
        [DataMember] public string ImagesPath { get; set; }
        [DataMember] public string OutputPath { get; set; }
        [DataMember] public string SettingsPath { get; set; }
        [DataMember] public string RecipeSettingsPath { get; set; }
    }

    // [Claude] [Change time: 2026-08-15] [Purpose: Cấu hình cho mode "createsamplemem". Cố ý KHÔNG có trường
    // overlap -- nó là đại lượng dẫn xuất từ CapturePitch/CamRes. TileWidth/TileHeight = 0 nghĩa là dùng
    // ImageWidth/ImageHeight.]
    [DataContract]
    internal sealed class CreateSampleMemTestConfig
    {
        [DataMember] public string RasterImagePath { get; set; }
        [DataMember] public string TemplatePayloadPath { get; set; }
        [DataMember] public string OutputPath { get; set; }

        [DataMember] public double CapturePitchX { get; set; }
        [DataMember] public double CapturePitchY { get; set; }
        [DataMember] public double CamResX { get; set; }
        [DataMember] public double CamResY { get; set; }
        [DataMember] public int ImageWidth { get; set; }
        [DataMember] public int ImageHeight { get; set; }
        [DataMember] public int Rows { get; set; }
        [DataMember] public int Columns { get; set; }
        [DataMember] public double StartOffsetX { get; set; }
        [DataMember] public double StartOffsetY { get; set; }
        [DataMember] public int TileWidth { get; set; }
        [DataMember] public int TileHeight { get; set; }
    }

    [DataContract]
    internal sealed class GlobalConfig
    {
        [DataMember] public AlignStitchTestConfig AlignStitch { get; set; }
        [DataMember] public CreateSampleTestConfig CreateSample { get; set; }
        [DataMember] public AlignStitchMemTestConfig AlignStitchMem { get; set; }
        [DataMember] public CreateSampleMemTestConfig CreateSampleMem { get; set; }

        /// <summary>Reads global_config.json when present; returns null when absent because callers may supply every value through command-line arguments.</summary>
        public static GlobalConfig ReadOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using (var stream = File.OpenRead(path))
                return (GlobalConfig)new DataContractJsonSerializer(typeof(GlobalConfig)).ReadObject(stream);
        }
    }
}
