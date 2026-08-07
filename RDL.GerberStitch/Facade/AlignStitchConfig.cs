using System;

namespace RDL.GerberStitch.Facade
{
    /// <summary>
    /// Config align/stitch tối giản cho RDL Master/Worker. Chỉ phơi ra những tham số
    /// thường phải chỉnh theo dây chuyền; phần còn lại của pipeline giữ nguyên default
    /// đã được chứng minh qua các run Phase 0 (xem docs/Phase0_Closeout.md).
    ///
    /// Tên trùng với GerberViewer.Stitching.Models.AlignStitchConfig của Core một cách
    /// cố ý — cùng vai trò (config cho Align+Stitch), khác tầng (façade vs pipeline nội
    /// bộ). Vì lớp này được khai báo ngay trong namespace RDL.GerberStitch.Facade, tên
    /// trần "AlignStitchConfig" bên trong project này luôn phân giải về lớp này; khi cần
    /// chạm tới kiểu của Core, mapper nội bộ (GerberStitchFacade.BuildCoreConfig) luôn
    /// viết đủ tên "GerberViewer.Stitching.Models.AlignStitchConfig" — đúng quy ước mà
    /// chính GerberViewer.Views.AlignStitchingControl.CloneConfigForRun đã dùng.
    /// </summary>
    public sealed class AlignStitchConfig
    {
        // ── Engine ──

        /// <summary>
        /// Engine stitch. Giá trị hợp lệ: "OpenCv" | "HalconProjectiveMosaic" |
        /// "HalconProjectiveMosaicRebased" | "HalconWarpThenTileOffsetExperimental" |
        /// "HalconThenOpenCvFallback". Default HalconProjectiveMosaicRebased — nhanh hơn
        /// OpenCv 17x ở khâu stitch (13.3s vs 231.7s trên dataset 80 tile tham chiếu).
        /// </summary>
        public string StitchingEngine { get; set; } = "HalconProjectiveMosaicRebased";

        /// <summary>
        /// Bật blending ở đường seam. Default false: HOperatorSet.GenProjectiveMosaic
        /// (engine HALCON) không có tham số blending — bật lên chỉ sinh warning mà
        /// không có tác dụng, vùng overlap vẫn bị ghi đè cứng. Muốn blending thật thì
        /// đổi StitchingEngine sang "OpenCv" đồng thời — đánh đổi: chậm hơn 17x.
        /// </summary>
        public bool EnableBlending { get; set; } = false;

        // ── Ngưỡng Direct Alignment ──

        /// <summary>
        /// Ngưỡng match score tối thiểu cho matcher HALCON NCC. Default 0.10, khớp
        /// GerberViewer.Stitching.Configuration.HalconNccOptions.MinScore của Core.
        /// KHÔNG đặt 0.7 — cao gấp ~5-7 lần default thật, sẽ loại gần hết tile.
        /// </summary>
        public double NccMinScore { get; set; } = 0.10;

        /// <summary>
        /// Ngưỡng tương quan ECC (refinement matcher). Default 0.13, khớp
        /// EccOptions.MinCorrelation của Core.
        /// </summary>
        public double EccMinCorrelation { get; set; } = 0.13;

        /// <summary>Dịch chuyển tối đa cho phép (px) khi khớp trực tiếp.</summary>
        public double MaxTranslationPixels { get; set; } = 300;

        /// <summary>
        /// Xoay tuyệt đối tối đa (độ) cho phép khi khớp trực tiếp. Chốt 0.1 cho RDL —
        /// siết hơn default cấu trúc của Core (0.5), vì dây chuyền RDL đặt tile bằng
        /// cơ cấu cơ khí, sai lệch xoay thực tế rất nhỏ.
        /// </summary>
        public double MaxAbsRotationDeg { get; set; } = 0.1;

        // ── Fallback RDL ──

        /// <summary>
        /// true: khi align/stitch fail, Worker tự fallback sang merge cũ (NewMergeFunc).
        /// false: trả lỗi -300 về Master. Cờ này chỉ dành cho caller (Worker) đọc — Core
        /// không biết khái niệm NewMergeFunc.
        /// </summary>
        public bool FallbackToLegacyMerge { get; set; } = false;

        internal void Validate()
        {
            if (NccMinScore <= 0 || NccMinScore > 1)
                throw new ArgumentOutOfRangeException(nameof(NccMinScore), NccMinScore, "NccMinScore phải trong (0, 1].");
            if (EccMinCorrelation <= 0 || EccMinCorrelation > 1)
                throw new ArgumentOutOfRangeException(nameof(EccMinCorrelation), EccMinCorrelation, "EccMinCorrelation phải trong (0, 1].");
            if (MaxTranslationPixels <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxTranslationPixels), MaxTranslationPixels, "MaxTranslationPixels phải > 0.");
            if (MaxAbsRotationDeg <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxAbsRotationDeg), MaxAbsRotationDeg, "MaxAbsRotationDeg phải > 0.");
            if (ParseEngine(StitchingEngine) == null)
                throw new ArgumentException("StitchingEngine không hợp lệ: " + StitchingEngine, nameof(StitchingEngine));
        }

        internal static GerberViewer.Stitching.Models.StitchingEngine? ParseEngine(string value)
        {
            GerberViewer.Stitching.Models.StitchingEngine parsed;
            if (Enum.TryParse(value, true, out parsed)) return parsed;
            return null;
        }
    }
}
