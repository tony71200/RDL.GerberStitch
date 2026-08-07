# Phase 1 — Task 1.1: Hoàn thiện project wrapper `RDL.GerberStitch`

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 0.5 *(giảm từ 1.5 — project đã được tạo, chỉ còn sửa cấu hình)*
**Phụ thuộc:** `Phase0_Closeout.md` (quyết định 0.2 HALCON 25.05, quyết định 0.5 giữ OpenCvSharp)
**Platform:** C# 7.3, .NET Framework 4.8, Class Library, **x64**

**Bắt buộc đọc trước khi sửa:**
1. `AGENTS.md` §3.1 (ranh giới façade), §4 (build x64, không tự chạy test)
2. `docs/Phase0_Closeout.md` §2 (hai quyết định Phase 0)

**Ràng buộc:** Không thêm NuGet package. Không thêm project test. Không tự chạy test.

**Comment thay đổi lớn:**
```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: ...]
```

---

## 0. Vấn đề

Bản trước của doc này mô tả việc **tạo mới** project. Việc đó **đã xong** — `RDL.GerberStitch.csproj` tồn tại, nằm trong `RDL.GerberStitch.sln` với GUID `{B9562199-720A-4CCA-A4D3-797412E117E8}`, đã có `ProjectReference` tới cả `GerberEngine` và `GerberStitching.Core`.

Nhưng exit gate cũ đánh dấu `[x]` "build thành công" **không đúng thực tế**. Còn 5 vấn đề mở, trong đó 2 vấn đề khiến façade không dùng được từ bên ngoài.

| # | Vấn đề | Bằng chứng |
|---|---|---|
| 1 | Chưa từng build ra output | `RDL.GerberStitch/bin/x64/Debug/` **rỗng** (so với `GerberEngine/bin/x64/Debug/GerberEngine.dll` đã có) |
| 2 | Façade khai báo `internal` | `Facade/GerberStitchFacade.cs:9` — `internal class GerberStitchFacade` |
| 3 | DTO façade trùng tên type public của Core | `Facade/AlignStitchConfig.cs:9` — `internal class AlignStitchConfig`, trùng `GerberViewer.Stitching.Models.AlignStitchConfig` (`Models/WorkflowModels.cs:72`) |
| 4 | Hint path HALCON là đường dẫn máy cá nhân | `RDL.GerberStitch.csproj` + `GerberStitching.Core.csproj` trỏ `C:\Users\USER\AppData\Local\Programs\MVTec\HALCON-25.05-Progress\bin\dotnet35\` |
| 5 | `Newtonsoft.Json` trỏ ra ngoài repo | `..\..\Lib_Supporter\dll\Newtonsoft.Json.dll` — không kiểm soát version |

Vấn đề 2 và 3 là chặn đường: `internal` thì Master/Worker không gọi được; trùng tên thì mọi call site phải dùng alias.

---

## 1. Sửa access modifier — façade phải `public`

**File:** `RDL.GerberStitch/Facade/GerberStitchFacade.cs`

```csharp
// TRƯỚC
internal class GerberStitchFacade

// SAU
// [Claude] [Change time: 2026-08-07] [Purpose: Façade là API duy nhất Master/Worker gọi — phải public, không internal.]
public sealed class GerberStitchFacade
```

`sealed` vì không có kịch bản kế thừa façade; giữ bề mặt API nhỏ nhất.

> Thân class để trống ở task này. Nội dung API viết ở **Task 1.2**.

---

## 2. Đổi tên DTO tránh xung đột với Core

`RDL.GerberStitch` reference `GerberStitching.Core`, nên type `RDL.GerberStitch.Facade.AlignStitchConfig` sẽ **cùng tồn tại** với `GerberViewer.Stitching.Models.AlignStitchConfig`. Bên trong façade — nơi phải chạm cả hai — mọi dòng sẽ cần alias.

**Quy ước đặt tên:** DTO façade mang tiền tố `Rdl`.

| File hiện tại | Đổi thành | Type |
|---|---|---|
| `Facade/AlignStitchConfig.cs` | `Facade/RdlAlignStitchOptions.cs` | `public sealed class RdlAlignStitchOptions` |

Các DTO còn lại (`RdlCapturedTile`, `RdlStitchResult`, `RdlGridConfig`) tạo ở **Task 1.2**, cùng quy ước.

> **Lý do không dùng thẳng type của Core:** mục tiêu Phase 1 là *"ẩn chi tiết pipeline sau vài hàm façade"* (roadmap task 1.1). Nếu Master/Worker phải khai báo `GerberViewer.Stitching.Models.AlignStitchConfig` thì façade không ẩn được gì. Xem thêm Task 1.4 về cách map `RdlAlignStitchOptions` → `AlignStitchConfig` của Core.

---

## 3. Hint path HALCON — bỏ đường dẫn máy cá nhân

**Vấn đề:** cả hai csproj trỏ vào `C:\Users\USER\AppData\Local\...`. Trên máy build RDL đường dẫn này không tồn tại → lỗi resolve reference.

**Sửa trong `RDL.GerberStitch.csproj`** (áp dụng cho cả `halcondotnetxl` và `hdevenginedotnetxl`):

```xml
<Reference Include="halcondotnetxl">
  <HintPath>$(HALCONROOT)\bin\dotnet35\halcondotnetxl.dll</HintPath>
  <Private>False</Private>
</Reference>
```

- `$(HALCONROOT)` — biến môi trường HALCON đặt sẵn khi cài. Tránh hard-code đường dẫn người dùng.
- `<Private>False</Private>` — **không** copy local. Master/Worker đã có `halcondotnetxl.dll` trong folder deploy của họ; copy thêm bản khác version sẽ gây xung đột load assembly.

> ⚠ **Chưa đổi version ở task này.** Việc nâng 18.11 → 25.05 trên Master/Worker thuộc **Task 1.3**. Task này chỉ bỏ đường dẫn cứng.

> `GerberStitching.Core.csproj` cũng có cùng vấn đề. Sửa luôn cho đồng bộ — đây là sửa cấu hình build, không đụng logic pipeline nên không vi phạm `AGENTS.md` §3.1.

---

## 4. `Newtonsoft.Json` — chốt nguồn

Hiện trỏ `..\..\Lib_Supporter\dll\Newtonsoft.Json.dll` (ngoài repo). Master dùng `Newtonsoft.Json, Version=13.0.0.0` (`WindowsFormsApp2.csproj:91`).

**Việc cần làm:** xác nhận DLL ở `Lib_Supporter` đúng version 13.0.0.0 để khớp Master.

- Nếu **khớp** → giữ nguyên hint path, ghi chú lại trong doc deploy (Task 1.3).
- Nếu **lệch** → báo lại trước khi tự đổi. Không tự thêm NuGet package (ràng buộc đầu doc).

> Kiểm tra bằng:
> ```powershell
> (Get-Item "..\..\Lib_Supporter\dll\Newtonsoft.Json.dll").VersionInfo.FileVersion
> ```

---

## 5. `LangVersion` — thiếu ở cấu hình AnyCPU

`RDL.GerberStitch.csproj` đặt `<LangVersion>7.3</LangVersion>` cho `Debug|x64` và `Release|x64`, nhưng **không** đặt cho `Debug|AnyCPU` / `Release|AnyCPU`.

Theo `AGENTS.md` §1 project chỉ build x64, nên đây không phải lỗi chặn. Nhưng nếu ai lỡ build AnyCPU sẽ nhận thông báo lỗi C# khó hiểu thay vì lỗi platform rõ ràng.

**Đề xuất:** nâng `<LangVersion>7.3</LangVersion>` lên `PropertyGroup` chung đầu file để áp cho mọi cấu hình. Sửa 1 dòng, bỏ được 2 dòng trùng.

---

## 6. Thư mục `Internal/`

`.csproj` khai báo `<Folder Include="Internal\" />` nhưng thư mục rỗng.

Giữ nguyên — đây là chỗ dành cho adapter nội bộ (mapper Core↔façade) sẽ viết ở Task 1.2 và 1.4. Không xoá.

---

## 7. Danh sách file thay đổi

| File | Thay đổi |
|---|---|
| `RDL.GerberStitch/Facade/GerberStitchFacade.cs` | `internal` → `public sealed` (§1) |
| `RDL.GerberStitch/Facade/AlignStitchConfig.cs` | Đổi tên file + type → `RdlAlignStitchOptions`, `internal` → `public sealed` (§2) |
| `RDL.GerberStitch/RDL.GerberStitch.csproj` | Hint path HALCON → `$(HALCONROOT)` + `Private=False` (§3); gộp `LangVersion` (§5); cập nhật `<Compile Include>` theo tên file mới |
| `GerberStitching.Core/GerberStitching.Core.csproj` | Hint path HALCON → `$(HALCONROOT)` (§3) |

**Không đụng:** file `.cs` nào trong `GerberStitching.Core` hay `GerberEngine`.

---

## 8. Tiêu chí nghiệm thu

1. Build sạch cả 2 cấu hình:
   ```bash
   msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
   ```
   ```bash
   msbuild RDL.GerberStitch.sln /p:Configuration=Release /p:Platform=x64
   ```
2. `RDL.GerberStitch/bin/x64/Debug/RDL.GerberStitch.dll` **tồn tại** (hiện đang rỗng — đây là điểm thay đổi chính so với trước).
3. `bin/x64/Debug/` **không** chứa `halcondotnetxl.dll` (do `Private=False`) — xác nhận không copy local.
4. Build trên máy **không** có đường dẫn `C:\Users\USER\...` vẫn resolve được HALCON qua `$(HALCONROOT)`.
5. Từ một console project trống: `new RDL.GerberStitch.Facade.GerberStitchFacade();` **compile được** — chứng minh façade đã `public`.
6. Trong file façade, viết được `var c = new RdlAlignStitchOptions();` **không cần** alias, đồng thời `using GerberViewer.Stitching.Models;` không gây lỗi CS0104 (ambiguous reference).
7. `git status` — không file `.cs` nào của Core/GerberEngine bị sửa.

---

## 9. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| `$(HALCONROOT)` không được đặt trên máy build → build fail với lỗi khó đoán | Thêm `<Error Condition="'$(HALCONROOT)'==''" Text="HALCONROOT chưa được đặt..." />` vào target `BeforeBuild` để báo lỗi rõ ràng |
| `Private=False` làm runtime không tìm thấy HALCON khi chạy harness Task 1.5 | Harness tự copy hoặc trỏ probing path — xử lý ở Task 1.3 (đóng gói runtime), không sửa ngược lại thành `Private=True` |
| Đổi tên `AlignStitchConfig` → `RdlAlignStitchOptions` làm hỏng code đang tham chiếu | Cả 2 type hiện là stub rỗng và `internal`, không có call site nào. Rủi ro thực tế bằng 0 |
| Version `Newtonsoft.Json` lệch Master → xung đột lúc deploy | §4 yêu cầu kiểm tra trước; nếu lệch thì dừng hỏi, không tự đổi |
