# Grid Pitch Calibration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: dùng `superpowers:subagent-driven-development` (khuyến nghị) hoặc `superpowers:executing-plans` để thực thi từng task. Các bước dùng cú pháp checkbox (`- [ ]`) để theo dõi.

**Goal:** Dựng lưới chụp trong RDL.GerberStitch theo đúng công thức RDL_Master (`step = CapturePitch / CamRes`, overlap là đại lượng dẫn xuất), thêm bước đo-để-cảnh-báo phát hiện lệch lưới ngay từ đầu run, và xuất payload `sample_<fov>_o<overlap>.json` để test nhiều FOV.

**Architecture:** Ba lớp tách rời. (1) `CaptureGridCalculator` — thuần số học trong façade, không I/O, không HALCON/OpenCV, sinh `TileRect[]` mà hai đường vào sẵn có (`GenerateSampleManifestFromRects`, overload in-memory `RunAlignStitch`) đã nhận. (2) `CaptureGridJsonWriter` — merge kết quả vào một file template theo schema `sample_prepare.json`, giữ nguyên mọi trường thuộc về Master. (3) `GridCalibrationProbe` trong Core — đo bước thật từ ảnh chụp bằng `matchTemplate`, ghi vào report, fail sớm khi lệch quá ngưỡng. Pose graph **không đổi thuật toán**, chỉ sửa phần báo cáo và một giá trị mặc định.

**Tech Stack:** C# 7.3, .NET Framework 4.8, x64. OpenCvSharp4 4.13 (`Cv2.MatchTemplate`), Newtonsoft.Json (chỉ ở project `RDL.GerberStitch`), HALCON 25.05 Progress (không dùng thêm call site mới). Task 5 dùng Python 3 + opencv-python + numpy + matplotlib + Tkinter, nằm ngoài solution.

**Spec:** [`docs/superpowers/specs/2026-08-15-grid-pitch-calibration-design.md`](../specs/2026-08-15-grid-pitch-calibration-design.md)

---

## Global Constraints

- **Build:** `msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64`. Luôn target **x64**. Cần biến môi trường `HALCONROOT`.
- **Không thêm project test, không tự chạy test, không tự tuyên bố "đã pass"** (AGENTS.md §4). Mọi bước chạy ứng dụng đều đánh dấu **[USER]** — agent chỉ build và đọc file kết quả do user tạo ra.
- **Ranh giới façade** (AGENTS.md §3.1): Master/Worker chỉ gọi qua `RDL.GerberStitch.Facade.*`. Không rò kiểu nội bộ của Core ra khỏi façade.
- **Không đổi hành vi pipeline matcher** trong đợt này (AGENTS.md §3.1) — chỉ hình học lưới + báo cáo.
- **Không thêm cơ chế license-check HALCON mới** (AGENTS.md §3.3).
- **Convention:** PascalCase public, camelCase local, `_camelCase` private field. Code debug-only bọc `#if DEBUG`. Bám convention của file đang sửa.
- **Comment đầu mỗi khối sửa:** `// [Tony YYYY-MM-DD] [Purpose: ...]` theo mẫu đang có trong repo.
- **BẮT BUỘC:** sau mỗi task, thêm entry mới vào `docs/implement_code.html` (không ghi đè entry cũ) — gồm ngày, phạm vi, file đã đổi, đánh đổi thiết kế, và **mọi lỗi build/chạy gặp phải kèm cách fix**.

## Quy tắc thực thi (bắt buộc)

1. **Tick checkbox ngay khi xong từng bước.** Không tick trước. Không tick cả cụm một lúc.
2. **Mỗi task có mục "Checklist bàn giao"** ở cuối — phải tick đủ mới được coi là xong task.
3. **Sau mỗi task, ghi một dòng vào §Lịch sử thay đổi** ở cuối file này: ngày, task, commit hash, tóm tắt, và lỗi gặp phải.
4. **Nếu phải lệch khỏi plan** (plan sai, API khác thực tế, lỗi không lường trước): dừng, ghi lý do vào §Lịch sử thay đổi, sửa plan, rồi mới đi tiếp. Không im lặng làm khác.
5. Commit sau mỗi task. Không gộp nhiều task vào một commit.

## Số liệu tham chiếu (đã đo, dùng để đối chiếu kết quả)

Bộ dữ liệu test: `H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\20260725 Q168 2-1 org`
(80 ảnh `.bmp`, mỗi ảnh 4096×4096 mono8), raster `q168_Gerber2-1 to 2-4.tiff` = **40418 × 32364**, lưới **8 hàng × 10 cột**.

| Đại lượng | Giá trị |
|---|---|
| Bước ngang thật (đo, n=27) | **4031.3 px** (std 0.54) |
| Bước dọc thật (đo, n=30) | **4031.7 px** (std 0.51) |
| Rotation toàn cục thật | **+0.047°** |
| `CaptureOverlap` thật | **64.5 px** |
| Bước khai hiện tại (sai) | 4096 |
| Thứ tự tile | `idx(r,c) = c*Rows + (c chẵn ? r : Rows−1−r)` — zigzag column-major |
| `StartX/Y_EdgePath` | 0 |

**Tham số recipe dùng cho test** (suy từ `LensCalib = 0.64 µm/px`, `resolution_umPx = 0.65 µm/px`):

```
CamResX = CamResY = 0.65          (µm/px)
CapturePitchX = CapturePitchY = 2621.44   (µm)  = 4096 × 0.64
⇒ step = 2621.44 / 0.65 = 4033.0 px
```

So với đo được 4031.3 / 4031.7 → **Δ ≈ 1.7 / 1.3 px** → probe trả `Ok` (ngưỡng 2 px). Nếu probe trả `Warning`, dùng giá trị đo để tinh chỉnh `CapturePitch`: `CapturePitchX = 4031.3 × 0.65 = 2620.35 µm`.

---

## File Structure

| File | Trách nhiệm | Task |
|---|---|---|
| `RDL.GerberStitch/Facade/CaptureGridSpec.cs` | **Tạo.** DTO đầu vào + `CaptureOrder` enum. Không logic. | 1 |
| `RDL.GerberStitch/Facade/CaptureGridResult.cs` | **Tạo.** DTO đầu ra. Không logic. | 1 |
| `RDL.GerberStitch/Facade/CaptureGridCalculator.cs` | **Tạo.** Toàn bộ phép tính lưới. Thuần hàm tĩnh, không I/O. | 1 |
| `RDL.GerberStitch/Facade/GerberStitchFacade.cs` | **Sửa.** Thêm `BuildCaptureGrid`. | 1 |
| `RDL.GerberStitch.Harness/GlobalConfig.cs` | **Sửa.** Thêm section `CreateSampleMem`. | 1 |
| `RDL.GerberStitch.Harness/Program.cs` | **Sửa.** Thêm `--mode createsamplemem`. | 1, 2 |
| `RDL.GerberStitch/Facade/CaptureGridJsonWriter.cs` | **Tạo.** Merge lưới vào template JSON, giữ trường lạ. | 2 |
| `GerberStitching.Core/Alignment/GridCalibrationProbe.cs` | **Tạo.** Đo bước thật + chính sách ngưỡng. | 3 |
| `GerberStitching.Core/Models/WorkflowModels.cs` | **Sửa.** `GridCalibrationReport`, `NeighborEdgeMeasurementState`. | 3, 4 |
| `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs` | **Sửa.** Gọi probe; đếm bậc đỉnh. | 3, 4 |
| `GerberStitching.Core/Alignment/Graph/PoseGraphOptions.cs` | **Sửa.** `MaxPoseCorrectionPixels` suy từ `CaptureOverlap`. | 4 |
| `GerberStitching.Core/Alignment/Graph/PoseGraphReport.cs` | **Sửa.** Đếm cạnh theo trạng thái + bậc đỉnh. | 4 |
| `tools/ecc_sandbox/config.py` | **Tạo.** Đọc `align_stitch.ini`, fallback đúng mặc định C#. | 5 |
| `tools/ecc_sandbox/preprocess.py` | **Tạo.** Port `FlattenAndEnhance` / `ToBinaryTraces` (Findings §8.3). | 5 |
| `tools/ecc_sandbox/pyramid_ecc.py` | **Tạo.** Port 1:1 `PyramidEccMatcher.cs`. | 5 |
| `tools/ecc_sandbox/pairs.py` | **Tạo.** Liệt kê cặp Direct / Neighbor từ payload. | 5 |
| `tools/ecc_sandbox/app.py` | **Tạo.** UI Tkinter + matplotlib. | 5 |
| `tools/ecc_sandbox/README.md`, `requirements.txt` | **Tạo.** Hướng dẫn chạy. | 5 |
| `docs/implement_code.html` | **Sửa.** Entry mới mỗi task. | 1–6 |

> `tools/ecc_sandbox/` là công cụ khảo sát bằng Python, **không** nằm trong `RDL.GerberStitch.sln`
> và không ảnh hưởng build. Không phải test project nên không vi phạm AGENTS.md §4.

---

## ⚠️ Cảnh báo tương tác giữa Task 1 và Task 3 — ĐỌC TRƯỚC KHI CHẠY

Sau khi sửa lưới (Task 1), số liệu residual mà pipeline tự báo **có thể trông tệ hơn**, không phải tốt hơn. Đây là hành vi đã dự đoán, không phải regression:

- Hiện tại: `expected = 4096`, phase correlation bị alias trả `measured ≈ 4127` → `residual ≈ +31` → lọt qua `MaxEdgeResidualPixels = 40`.
- Sau khi sửa lưới: `expected = 4033`. Nếu alias vẫn còn, `measured` vẫn ≈ 4127 → `residual ≈ +94` → **vượt 40 → mọi cạnh bị gate out**, `edgesGatedOut = 142`.

**Nếu thấy `edgesGatedOut` tăng vọt sau Task 1, KHÔNG được nới `MaxEdgeResidualPixels`.** Đó là bằng chứng bug alias ROI vẫn còn, và là dữ liệu đầu vào cho quyết định ở §7 spec (ngoài phạm vi đợt này). Ghi số vào §Lịch sử thay đổi và báo user.

Ngược lại, khả năng cao alias sẽ **tự hết** với `TileWidth = ImageWidth = 4096`: khi đó ROI rộng đúng `CaptureOverlap = 64.5 px`, trùng khít vùng chồng thật, phase correlation tìm shift ≈ 0. Đây là lý do Task 6 chạy case 4096 **trước tiên**.

---

## Task 1: `CaptureGridCalculator` + harness mode in bảng lưới

**Files:**
- Create: `RDL.GerberStitch/Facade/CaptureGridSpec.cs`
- Create: `RDL.GerberStitch/Facade/CaptureGridResult.cs`
- Create: `RDL.GerberStitch/Facade/CaptureGridCalculator.cs`
- Modify: `RDL.GerberStitch/Facade/GerberStitchFacade.cs` (thêm 1 method)
- Modify: `RDL.GerberStitch.Harness/GlobalConfig.cs`
- Modify: `RDL.GerberStitch.Harness/Program.cs`
- Modify: `docs/implement_code.html`

**Interfaces:**
- Consumes: `RDL.GerberStitch.Facade.TileRect` (đã có, `GerberStitchFacade.cs:43`).
- Produces:
  - `CaptureGridCalculator.Build(CaptureGridSpec) → CaptureGridResult`
  - `GerberStitchFacade.BuildCaptureGrid(CaptureGridSpec) → CaptureGridResult`
  - `CaptureGridResult.Tiles` là `IList<TileRect>`, `OrderIndex` liên tục `0..N-1`
  - Task 2 dùng `CaptureGridResult` để ghi JSON; Task 6 dùng `BuildCaptureGrid` để sweep FOV.

---

- [x] **Bước 1.1: Tạo `CaptureGridSpec.cs`**

```csharp
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
```

- [x] **Bước 1.2: Tạo `CaptureGridResult.cs`**

```csharp
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
```

- [x] **Bước 1.3: Tạo `CaptureGridCalculator.cs`**

Công thức đã xác minh từng pixel với `docs/sample_prepare.json`: `step = 2599/0.65 = 3998.4444`,
`(int)(0×s)=0`, `(int)(1×s)=3998`, `(int)(2×s)=7996`, `(int)(3×s)=11995` — khớp đúng dãy trong file.
**Giữ nguyên truncate `(int)`**, không đổi sang `Math.Round`.

```csharp
using System;
using System.Collections.Generic;

namespace RDL.GerberStitch.Facade
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Dựng lưới chụp theo ĐÚNG công thức RDL_Master.
    // Nguồn: MapEngine/MapCore.cs:1154 (x = col * captureLayout.PitchX) và :1167
    // (FOV_Image.X = (int)(FOV_World.X / camResX)). Overlap ở MapCore.cs:1073 KHÔNG tham gia phép tính vị trí
    // (khối dùng nó đã bị comment ở 1082-1088) -- nó chỉ là đại lượng dẫn xuất, xem MainForm.cs:3243.
    // Thuần số học: không I/O, không HALCON, không OpenCV.]
    public static class CaptureGridCalculator
    {
        public static CaptureGridResult Build(CaptureGridSpec spec)
        {
            if (spec == null)
                throw new ArgumentNullException("spec");
            if (spec.CamResX <= 0d || spec.CamResY <= 0d)
                throw new ArgumentException("CamResX/CamResY phải > 0 (µm/px).", "spec");
            if (spec.CapturePitchX <= 0d || spec.CapturePitchY <= 0d)
                throw new ArgumentException("CapturePitchX/CapturePitchY phải > 0 (µm).", "spec");
            if (spec.ImageWidth <= 0 || spec.ImageHeight <= 0)
                throw new ArgumentException("ImageWidth/ImageHeight phải > 0.", "spec");
            if (spec.Rows <= 0 || spec.Columns <= 0)
                throw new ArgumentException("Rows/Columns phải > 0.", "spec");

            double stepX = spec.CapturePitchX / spec.CamResX;
            double stepY = spec.CapturePitchY / spec.CamResY;

            int tileWidth = spec.TileWidth.HasValue && spec.TileWidth.Value > 0
                                ? spec.TileWidth.Value
                                : spec.ImageWidth;
            int tileHeight = spec.TileHeight.HasValue && spec.TileHeight.Value > 0
                                 ? spec.TileHeight.Value
                                 : spec.ImageHeight;
            if (tileWidth < spec.ImageWidth || tileHeight < spec.ImageHeight)
                throw new ArgumentException(
                    "TileWidth/TileHeight phải >= ImageWidth/ImageHeight; cửa sổ crop không được nhỏ hơn ảnh chụp.",
                    "spec");

            // Master thay -1 bằng nửa ảnh (MainForm.cs:3233-3237). Mặc định 0 khớp StartX_EdgePath=0.
            double startOffsetX = spec.StartOffsetX == -1d ? spec.ImageWidth / 2d : spec.StartOffsetX;
            double startOffsetY = spec.StartOffsetY == -1d ? spec.ImageHeight / 2d : spec.StartOffsetY;

            // Biên tìm kiếm dôi ra chia ĐỀU hai phía. Hiện trạng cũ dồn hết về phải/dưới.
            int marginX = (tileWidth - spec.ImageWidth) / 2;
            int marginY = (tileHeight - spec.ImageHeight) / 2;

            var result = new CaptureGridResult
            {
                StepXPx = stepX,
                StepYPx = stepY,
                CaptureOverlapXPx = spec.ImageWidth - stepX,
                CaptureOverlapYPx = spec.ImageHeight - stepY,
                TileOverlapXPx = tileWidth - stepX,
                TileOverlapYPx = tileHeight - stepY,
                TileWidth = tileWidth,
                TileHeight = tileHeight,
                RequiredWidth = (int)((spec.Columns - 1) * stepX) + tileWidth,
                RequiredHeight = (int)((spec.Rows - 1) * stepY) + tileHeight
            };

            if (result.CaptureOverlapXPx <= 0d || result.CaptureOverlapYPx <= 0d)
                result.Warnings.Add(
                    "CaptureOverlap <= 0: các ảnh chụp kề nhau KHÔNG chồng nhau (X=" +
                    result.CaptureOverlapXPx.ToString("0.##") + ", Y=" +
                    result.CaptureOverlapYPx.ToString("0.##") +
                    "). Neighbor recovery sẽ không có vùng chung để đo.");

            foreach (var rc in EnumerateOrder(spec))
            {
                int row = rc.Key;
                int column = rc.Value;

                // ExpectedX = (int)((c*PitchX - StartOffsetX*CamResX) / CamResX) = (int)(c*stepX - StartOffsetX)
                int expectedX = (int)(column * stepX - startOffsetX);
                int expectedY = (int)(row * stepY - startOffsetY);

                int left = expectedX - marginX;
                int top = expectedY - marginY;
                int right = left + tileWidth;
                int bottom = top + tileHeight;

                if (spec.RasterWidth > 0)
                {
                    left = Math.Max(0, Math.Min(spec.RasterWidth - 1, left));
                    right = Math.Max(left + 1, Math.Min(spec.RasterWidth, right));
                }
                else if (left < 0)
                {
                    left = 0;
                    right = left + tileWidth;
                }

                if (spec.RasterHeight > 0)
                {
                    top = Math.Max(0, Math.Min(spec.RasterHeight - 1, top));
                    bottom = Math.Max(top + 1, Math.Min(spec.RasterHeight, bottom));
                }
                else if (top < 0)
                {
                    top = 0;
                    bottom = top + tileHeight;
                }

                int orderIndex = result.Tiles.Count;
                if (right - left != tileWidth || bottom - top != tileHeight)
                    result.ClampedTileIndices.Add(orderIndex);

                result.Tiles.Add(new TileRect
                {
                    OrderIndex = orderIndex,
                    Row = row,
                    Column = column,
                    X = left,
                    Y = top,
                    Width = right - left,
                    Height = bottom - top
                });
            }

            if (spec.RasterWidth > 0 && result.RequiredWidth > spec.RasterWidth)
                result.Warnings.Add("Lưới tràn bề rộng raster " +
                                    (result.RequiredWidth - spec.RasterWidth) + " px (cần " +
                                    result.RequiredWidth + ", raster " + spec.RasterWidth + ").");
            if (spec.RasterHeight > 0 && result.RequiredHeight > spec.RasterHeight)
                result.Warnings.Add("Lưới tràn chiều cao raster " +
                                    (result.RequiredHeight - spec.RasterHeight) + " px (cần " +
                                    result.RequiredHeight + ", raster " + spec.RasterHeight + ").");
            if (result.ClampedTileIndices.Count > 0)
                result.Warnings.Add(result.ClampedTileIndices.Count +
                                    " tile bị kẹp vào biên raster nên nhỏ hơn kích thước tile khai báo.");

            return result;
        }

        /// <summary>Sinh (row, column) theo đúng thứ tự OrderIndex của Master.
        /// ColumnMajorZigzag: idx(r,c) = c*Rows + (c chẵn ? r : Rows-1-r).</summary>
        private static IEnumerable<KeyValuePair<int, int>> EnumerateOrder(CaptureGridSpec spec)
        {
            if (spec.Order == CaptureOrder.RowMajorZigzag)
            {
                for (int r = 0; r < spec.Rows; r++)
                    for (int i = 0; i < spec.Columns; i++)
                    {
                        int c = r % 2 == 0 ? i : spec.Columns - 1 - i;
                        yield return new KeyValuePair<int, int>(r, c);
                    }
                yield break;
            }

            for (int c = 0; c < spec.Columns; c++)
                for (int i = 0; i < spec.Rows; i++)
                {
                    int r = c % 2 == 0 ? i : spec.Rows - 1 - i;
                    yield return new KeyValuePair<int, int>(r, c);
                }
        }
    }
}
```

- [x] **Bước 1.4: Thêm `BuildCaptureGrid` vào `GerberStitchFacade.cs`**

Chèn ngay **sau** method `GenerateSampleManifestFromRects` (kết thúc quanh dòng 362, trước
`private static string ValidateRects`).

```csharp
        /// <summary>
        /// Dựng lưới chụp theo đúng công thức RDL_Master: bước lưới = CapturePitch / CamRes, overlap là đại
        /// lượng DẪN XUẤT chứ không phải tham số vào. Trả về TileRect[] dùng được ngay cho
        /// GenerateSampleManifestFromRects và cho overload in-memory của RunAlignStitch.
        /// Thuần số học — không đọc ảnh, không đụng HALCON.
        /// </summary>
        // [Claude] [Change time: 2026-08-15] [Purpose: Thay đường sinh lưới cũ (step = tile - overlap nhập tay)
        // bằng công thức Master. Xem docs/superpowers/specs/2026-08-15-grid-pitch-calibration-design.md.]
        public CaptureGridResult BuildCaptureGrid(CaptureGridSpec spec)
        {
            return CaptureGridCalculator.Build(spec);
        }
```

- [x] **Bước 1.5: Thêm section `CreateSampleMem` vào `GlobalConfig.cs`**

Chèn sau `AlignStitchMemTestConfig` (dòng 35), và thêm property vào `GlobalConfig`.

```csharp
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
```

Trong `GlobalConfig` thêm:

```csharp
        [DataMember] public CreateSampleMemTestConfig CreateSampleMem { get; set; }
```

- [x] **Bước 1.6: Thêm mode `createsamplemem` vào `Program.cs`**

Trong `Main`, ngay sau nhánh `alignstitchmem` (quanh dòng 32):

```csharp
            if (string.Equals(mode, "createsamplemem", StringComparison.OrdinalIgnoreCase))
                return RunCreateSampleMem(args, globalConfig);
```

Thêm method mới, đặt trước `// ── Helpers ───`:

```csharp
        // ── Mode: createsamplemem ──────────────────────────────────────────────
        // [Claude] [Change time: 2026-08-15] [Purpose: Dựng lưới theo công thức Master và in ra để đối chiếu,
        // không cần chạy cả pipeline. Task 2 sẽ thêm phần ghi file JSON.]

        private static int RunCreateSampleMem(string[] args, GlobalConfig config)
        {
            var cfg = config != null ? config.CreateSampleMem : null;
            if (cfg == null)
            {
                Console.Error.WriteLine("Thiếu section \"CreateSampleMem\" trong global_config.json.");
                return 2;
            }

            int tileWidth = ParseIntArg(args, "--tile", cfg.TileWidth);
            int tileHeight = ParseIntArg(args, "--tileh", cfg.TileHeight > 0 ? cfg.TileHeight : tileWidth);

            var spec = new CaptureGridSpec
            {
                CapturePitchX = cfg.CapturePitchX,
                CapturePitchY = cfg.CapturePitchY,
                CamResX = cfg.CamResX,
                CamResY = cfg.CamResY,
                ImageWidth = cfg.ImageWidth,
                ImageHeight = cfg.ImageHeight,
                Rows = cfg.Rows,
                Columns = cfg.Columns,
                StartOffsetX = cfg.StartOffsetX,
                StartOffsetY = cfg.StartOffsetY,
                TileWidth = tileWidth > 0 ? (int?)tileWidth : null,
                TileHeight = tileHeight > 0 ? (int?)tileHeight : null,
                Order = CaptureOrder.ColumnMajorZigzag
            };

            var rasterPath = GetArg(args, "--raster", cfg.RasterImagePath);
            if (!string.IsNullOrWhiteSpace(rasterPath) && File.Exists(rasterPath))
            {
                using (var probe = System.Drawing.Image.FromFile(rasterPath))
                {
                    spec.RasterWidth = probe.Width;
                    spec.RasterHeight = probe.Height;
                }
            }

            CaptureGridResult grid;
            try
            {
                grid = new GerberStitchFacade().BuildCaptureGrid(spec);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine("Spec không hợp lệ: " + ex.Message);
                return 2;
            }

            Console.WriteLine("mode              = createsamplemem");
            Console.WriteLine("raster            = " + rasterPath + "  (" + spec.RasterWidth + " x " +
                              spec.RasterHeight + ")");
            Console.WriteLine("StepX / StepY     = " + grid.StepXPx.ToString("0.####") + " / " +
                              grid.StepYPx.ToString("0.####"));
            Console.WriteLine("CaptureOverlap    = " + grid.CaptureOverlapXPx.ToString("0.##") + " / " +
                              grid.CaptureOverlapYPx.ToString("0.##") + "   (vat ly, theo ban may)");
            Console.WriteLine("TileOverlap       = " + grid.TileOverlapXPx.ToString("0.##") + " / " +
                              grid.TileOverlapYPx.ToString("0.##") + "   (cua so crop)");
            Console.WriteLine("Tile size         = " + grid.TileWidth + " x " + grid.TileHeight);
            Console.WriteLine("Required          = " + grid.RequiredWidth + " x " + grid.RequiredHeight);
            Console.WriteLine("Tiles             = " + grid.Tiles.Count);
            Console.WriteLine("Clamped tiles     = " + grid.ClampedTileIndices.Count);
            foreach (var w in grid.Warnings)
                Console.WriteLine("  WARN: " + w);

            Console.WriteLine();
            Console.WriteLine("10 tile dau (OrderIndex Row Col X Y W H):");
            for (int i = 0; i < Math.Min(10, grid.Tiles.Count); i++)
            {
                var t = grid.Tiles[i];
                Console.WriteLine(string.Format("  {0,3} ({1},{2}) {3,7} {4,7} {5,5} {6,5}",
                                                t.OrderIndex, t.Row, t.Column, t.X, t.Y, t.Width, t.Height));
            }
            return 0;
        }
```

Thêm helper (đặt cạnh `GetArg`):

```csharp
        private static int ParseIntArg(string[] args, string name, int fallback)
        {
            var raw = GetArg(args, name, null);
            int parsed;
            return !string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out parsed) ? parsed : fallback;
        }
```

Thêm `using RDL.GerberStitch.Facade;` ở đầu `Program.cs` nếu chưa có.

- [x] **Bước 1.7: Điền `global_config.json`**

Thêm section (giữ nguyên các section cũ):

```json
  "CreateSampleMem": {
    "RasterImagePath": "H:\\005_Project\\AOI_2026_07_imp\\20260720_Gerber_Align\\q168_Gerber2-1 to 2-4.tiff",
    "TemplatePayloadPath": "H:\\005_Project\\AOI_2026_07_imp\\20260812_RDL\\docs\\sample_prepare.json",
    "OutputPath": "H:\\005_Project\\AOI_2026_07_imp\\20260815_grid",
    "CapturePitchX": 2621.44,
    "CapturePitchY": 2621.44,
    "CamResX": 0.65,
    "CamResY": 0.65,
    "ImageWidth": 4096,
    "ImageHeight": 4096,
    "Rows": 8,
    "Columns": 10,
    "StartOffsetX": 0,
    "StartOffsetY": 0,
    "TileWidth": 4096,
    "TileHeight": 4096
  }
```

- [x] **Bước 1.8: Build**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Kỳ vọng: `Build succeeded`. Nếu gặp `CS0246` về `CaptureGridSpec` trong Harness → thiếu
`using RDL.GerberStitch.Facade;`. Nếu gặp `CS0012` về `System.Drawing` → thêm `<Reference Include="System.Drawing" />`
(Harness đã có sẵn ở dòng 60). Ghi mọi lỗi + cách fix vào `docs/implement_code.html`.

- [ ] **Bước 1.9: [USER] Chạy và đối chiếu**

```bash
RDL.GerberStitch.Harness.exe --mode createsamplemem
```

Kết quả **phải** khớp:

```
StepX / StepY     = 4033.0    / 4033.0        (= 2621.44 / 0.65)
CaptureOverlap    = 63 / 63                   (= 4096 - 4033.0)
TileOverlap       = 63 / 63                   (TileWidth = ImageWidth)
Tile size         = 4096 x 4096
Required          = 40393 x 32327             (raster 40418 x 32364 ⇒ VỪA, không tràn)
Tiles             = 80
Clamped tiles     = 0                         ← hiện trạng cũ là 13
10 tile dau       : (0,0) 0 0 / (1,0) 0 4033 / (2,0) 0 8066 / (3,0) 0 12099 ...
```

Đối chiếu quan trọng nhất: **`Clamped tiles = 0`** và **`Required` nhỏ hơn kích thước raster**.

- [ ] **Bước 1.10: [USER] Sweep 4 FOV**

```bash
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4192
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4240
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4320
```

`CaptureOverlap` phải **giữ nguyên 63** ở cả bốn lần (nó không phụ thuộc TileWidth).
`TileOverlap` phải là `63 / 159 / 207 / 287`. Nếu `CaptureOverlap` đổi theo `--tile` thì code đã lẫn hai loại overlap — quay lại Bước 1.3.

- [x] **Bước 1.11: Ghi `docs/implement_code.html`**

Entry mới: ngày `2026-08-15`, phạm vi "Task 1 — CaptureGridCalculator", danh sách file, đánh đổi
(giữ truncate `(int)` để khớp Master từng pixel thay vì `Math.Round`; overlap chuyển từ input thành output),
và mọi lỗi build gặp phải.

- [ ] **Bước 1.12: Commit**

```bash
git add RDL.GerberStitch/Facade/CaptureGridSpec.cs RDL.GerberStitch/Facade/CaptureGridResult.cs RDL.GerberStitch/Facade/CaptureGridCalculator.cs RDL.GerberStitch/Facade/GerberStitchFacade.cs RDL.GerberStitch.Harness/GlobalConfig.cs RDL.GerberStitch.Harness/Program.cs RDL.GerberStitch.Harness/global_config.json docs/implement_code.html
git commit -m "Add CaptureGridCalculator using the Master pitch formula"
```

- [x] **Bước 1.13: Ghi §Lịch sử thay đổi** (commit hash, số đo thực tế, lỗi gặp phải)

### Checklist bàn giao Task 1

- [x] `CaptureGridSpec` **không có** trường overlap nào
- [x] `CaptureGridResult` tách rõ `CaptureOverlap*` và `TileOverlap*`
- [x] `ExpectedX` dùng truncate `(int)`, không phải `Math.Round`
- [x] Thứ tự tile khớp `idx(r,c) = c*Rows + (c chẵn ? r : Rows−1−r)`
- [x] Biên dôi chia đều hai phía (`marginX = (tileWidth − ImageWidth) / 2`)
- [x] Build x64 thành công
- [ ] [USER] `Clamped tiles = 0`, `Required` < kích thước raster
- [ ] [USER] `CaptureOverlap` không đổi khi sweep `--tile`
- [x] Entry `docs/implement_code.html` đã thêm
- [ ] Commit xong, §Lịch sử thay đổi đã cập nhật

---

## Task 2: `CaptureGridJsonWriter` — xuất `sample_<fov>_o<overlap>.json`

**Files:**
- Create: `RDL.GerberStitch/Facade/CaptureGridJsonWriter.cs`
- Modify: `RDL.GerberStitch.Harness/Program.cs` (`RunCreateSampleMem` ghi file)
- Modify: `RDL.GerberStitch.Harness/RDL.GerberStitch.Harness.csproj` (reference Newtonsoft copy-local)
- Modify: `docs/implement_code.html`

**Interfaces:**
- Consumes: `CaptureGridResult`, `CaptureGridSpec` (Task 1).
- Produces: `CaptureGridJsonWriter.Write(templatePath, outputDirectory, sampleImagePath, spec, grid) → string`
  (trả về đường dẫn file đã ghi). Task 5 đọc payload này; Task 6 dùng để sinh 4 payload.

---

- [ ] **Bước 2.1: Đảm bảo Newtonsoft.Json có trong output của Harness**

`RDL.GerberStitch.csproj` đã reference `..\..\Lib_Supporter\dll\Newtonsoft.Json.dll` (dòng 62–64).
`RDL.GerberStitch.Harness.csproj` **chưa** reference. Vì Harness là copy-local (CLAUDE.md), thêm:

```xml
    <Reference Include="Newtonsoft.Json">
      <HintPath>..\..\Lib_Supporter\dll\Newtonsoft.Json.dll</HintPath>
      <Private>True</Private>
    </Reference>
```

Không thêm sẽ gặp `FileNotFoundException: Could not load file or assembly 'Newtonsoft.Json'` **lúc chạy**,
không phải lúc build — đúng loại lỗi phải ghi vào `implement_code.html`.

- [ ] **Bước 2.2: Tạo `CaptureGridJsonWriter.cs`**

Dùng `JObject` để **merge**, không serialize từ DTO — vì `DataContractJsonSerializer` sẽ nuốt mất
mọi trường thuộc về Master (`JobName`, `workmode`, `preProcessing`, các `Save*Folder`…) mà ta không sở hữu.

```csharp
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
```

- [ ] **Bước 2.3: Gọi writer trong `RunCreateSampleMem`**

Thêm ngay trước `return 0;`:

```csharp
            var templatePath = GetArg(args, "--template", cfg.TemplatePayloadPath);
            var outputPath = GetArg(args, "--out", cfg.OutputPath);
            if (!string.IsNullOrWhiteSpace(templatePath) && !string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var written = CaptureGridJsonWriter.Write(templatePath, outputPath, rasterPath, spec, grid);
                    Console.WriteLine();
                    Console.WriteLine("Payload           = " + written);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Ghi payload that bai: " + ex.Message);
                    return 1;
                }
            }
```

- [ ] **Bước 2.4: Build**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Bước 2.5: [USER] Chạy và kiểm file**

```bash
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4096
```

Kỳ vọng in `Payload = ...\sample_4096_o63.json`. Mở file, kiểm:

- `GerberTiles` có **80** phần tử, `OrderIndex` liên tục `0..79`
- `GerberTiles[0]` = `{OrderIndex:0, Row:0, Column:0, ExpectedX:0, ExpectedY:0, Width:4096, Height:4096}`
- `GerberTiles[1]` = `Row:1, Column:0, ExpectedY:4033`
- `GerberTiles[8]` = `Row:7, Column:1, ExpectedX:4033` (zigzag: cột 1 đi ngược)
- `OVerLapX_EdgePath` = **63** (không phải 96/144/224)
- Các trường `JobName`, `workmode`, `preProcessing`, `SaveDieFolder`… **vẫn còn nguyên** từ template

- [ ] **Bước 2.6: [USER] Sinh đủ 4 payload**

```bash
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4192
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4240
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4320
```

Ra `sample_4192_o159.json`, `sample_4240_o207.json`, `sample_4320_o287.json`.
Trong **cả bốn** file, `OVerLapX_EdgePath` phải bằng **63** — vì đó là `CaptureOverlap`, không đổi theo tile.

- [ ] **Bước 2.7: Ghi `docs/implement_code.html`** (nhớ ghi lỗi Newtonsoft nếu gặp)

- [ ] **Bước 2.8: Commit**

```bash
git add RDL.GerberStitch/Facade/CaptureGridJsonWriter.cs RDL.GerberStitch.Harness/Program.cs RDL.GerberStitch.Harness/RDL.GerberStitch.Harness.csproj docs/implement_code.html
git commit -m "Emit sample_<fov>_o<overlap>.json from the capture grid"
```

- [ ] **Bước 2.9: Ghi §Lịch sử thay đổi**

### Checklist bàn giao Task 2

- [ ] Harness có reference Newtonsoft.Json với `<Private>True</Private>`
- [ ] Writer dùng `JObject` merge, không serialize từ DTO
- [ ] Template đọc bằng `File.ReadAllText(..., Encoding.UTF8)` để bóc BOM
- [ ] Ghi file bằng `new UTF8Encoding(true)` (giữ BOM như payload thật)
- [ ] `OVerLapX/Y_EdGePath` = `CaptureOverlap`, **không** phải `TileOverlap`
- [ ] Tên file dùng `TileOverlap`
- [ ] [USER] 4 file sinh ra, trường của Master còn nguyên
- [ ] Entry `implement_code.html` + commit + §Lịch sử thay đổi

---

## Task 3: `GridCalibrationProbe` — đo để cảnh báo

**Files:**
- Create: `GerberStitching.Core/Alignment/GridCalibrationProbe.cs`
- Modify: `GerberStitching.Core/Models/WorkflowModels.cs` (thêm `GridCalibrationReport` + field trên report)
- Modify: `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs` (gọi probe)
- Modify: `docs/implement_code.html`

**Interfaces:**
- Consumes: `WorkflowImageCache.GetMono8(string path)` (đã có, dùng ở `AlignStitchWorkflowService.cs:922`),
  `SampleTileInfo` (`ExpectedX/Y`, `Row`, `Column`), `CapturedImageInfo` (`OrderIndex`, `FilePath`).
- Produces: `GridCalibrationProbe.Run(...) → GridCalibrationReport` với
  `Status ∈ {Ok, Warning, Mismatch, Inconclusive}`.

---

- [ ] **Bước 3.1: Thêm model vào `WorkflowModels.cs`**

Đặt cạnh `RecoveryEdgePurpose` (dòng 436).

```csharp
    // [Claude] [Change time: 2026-08-15] [Purpose: Kết quả bước đo bước lưới thật từ ảnh chụp. Chỉ để CẢNH BÁO --
    // nguồn sự thật của lưới vẫn là ExpectedX/Y do Master truyền vào. Xem spec §4.]
    public enum GridCalibrationStatus
    {
        /// <summary>Không đủ mẫu tin cậy để kết luận. KHÔNG được coi là lỗi — board có thể có vùng trống lớn.</summary>
        Inconclusive = 0,
        Ok = 1,
        Warning = 2,
        Mismatch = 3
    }

    public sealed class GridCalibrationReport
    {
        public GridCalibrationStatus Status { get; set; } = GridCalibrationStatus.Inconclusive;
        public double DeclaredStepX { get; set; } = double.NaN;
        public double DeclaredStepY { get; set; } = double.NaN;
        public double MeasuredStepX { get; set; } = double.NaN;
        public double MeasuredStepY { get; set; } = double.NaN;
        public double MeasuredRotationDeg { get; set; } = double.NaN;
        public double DeltaX { get; set; } = double.NaN;
        public double DeltaY { get; set; } = double.NaN;
        public int SampleCountX { get; set; }
        public int SampleCountY { get; set; }
        public double MedianScore { get; set; } = double.NaN;
        public string Message { get; set; }
    }
```

Thêm vào class report chính (cùng chỗ khai báo `RecoveryEdges`, `PoseGraph`):

```csharp
        public GridCalibrationReport GridCalibration { get; set; }
```

- [ ] **Bước 3.2: Tạo `GridCalibrationProbe.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using GerberViewer.Stitching.Models;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-15] [Purpose: Đo bước lưới THẬT từ ảnh chụp bằng matchTemplate trên dải
    // RỘNG, độc lập hoàn toàn với đường Neighbor Recovery (đường đó lấy bề rộng ROI từ hình học tile nên chỉ
    // 96 px và bị khoá nhầm chu kỳ trên board tuần hoàn -- xem spec §1.4).
    // Chỉ ĐO và CẢNH BÁO, không ghi đè lưới.]
    public sealed class GridCalibrationOptions
    {
        /// <summary>Số cặp lấy mẫu mỗi trục. 8+8 là đủ; không cần quét cả 142 cạnh.</summary>
        public int MaxPairsPerAxis { get; set; } = 8;
        /// <summary>Bề dày dải template, px. Phải nhỏ hơn CaptureOverlap thật.</summary>
        public int TemplateThicknessPx { get; set; } = 64;
        /// <summary>Biên tìm kiếm quanh bước khai, px.</summary>
        public int SearchMarginPx { get; set; } = 240;
        /// <summary>Biên tìm kiếm theo trục vuông góc, px.</summary>
        public int PerpendicularMarginPx { get; set; } = 80;
        public double MinProbeScore { get; set; } = 0.35;
        public double WarnPixels { get; set; } = 2.0;
        public double FailPixels { get; set; } = 16.0;
        public int MinSamplesPerAxis { get; set; } = 4;
    }

    public static class GridCalibrationProbe
    {
        public static GridCalibrationReport Run(IList<CapturedImageInfo> ordered,
                                                IDictionary<int, SampleTileInfo> tileByOrder,
                                                WorkflowImageCache imageCache,
                                                GridCalibrationOptions options)
        {
            if (options == null) options = new GridCalibrationOptions();
            var report = new GridCalibrationReport();
            if (ordered == null || tileByOrder == null || imageCache == null || ordered.Count < 2)
            {
                report.Message = "Không đủ ảnh để đo.";
                return report;
            }

            var tileByRowCol = new Dictionary<string, SampleTileInfo>();
            var orderByRowCol = new Dictionary<string, int>();
            foreach (var cap in ordered)
            {
                SampleTileInfo tile;
                if (!tileByOrder.TryGetValue(cap.OrderIndex, out tile)) continue;
                var key = tile.Row + ":" + tile.Column;
                tileByRowCol[key] = tile;
                orderByRowCol[key] = cap.OrderIndex;
            }

            var capByOrder = ordered.ToDictionary(c => c.OrderIndex);
            var xs = new List<double>();
            var xp = new List<double>();
            var ys = new List<double>();
            var yp = new List<double>();
            var scores = new List<double>();

            foreach (var kv in tileByRowCol)
            {
                if (xs.Count >= options.MaxPairsPerAxis && ys.Count >= options.MaxPairsPerAxis) break;
                var anchorTile = kv.Value;

                if (xs.Count < options.MaxPairsPerAxis)
                    Measure(anchorTile, anchorTile.Row, anchorTile.Column + 1, true, tileByRowCol, orderByRowCol,
                            capByOrder, imageCache, options, xs, xp, scores);

                if (ys.Count < options.MaxPairsPerAxis)
                    Measure(anchorTile, anchorTile.Row + 1, anchorTile.Column, false, tileByRowCol, orderByRowCol,
                            capByOrder, imageCache, options, ys, yp, scores);
            }

            report.SampleCountX = xs.Count;
            report.SampleCountY = ys.Count;
            report.MedianScore = scores.Count == 0 ? double.NaN : Median(scores);

            if (xs.Count < options.MinSamplesPerAxis && ys.Count < options.MinSamplesPerAxis)
            {
                report.Status = GridCalibrationStatus.Inconclusive;
                report.Message = "Chỉ đo được " + xs.Count + " cặp ngang / " + ys.Count +
                                 " cặp dọc đạt ngưỡng điểm. Không kết luận (board có thể có vùng trống lớn).";
                return report;
            }

            if (xs.Count > 0) { report.MeasuredStepX = Median(xs); }
            if (ys.Count > 0) { report.MeasuredStepY = Median(ys); }

            var perp = new List<double>();
            perp.AddRange(xp);
            perp.AddRange(yp.Select(v => -v));
            if (perp.Count > 0 && !double.IsNaN(report.MeasuredStepX))
                report.MeasuredRotationDeg = Math.Atan2(Median(perp), report.MeasuredStepX) * 180d / Math.PI;

            report.DeclaredStepX = DeclaredStep(tileByRowCol, true);
            report.DeclaredStepY = DeclaredStep(tileByRowCol, false);
            report.DeltaX = report.MeasuredStepX - report.DeclaredStepX;
            report.DeltaY = report.MeasuredStepY - report.DeclaredStepY;

            double worst = 0d;
            if (!double.IsNaN(report.DeltaX)) worst = Math.Max(worst, Math.Abs(report.DeltaX));
            if (!double.IsNaN(report.DeltaY)) worst = Math.Max(worst, Math.Abs(report.DeltaY));

            if (worst > options.FailPixels)
                report.Status = GridCalibrationStatus.Mismatch;
            else if (worst > options.WarnPixels)
                report.Status = GridCalibrationStatus.Warning;
            else
                report.Status = GridCalibrationStatus.Ok;

            report.Message = "Bước khai X=" + Fmt(report.DeclaredStepX) + " Y=" + Fmt(report.DeclaredStepY) +
                             "; đo được X=" + Fmt(report.MeasuredStepX) + " Y=" + Fmt(report.MeasuredStepY) +
                             "; lệch X=" + Fmt(report.DeltaX) + " Y=" + Fmt(report.DeltaY) +
                             "; rotation=" + Fmt(report.MeasuredRotationDeg) + "°" +
                             "; n=" + report.SampleCountX + "/" + report.SampleCountY +
                             "; medianScore=" + Fmt(report.MedianScore) + ".";
            return report;
        }

        private static void Measure(SampleTileInfo anchorTile, int targetRow, int targetColumn, bool horizontal,
                                    IDictionary<string, SampleTileInfo> tileByRowCol,
                                    IDictionary<string, int> orderByRowCol,
                                    IDictionary<int, CapturedImageInfo> capByOrder,
                                    WorkflowImageCache imageCache, GridCalibrationOptions options,
                                    IList<double> steps, IList<double> perpendiculars, IList<double> scores)
        {
            var key = targetRow + ":" + targetColumn;
            int anchorOrder, targetOrder;
            if (!orderByRowCol.TryGetValue(anchorTile.Row + ":" + anchorTile.Column, out anchorOrder)) return;
            if (!orderByRowCol.TryGetValue(key, out targetOrder)) return;

            CapturedImageInfo anchorCap, targetCap;
            if (!capByOrder.TryGetValue(anchorOrder, out anchorCap)) return;
            if (!capByOrder.TryGetValue(targetOrder, out targetCap)) return;

            Mat anchor = imageCache.GetMono8(anchorCap.FilePath);
            Mat target = imageCache.GetMono8(targetCap.FilePath);
            if (anchor == null || target == null || anchor.Empty() || target.Empty()) return;

            var targetTile = tileByRowCol[key];
            double declared = horizontal
                                  ? targetTile.ExpectedX - anchorTile.ExpectedX
                                  : targetTile.ExpectedY - anchorTile.ExpectedY;
            if (declared <= 0) return;

            int thickness = options.TemplateThicknessPx;
            int pad = options.PerpendicularMarginPx;
            int lo = (int)Math.Max(0, declared - options.SearchMarginPx);
            int hi = (int)Math.Min(horizontal ? anchor.Cols : anchor.Rows, declared + options.SearchMarginPx);
            if (hi - lo < thickness) return;

            // Lấy dải dài theo trục vuông góc, chừa pad hai đầu để còn biên tìm kiếm.
            int longLo = pad * 2;
            int longHi = (horizontal ? target.Rows : target.Cols) - pad * 2;
            if (longHi - longLo < 256) return;

            Rect templateRect = horizontal
                                    ? new Rect(0, longLo, thickness, longHi - longLo)
                                    : new Rect(longLo, 0, longHi - longLo, thickness);
            Rect searchRect = horizontal
                                  ? new Rect(lo, longLo - pad, hi - lo, longHi - longLo + pad * 2)
                                  : new Rect(longLo - pad, lo, longHi - longLo + pad * 2, hi - lo);

            if (!Contains(target, templateRect) || !Contains(anchor, searchRect)) return;

            using (var template = new Mat(target, templateRect))
            using (var search = new Mat(anchor, searchRect))
            using (var response = new Mat())
            {
                if (search.Rows < template.Rows || search.Cols < template.Cols) return;
                Cv2.MatchTemplate(search, template, response, TemplateMatchModes.CCoeffNormed);
                double minVal, maxVal;
                Point minLoc, maxLoc;
                Cv2.MinMaxLoc(response, out minVal, out maxVal, out minLoc, out maxLoc);
                if (maxVal < options.MinProbeScore) return;

                scores.Add(maxVal);
                if (horizontal)
                {
                    steps.Add(lo + maxLoc.X);
                    perpendiculars.Add(maxLoc.Y - pad);
                }
                else
                {
                    steps.Add(lo + maxLoc.Y);
                    perpendiculars.Add(maxLoc.X - pad);
                }
            }
        }

        private static bool Contains(Mat image, Rect r)
        {
            return r.X >= 0 && r.Y >= 0 && r.Width > 0 && r.Height > 0 &&
                   r.X + r.Width <= image.Cols && r.Y + r.Height <= image.Rows;
        }

        private static double DeclaredStep(IDictionary<string, SampleTileInfo> tileByRowCol, bool horizontal)
        {
            var deltas = new List<double>();
            foreach (var kv in tileByRowCol)
            {
                var a = kv.Value;
                var key = horizontal ? a.Row + ":" + (a.Column + 1) : (a.Row + 1) + ":" + a.Column;
                SampleTileInfo b;
                if (!tileByRowCol.TryGetValue(key, out b)) continue;
                deltas.Add(horizontal ? b.ExpectedX - a.ExpectedX : b.ExpectedY - a.ExpectedY);
            }
            return deltas.Count == 0 ? double.NaN : Median(deltas);
        }

        private static double Median(IList<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            int n = sorted.Count;
            if (n == 0) return double.NaN;
            return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2d;
        }

        private static string Fmt(double v)
        {
            return double.IsNaN(v) ? "n/a" : v.ToString("0.##");
        }
    }
}
```

- [ ] **Bước 3.3: Gọi probe trong `AlignStitchWorkflowService.RunAsync`**

Chèn **sau** khối kiểm `ModelGeneration=Pregenerate` (kết thúc dòng 174) và **trước** vòng `for` ở dòng 176:

```csharp
                // [Claude] [Change time: 2026-08-15] [Purpose: Đo bước lưới thật trước khi Direct Alignment chạy.
                // Một lệch pitch hệ thống làm hỏng TOÀN BỘ pipeline phía sau nhưng chỉ lộ ra ở ảnh stitched cuối
                // cùng; bắt ở đây tốn ~2 giây. Xem spec §4.]
                report.GridCalibration =
                    GridCalibrationProbe.Run(ordered, tileByOrder, _imageCache, new GridCalibrationOptions());
                var calibration = report.GridCalibration;
                if (calibration != null && !string.IsNullOrEmpty(calibration.Message))
                {
                    if (calibration.Status == GridCalibrationStatus.Mismatch)
                    {
                        throw new InvalidOperationException(
                            "GridMismatch: lưới khai báo lệch quá xa bước chụp thật. " + calibration.Message +
                            " Sinh lại payload bằng CaptureGridCalculator (step = CapturePitch / CamRes) " +
                            "thay vì nhập overlap bằng tay.");
                    }
                    if (calibration.Status == GridCalibrationStatus.Warning)
                        report.Warnings.Add("GridCalibration: " + calibration.Message);
                    else
                        report.Messages.Add("GridCalibration: " + calibration.Message);
                }
```

> Đã xác minh: `report.Messages` và `report.Warnings` đều là `IList<string>`
> (`WorkflowModels.cs:222-223`). `ordered` là `IList<CapturedImageInfo>`, `tileByOrder` là
> `IDictionary<int, SampleTileInfo>` (`Mapping/WorkflowImageMap.cs:12,14`), và
> `WorkflowImageCache.GetMono8(string) → Mat` (`WorkflowImageCache.cs:15`). Signature của
> `GridCalibrationProbe.Run` ở Bước 3.2 khớp chính xác — không cần điều chỉnh.

- [ ] **Bước 3.4: Build**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Lỗi hay gặp: `CS0104` khi `Point`/`Rect`/`Size` mơ hồ giữa `System.Drawing` và `OpenCvSharp` —
chỉ định rõ `OpenCvSharp.Point`, `OpenCvSharp.Rect`. Ghi vào `implement_code.html`.

- [ ] **Bước 3.5: [USER] Chạy với lưới CŨ (bước 4096) — phải fail sớm**

```bash
RDL.GerberStitch.Harness.exe --mode alignstitchmem
```

(dùng payload cũ có bước 4096). Kỳ vọng: run **dừng** với `GridMismatch`, `Δ ≈ −64` px.
Đây là bằng chứng probe hoạt động đúng.

- [ ] **Bước 3.6: [USER] Chạy với payload MỚI `sample_4096_o63.json` — phải `Ok`**

Trỏ `AlignStitchMem.PayloadPath` vào file sinh ở Task 2. Kỳ vọng trong `processing_report.json`:

```
gridCalibration.status        = Ok
gridCalibration.measuredStepX ≈ 4031.3   (declared 4033.0, Δ ≈ −1.7)
gridCalibration.measuredStepY ≈ 4031.7   (declared 4033.0, Δ ≈ −1.3)
gridCalibration.medianScore   > 0.8
```

Nếu ra `Warning` (|Δ| > 2), chỉnh `CapturePitchX/Y` xuống `2620.35` trong `global_config.json`,
sinh lại payload, chạy lại. **Không nới ngưỡng `WarnPixels`.**

- [ ] **Bước 3.7: Ghi `docs/implement_code.html`**

- [ ] **Bước 3.8: Commit**

```bash
git add GerberStitching.Core/Alignment/GridCalibrationProbe.cs GerberStitching.Core/Models/WorkflowModels.cs GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs docs/implement_code.html
git commit -m "Add grid calibration probe that measures the real capture step"
```

- [ ] **Bước 3.9: Ghi §Lịch sử thay đổi** (kèm số `measuredStepX/Y` thực tế đo được)

### Checklist bàn giao Task 3

- [ ] Probe chạy **trước** vòng lặp Direct Alignment
- [ ] `Inconclusive` **không** làm fail run
- [ ] `Mismatch` ném exception, không chạy tiếp
- [ ] Probe dùng dải tìm kiếm rộng (`SearchMarginPx = 240`), **không** dùng ROI 96 px của Neighbor Recovery
- [ ] Mọi `Mat` mượn từ `WorkflowImageCache` **không** bị dispose (cache sở hữu chúng); chỉ `Mat` tạo mới trong `using` mới được dispose
- [ ] Build x64 thành công
- [ ] [USER] lưới cũ ⇒ `Mismatch`; payload mới ⇒ `Ok`
- [ ] Entry `implement_code.html` + commit + §Lịch sử thay đổi

---

## Task 4: Báo cáo pose graph trung thực

**Files:**
- Modify: `GerberStitching.Core/Alignment/Graph/PoseGraphReport.cs`
- Modify: `GerberStitching.Core/Alignment/Graph/PoseGraphOptions.cs`
- Modify: `GerberStitching.Core/Alignment/Graph/GlobalPoseGraphOptimizer.cs`
- Modify: `docs/implement_code.html`

**Interfaces:**
- Consumes: `PoseGraphReport` (đã có `EdgesTotal/EdgesUsed/EdgesGatedOut`, `PoseGraphTileEntry.EdgeCount`),
  `CaptureGridResult.CaptureOverlapXPx` (Task 1) — truyền qua config, không reference chéo project.
- Produces: `PoseGraphReport.EdgesMeasured`, `.EdgesExpectedOnly`, `.EdgesRejectedByLegacyClosure`,
  `PoseGraphTileEntry.MeasuredEdgeCount`, `.ExistingNeighborCount`.

> **Đã xác minh trước khi viết plan (quan trọng — đừng làm lại việc thừa):**
> `GlobalPoseGraphOptimizer.cs:107` với `UseRejectedEdges = true` **giữ nguyên**
> `MeasuredTargetToAnchorTransform` (dùng ở dòng 146–150). Dữ liệu thật xác nhận: cả **142/142**
> cạnh `FullPoseReconciliation` đều có measured transform khác expected và có `phaseScore`;
> `edgesUsed = 142`, `edgesGatedOut = 0`.
> **Việc "đo đủ 4 cạnh" ĐÃ CÓ SẴN.** Dòng log `rejected=120` chỉ là cờ legacy cycle-closure mà pose
> graph cố ý bỏ qua — và chính nó đã khiến báo cáo phân tích cũ kết luận sai.
> Task này vì vậy **chỉ sửa phần báo cáo**, không đụng thuật toán.

---

- [ ] **Bước 4.1: Thêm field vào `PoseGraphReport.cs`**

```csharp
        // [Claude] [Change time: 2026-08-15] [Purpose: EdgesTotal/EdgesUsed không phân biệt được "cạnh có phép đo
        // ảnh thật" với "cạnh rơi về lưới". Dòng log "rejected=120" của legacy closure đã bị đọc nhầm thành
        // "120 cạnh bị vứt", trong khi pose graph vẫn dùng đủ 142. Ba số dưới nói đúng chuyện gì xảy ra.]
        /// <summary>Cạnh có phép đo ảnh riêng (measured khác expected và hữu hạn).</summary>
        public int EdgesMeasured { get; set; }
        /// <summary>Cạnh không đo được, measured trùng expected — chỉ mang thông tin lưới.</summary>
        public int EdgesExpectedOnly { get; set; }
        /// <summary>Cạnh bị legacy cycle-closure đánh dấu không accepted. KHÔNG có nghĩa là bị loại khỏi
        /// pose graph: khi UseRejectedEdges=true chúng vẫn được dùng kèm phép đo của chính chúng.</summary>
        public int EdgesRejectedByLegacyClosure { get; set; }
```

Trong `PoseGraphTileEntry`:

```csharp
        /// <summary>Số cạnh có phép đo ảnh thật nối vào đỉnh này.</summary>
        public int MeasuredEdgeCount { get; set; }
        /// <summary>Số neighbor trực giao tồn tại trong lưới (4 ở giữa, 3 ở cạnh, 2 ở góc).</summary>
        public int ExistingNeighborCount { get; set; }
```

- [ ] **Bước 4.2: Đếm trong `GlobalPoseGraphOptimizer.cs`**

Trong vòng `foreach (var kv in dedup...)` (dòng 98), sau khi lấy `e`, cộng dồn:

```csharp
                // [Claude] [Change time: 2026-08-15] [Purpose: Phân biệt cạnh có phép đo thật với cạnh rơi về lưới.]
                bool hasMeasurement =
                    e.MeasuredTargetToAnchorTransform != null &&
                    Homography.IsFinite(e.MeasuredTargetToAnchorTransform) &&
                    e.ExpectedTargetToAnchorTransform != null &&
                    (Math.Abs(e.MeasuredTargetToAnchorTransform[0, 2] -
                              e.ExpectedTargetToAnchorTransform[0, 2]) > 1e-6 ||
                     Math.Abs(e.MeasuredTargetToAnchorTransform[1, 2] -
                              e.ExpectedTargetToAnchorTransform[1, 2]) > 1e-6);
                if (hasMeasurement) report.EdgesMeasured++;
                else report.EdgesExpectedOnly++;
                if (!e.Accepted) report.EdgesRejectedByLegacyClosure++;
```

Sau khi `graphEdges` dựng xong (sau dòng 158), điền bậc đỉnh:

```csharp
                // Bậc đỉnh: cho thấy ngay tile nào chỉ được giữ bằng 1 cạnh duy nhất.
                var measuredDegree = new Dictionary<int, int>();
                foreach (var kv in dedup)
                {
                    var e = kv.Value;
                    if (e.MeasuredTargetToAnchorTransform == null) continue;
                    Increment(measuredDegree, e.AnchorOrderIndex);
                    Increment(measuredDegree, e.TargetOrderIndex);
                }
```

với helper private:

```csharp
        private static void Increment(IDictionary<int, int> counter, int key)
        {
            int current;
            counter[key] = counter.TryGetValue(key, out current) ? current + 1 : 1;
        }
```

Đếm số neighbor **tồn tại trong lưới** (4 ở giữa, 3 ở cạnh, 2 ở góc) từ `states` — mỗi
`TileWorkflowState` có `Row`/`Column` qua tile của nó. Thêm helper:

```csharp
        // [Claude] [Change time: 2026-08-15] [Purpose: Bậc "đáng lẽ có" của mỗi đỉnh, để so với bậc "đo được".]
        private static Dictionary<int, int> ExistingNeighborCounts(IList<TileWorkflowState> states,
                                                                   IDictionary<int, SampleTileInfo> tileByOrder)
        {
            var occupied = new HashSet<long>();
            foreach (var s in states)
            {
                SampleTileInfo t;
                if (tileByOrder != null && tileByOrder.TryGetValue(s.OrderIndex, out t))
                    occupied.Add(((long)t.Row << 32) ^ (uint)t.Column);
            }

            var result = new Dictionary<int, int>();
            foreach (var s in states)
            {
                SampleTileInfo t;
                if (tileByOrder == null || !tileByOrder.TryGetValue(s.OrderIndex, out t)) continue;
                int n = 0;
                if (occupied.Contains(((long)(t.Row) << 32) ^ (uint)(t.Column + 1))) n++;
                if (occupied.Contains(((long)(t.Row) << 32) ^ (uint)(t.Column - 1))) n++;
                if (occupied.Contains(((long)(t.Row + 1) << 32) ^ (uint)t.Column)) n++;
                if (occupied.Contains(((long)(t.Row - 1) << 32) ^ (uint)t.Column)) n++;
                result[s.OrderIndex] = n;
            }
            return result;
        }
```

`Optimize(...)` hiện đã nhận `IList<TileWorkflowState> states` (dòng 15). Cần thêm tham số
`IDictionary<int, SampleTileInfo> tileByOrder` vào signature và truyền từ chỗ gọi trong
`AlignStitchWorkflowService` (biến `tileByOrder` đã có sẵn ở dòng 152).

Tại chỗ tạo entry (dòng 295–301), bổ sung hai dòng:

```csharp
                    var neighborCounts = ExistingNeighborCounts(states, tileByOrder);   // tính 1 lần trước vòng lặp
                    ...
                    tileEntries.Add(new PoseGraphTileEntry {
                        // ... các trường đang có, giữ nguyên ...
                        EdgeCount = edgeCountByNode[i],
                        MeasuredEdgeCount = measuredDegree.ContainsKey(orderIndex) ? measuredDegree[orderIndex] : 0,
                        ExistingNeighborCount = neighborCounts.ContainsKey(orderIndex) ? neighborCounts[orderIndex] : 0
                    });
```

`orderIndex` là OrderIndex của node thứ `i` — dùng đúng biến đang có ở vòng lặp đó
(cùng biến đang điền `OrderIndex = ...` trong khối `new PoseGraphTileEntry`). Khai
`neighborCounts` **trước** vòng `for` để không tính lại mỗi tile.

- [ ] **Bước 4.3: Cảnh báo khi không hội tụ**

Sau khi solver chạy xong, nơi `report.Converged` được gán:

```csharp
            // [Claude] [Change time: 2026-08-15] [Purpose: converged=false ở cả 6 run tham chiếu mà runStatus vẫn
            // báo thành công. Không hội tụ phải nhìn thấy được.]
            if (!report.Converged)
                report.Warnings.Add("PoseGraph không hội tụ sau " + report.Iterations +
                                    " vòng lặp. Kết quả vẫn được áp dụng nhưng nên xem lại chất lượng cạnh.");
```

- [ ] **Bước 4.4: `MaxPoseCorrectionPixels` suy từ `CaptureOverlap`**

Trong `PoseGraphOptions.cs`, đổi mô tả và thêm cơ chế suy:

```csharp
        [Category("Safety")]
        [Description("Huỷ kết quả pose-graph và giữ pose cũ khi một tile bất kỳ dịch quá ngưỡng này. " +
                     "Zero tắt guard. Âm (mặc định -1) nghĩa là SUY TỪ overlap chụp thật: " +
                     "2 × CaptureOverlap. Một pose dịch quá 2 lần overlap thì chắc chắn đã mất khớp, " +
                     "nên không cần chọn hằng số bằng tay.")]
        public double MaxPoseCorrectionPixels { get; set; } = -1.0;

        /// <summary>Giá trị dùng thật, sau khi suy từ overlap khi MaxPoseCorrectionPixels &lt; 0.</summary>
        public double ResolveMaxPoseCorrectionPixels(double captureOverlapPixels)
        {
            if (MaxPoseCorrectionPixels >= 0d) return MaxPoseCorrectionPixels;
            return captureOverlapPixels > 0d ? 2d * captureOverlapPixels : 351.0;
        }
```

Tại chỗ gọi trong `AlignStitchWorkflowService`, ngay trước khi gọi `GlobalPoseGraphOptimizer.Optimize(...)`:

```csharp
                // [Claude] [Change time: 2026-08-15] [Purpose: Guard dịch-pose suy từ overlap chụp thật thay vì
                // một hằng số chọn tay. CaptureOverlap = ImageWidth - bước lưới khai; lấy bước lưới từ probe
                // (Task 3) vì nó đã tính median trên toàn lưới.]
                double captureOverlapPx = 0d;
                var calib = report.GridCalibration;
                if (calib != null && !double.IsNaN(calib.DeclaredStepX) && ordered.Count > 0)
                {
                    var firstMat = _imageCache.GetMono8(ordered[0].FilePath);
                    if (firstMat != null && !firstMat.Empty())
                        captureOverlapPx = firstMat.Cols - calib.DeclaredStepX;
                }
                double maxPoseCorrection =
                    config.PoseGraph.ResolveMaxPoseCorrectionPixels(captureOverlapPx);
```

Rồi truyền `maxPoseCorrection` xuống chỗ đang đọc `options.MaxPoseCorrectionPixels` trong
`GlobalPoseGraphOptimizer`. Cách ít xâm lấn nhất: thêm một property
`double ResolvedMaxPoseCorrectionPixels` vào `PoseGraphOptions`, gán trước khi gọi `Optimize`,
và đổi chỗ đọc bên trong optimizer sang property mới.

Khi `captureOverlapPx <= 0` (probe `Inconclusive`, không có `DeclaredStepX`), hàm trả về `351.0` —
đúng hành vi cũ, không có regression.

- [ ] **Bước 4.5: Build**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Bước 4.6: [USER] Chạy với payload mới, kiểm report**

Trong `processing_report.json`, mục `poseGraph` phải có:

```
edgesTotal                  = 142
edgesMeasured               > 100      ← số mới
edgesExpectedOnly           nhỏ
edgesRejectedByLegacyClosure           ← thay cho cách đọc nhầm "rejected=120"
tiles[].measuredEdgeCount / .existingNeighborCount
```

Kiểm bằng:

```bash
python -c "import json;d=json.load(open('processing_report.json',encoding='utf-8-sig'));p=d['poseGraph'];print({k:p[k] for k in p if k.startswith('edges')});t=p['tiles'];print('bac<2:',[x['orderIndex'] for x in t if x['measuredEdgeCount']<2])"
```

- [ ] **Bước 4.7: Ghi `docs/implement_code.html`**

- [ ] **Bước 4.8: Commit**

```bash
git add GerberStitching.Core/Alignment/Graph/PoseGraphReport.cs GerberStitching.Core/Alignment/Graph/PoseGraphOptions.cs GerberStitching.Core/Alignment/Graph/GlobalPoseGraphOptimizer.cs docs/implement_code.html
git commit -m "Report edge measurement state and vertex degree in the pose graph"
```

- [ ] **Bước 4.9: Ghi §Lịch sử thay đổi**

### Checklist bàn giao Task 4

- [ ] **Không** đổi thuật toán solver, **không** thêm cạnh chéo, **không** bật bậc tự do scale
- [ ] `MaxIterations` giữ nguyên 8
- [ ] `EdgesMeasured` / `EdgesExpectedOnly` / `EdgesRejectedByLegacyClosure` có trong report
- [ ] `MeasuredEdgeCount` / `ExistingNeighborCount` có trên từng tile
- [ ] `converged == false` sinh warning
- [ ] `MaxPoseCorrectionPixels = -1` suy ra `2 × CaptureOverlap`
- [ ] Build x64 thành công
- [ ] [USER] report có đủ số mới
- [ ] Entry `implement_code.html` + commit + §Lịch sử thay đổi

---

## Task 5: Sandbox Python — mô phỏng tiền xử lý + PyramidECC có UI

**Mục đích:** dò tham số tiền xử lý (§8 của `RDL_GerberStitch_Findings.md`) và quan sát ECC **trước khi**
viết bất kỳ dòng C# nào. Vòng lặp thử–sai trong Python mất vài giây; trong C# mất vài phút build + vài
phút chạy cả lô 80 tile. Đây là công cụ khảo sát, **không** phải code sản phẩm.

**Ràng buộc:** nằm ngoài `RDL.GerberStitch.sln`, không thêm project vào solution, không đụng code C#.
Không vi phạm AGENTS.md §4 (đây không phải test project).

**Files:**
- Create: `tools/ecc_sandbox/requirements.txt`
- Create: `tools/ecc_sandbox/config.py`
- Create: `tools/ecc_sandbox/preprocess.py`
- Create: `tools/ecc_sandbox/pyramid_ecc.py`
- Create: `tools/ecc_sandbox/pairs.py`
- Create: `tools/ecc_sandbox/app.py`
- Create: `tools/ecc_sandbox/README.md`
- Modify: `docs/implement_code.html`

**Interfaces:**
- Consumes: payload `sample_<fov>_o<overlap>.json` (Task 2), thư mục ảnh chụp, raster Gerber,
  `RDL.GerberStitch.Harness/align_stitch.ini`.
- Produces: không có API cho C#. Đầu ra là **số liệu** để quyết định tham số ở đợt sau.

**Đối chiếu 1:1 với C# — mọi con số dưới đây lấy từ code thật, không phải mặc định của OpenCV:**

| Tham số | Giá trị | Nguồn |
|---|---|---|
| `MotionModel` | `Euclidean` | `AlignStitchStageOptions.cs:92` |
| `PyramidLevels` | 3 | `AlignStitchStageOptions.cs:93` |
| `MaxIterations` | 80 | `AlignStitchStageOptions.cs:94` |
| `Epsilon` | 1e-5 | `AlignStitchStageOptions.cs:95` |
| `MinCorrelation` | 0.13 | `AlignStitchStageOptions.cs:96` + `align_stitch.ini` |
| `MaxAbsRotationDeg` | 0.1 | `MatcherOptions.cs:21` + `align_stitch.ini` |
| `MinScale` / `MaxScale` | 0.90 / 1.10 | `MatcherOptions.cs:22-23` |
| `MaxTranslationPixels` | 300 | `align_stitch.ini` |
| `gaussFiltSize` của ECC | **5** | mặc định của OpenCvSharp — Python phải truyền tường minh |

---

- [ ] **Bước 5.1: `requirements.txt`**

```
opencv-python>=4.8
numpy>=1.24
matplotlib>=3.7
Pillow>=10.0
```

Tkinter đi kèm Python trên Windows, không cần cài. Cài bằng:

```bash
python -m pip install -r tools/ecc_sandbox/requirements.txt
```

- [ ] **Bước 5.2: `config.py` — đọc đúng config đang dùng**

```python
"""Doc cau hinh tu align_stitch.ini, fallback ve DUNG mac dinh cua C#."""
import configparser
import os

# Mac dinh lay tu code C# that -- KHONG phai mac dinh cua OpenCV.
DEFAULTS = {
    # EccOptions (Configuration/AlignStitchStageOptions.cs:90-99)
    "EccMotionModel": "Euclidean",   # Translation | Euclidean | Affine
    "EccPyramidLevels": 3,
    "EccMaxIterations": 80,
    "EccEpsilon": 1e-5,
    "EccMinCorrelation": 0.13,
    "EccGaussFiltSize": 5,           # mac dinh cua OpenCvSharp Cv2.FindTransformECC
    # MatcherOptions.cs:21-23
    "MaxAbsRotationDeg": 0.1,
    "MinScale": 0.90,
    "MaxScale": 1.10,
    "MaxTranslationPixels": 300.0,
    # AlignmentPreprocessingOptions.Contrast -- 100 nghia la KHONG lam gi
    "Contrast": 100.0,
    # Findings §8.3 -- gia tri de xuat, PHAI hieu chinh tren anh that (§8.4)
    "BackgroundSigma": 51.0,
    "ClaheClipLimit": 3.0,
    "ClaheTile": 16,
    "AdaptiveBlockSize": 51,
    "AdaptiveC": -5.0,
    "CloseKernel": 5,
    "CloseIterations": 2,
}

_INI_MAP = {
    "eccmincorrelation": ("EccMinCorrelation", float),
    "maxtranslationpixels": ("MaxTranslationPixels", float),
    "maxabsrotationdeg": ("MaxAbsRotationDeg", float),
}


def load(ini_path=None):
    """Tra ve dict cau hinh. Key thieu trong ini giu nguyen mac dinh C#."""
    cfg = dict(DEFAULTS)
    if not ini_path or not os.path.exists(ini_path):
        return cfg
    parser = configparser.ConfigParser()
    # File ini cua repo co dong comment bat dau bang ';' -- configparser xu ly duoc.
    parser.read(ini_path, encoding="utf-8")
    if not parser.has_section("GerberAlignStitch"):
        return cfg
    for raw_key, raw_value in parser.items("GerberAlignStitch"):
        mapped = _INI_MAP.get(raw_key.lower())
        if mapped is None:
            continue
        name, caster = mapped
        try:
            cfg[name] = caster(raw_value)
        except ValueError:
            pass
    return cfg
```

- [ ] **Bước 5.3: `preprocess.py` — port §8.3**

```python
"""Port 1:1 cua CapturedImagePreprocessor de xuat o Findings §8.3.

Thu tu co y: san phang illumination TRUOC khi tang tuong phan, vi CLAHE tren nen
chua phang se khuech dai chinh bong do.
"""
import cv2
import numpy as np


def increase_contrast(mono8, contrast_percent):
    """Tuong duong ModalityAwarePreprocessor.IncreaseContrast.
    100% => tra ve nguyen ban (dung nhu C# return som o dong 156)."""
    if abs(contrast_percent - 100.0) < 1e-9:
        return mono8.copy()
    alpha = contrast_percent / 100.0
    beta = 128.0 * (1.0 - alpha)
    return cv2.convertScaleAbs(mono8, alpha=alpha, beta=beta)


def flatten_and_enhance(mono8, bg_sigma=51.0, clip_limit=3.0, clahe_tile=16):
    """Buoc 1+2. AN TOAN cho ECC va phase correlation -- giu gradient lien tuc.

    bg_sigma phai LON hon nhieu lan be rong trace, neu khong se xoa luon ca trace.
    """
    if mono8 is None or mono8.size == 0:
        raise ValueError("mono8 rong")
    # 1. Chia cho chinh nen da blur manh => khu bong do va nen xam khong deu.
    bg = cv2.GaussianBlur(mono8, (0, 0), bg_sigma)
    src = mono8.astype(np.float32)
    den = bg.astype(np.float32)
    den[den < 1.0] = 1.0
    flat = np.clip(src / den * 255.0, 0, 255).astype(np.uint8)

    # 2. Tang tuong phan CUC BO. ConvertTo tuyen tinh toan cuc khong xu ly duoc nen khong deu.
    clahe = cv2.createCLAHE(clipLimit=clip_limit, tileGridSize=(clahe_tile, clahe_tile))
    return clahe.apply(flat)


def to_binary_traces(enhanced_mono8, block_size=51, c=-5.0,
                     close_kernel=5, close_iterations=2):
    """Buoc 3+4. CHI dung cho chamfer / Hausdorff / ICP / skeleton.

    KHONG duoc dua ket qua nay vao ECC: anh nhi phan co gradient bang 0 o moi noi
    tru bien 1 pixel, ECC se khong hoi tu (Findings §8.2).
    """
    if block_size % 2 == 0:
        block_size += 1
    binary = cv2.adaptiveThreshold(enhanced_mono8, 255,
                                   cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
                                   cv2.THRESH_BINARY, block_size, c)
    if close_kernel <= 1 or close_iterations <= 0:
        return binary
    k = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (close_kernel, close_kernel))
    return cv2.morphologyEx(binary, cv2.MORPH_CLOSE, k, iterations=close_iterations)


def build_variants(mono8, cfg, flatten=True, binarize=False):
    """Tra ve dict cac buoc trung gian de UI hien tung cua so."""
    out = {"raw": mono8}
    stage = increase_contrast(mono8, cfg["Contrast"])
    out["contrast"] = stage
    if flatten:
        stage = flatten_and_enhance(stage, cfg["BackgroundSigma"],
                                    cfg["ClaheClipLimit"], cfg["ClaheTile"])
        out["flattened"] = stage
    if binarize:
        out["binary"] = to_binary_traces(stage, cfg["AdaptiveBlockSize"], cfg["AdaptiveC"],
                                         cfg["CloseKernel"], cfg["CloseIterations"])
    out["final"] = stage
    return out
```

- [ ] **Bước 5.4: `pyramid_ecc.py` — port 1:1 `PyramidEccMatcher.cs`**

Đối chiếu từng dòng với `GerberStitching.Core/Matching/OpenCv/PyramidEccMatcher.cs`.
Ba chỗ dễ sai nhất, đã đánh dấu trong code:

1. `cv2.findTransformECC(templateImage=reference, inputImage=moving, ...)` — **thứ tự này ngược trực giác**.
2. Pyramid index 0 = **full resolution**; vòng lặp chạy từ index lớn nhất (nhỏ nhất về kích thước) về 0.
3. `scale` chỉ nhân/chia phần **translation**, không đụng phần xoay.

```python
"""Port 1:1 cua GerberStitching.Core/Matching/OpenCv/PyramidEccMatcher.cs."""
import math
import numpy as np
import cv2

MOTION = {
    "Translation": cv2.MOTION_TRANSLATION,
    "Euclidean": cv2.MOTION_EUCLIDEAN,
    "Affine": cv2.MOTION_AFFINE,
}


def _restrict_motion(m, motion_model):
    """RestrictMotion (PyramidEccMatcher.cs:277)."""
    if motion_model == "Translation":
        return np.array([[1.0, 0.0, m[0, 2]], [0.0, 1.0, m[1, 2]], [0.0, 0.0, 1.0]])
    if motion_model == "Euclidean":
        angle = math.atan2(m[1, 0], m[0, 0])
        c, s = math.cos(angle), math.sin(angle)
        return np.array([[c, -s, m[0, 2]], [s, c, m[1, 2]], [0.0, 0.0, 1.0]])
    return np.array([[m[0, 0], m[0, 1], m[0, 2]],
                     [m[1, 0], m[1, 1], m[1, 2]],
                     [0.0, 0.0, 1.0]])


def _to_warp_at_scale(full, scale, motion_model):
    """ToWarpMatAtScale (:256) -- CHI translation duoc nhan scale."""
    r = _restrict_motion(full, motion_model)
    return np.array([[r[0, 0], r[0, 1], r[0, 2] * scale],
                     [r[1, 0], r[1, 1], r[1, 2] * scale]], dtype=np.float32)


def _from_warp_at_scale(warp, scale):
    """FromWarpMatAtScale (:269)."""
    s = 1.0 if abs(scale) < 1e-12 else scale
    return np.array([[float(warp[0, 0]), float(warp[0, 1]), float(warp[0, 2]) / s],
                     [float(warp[1, 0]), float(warp[1, 1]), float(warp[1, 2]) / s],
                     [0.0, 0.0, 1.0]])


def _build_pyramids(ref32, mov32, levels):
    """BuildPyramids (:219). Index 0 = FULL resolution."""
    rp, mp = [ref32.copy()], [mov32.copy()]
    for _ in range(1, max(1, levels)):
        if rp[-1].shape[1] < 32 or rp[-1].shape[0] < 32:
            break
        rp.append(cv2.pyrDown(rp[-1]))
        mp.append(cv2.pyrDown(mp[-1]))
    return rp, mp


def match(reference_mono8, moving_mono8, cfg, initial_moving_to_reference=None):
    """Tra ve dict ket qua, mo phong MatchResult cua C#.

    reference = tile Gerber, moving = anh chup. Transform tra ve la MovingImage -> ReferenceImage,
    dung contract ghi o Diagnostics["TransformDirection"] (:122).
    """
    result = {"success": False, "matcher": "PyramidEccMatcher", "levels": [],
              "failure_reason": None, "message": None}

    if reference_mono8.shape != moving_mono8.shape:
        result["failure_reason"] = "SizeMismatch"
        result["message"] = ("ReferenceImage va MovingImage phai cung kich thuoc "
                             "(MatcherGeometryValidator.ValidatePreparedPair). "
                             "reference=%s moving=%s" % (reference_mono8.shape, moving_mono8.shape))
        return result

    ref32 = reference_mono8.astype(np.float32)
    mov32 = moving_mono8.astype(np.float32)

    motion_model = cfg["EccMotionModel"]
    motion_type = MOTION[motion_model]
    rp, mp = _build_pyramids(ref32, mov32, cfg["EccPyramidLevels"])

    if initial_moving_to_reference is None:
        full_ref_to_mov = np.eye(3)
    else:
        full_ref_to_mov = np.linalg.inv(np.asarray(initial_moving_to_reference, dtype=float))
    full_ref_to_mov = _restrict_motion(full_ref_to_mov, motion_model)

    correlation = float("nan")
    criteria = (cv2.TERM_CRITERIA_COUNT | cv2.TERM_CRITERIA_EPS,
                max(1, int(cfg["EccMaxIterations"])), float(cfg["EccEpsilon"]))

    # Chay tu muc THO nhat (index lon nhat) ve muc day du (index 0).
    for level in range(len(rp) - 1, -1, -1):
        scale = rp[level].shape[1] / float(max(1, ref32.shape[1]))
        warp = _to_warp_at_scale(full_ref_to_mov, scale, motion_model)
        try:
            # THU TU: template = reference, input = moving. gaussFiltSize=5 khop OpenCvSharp.
            correlation, warp = cv2.findTransformECC(
                rp[level], mp[level], warp, motion_type, criteria,
                None, int(cfg["EccGaussFiltSize"]))
        except cv2.error as ex:
            result["failure_reason"] = "RuntimeFailure"
            result["message"] = "ECC khong hoi tu o level %d: %s" % (level, ex)
            result["levels"].append({"level": level, "size": rp[level].shape[::-1],
                                     "scale": scale, "correlation": None})
            return result
        full_ref_to_mov = _from_warp_at_scale(warp, scale)
        result["levels"].append({"level": level, "size": rp[level].shape[::-1],
                                 "scale": scale, "correlation": float(correlation)})

    moving_to_reference = np.linalg.inv(full_ref_to_mov)
    tx = moving_to_reference[0, 2]
    ty = moving_to_reference[1, 2]
    rotation_deg = math.degrees(math.atan2(moving_to_reference[1, 0], moving_to_reference[0, 0]))
    scale_value = math.hypot(moving_to_reference[0, 0], moving_to_reference[1, 0])

    result.update({
        "matrix": moving_to_reference,
        "translation_x": tx,
        "translation_y": ty,
        "rotation_deg": rotation_deg,
        "scale": scale_value,
        "raw_score": float(correlation),
        "normalized_confidence": max(0.0, min(1.0, (correlation + 1.0) / 2.0)),
        "pyramid_levels": len(rp),
    })

    # ValidateMovingToReference (:161) -- dung thu tu kiem tra nhu C#.
    if abs(tx) > cfg["MaxTranslationPixels"] or abs(ty) > cfg["MaxTranslationPixels"]:
        result["failure_reason"] = "NonFiniteTransform"
        result["message"] = "Translation vuot MaxTranslationPixels (%.1f)." % cfg["MaxTranslationPixels"]
        return result
    if math.isnan(correlation) or math.isinf(correlation) or correlation < cfg["EccMinCorrelation"]:
        result["failure_reason"] = "CorrelationBelowThreshold"
        result["message"] = "Correlation %.4f < MinCorrelation %.4f." % (correlation, cfg["EccMinCorrelation"])
        return result
    if abs(rotation_deg) > cfg["MaxAbsRotationDeg"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Rotation %.4f deg vuot MaxAbsRotationDeg %.4f." % (
            rotation_deg, cfg["MaxAbsRotationDeg"])
        return result
    if scale_value < cfg["MinScale"] or scale_value > cfg["MaxScale"]:
        result["failure_reason"] = "GeometryRejected"
        result["message"] = "Scale %.6f ngoai khoang [%.2f, %.2f]." % (
            scale_value, cfg["MinScale"], cfg["MaxScale"])
        return result

    result["success"] = True
    return result


def warp_moving_to_reference(moving_mono8, matrix, size_wh):
    """Dung de ve ket qua: dua anh moving ve he toa do reference."""
    m = np.asarray(matrix, dtype=np.float32)[:2, :]
    return cv2.warpAffine(moving_mono8, m, size_wh, flags=cv2.INTER_LINEAR)
```

- [ ] **Bước 5.5: `pairs.py` — liệt kê cặp cho người dùng chọn**

```python
"""Sinh danh sach cap de kiem tra.

Direct   : tile Gerber (crop tu raster) <-> anh chup cung orderIndex
Neighbor : anh chup <-> anh chup ke ben
"""
import json
import os
import cv2
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None


def load_payload(path):
    with open(path, "r", encoding="utf-8-sig") as fh:
        data = json.load(fh)
    tiles = sorted(data["GerberTiles"], key=lambda t: t["OrderIndex"])
    return {
        "tiles": tiles,
        "sample_path": data.get("GerberSampleImagePath"),
        "image_width": data.get("Width_CaptureImages", 4096),
        "image_height": data.get("Height_CaptureImages", 4096),
        "overlap_x": data.get("OVerLapX_EdgePath"),
        "overlap_y": data.get("OVerLapY_EdgePath"),
    }


class RasterSource(object):
    """Doc crop tu raster Gerber ma khong nap ca anh 40418x32364 vao RAM."""

    def __init__(self, path):
        self.image = Image.open(path)
        self.width, self.height = self.image.size

    def crop_mono8(self, x, y, w, h):
        x0 = max(0, min(self.width - 1, int(x)))
        y0 = max(0, min(self.height - 1, int(y)))
        x1 = min(self.width, x0 + int(w))
        y1 = min(self.height, y0 + int(h))
        patch = self.image.crop((x0, y0, x1, y1)).convert("L")
        arr = np.asarray(patch, dtype=np.uint8)
        if arr.shape[0] != h or arr.shape[1] != w:
            padded = np.full((int(h), int(w)), 255, dtype=np.uint8)
            padded[:arr.shape[0], :arr.shape[1]] = arr
            return padded
        return arr


def captured_path(folder, order_index, extension=".bmp"):
    return os.path.join(folder, str(order_index) + extension)


def load_captured(folder, order_index, extension=".bmp"):
    path = captured_path(folder, order_index, extension)
    img = cv2.imread(path, cv2.IMREAD_GRAYSCALE)
    if img is None:
        raise IOError("Khong doc duoc " + path)
    return img


def direct_pair(payload, raster, images_folder, order_index, step_override=None, extension=".bmp"):
    """Reference = crop raster tai vet chan danh nghia cua anh chup, CUNG kich thuoc anh chup.

    step_override cho phep so sanh buoc luoi khac nhau (vd 4096 vs 4031.5) ma khong phai
    sinh lai payload -- dung de nhin tan mat anh huong cua sai buoc len ECC.
    """
    tile = next(t for t in payload["tiles"] if t["OrderIndex"] == order_index)
    w, h = payload["image_width"], payload["image_height"]
    if step_override is None:
        x = tile["ExpectedX"] + (tile["Width"] - w) // 2
        y = tile["ExpectedY"] + (tile["Height"] - h) // 2
    else:
        x = int(tile["Column"] * step_override)
        y = int(tile["Row"] * step_override)
    reference = raster.crop_mono8(x, y, w, h)
    moving = load_captured(images_folder, order_index, extension)
    return reference, moving, {"reference_origin": (x, y), "tile": tile}


def neighbor_pair(payload, images_folder, anchor_index, target_index, extension=".bmp"):
    return (load_captured(images_folder, anchor_index, extension),
            load_captured(images_folder, target_index, extension),
            {"anchor": anchor_index, "target": target_index})


def index_of(payload, row, column):
    for t in payload["tiles"]:
        if t["Row"] == row and t["Column"] == column:
            return t["OrderIndex"]
    return None
```

- [ ] **Bước 5.6: `app.py` — UI Tkinter**

**Bố cục đã được user duyệt ngày 2026-08-15. Không tự đổi cấu trúc panel/tab.**

```
┌─ RDL.GerberStitch — ECC / Preprocessing Sandbox ───────────────────────────┐
│ ┌── panel trái (~212px) ──┐ ┌── panel phải (giãn) ────────────────────────┐│
│ │ [Dữ liệu]               │ │ ( Tiền xử lý ) ( Kết quả match ) ( Ma trận )││
│ │   Payload JSON  + nút   │ │ ┌─────────────────────────────────────────┐ ││
│ │   Thư mục ảnh   + nút   │ │ │ Tab 1: lưới 2 hàng × N cột              │ ││
│ │   Raster Gerber + nút   │ │ │   hàng trên = reference (tile Gerber)   │ ││
│ │   Đuôi file             │ │ │   hàng dưới = moving (ảnh chụp)         │ ││
│ │ [Cặp kiểm tra]          │ │ │   cột = raw · contrast · flattened      │ ││
│ │   ◉ Direct  ○ Neighbor  │ │ │         · binary (chỉ khi bật)          │ ││
│ │   Row / Column          │ │ │ Tab 2: overlay 2 kênh, TRƯỚC | SAU      │ ││
│ │   Hướng neighbor        │ │ │ Tab 3: correlation TỪNG level pyramid,  │ ││
│ │   Step ghi đè  ← nổi bật│ │ │         ma trận 3×3, kết luận màu       │ ││
│ │ [Tiền xử lý (§8.3)]     │ │ └─────────────────────────────────────────┘ ││
│ │ [PyramidECC (từ ini)]   │ │                                             ││
│ │ [        CHẠY        ]  │ │                                             ││
│ └─────────────────────────┘ └─────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────────────────────┘
```

Bốn quyết định thiết kế đã chốt, **giữ nguyên khi cài đặt**:

1. **Ô `Step ghi đè` phải nổi bật khác các ô còn lại** (đổi màu viền/chữ). Nó là tham số duy nhất
   không tương ứng với bất kỳ setting nào trong C# — nó tồn tại để so `4096` với `4031.5` tại chỗ.
2. **Tab 1 luôn có dòng nhắc dưới lưới**: cột `binary` không bao giờ đi vào ECC (Findings §8.2).
   Đặt cảnh báo ngay chỗ dễ phạm lỗi nhất.
3. **Tab 2 dùng overlay hai kênh màu, KHÔNG phải hai ảnh cạnh nhau.** Đặt cạnh nhau thì mắt không
   thấy lệch 10 px trên khung 4096; overlay thì trùng ra xám, lệch thì hiện viền màu.
4. **Tab 3 in correlation của TỪNG level pyramid**, không chỉ số cuối. Chuỗi tăng dần
   (`0.712 → 0.839 → 0.908`) nghĩa là hội tụ tốt; chuỗi *giảm* khi xuống level mịn hơn là dấu hiệu
   bám cực trị cục bộ. `processing_report.json` hiện không có thông tin này.

Dùng **ô nhập số**, không dùng thanh trượt, cho các tham số tiền xử lý — vì giá trị đã thử phải chép
được nguyên văn vào bảng kết quả ở Bước 5.10.

```python
"""Sandbox UI: chon cap -> tien xu ly -> PyramidECC -> xem ma tran + ket qua match."""
import os
import sys
import traceback
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

import cv2
import numpy as np
from matplotlib.backends.backend_tkagg import FigureCanvasTkAgg
from matplotlib.figure import Figure

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import config as cfg_mod
import pairs as pairs_mod
import preprocess as pre
import pyramid_ecc as ecc

PREVIEW = 512


def thumb(img):
    if img is None:
        return np.zeros((8, 8), dtype=np.uint8)
    scale = PREVIEW / float(max(img.shape[0], img.shape[1]))
    if scale >= 1.0:
        return img
    return cv2.resize(img, (int(img.shape[1] * scale), int(img.shape[0] * scale)),
                      interpolation=cv2.INTER_AREA)


class App(object):
    def __init__(self, root):
        self.root = root
        root.title("RDL.GerberStitch — ECC / Preprocessing Sandbox")
        self.cfg = cfg_mod.load(self._guess_ini())
        self.payload = None
        self.raster = None
        self.vars = {}

        outer = ttk.Frame(root, padding=6)
        outer.pack(fill="both", expand=True)
        left = ttk.Frame(outer)
        left.pack(side="left", fill="y", padx=(0, 8))
        right = ttk.Frame(outer)
        right.pack(side="left", fill="both", expand=True)

        self._build_inputs(left)
        self._build_params(left)
        self._build_tabs(right)

    # ---------- helpers ----------
    def _guess_ini(self):
        here = os.path.dirname(os.path.abspath(__file__))
        candidate = os.path.abspath(os.path.join(
            here, "..", "..", "RDL.GerberStitch.Harness", "align_stitch.ini"))
        return candidate if os.path.exists(candidate) else None

    def _row(self, parent, label, key, default, width=10):
        frame = ttk.Frame(parent)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text=label, width=22).pack(side="left")
        var = tk.StringVar(value=str(default))
        ttk.Entry(frame, textvariable=var, width=width).pack(side="left", fill="x", expand=True)
        self.vars[key] = var
        return var

    def _num(self, key, caster=float):
        return caster(self.vars[key].get())

    # ---------- panels ----------
    def _build_inputs(self, parent):
        box = ttk.LabelFrame(parent, text="Dữ liệu", padding=6)
        box.pack(fill="x")
        self._row(box, "Payload JSON", "payload", "", 32)
        ttk.Button(box, text="Chọn payload…", command=self.pick_payload).pack(fill="x", pady=2)
        self._row(box, "Thư mục ảnh chụp", "images", "", 32)
        ttk.Button(box, text="Chọn thư mục…", command=self.pick_images).pack(fill="x", pady=2)
        self._row(box, "Raster Gerber", "raster", "", 32)
        ttk.Button(box, text="Chọn raster…", command=self.pick_raster).pack(fill="x", pady=2)
        self._row(box, "Đuôi file ảnh", "ext", ".bmp")

        sel = ttk.LabelFrame(parent, text="Cặp kiểm tra", padding=6)
        sel.pack(fill="x", pady=(8, 0))
        self.mode = tk.StringVar(value="direct")
        ttk.Radiobutton(sel, text="Direct  (tile Gerber ↔ ảnh chụp)",
                        variable=self.mode, value="direct").pack(anchor="w")
        ttk.Radiobutton(sel, text="Neighbor (ảnh chụp ↔ ảnh chụp)",
                        variable=self.mode, value="neighbor").pack(anchor="w")
        self._row(sel, "Row", "row", 1, 6)
        self._row(sel, "Column", "col", 1, 6)
        self._row(sel, "Hướng neighbor", "dir", "right", 8)
        self._row(sel, "Step ghi đè (0=payload)", "step", 0, 10)

    def _build_params(self, parent):
        pp = ttk.LabelFrame(parent, text="Tiền xử lý (§8.3)", padding=6)
        pp.pack(fill="x", pady=(8, 0))
        self.flatten = tk.BooleanVar(value=True)
        ttk.Checkbutton(pp, text="FlattenAndEnhance", variable=self.flatten).pack(anchor="w")
        self.binarize = tk.BooleanVar(value=False)
        ttk.Checkbutton(pp, text="ToBinaryTraces (chỉ để xem)",
                        variable=self.binarize).pack(anchor="w")
        self._row(pp, "Contrast (%)", "contrast", self.cfg["Contrast"])
        self._row(pp, "Background sigma", "bgsigma", self.cfg["BackgroundSigma"])
        self._row(pp, "CLAHE clip limit", "clip", self.cfg["ClaheClipLimit"])
        self._row(pp, "CLAHE tile", "clahetile", self.cfg["ClaheTile"])
        self._row(pp, "Adaptive blockSize", "block", self.cfg["AdaptiveBlockSize"])
        self._row(pp, "Adaptive C", "cval", self.cfg["AdaptiveC"])
        self._row(pp, "Close kernel", "ck", self.cfg["CloseKernel"])

        ep = ttk.LabelFrame(parent, text="PyramidECC (từ align_stitch.ini)", padding=6)
        ep.pack(fill="x", pady=(8, 0))
        frame = ttk.Frame(ep)
        frame.pack(fill="x", pady=1)
        ttk.Label(frame, text="MotionModel", width=22).pack(side="left")
        self.motion = tk.StringVar(value=self.cfg["EccMotionModel"])
        ttk.Combobox(frame, textvariable=self.motion, width=12, state="readonly",
                     values=["Translation", "Euclidean", "Affine"]).pack(side="left")
        self._row(ep, "PyramidLevels", "levels", self.cfg["EccPyramidLevels"])
        self._row(ep, "MaxIterations", "iters", self.cfg["EccMaxIterations"])
        self._row(ep, "Epsilon", "eps", self.cfg["EccEpsilon"])
        self._row(ep, "MinCorrelation", "mincorr", self.cfg["EccMinCorrelation"])
        self._row(ep, "MaxAbsRotationDeg", "maxrot", self.cfg["MaxAbsRotationDeg"])
        self._row(ep, "MaxTranslationPixels", "maxtrans", self.cfg["MaxTranslationPixels"])

        ttk.Button(parent, text="CHẠY", command=self.run).pack(fill="x", pady=8)

    def _build_tabs(self, parent):
        self.tabs = ttk.Notebook(parent)
        self.tabs.pack(fill="both", expand=True)

        self.fig_pre = Figure(figsize=(9, 5.2), dpi=90)
        self.canvas_pre = self._add_tab(self.fig_pre, "Tiền xử lý")

        self.fig_match = Figure(figsize=(9, 5.2), dpi=90)
        self.canvas_match = self._add_tab(self.fig_match, "Kết quả match")

        text_tab = ttk.Frame(self.tabs)
        self.tabs.add(text_tab, text="Ma trận & log")
        self.log = tk.Text(text_tab, wrap="none", font=("Consolas", 9))
        self.log.pack(fill="both", expand=True)

    def _add_tab(self, figure, title):
        tab = ttk.Frame(self.tabs)
        self.tabs.add(tab, text=title)
        canvas = FigureCanvasTkAgg(figure, master=tab)
        canvas.get_tk_widget().pack(fill="both", expand=True)
        return canvas

    # ---------- file pickers ----------
    def pick_payload(self):
        path = filedialog.askopenfilename(filetypes=[("JSON", "*.json")])
        if path:
            self.vars["payload"].set(path)

    def pick_images(self):
        path = filedialog.askdirectory()
        if path:
            self.vars["images"].set(path)

    def pick_raster(self):
        path = filedialog.askopenfilename(
            filetypes=[("Ảnh", "*.tif *.tiff *.png *.bmp *.jpg")])
        if path:
            self.vars["raster"].set(path)

    def say(self, text):
        self.log.insert("end", text + "\n")
        self.log.see("end")

    # ---------- run ----------
    def current_cfg(self):
        c = dict(self.cfg)
        c.update({
            "Contrast": self._num("contrast"),
            "BackgroundSigma": self._num("bgsigma"),
            "ClaheClipLimit": self._num("clip"),
            "ClaheTile": self._num("clahetile", int),
            "AdaptiveBlockSize": self._num("block", int),
            "AdaptiveC": self._num("cval"),
            "CloseKernel": self._num("ck", int),
            "EccMotionModel": self.motion.get(),
            "EccPyramidLevels": self._num("levels", int),
            "EccMaxIterations": self._num("iters", int),
            "EccEpsilon": self._num("eps"),
            "EccMinCorrelation": self._num("mincorr"),
            "MaxAbsRotationDeg": self._num("maxrot"),
            "MaxTranslationPixels": self._num("maxtrans"),
        })
        return c

    def run(self):
        try:
            self._run()
        except Exception as ex:
            self.say("LỖI: " + str(ex))
            self.say(traceback.format_exc())
            messagebox.showerror("Lỗi", str(ex))

    def _run(self):
        self.log.delete("1.0", "end")
        c = self.current_cfg()
        payload_path = self.vars["payload"].get()
        images = self.vars["images"].get()
        ext = self.vars["ext"].get() or ".bmp"
        if not payload_path or not images:
            raise ValueError("Cần chọn payload JSON và thư mục ảnh chụp.")

        payload = pairs_mod.load_payload(payload_path)
        row, col = self._num("row", int), self._num("col", int)
        order = pairs_mod.index_of(payload, row, col)
        if order is None:
            raise ValueError("Không có tile ở (row=%d, col=%d)." % (row, col))

        step = self._num("step")
        step = None if step <= 0 else step

        if self.mode.get() == "direct":
            raster_path = self.vars["raster"].get()
            if not raster_path:
                raise ValueError("Mode Direct cần raster Gerber.")
            raster = pairs_mod.RasterSource(raster_path)
            reference, moving, meta = pairs_mod.direct_pair(
                payload, raster, images, order, step, ext)
            self.say("Direct: tile order=%d (row=%d, col=%d), reference origin=%s"
                     % (order, row, col, meta["reference_origin"]))
        else:
            direction = self.vars["dir"].get().strip().lower()
            delta = {"right": (0, 1), "left": (0, -1),
                     "bottom": (1, 0), "top": (-1, 0)}[direction]
            target = pairs_mod.index_of(payload, row + delta[0], col + delta[1])
            if target is None:
                raise ValueError("Không có neighbor hướng " + direction)
            reference, moving, meta = pairs_mod.neighbor_pair(payload, images, order, target, ext)
            self.say("Neighbor: anchor=%d target=%d (%s)" % (order, target, direction))

        ref_v = pre.build_variants(reference, c, self.flatten.get(), self.binarize.get())
        mov_v = pre.build_variants(moving, c, self.flatten.get(), self.binarize.get())
        self._draw_preprocess(ref_v, mov_v)

        result = ecc.match(ref_v["final"], mov_v["final"], c)
        self._report(result)
        self._draw_match(ref_v["final"], mov_v["final"], result)

    def _draw_preprocess(self, ref_v, mov_v):
        keys = [k for k in ("raw", "contrast", "flattened", "binary") if k in ref_v]
        self.fig_pre.clear()
        for i, key in enumerate(keys):
            for j, (name, variants) in enumerate((("reference", ref_v), ("moving", mov_v))):
                ax = self.fig_pre.add_subplot(2, len(keys), j * len(keys) + i + 1)
                ax.imshow(thumb(variants[key]), cmap="gray", vmin=0, vmax=255)
                ax.set_title("%s · %s" % (name, key), fontsize=8)
                ax.axis("off")
        self.fig_pre.tight_layout()
        self.canvas_pre.draw()

    def _draw_match(self, reference, moving, result):
        self.fig_match.clear()
        h, w = reference.shape
        before = cv2.merge([thumb(reference), thumb(moving), thumb(reference)])
        ax1 = self.fig_match.add_subplot(1, 2, 1)
        ax1.imshow(before)
        ax1.set_title("TRƯỚC — magenta=reference, xanh=moving", fontsize=8)
        ax1.axis("off")

        ax2 = self.fig_match.add_subplot(1, 2, 2)
        if result.get("matrix") is not None:
            warped = ecc.warp_moving_to_reference(moving, result["matrix"], (w, h))
            after = cv2.merge([thumb(reference), thumb(warped), thumb(reference)])
            ax2.imshow(after)
            ax2.set_title("SAU — trùng nhau ⇒ xám, lệch ⇒ viền màu", fontsize=8)
        else:
            ax2.text(0.5, 0.5, "Không có ma trận", ha="center", va="center")
        ax2.axis("off")
        self.fig_match.tight_layout()
        self.canvas_match.draw()

    def _report(self, r):
        self.say("")
        self.say("=== PyramidECC ===")
        for lv in r["levels"]:
            corr = lv["correlation"]
            self.say("  level %d  size=%sx%s  scale=%.4f  corr=%s"
                     % (lv["level"], lv["size"][0], lv["size"][1], lv["scale"],
                        "n/a" if corr is None else "%.5f" % corr))
        self.say("")
        if r.get("matrix") is None:
            self.say("THẤT BẠI: %s — %s" % (r["failure_reason"], r["message"]))
            return
        m = r["matrix"]
        self.say("Ma trận MovingImage -> ReferenceImage:")
        for i in range(3):
            self.say("   [ %12.6f  %12.6f  %12.4f ]" % (m[i, 0], m[i, 1], m[i, 2]))
        self.say("")
        self.say("  translation = (%.3f, %.3f) px" % (r["translation_x"], r["translation_y"]))
        self.say("  rotation    = %.5f deg" % r["rotation_deg"])
        self.say("  scale       = %.8f" % r["scale"])
        self.say("  rawScore    = %.5f   (MinCorrelation %.3f)"
                 % (r["raw_score"], self.current_cfg()["EccMinCorrelation"]))
        self.say("  confidence  = %.5f" % r["normalized_confidence"])
        self.say("")
        if r["success"]:
            self.say("KẾT LUẬN: ACCEPTED")
        else:
            self.say("KẾT LUẬN: REJECTED — %s: %s" % (r["failure_reason"], r["message"]))


def main():
    root = tk.Tk()
    root.geometry("1400x900")
    App(root)
    root.mainloop()


if __name__ == "__main__":
    main()
```

- [ ] **Bước 5.7: `README.md`**

Ghi rõ: đây là công cụ khảo sát, không phải code sản phẩm; không nằm trong `.sln`; cách chạy;
ý nghĩa ô "Step ghi đè"; và cảnh báo **không** bật `ToBinaryTraces` rồi đưa vào ECC (Findings §8.2 —
ảnh nhị phân có gradient bằng 0 nên ECC không hội tụ).

```bash
python tools/ecc_sandbox/app.py
```

- [ ] **Bước 5.8: [USER] Kiểm chứng port đúng — đối chiếu với C#**

Đây là bước quan trọng nhất của task. Chạy sandbox ở **Direct**, `FlattenAndEnhance = tắt`,
`Contrast = 100` (tức không tiền xử lý gì, đúng hiện trạng pipeline), trên một tile mà
`processing_report.json` có `eccCorrelation`. So `rawScore` của sandbox với `eccCorrelation` trong report.

Lệch dưới ~0.02 là port đúng. Lệch nhiều hơn ⇒ kiểm ba chỗ đã đánh dấu ở Bước 5.4
(thứ tự template/input, chiều duyệt pyramid, scale chỉ nhân translation).

- [ ] **Bước 5.9: [USER] Thí nghiệm bước lưới**

Cùng một tile, chạy hai lần với ô **Step ghi đè**: `4096` rồi `4031.5`.
`rawScore` phải **cao hơn rõ rệt** ở 4031.5. Đây là xác nhận trực quan cho toàn bộ Task 1–3.

- [ ] **Bước 5.10: [USER] Dò tham số tiền xử lý**

Bật `FlattenAndEnhance`, quét `BackgroundSigma` (31 / 51 / 81) và `ClaheClipLimit` (2 / 3 / 4)
trên 5–6 tile. Ghi bảng `rawScore` trước/sau.

**Tiêu chí của Findings §8.5: ECC correlation không được GIẢM.** Nếu giảm ⇒ tiền xử lý đang phá
gradient, không đưa vào C#.

- [ ] **Bước 5.11: Ghi `docs/implement_code.html`** — kèm bảng số liệu thu được

- [ ] **Bước 5.12: Commit**

```bash
git add tools/ecc_sandbox docs/implement_code.html
git commit -m "Add Python sandbox for preprocessing and pyramid ECC experiments"
```

- [ ] **Bước 5.13: Ghi §Lịch sử thay đổi**

### Checklist bàn giao Task 5

- [ ] Sandbox nằm ngoài `.sln`, không sửa file C# nào
- [ ] `config.py` đọc `align_stitch.ini`, fallback đúng mặc định C# (không phải mặc định OpenCV)
- [ ] `pyramid_ecc.py` truyền `gaussFiltSize=5` tường minh
- [ ] Thứ tự `findTransformECC(reference, moving, …)` đúng chiều template/input
- [ ] Pyramid index 0 = full resolution, duyệt từ index lớn về 0
- [ ] `scale` chỉ nhân/chia phần translation
- [ ] Chuỗi kiểm tra ValidateMovingToReference đúng thứ tự như C#
- [ ] UI có: chọn cặp, cửa sổ tiền xử lý, cửa sổ match trước/sau, ma trận 3×3, kết luận accept/reject
- [ ] [USER] `rawScore` sandbox khớp `eccCorrelation` trong report (lệch < 0.02)
- [ ] [USER] step 4031.5 cho score cao hơn step 4096
- [ ] [USER] bảng dò tham số đã ghi; ECC correlation **không giảm**
- [ ] Entry `implement_code.html` + commit + §Lịch sử thay đổi

---

## Task 6: Sweep 4 FOV và tổng kết

**Files:**
- Modify: `docs/implement_code.html`
- Create: `docs/superpowers/plans/2026-08-15-grid-pitch-calibration-results.md`

**Interfaces:** không sinh API mới. Đây là task đo đạc và ghi nhận.

---

- [ ] **Bước 6.1: [USER] Chạy case 4096 trước tiên**

Chạy `alignstitchmem` với `sample_4096_o63.json`. Case này quan trọng nhất: `TileWidth = ImageWidth`
nên ROI của Neighbor Recovery rộng đúng `CaptureOverlap` — nếu alias tự hết thì thấy ở đây.

- [ ] **Bước 6.2: [USER] Chạy 3 case còn lại**

`sample_4192_o159.json`, `sample_4240_o207.json`, `sample_4320_o287.json`.

- [ ] **Bước 6.3: Thu thập số liệu**

Với mỗi run, ghi lại bảng:

```bash
python -c "
import json,sys,statistics as st,collections
d=json.load(open(sys.argv[1],encoding='utf-8-sig'))
g=d.get('gridCalibration'); p=d['poseGraph']
print('gridCalibration', {k:g[k] for k in ('status','measuredStepX','measuredStepY','deltaX','deltaY','medianScore')} if g else None)
print('edges', {k:p[k] for k in p if k.startswith('edges')})
print('residual before/after max', p['beforeResidualMax'], p['afterResidualMax'], 'converged', p['converged'])
last={}
for t in d['tileReports']: last[t['orderIndex']]=t
print('poseSource', dict(collections.Counter(v['poseSource'] for v in last.values())))
acc=[x for x in d['recoveryEdges'] if x['accepted']]
v=[x['residualDx'] for x in acc if round(x['expectedTargetToAnchorTransform'][0][2])>0]
print('residualDx n=%d mean=%.2f std=%.2f'%(len(v),st.mean(v),st.pstdev(v)) if v else 'residualDx: none')
" <duong_dan>/processing_report.json
```

- [ ] **Bước 6.4: So với tiêu chí nghiệm thu**

| Metric | Trước | Mục tiêu |
|---|---|---|
| `gridCalibration.status` | — | `Ok` |
| Residual mỗi cạnh, \|mean\| | 64.5 px (thật) | **< 2 px** |
| `edgesMeasured` | 22 accepted / 142 | **> 100** |
| `beforeResidualMax` | 573.6 | **< 40** |
| Tile bị kẹp | 13 | **0** |
| Tile blank | 29 | **29** (giữ nguyên — hợp lệ) |
| Bậc đỉnh < 2 | — | chỉ trong vùng trống thật |

- [ ] **Bước 6.5: Ghi kết quả vào `docs/superpowers/plans/2026-08-15-grid-pitch-calibration-results.md`**

Bảng 4 case × các metric trên, kèm nhận định. **Nếu `edgesGatedOut` tăng vọt**, ghi rõ — đó là dữ
liệu quyết định cho hạng mục ROI/alias ở §7 spec, **không** được xử lý bằng cách nới
`MaxEdgeResidualPixels`.

- [ ] **Bước 6.6: Ghi `docs/implement_code.html`** — entry tổng kết

- [ ] **Bước 6.7: Commit**

```bash
git add docs/superpowers/plans/2026-08-15-grid-pitch-calibration-results.md docs/implement_code.html
git commit -m "Record grid pitch calibration sweep results for four FOV configs"
```

- [ ] **Bước 6.8: Ghi §Lịch sử thay đổi**

### Checklist bàn giao Task 6

- [ ] 4 case đã chạy, số liệu đã thu
- [ ] Bảng so sánh với tiêu chí nghiệm thu đã điền
- [ ] `edgesGatedOut` bất thường (nếu có) đã ghi rõ, **không** nới `MaxEdgeResidualPixels`
- [ ] File kết quả đã tạo và commit
- [ ] Entry `implement_code.html` + §Lịch sử thay đổi

---

## Lịch sử thay đổi

> **Bắt buộc cập nhật sau mỗi task.** Một dòng mỗi lần thay đổi. Cột "Lỗi & cách fix" là cột dễ mất
> nhất giữa các phiên làm việc — ghi cả lỗi tưởng nhỏ (CS0104, CS0012, thiếu DLL lúc chạy).

| Ngày | Task | Commit | Thay đổi | Lỗi gặp phải & cách fix |
|---|---|---|---|---|
| 2026-08-15 | — | `633bb96` | Viết spec thiết kế | — |
| 2026-08-15 | — | (plan này) | Viết implementation plan. Xác minh trước khi viết: `UseRejectedEdges=true` đã giữ measured transform, 142/142 cạnh có phép đo thật ⇒ Task 4 thu hẹp còn sửa báo cáo. | — |
| 2026-08-15 | — | (plan này) | Chèn Task 5 mới (sandbox Python: tiền xử lý §8.3 + PyramidECC có UI). Task "Sweep 4 FOV" cũ dời thành Task 6, các bước đánh lại số 6.x. | — |
| 2026-08-15 | 5 | (plan này) | User đã duyệt bố cục UI của `app.py`. Chốt 4 quyết định thiết kế ở Bước 5.6 — không tự đổi cấu trúc panel/tab khi cài đặt. | — |
| 2026-08-15 | 1 | `bd3dbe6` | `CaptureGridCalculator`/`CaptureGridSpec`/`CaptureGridResult` (mới) + `GerberStitchFacade.BuildCaptureGrid` + harness mode `createsamplemem`. Build x64 PASS, 0 lỗi mới. Bước 1.9/1.10 ([USER]) đang chờ user chạy `RDL.GerberStitch.Harness.exe --mode createsamplemem` để đối chiếu `Clamped tiles = 0` và sweep `--tile`. | Không gặp lỗi build nào (đã thêm `<Compile Include>` cho 3 file mới vào `.csproj` ngay từ đầu, tránh lặp lại CS0246 đã gặp ở entry 11 trong `implement_code.html`). |
| | | | | |

### Ghi chú cho người thực thi

- Không tick checkbox trước khi làm.
- Nếu một bước trong plan sai so với code thật, **dừng, ghi vào bảng trên, sửa plan**, rồi mới đi tiếp.
- Không gộp task vào một commit.
- Mọi bước `[USER]` phải chờ user chạy và báo kết quả — agent không tự chạy ứng dụng, không tự tuyên bố pass (AGENTS.md §4).
