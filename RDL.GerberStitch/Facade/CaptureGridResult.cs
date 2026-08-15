using System.Collections.Generic;

namespace RDL.GerberStitch.Facade
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Kết quả dựng lưới. Tách RÕ hai loại overlap -- lẫn hai cái
    // này chính là nguyên nhân gốc của vụ lệch lưới; xem spec §3.2.1.]
    public sealed class CaptureGridResult
    {
        public IList<TileRect> Tiles { get; set; } = new List<TileRect>();

        /// <summary>Bước lưới = CapturePitchX / CamResX. Giữ dạng double, KHÔNG làm tròn.</summary>
        public double StepXPx { get; set; }
        public double StepYPx { get; set; }

        /// <summary>Hai ẢNH CHỤP kề nhau chồng nhau bao nhiêu px = ImageWidth - StepXPx.
        /// Đại lượng VẬT LÝ, do bàn máy quyết định, KHÔNG đổi theo TileWidth.
        /// Đây là giá trị ghi vào OVerLapX_EdgePath và dùng cho mọi ngưỡng.</summary>
        public double CaptureOverlapXPx { get; set; }
        public double CaptureOverlapYPx { get; set; }

        /// <summary>Hai CỬA SỔ CROP chồng nhau bao nhiêu px = TileWidth - StepXPx.
        /// Thuần quy ước, đổi theo TileWidth. CHỈ dùng cho hậu tố tên file o&lt;overlap&gt;.</summary>
        public double TileOverlapXPx { get; set; }
        public double TileOverlapYPx { get; set; }

        public int TileWidth { get; set; }
        public int TileHeight { get; set; }

        /// <summary>Bề rộng raster tối thiểu để lưới không tràn = (Columns-1)*StepX + TileWidth.</summary>
        public int RequiredWidth { get; set; }
        public int RequiredHeight { get; set; }

        /// <summary>OrderIndex của các tile bị kẹp vào biên raster (rect nhỏ hơn TileWidth×TileHeight).</summary>
        public IList<int> ClampedTileIndices { get; set; } = new List<int>();

        public IList<string> Warnings { get; set; } = new List<string>();
    }
}
