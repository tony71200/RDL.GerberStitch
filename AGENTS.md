# AGENTS.md — RDL.GerberStitch (Gerber Align & Stitch façade library · .NET Framework 4.8)

> Chuẩn mở: https://agents.md — áp dụng cho mọi agent (Claude Code, GitHub Copilot, Codex, Cursor...).

## 1. Tổng quan project

- **Loại ứng dụng:** 3 project **Class Library** (.NET Framework 4.8) — **không có WinForms UI** trong solution này (không có file `.Designer.cs`, không có `Form`).
- **Mục đích:** đóng gói pipeline **align + stitch ảnh Gerber** (port từ repo `tony71200/GerberViewer`, nhánh `2026-08-04_Ver4_implement_claude`) thành một thư viện façade (`RDL.GerberStitch.dll`) để hệ thống **RDL Master/Worker AOI** (nằm ở solution khác) reference và gọi. Đây là phần **Phase 0/1** của roadmap tích hợp — xem [`docs/roadmap_GerberAlignStitch_integration_EN.md`](docs/roadmap_GerberAlignStitch_integration_EN.md) để hiểu toàn cảnh (Master sinh manifest lúc Prepare → Worker chạy Align→PoseGraph→Stitch cho cả batch → trả về path ảnh đã stitch).
- **Ngôn ngữ:** C# 7.3
- **Platform build:** **x64** mặc định — không build AnyCPU/x86 trừ khi được yêu cầu rõ ràng khác.

## 2. Cấu trúc solution

| Project | RootNamespace | Vai trò |
|---|---|---|
| `GerberEngine` | `GerberEngine` | Parse/render file Gerber thuần (`GerberParser`, `GerberRenderer`, `ApertureMacroProcessor`, `CoordinateTransformer`, `GerberModels`). **Không** phụ thuộc HALCON/OpenCV. |
| `GerberStitching.Core` | `GerberViewer.Stitching` *(khác tên project — namespace giữ nguyên từ repo gốc GerberViewer)* | Toàn bộ pipeline Align→PoseGraph→Stitch: `Alignment/` (matcher direct + recovery + pose graph optimizer), `Matching/Halcon/` + `Matching/OpenCv/` (HalconNcc, HalconShapeModel, PyramidPhaseCorrelation, PyramidEcc), `Stitching/` (HalconProjectiveMosaicRebasedEngine, HalconWarpThenTileOffsetEngine, GlobalTransformStitcher), `Configuration/`, `Imaging/` (gồm `SampleTileGenerator` — sinh sample tile cho Master), `Comparison/`, `RobotManager/`, `Diagnostics/` (bọc `#if DEBUG`). Phụ thuộc **HALCON 25.05 Progress** (`halcondotnetxl`, `hdevenginedotnetxl`, hint path cứng tới `C:\Users\USER\AppData\Local\Programs\MVTec\HALCON-25.05-Progress\bin\dotnet35\`) và **OpenCvSharp4 4.13** (NuGet, qua `packages/`). |
| `RDL.GerberStitch` | `RDL.GerberStitch` | **Façade wrapper duy nhất** — Master/Worker chỉ được reference DLL này, không reference thẳng `GerberStitching.Core`/`GerberEngine`. Chứa `Facade/GerberStitchFacade.cs` + `Facade/AlignStitchConfig.cs`. **Hiện tại còn là stub rỗng** — xem [`docs/Phase1_Task01.md`](docs/Phase1_Task01.md)–[`Task04.md`](docs/Phase1_Task04.md) cho kế hoạch triển khai 2 hàm façade chính: `GenerateSampleManifest` (Master gọi lúc Prepare) và `RunAlignStitch` (Worker gọi khi nhận lô, bọc `AlignStitchWorkflowService.RunAsync`). |

`docs/` chứa roadmap + task spec chi tiết bằng tiếng Việt/Anh — **đọc trước khi triển khai Phase 1 task tương ứng**, vì mỗi task doc đã trace sẵn dòng code cụ thể cần sửa.

## 3. Quy tắc BẮT BUỘC — không được vi phạm

### 3.1. Ranh giới façade (thay cho quy tắc `.Designer.cs` — không áp dụng, repo này không có WinForms)
- Master/Worker (ngoài repo) chỉ được gọi qua `RDL.GerberStitch.Facade.*`. Không rò rỉ kiểu nội bộ của `GerberStitching.Core`/`GerberEngine` ra khỏi façade nếu tránh được.
- `GerberStitching.Core` là **code port từ repo GerberViewer gốc** (không phải viết mới trong RDL) — khi sửa logic trong đó (Alignment/Matching/Stitching), ưu tiên giữ đúng hành vi đã có; nếu cần đổi hành vi pipeline, xác nhận với user trước vì có thể ảnh hưởng tới kết quả đã kiểm chứng ở repo nguồn.
- Không có `.Designer.cs` trong repo này. Nếu về sau có project WinForms được thêm vào solution (vd. test harness), hỏi user trước khi quyết định quy tắc tách control/logic — hiện chưa cần.

### 3.2. Base/framework class (`EWindowControl`, `ELog_1_0`, ...) — chưa liên quan tới repo này
- **Không tìm thấy** các class này trong repo hiện tại (không có project WinForms/RDL framework nào được reference — `csproj` chỉ trỏ tới `..\..\Lib_Supporter\dll\Newtonsoft.Json.dll`).
- Các class này (nếu tồn tại) thuộc về **solution RDL Master/Worker** — nơi cuối cùng sẽ reference `RDL.GerberStitch.dll`. Điểm cần lưu ý cho tương lai:
  - Khi façade (`GerberStitchFacade`) được tích hợp vào Worker (Phase 3 trong roadmap) hoặc Master (Phase 2/4), nếu Worker/Master có sẵn base class kiểu `EWindowControl`/`ELog_1_0`, **không sửa trực tiếp** các class đó từ phía tích hợp này — theo đúng nguyên tắc chung của template RDL.
  - Việc này **chưa phát sinh trong repo hiện tại**. Nếu bạn thấy cần dùng/kế thừa một base class như vậy, dừng lại và hỏi user — đừng giả định vị trí file.

### 3.3. HALCON license — CHƯA có gate tập trung (open item)
- Repo hiện tại **chưa có** một bước kiểm tra license HALCON tập trung trước khi gọi HALCON. Cái đang có chỉ là xử lý lỗi khi license hết hạn (`HalconImageInteropException`, `LicenseExpired` flag trong `GerberStitching.Core/Imaging/ImageInterop/ImageInteropService.cs`) — đây là **catch sau khi lỗi xảy ra**, không phải pre-flight check.
- Việc chốt version HALCON (25.05, khớp với Worker) + xác minh license/dongle là rủi ro đã được ghi nhận trong roadmap (Phase 0.2, mục Rủi ro §5).
- **Khi thêm call site HALCON mới:** không tự chế ra một cơ chế license-check mới — gắn cờ (flag) rằng gate này chưa tồn tại và hỏi user muốn xử lý thế nào (tái dùng cơ chế của Master/Worker khi tích hợp, hay thêm try/catch quanh `HalconImageInteropException` theo mẫu đã có ở `ImageInteropService`).

## 4. Build & Test

- Build mặc định target **x64**: `msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64` (đổi `Release` khi cần).
- Solution phụ thuộc HALCON 25.05 Progress cài local (`hint path` cứng, xem mục 2) và OpenCvSharp4 4.13 (NuGet, đã có sẵn trong `packages/`) — máy build cần cài HALCON đúng version, nếu không sẽ lỗi reference.
- Agent **không tự chạy test và không tự coi là đã pass** — việc thực thi/kiểm thử ứng dụng do user thực hiện thủ công.
- Vai trò của agent khi sinh/review code: tập trung phát hiện **lỗi flow và lỗi logic** (thứ tự gọi sai, null reference, quản lý `HObject`/tài nguyên HALCON không dispose, race condition trong `Task`/`async`...), không tự sinh unit test/project test trừ khi được yêu cầu rõ.

## 5. Coding convention

> Chưa chuẩn hóa (chưa có `.editorconfig`/StyleCop). Cho đến khi có quy ước riêng:
> - Bám theo convention **đã có sẵn trong file đang sửa** thay vì áp convention ngoài vào (lưu ý `GerberStitching.Core` giữ nguyên convention từ repo GerberViewer gốc).
> - File mới: PascalCase cho class/method/public property, camelCase cho biến local, `_camelCase` cho field private.
> - Code debug-only phải nằm trong `#if DEBUG` (xem `Configuration/...`, `Diagnostics/DebugHtmlReportWriter.cs`) để Release build không mang theo diagnostic type/behavior.

## 6. Khi không chắc

Nếu không rõ: cấu trúc solution RDL Master/Worker ở ngoài repo này, HALCON license-check nên tái dùng cơ chế nào, hoặc hành vi pipeline trong `GerberStitching.Core` có được phép đổi hay không — **hỏi lại trước khi sửa**, không đoán hay tự suy diễn.
