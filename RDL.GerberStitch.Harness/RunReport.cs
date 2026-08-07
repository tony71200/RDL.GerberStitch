using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Ghi run_report.json cạnh output mỗi lần chạy — đóng nốt mục tiêu run_report.json của docs/Phase1_Task05.md §4.1 (superseded), giờ thực hiện qua harness thật ở Task 1.6.]
    [DataContract]
    internal sealed class RunReport
    {
        [DataMember] public string Mode { get; set; }
        [DataMember] public string RunUtc { get; set; }
        [DataMember] public long ElapsedMs { get; set; }
        [DataMember] public long WallClockMs { get; set; }
        [DataMember(EmitDefaultValue = false)] public long? PeakWorkingSetMb { get; set; }
        [DataMember(EmitDefaultValue = false)] public string PeakAtStage { get; set; }
        [DataMember] public bool Success { get; set; }
        [DataMember(EmitDefaultValue = false)] public string TiffPath { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ManifestPath { get; set; }
        [DataMember(EmitDefaultValue = false)] public string OutputDirectory { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? TileCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? AlignedTileCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? BlankTileCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? FailedTileCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? ErrorCode { get; set; }
        [DataMember(EmitDefaultValue = false)] public int? WarningCount { get; set; }
        [DataMember(EmitDefaultValue = false)] public string ErrorMessage { get; set; }

        /// <summary>Ghi report ra "&lt;folder&gt;\run_report.json". Best-effort — lỗi ghi (vd ổ đầy)
        /// không được che giấu kết quả run thật, chỉ in cảnh báo ra console.</summary>
        public static void WriteTo(string folder, RunReport report)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "run_report.json");
                using (var stream = File.Create(path))
                    new DataContractJsonSerializer(typeof(RunReport)).WriteObject(stream, report);
                Console.WriteLine("RunReport      : " + path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Không ghi được run_report.json: " + ex.Message);
            }
        }
    }
}
