# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Toàn bộ quy tắc project nằm ở @AGENTS.md — đọc và tuân thủ file đó trước khi thao tác trên bất kỳ file nào trong solution.

## Build

- `msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64` (build toàn solution, target x64 — xem AGENTS.md §4). Đổi `Debug` → `Release` khi cần.
- Cần đặt biến môi trường **`HALCONROOT`** trỏ tới thư mục cài HALCON 25.05 Progress (các `.csproj` dùng `$(HALCONROOT)\bin\dotnet35\halcondotnetxl.dll` làm hint path) — nếu không, reference `halcondotnetxl`/`hdevenginedotnetxl` sẽ lỗi resolve. `RDL.GerberStitch`/`GerberStitching.Core` đặt `Private=False` (không copy local — dùng chung DLL Master/Worker đã có); riêng `RDL.GerberStitch.Harness` copy-local để tự chạy độc lập được.
- NuGet packages (OpenCvSharp4 4.13, System.Drawing.Common, System.Memory, ...) đã có sẵn trong `packages/` — không cần restore riêng trừ khi thêm package mới.
- Không có test project trong solution; không tự thêm hoặc tự chạy test (xem AGENTS.md §4) — kiểm thử là việc của user.
- Chạy thử façade với dữ liệu thật: `RDL.GerberStitch.Harness.exe` (xem `docs/Phase1_Task06.md`) — có 2 mode `--mode alignstitch` (mặc định) và `--mode createsample`, đọc tham số từ CLI arg hoặc `global_config.json` cạnh file `.exe`.

## Kiến trúc

Solution gồm 3 project, phụ thuộc một chiều: `RDL.GerberStitch` → (`GerberStitching.Core`, `GerberEngine`). Xem AGENTS.md §2 để biết vai trò từng project. Điểm cần nắm khi đọc code:

- **`GerberEngine`** — parser/renderer file Gerber thuần túy (`GerberParser`, `GerberRenderer`), không phụ thuộc HALCON/OpenCV.
- **`GerberStitching.Core`** (namespace `GerberViewer.Stitching`, port từ repo `tony71200/GerberViewer`) — pipeline align + stitch:
  - `Imaging/SampleTileGenerator` sinh sample tile + manifest (phía Master sẽ dùng lúc Prepare).
  - `Matching/Halcon/` + `Matching/OpenCv/` — các matcher cụ thể (HalconNcc, HalconShapeModel, PyramidPhaseCorrelation, PyramidEcc), lựa chọn qua `Matching/MatcherFactory` + `Matching/MatcherPipeline`.
  - `Alignment/AlignStitchWorkflowService.RunAsync` — entry point chính của pipeline: Direct Alignment → Failure Recovery → Neighbor Graph → Pose Graph Optimizer (`Alignment/Graph/GlobalPoseGraphOptimizer`) → Validate → `Stitching/WorkflowStitchingService.Stitch` (chọn `HalconProjectiveMosaicRebasedEngine` hoặc `HalconWarpThenTileOffsetEngine`) → ghi TIFF ra `Stitching/TiffBigWriter`.
  - `Configuration/` — mapping config (`AlignStitchConfig`, `GerberSampleConfig`) + `AppPaths`.
- **`RDL.GerberStitch`** — façade wrapper duy nhất mà hệ thống **RDL Master/Worker** (solution khác, ngoài repo này) sẽ reference. `Facade/GerberStitchFacade.cs` đã triển khai: `GenerateSampleManifest`/`GenerateSampleManifestFromRaster` (Master gọi lúc Prepare — render Gerber hoặc dùng raster có sẵn) và `RunAlignStitch` (Worker gọi khi nhận lô, bọc `AlignStitchWorkflowService.RunAsync`). Chi tiết thiết kế + các đánh đổi đã chốt nằm ở `docs/Phase1_Task02.md` mục "Đã triển khai".
- **`RDL.GerberStitch.Harness`** — console app test façade với dữ liệu thật, không phải project test theo nghĩa NUnit/xUnit. Xem `docs/Phase1_Task06.md`.

Bối cảnh tích hợp đầy đủ (vì sao có façade này, Master/Worker sẽ gọi và trao đổi dữ liệu ra sao) nằm ở `docs/roadmap_GerberAlignStitch_integration_EN.md` — đọc trước khi làm bất kỳ Phase 1 task nào trong `docs/`.

## Ghi chú riêng cho Claude Code

- **Không có WinForms trong repo này** — quy tắc `.Designer.cs` vs code-behind của template gốc không áp dụng ở đây; ranh giới cần giữ là **façade boundary** (Master/Worker chỉ gọi qua `RDL.GerberStitch.Facade.*`) — xem AGENTS.md §3.1.
- **Base/framework class** (`EWindowControl`, `ELog_1_0`...): không tồn tại trong repo này. Chỉ có thể liên quan khi façade được tích hợp vào solution RDL Master/Worker sau này — xem AGENTS.md §3.2 trước khi giả định vị trí file, đừng tự suy diễn.
- **HALCON license:** repo **chưa có** gate kiểm tra license tập trung — chỉ có xử lý lỗi *sau khi* license hết hạn (`GerberStitching.Core/Imaging/ImageInterop/ImageInteropService.cs`). Coi đây là open item đã biết (roadmap Phase 0.2); khi thêm call site HALCON mới, không tự chế cơ chế license-check mới — hỏi user (xem AGENTS.md §3.3).
- **Test:** không tự thêm project test hoặc tự chạy test trừ khi được yêu cầu rõ. Vai trò khi review là bắt lỗi flow/logic (thứ tự gọi sai, dispose `HObject`/tài nguyên HALCON, race condition trong `Task`/`async`), không phải xác nhận "đã test pass".
- **Build:** giả định target **x64** khi đề xuất lệnh build/cấu hình.
- **Ghi log triển khai — BẮT BUỘC:** mỗi khi thực thi một task Phase (sửa `.cs`/`.csproj` thật, không phải chỉ viết doc kế hoạch), phải cập nhật `docs/implement_code.html` — thêm 1 entry mới (không ghi đè entry cũ) gồm: ngày, task/phạm vi, danh sách file đã đổi, các điểm cần lưu ý khi đọc lại code (đánh đổi thiết kế, lý do chọn cách làm A thay vì B), và mọi lỗi gặp phải trong lúc build/chạy kèm cách fix (kể cả lỗi tưởng nhỏ như CS0104/CS0012 — đây chính là loại thông tin dễ mất nhất giữa các phiên làm việc). Không cần hỏi trước khi thêm entry; chỉ hỏi nếu cần **sửa** một entry cũ đã sai.
