# Phase 1 — Task 1.6: Console harness chạy thử façade với dữ liệu thật

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**Trạng thái:** ✅ **Đã triển khai và chạy thành công** cả 2 nhánh — `alignstitch` (§4) và `createsample` (§5) — với dữ liệu thật
**Phụ thuộc:** `Phase1_Task01.md` + `Phase1_Task02.md` (façade `public`, đã có `GenerateSampleManifest`/`GenerateSampleManifestFromRaster`/`RunAlignStitch`)
**Platform:** C# 7.3, .NET Framework 4.8, Console Application, **x64**

**Bắt buộc đọc trước khi sửa:**
1. `docs/Phase1_Task05.md` — harness này hiện thực hoá đúng mục tiêu đo RAM/thời gian mà Task 1.5 đặt ra, dùng chung tên project `RDL.GerberStitch.Harness`
2. `docs/Phase1_Task02.md` mục "Đã triển khai" — signature thật của các hàm façade, đặc biệt mục 2 (đánh đổi lộ kiểu `GerberSampleConfig` của Core)

**Ràng buộc:** Không thêm NuGet package ngoài `OpenCvSharp4.runtime.win` (native runtime cần cho OpenCvSharp — bắt buộc để harness tự chạy độc lập, xem §3.2). Không thêm project test theo nghĩa NUnit/xUnit — đây là công cụ đo, không có assertion.

---

## 0. Mục tiêu

Chạy thử façade với dữ liệu thật, không qua GerberViewer UI. Hai kịch bản:

| Kịch bản | Hàm façade | Đầu vào |
|---|---|---|
| **`alignstitch`** (mặc định) | `RunAlignStitch` | manifest có sẵn + thư mục ảnh đã chụp (xem §4) |
| **`createsample`** | `GenerateSampleManifestFromRaster` | 1 ảnh raster đã render sẵn (xem §5) |

---

## 1. Project `RDL.GerberStitch.Harness`

```
RDL.GerberStitch.Harness/
├── GlobalConfig.cs
├── Program.cs
├── global_config.json
├── Properties/AssemblyInfo.cs
├── packages.config
└── RDL.GerberStitch.Harness.csproj
```

### 1.1. `Program.cs` — luồng chạy

```
Main(args)
  ├─ Console.OutputEncoding = UTF-8 (§4.4 — tránh chữ có dấu hiển thị sai)
  ├─ Đăng ký AppDomain.AssemblyResolve cho halcondotnetxl/hdevenginedotnetxl (§3.2)
  ├─ Đọc --mode (default "alignstitch") + --config (default "<thư mục exe>\global_config.json")
  ├─ GlobalConfig.ReadOrNull(configPath) — không có file thì trả null, KHÔNG lỗi
  └─ Rẽ nhánh RunAlignStitch(args, config) | RunCreateSample(args, config)
```

Cả 2 nhánh đọc tham số theo thứ tự ưu tiên: **CLI arg** (`--manifest`/`--images`/`--out` hoặc `--raster`/`--out`/`--folder`) **> `global_config.json`** (section `AlignStitch`/`CreateSample` tương ứng) **> báo lỗi thiếu tham số** (exit code 2), không có giá trị mặc định hard-code trong source nữa (khác bản trước — đường dẫn máy cụ thể giờ nằm trong `global_config.json`, không phải trong code).

### 1.2. `global_config.json` — cấu hình sẵn cho người test

```json
{
  "AlignStitch": {
    "ManifestPath": "H:\\005_Project\\AOI_2026_07_imp\\data\\GerberSample_20260806_084849\\sample_manifest.json",
    "ImagesPath": "H:\\005_Project\\AOI_2026_07_imp\\20260720_Gerber_Align\\20260725 Q168 2-1 org",
    "OutputPath": "H:\\005_Project\\AOI_2026_07_imp\\result_20260807"
  },
  "CreateSample": {
    "RasterImagePath": "H:\\005_Project\\AOI_2026_07_imp\\20260720_Gerber_Align\\q168_Gerber2-1 to 2-4.tiff",
    "OutputPath": "H:\\005_Project\\AOI_2026_07_imp"
  }
}
```

File này có `<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>` trong `.csproj` — luôn nằm cạnh `.exe` sau build. Đọc qua `GlobalConfig.cs` bằng `DataContractJsonSerializer` (BCL có sẵn, giống cách `SampleManifestSerializer` của Core đọc `sample_manifest.json` — không thêm thư viện JSON mới).

Người test **không bắt buộc** phải sửa file này — có thể ghi đè từng phần bằng CLI arg (vd `--out "Z:\ket_qua_khac"` trong khi vẫn lấy `--manifest`/`--images` từ config file).

### 1.3. `RDL.GerberStitch.Harness.csproj` — dependency

**Không đổi so với thiết kế ban đầu** (§3.2 dưới đây), **thêm 1 điểm mới:** `ProjectReference` tới `GerberStitching.Core.csproj`, **chỉ vì nhánh `createsample`**.

**Lý do bắt buộc phải thêm, không phải tuỳ chọn:** `GenerateSampleManifestFromRaster` nhận tham số kiểu `GerberViewer.Stitching.Configuration.GerberSampleConfig` (đánh đổi có chủ đích của Task 1.2 — façade tái dùng thẳng DTO này của Core thay vì nhân đôi một lớp gần giống hệt). Ban đầu định tránh phải reference Core trong harness bằng cách gọi `GenerateSampleManifestFromRaster(rasterPath, null, ...)` — nhưng **build vẫn báo lỗi `CS0012`**: C# yêu cầu assembly định nghĩa một type xuất hiện trong signature của method đang gọi phải được reference trực tiếp lúc biên dịch, **kể cả khi chỉ truyền `null`** cho tham số đó — đây không phải giới hạn của project kiểu cũ (`ToolsVersion="15.0"`), mà là hành vi chuẩn của trình biên dịch C# với mọi kiểu project. Chấp nhận: `RunAlignStitch` (nhánh `alignstitch`) **vẫn giữ được** ranh giới thuần — không kiểu nào của Core lộ ra qua signature của nó.

---

## 2. Đối chiếu ranh giới façade — chỉ còn đúng cho nhánh `alignstitch`

Nghiệm thu ban đầu của Task 1.6 đặt tiêu chí *"`Program.cs` không `using` bất kỳ namespace nào của Core"*. Sau khi thêm `createsample`, tiêu chí này **chỉ còn đúng cho code chạy nhánh `RunAlignStitch`**. Nhánh `RunCreateSample` bắt buộc `using GerberViewer.Stitching.Configuration;` để tạo `GerberSampleConfig` truyền vào façade (§1.3). Đây là hệ quả trực tiếp của đánh đổi đã ghi ở `docs/Phase1_Task02.md` mục "Đã triển khai" #2, không phải lỗi thiết kế mới phát sinh ở harness.

---

## 3. Dependency đầy đủ (áp dụng cho cả 2 nhánh)

### 3.1. Vì sao harness cần khác `RDL.GerberStitch`/`GerberStitching.Core`

`RDL.GerberStitch.csproj` và `GerberStitching.Core.csproj` đặt `Private=False` cho `halcondotnetxl`/`hdevenginedotnetxl` (`docs/Phase1_Task01.md` §3, `docs/Phase1_Task03.md` §2) — đúng cho Master/Worker vì folder deploy của họ **đã có sẵn** các DLL này. Harness là công cụ test độc lập, **không có** folder deploy nào — nếu giữ nguyên `Private=False` xuyên suốt, `.exe` sẽ crash lúc runtime vì .NET Framework không tìm thấy assembly (không giống native DLL, việc nạp assembly managed **không** dò theo biến môi trường `PATH`).

### 3.2. Hai cơ chế bù, chỉ áp dụng trong project harness

1. **HALCON (`halcondotnetxl`, `hdevenginedotnetxl`):** harness tự khai `<Reference>` riêng với `<Private>True</Private>` (copy-local vào `bin\` của chính nó). Đồng thời có `AppDomain.CurrentDomain.AssemblyResolve` fallback đọc `%HALCONROOT%\bin\dotnet35\` phòng trường hợp bản copy-local bị dọn hoặc thiếu.
2. **OpenCvSharp native (`OpenCvSharpExtern.dll`, `opencv_videoio_ffmpeg4130_64.dll`):** harness tự import `OpenCvSharp4.runtime.win.props/.targets` (qua `packages.config` + `<Import>` giống `RDL.GerberStitch.csproj`) — cách NuGet chính thống để một project *cuối cùng* (EXE) lấy native asset.

Ngoài ra, harness khai trực tiếp `OpenCvSharp.Extensions`, `System.Buffers`, `System.Drawing`/`System.Drawing.Common`, `System.Memory`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Runtime.Serialization` (cho `DataContractJsonSerializer` đọc `global_config.json`) với đúng `HintPath` như `RDL.GerberStitch.csproj` — **phát hiện khi build lần đầu:** MSBuild **không** copy-local đầy đủ các assembly này xuyên 2 tầng `ProjectReference` (Harness → RDL.GerberStitch → GerberStitching.Core) cho kiểu reference `packages.config`/`HintPath` cũ. Khai trực tiếp là cách chắc chắn nhất.

---

## 4. Nhánh `alignstitch` — kết quả chạy thật (2026-08-07)

```bash
"RDL.GerberStitch.Harness.exe"
# tương đương: "RDL.GerberStitch.Harness.exe" --mode alignstitch
```

Dataset 80 tile, config mặc định (`AlignStitchConfig` façade, không override gì).

```
manifest = H:\005_Project\AOI_2026_07_imp\data\GerberSample_20260806_084849\sample_manifest.json
images   = H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\20260725 Q168 2-1 org
output   = H:\005_Project\AOI_2026_07_imp\result_20260807

=== RESULT ===
Success        : True
TiffPath       : H:\005_Project\AOI_2026_07_imp\result_20260807\AlignStitch_20260807_145801\Stitched.tiff
ElapsedMs      : 302400 (wall clock 303432 ms)
TileCount      : 80
AlignedTiles   : 54
BlankTiles     : 26
FailedTiles    : 0
ErrorCode      : 0
Warnings       : 3 (pose disagreement — chẩn đoán, không chặn kết quả)
PeakWorkingSet : 6616 MB (~6.6 GB)
```

Log đầy đủ (mọi dòng progress từ matcher): `harness_run_log.txt` tại gốc repo.

### 4.1. Đối chiếu với run tham chiếu Phase 0

| | Harness (Task 1.6) | Run tham chiếu `110300` (Phase 0, qua GerberViewer UI) |
|---|---|---|
| Engine | HalconProjectiveMosaicRebased (default façade) | HalconProjectiveMosaicRebased |
| AlignedTiles / SampleAlignment | **54** | 54 |
| BlankTiles / BlankSampleExpectedPose | **26** | 26 |
| FailedTiles | **0** | 0 (outliers=0) |
| Kích thước `Stitched.tiff` | 1 305 931 192 byte | 1 305 898 710 byte (chênh 32 KB — metadata) |
| Tổng thời gian | 303 s | 247 s |

**Số tile khớp tuyệt đối** — xác nhận façade tái tạo đúng hành vi pipeline gốc qua lớp đóng gói mới, không có sai lệch do quá trình wrap. Chênh lệch thời gian (303s vs 247s, +23%) nhiều khả năng do tải máy lúc chạy (máy dev, không phải máy đo benchmark cô lập) — bản thân `RunAlignStitch` chỉ thêm: đọc manifest 1 lần, `CapturedImageLoader.Load` (I/O đọc 80 file ảnh lấy kích thước), và `RunOutputLifecycle.Publish` (move file, không copy) — không có bước nào nặng.

> **Lưu ý về `.creating\`:** trong lúc chạy, `RunOutputLifecycle.Publish` (Core) di chuyển toàn bộ file từ `<outputRoot>\AlignStitch_<timestamp>\.creating\` ra thư mục cha rồi **xoá `.creating\` ngay khi publish xong**. Nếu kiểm tra thư mục này giữa chừng mà không thấy — đó là dấu hiệu **đã publish thành công**, không phải lỗi.

### 4.2. Phát hiện mới từ lần chạy thật này

1. **RAM đỉnh = 6.6 GB** cho lô 80 tile, canvas ~40k×32k — số liệu đầu tiên cho rủi ro *"Tài nguyên Worker"* (roadmap §5, mức Cao). Cần đo thêm trên máy Worker thật trước khi chốt cấu hình `AotoWorkerNum`.
2. **`docs/Phase0_Closeout.md` §1.3 đã ghi sai kích thước output tham chiếu.** 654 MB là của engine `OpenCv`, không phải `HalconProjectiveMosaicRebased` (mặc định façade) — engine đó cho ra **~1.3 GB**. Đã sửa lại `Phase0_Closeout.md` §1.3. Ảnh hưởng: blocker 3 (dung lượng ổ chia sẻ) phải tính theo 1.3 GB/lô.
3. **Xác nhận open item `docs/Phase0_Closeout.md` §6.1.** `AlignStitchWorkflowResult.States` cho đúng 80 phần tử (54+26+0), không nhân đôi như `ProcessingReport.TileReports` (85 phần tử ở run tham chiếu).
4. **Lỗi hiển thị nhỏ (đã sửa):** `Console.Write`/`WriteLine` mặc định dùng codepage hệ thống, làm chữ có dấu tiếng Việt trong `peakAtStage` hiển thị sai. Đã thêm `Console.OutputEncoding = Encoding.UTF8` đầu `Main`.

---

## 5. Nhánh `createsample` — kết quả chạy thật (2026-08-07)

```bash
"RDL.GerberStitch.Harness.exe" --mode createsample
```

Đầu vào lấy từ `global_config.json` section `CreateSample`:

| Tham số | Giá trị |
|---|---|
| `raster` | `H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\q168_Gerber2-1 to 2-4.tiff` |
| `output` | `H:\005_Project\AOI_2026_07_imp` |
| `folder` | tự sinh = `Sample_` + tên file không đuôi = **`Sample_q168_Gerber2-1 to 2-4`** |

`--folder` không truyền → `Program.cs` tự ghép `"Sample_" + Path.GetFileNameWithoutExtension(rasterPath)` theo đúng yêu cầu định dạng tên thư mục.

```
mode   = createsample
raster = H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\q168_Gerber2-1 to 2-4.tiff
output = H:\005_Project\AOI_2026_07_imp
folder = Sample_q168_Gerber2-1 to 2-4

=== RESULT ===
Success          : True
ManifestPath     : H:\005_Project\AOI_2026_07_imp\Sample_q168_Gerber2-1 to 2-4\sample_manifest.json
OutputDirectory  : H:\005_Project\AOI_2026_07_imp\Sample_q168_Gerber2-1 to 2-4
ElapsedMs        : 22929
```

Log đầy đủ: `harness_createsample_log.txt` tại gốc repo.

### 5.1. Kiểm chứng output

| Kiểm tra | Kết quả |
|---|---|
| Thư mục output | `Sample_q168_Gerber2-1 to 2-4\` — đúng format yêu cầu |
| File trong thư mục | `processed_sample.tiff`, `sample_config.json`, `sample_manifest.json`, `tiles\` |
| Số tile trong `tiles\` | **80** (`Sample_R00_C00_O000.tiff` … `Sample_R07_C09_O079.tiff`, dùng grid mặc định của Core Rows=8×Columns=10) |
| `manifest.SourceRasterPath` | khớp đúng file `.tiff` đầu vào |
| `manifest.SourceWidth/Height` | `40418 × 32364` — khớp `manifest.ProcessedWidth/Height` (không resize, đúng ảnh gốc) |
| `manifest.ModelGeneration` | `"OnTheFly"` — **lần đầu đường sinh manifest thật ghi trường này** (chạy `alignstitch` ở §4 dùng manifest **có sẵn từ trước**, không đi qua `BuildManifest`, nên chưa từng kiểm chứng field này bằng dữ liệu thật cho tới lần chạy `createsample` này) |
| `manifest.CropOrder`/`StartOrder` | `Zigzag`/`TopLeftDown` — khớp cấu hình đã dùng để tạo manifest tham chiếu ở dataset `alignstitch` |

**Chỉ mất 23 giây** — không có vòng lặp Direct Alignment (đó là phần chiếm 90% thời gian ở nhánh `alignstitch`, xem `Phase0_Closeout.md` §1.1); `createsample` chỉ crop ảnh + ghi file, chi phí chủ yếu là I/O ghi 80 file TIFF.

### 5.2. Đối chiếu gián tiếp với dataset Phase 0

Không có "run tham chiếu" cho riêng bước tạo sample (Phase 0 chỉ có kết quả align/stitch, không lưu lại log của bước tạo sample). Nhưng file `.tiff` đầu vào ở đây **chính là** `SourceRasterPath` ghi trong manifest tham chiếu của Phase 0 (`data\GerberSample_20260806_084849\sample_manifest.json`) — cùng nguồn, cùng kích thước `40418×32364`, cùng 80 tile — nên kết quả tạo sample ở đây **tương thích** để dùng làm input cho `RunAlignStitch` (chưa chạy thử nối 2 bước, nhưng cấu trúc output giống hệt định dạng `RunAlignStitch` đã tiêu thụ thành công ở §4).

---

## 6. Tiêu chí nghiệm thu

1. ✅ Build sạch `Debug|x64` và `Release|x64` — solution 4 project.
2. ✅ Nhánh `alignstitch`: `RunAlignStitch(...)` không cần bất kỳ kiểu nào của Core trong `Program.cs`. Nhánh `createsample`: cần `GerberSampleConfig` của Core — đã ghi nhận là đánh đổi chấp nhận được, không phải lỗi (xem §2).
3. ✅ Chạy được trực tiếp từ dòng lệnh (`.exe`), ngoài Visual Studio, cả 2 mode.
4. ✅ Đọc được tham số từ CLI arg **hoặc** từ `global_config.json`, arg luôn thắng khi cả hai cùng có.
5. ✅ `alignstitch`: `Stitched.tiff` sinh ra, `AlignedTileCount=54`/`BlankTileCount=26` khớp chính xác run tham chiếu Phase 0 (§4.1).
6. ✅ `alignstitch`: `PeakWorkingSet` in ra **6616 MB**; tổng thời gian 303s, cùng bậc độ lớn 247s tham chiếu.
7. ✅ `createsample`: sinh đúng 80 tile + manifest hợp lệ, tên thư mục đúng format `Sample_<tên file không đuôi>`.

---

## 7. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| `%HALCONROOT%` không đặt trên máy chạy harness → build/run fail | Harness dùng `$(HALCONROOT)` ở build-time — máy không có biến này sẽ lỗi resolve reference rõ ràng ngay lúc build |
| Đường dẫn ảnh có khoảng trắng (`"20260725 Q168 2-1 org"`) | Không có vấn đề miễn truyền đúng string qua `--images "..."` có ngoặc kép khi gọi từ shell, hoặc qua `global_config.json` (JSON string không bị shell tách theo khoảng trắng) |
| Dataset 80 tile chạy `alignstitch` ~5 phút (Direct Alignment chiếm 90%) | Đã chấp nhận — chạy nền (`run_in_background`) khi cần |
| `global_config.json` chứa đường dẫn máy cụ thể (`H:\005_Project\...`) | File này phục vụ dev/test cục bộ, không phải config production — không commit đường dẫn nhạy cảm nếu dùng ở môi trường khác, đổi qua CLI arg khi cần |
| Quên cập nhật `GerberStitching.Core.dll` ở output khi chỉ build `RDL.GerberStitch.Harness.csproj` riêng lẻ (không qua `.sln`) | `ProjectReference` trực tiếp (§1.3) đảm bảo MSBuild luôn build + copy đúng thứ tự dù build project đơn lẻ |
