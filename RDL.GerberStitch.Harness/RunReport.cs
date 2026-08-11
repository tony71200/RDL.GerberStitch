using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace RDL.GerberStitch.Harness
{
    // [Claude] [Change time: 2026-08-07] [Purpose: Write run_report.json beside each run output, completing the superseded Phase1 Task05 reporting goal through the Task 1.6 harness.]
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

        /// <summary>Writes the report to &lt;folder&gt;\run_report.json on a best-effort basis. Write failures must not hide the run result and are reported only as console warnings.</summary>
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
                Console.Error.WriteLine("Could not write run_report.json: " + ex.Message);
            }
        }
    }
}
