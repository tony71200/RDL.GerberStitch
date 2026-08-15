using System;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RDL.GerberStitch.Facade
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Ghi CaptureGridResult ra file cùng schema sample_prepare.json.
    // Merge trên JObject chứ không serialize từ DTO: file payload thật có hàng chục trường thuộc về Master
    // (JobName, workmode, preProcessing, Save*Folder...) mà thư viện này không sở hữu và không được làm mất.]
    public static class CaptureGridJsonWriter
    {
        public static string Write(string templatePath, string outputDirectory, string sampleImagePath,
                                   CaptureGridSpec spec, CaptureGridResult grid)
        {
            if (string.IsNullOrWhiteSpace(templatePath) || !File.Exists(templatePath))
                throw new FileNotFoundException("Không tìm thấy file template payload.", templatePath);
            if (grid == null)
                throw new ArgumentNullException("grid");
            if (spec == null)
                throw new ArgumentNullException("spec");

            // Payload thật lưu kèm BOM UTF-8; đọc qua ReadAllText để BOM bị bóc trước khi parse.
            // Cùng lý do đã ghi ở BatchPayload.ReadOrNull.
            JObject root = JObject.Parse(File.ReadAllText(templatePath, Encoding.UTF8));

            var tiles = new JArray();
            foreach (TileRect t in grid.Tiles)
            {
                tiles.Add(new JObject
                {
                    { "OrderIndex", t.OrderIndex },
                    { "Row", t.Row },
                    { "Column", t.Column },
                    { "ExpectedX", t.X },
                    { "ExpectedY", t.Y },
                    { "Width", t.Width },
                    { "Height", t.Height }
                });
            }

            root["GerberTiles"] = tiles;
            if (!string.IsNullOrWhiteSpace(sampleImagePath))
                root["GerberSampleImagePath"] = sampleImagePath;

            // OVerLap*_EdgePath mang ngữ nghĩa CaptureOverlap của Master (MainForm.cs:3243),
            // KHÔNG phải TileOverlap. Xem spec §3.2.1.
            root["OVerLapX_EdgePath"] = (int)Math.Round(grid.CaptureOverlapXPx);
            root["OVerLapY_EdgePath"] = (int)Math.Round(grid.CaptureOverlapYPx);
            root["StartX_EdgePath"] = (int)Math.Round(spec.StartOffsetX);
            root["StartY_EdgePath"] = (int)Math.Round(spec.StartOffsetY);
            root["Width_CaptureImages"] = spec.ImageWidth;
            root["Height_CaptureImages"] = spec.ImageHeight;
            root["resolution_umPx_X"] = spec.CamResX;
            root["resolution_umPx_Y"] = spec.CamResY;

            Directory.CreateDirectory(outputDirectory);
            string fileName = BuildFileName(grid);
            string fullPath = Path.Combine(outputDirectory, fileName);
            File.WriteAllText(fullPath, root.ToString(Formatting.Indented), new UTF8Encoding(true));
            return fullPath;
        }

        /// <summary>sample_&lt;TileWidth&gt;_o&lt;TileOverlap&gt;.json. Hậu tố dùng TileOverlap vì chỉ nó phân
        /// biệt được các case sweep; CaptureOverlap không đổi theo TileWidth.</summary>
        public static string BuildFileName(CaptureGridResult grid)
        {
            int overlap = (int)Math.Round(grid.TileOverlapXPx);
            return "sample_" + grid.TileWidth.ToString(CultureInfo.InvariantCulture) +
                   "_o" + overlap.ToString(CultureInfo.InvariantCulture) + ".json";
        }
    }
}
