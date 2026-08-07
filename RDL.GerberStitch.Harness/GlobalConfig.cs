using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Cho phép người test đặt sẵn đường dẫn trong file thay vì gõ --arg mỗi lần; CLI arg vẫn được ưu tiên hơn nếu có truyền.]
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

        /// <summary>Đọc global_config.json nếu tồn tại; trả null nếu không có file (không phải lỗi —
        /// tester có thể chọn nhập toàn bộ qua CLI arg, không bắt buộc phải có file này).</summary>
        public static GlobalConfig ReadOrNull(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            using (var stream = File.OpenRead(path))
                return (GlobalConfig)new DataContractJsonSerializer(typeof(GlobalConfig)).ReadObject(stream);
        }
    }
}
