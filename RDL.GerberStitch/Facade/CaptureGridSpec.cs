using System.ComponentModel;

namespace RDL.GerberStitch.Facade
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Đầu vào cho CaptureGridCalculator. Cố ý KHÔNG có trường
    // overlap: overlap là đại lượng DẪN XUẤT (= ImageWidth - CapturePitch/CamRes), đúng như RDL_Master tính ở
    // MainForm.cs:3243. Cho phép nhập overlap bằng tay chính là bug đã gây lệch 580 px trên 9 bước --
    // xem docs/superpowers/specs/2026-08-15-grid-pitch-calibration-design.md §1.3.]
    public enum CaptureOrder
    {
        /// <summary>Đi hết một cột từ trên xuống, cột kế tiếp đi ngược lại. Khớp payload Master thật:
        /// idx(r,c) = c*Rows + (c chẵn ? r : Rows-1-r). Xác minh trên docs/sample_prepare.json (80 tile).</summary>
        ColumnMajorZigzag = 0,

        /// <summary>Đi hết một hàng từ trái sang, hàng kế tiếp đi ngược lại.</summary>
        RowMajorZigzag = 1
    }

    public sealed class CaptureGridSpec
    {
        /// <summary>Bước bàn máy theo X, đơn vị µm. = iPC_RcpInfo.CapturePitchX.</summary>
        public double CapturePitchX { get; set; }
        /// <summary>Bước bàn máy theo Y, đơn vị µm. = iPC_RcpInfo.CapturePitchY.</summary>
        public double CapturePitchY { get; set; }

        /// <summary>Độ phân giải camera theo X, đơn vị µm/px. = rcp.CamResX (= resolution_umPx_X).</summary>
        public double CamResX { get; set; }
        /// <summary>Độ phân giải camera theo Y, đơn vị µm/px.</summary>
        public double CamResY { get; set; }

        /// <summary>Bề rộng ảnh chụp thật, px. = iPC_RcpInfo.Image_Width.</summary>
        public int ImageWidth { get; set; }
        /// <summary>Chiều cao ảnh chụp thật, px.</summary>
        public int ImageHeight { get; set; }

        public int Rows { get; set; }
        public int Columns { get; set; }

        /// <summary>Offset gốc theo X, px. Mặc định 0 (khớp StartX_EdgePath=0 trong sample_prepare.json).
        /// Giá trị -1 được thay bằng ImageWidth/2 đúng như MainForm.cs:3233-3237.</summary>
        public double StartOffsetX { get; set; }
        public double StartOffsetY { get; set; }

        /// <summary>Bề rộng cửa sổ crop trên raster. null hoặc 0 ⇒ dùng ImageWidth. Đặt lớn hơn ImageWidth
        /// để nới biên tìm kiếm; phần dôi ra được chia ĐỀU hai phía.</summary>
        public int? TileWidth { get; set; }
        public int? TileHeight { get; set; }

        public CaptureOrder Order { get; set; }

        /// <summary>Kích thước raster sample, px. Dùng để kẹp rect và báo cáo phủ. 0 ⇒ bỏ qua kiểm tra phủ.</summary>
        public int RasterWidth { get; set; }
        public int RasterHeight { get; set; }
    }
}
