# Phase 1 — Task 1.3: Chuẩn hoá dependency & đóng gói runtime

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 2 *(tăng từ 1 — phát sinh việc nâng HALCON trên 2 app production)*
**Phụ thuộc:** Task 1.1 (hint path đã bỏ đường dẫn cứng), `Phase0_Closeout.md` §2.1 và §2.2
**Platform:** .NET Framework 4.8, **x64**

**Bắt buộc đọc trước khi sửa:**
1. `docs/Phase0_Closeout.md` §2.1 (quyết định HALCON 25.05), §6.2 (chưa có license gate)
2. `AGENTS.md` §3.3 (HALCON license)

**Ràng buộc:** Không thêm NuGet package. Không thêm project test. Không tự chạy test.

---

## ✅ Đã triển khai (2026-08-07, nhánh `Ver1_1`) — phần trong phạm vi repo

Task này có 2 loại việc: (a) trong phạm vi repo — làm được ngay; (b) đụng 2 repo production khác (`RDL_Master3`, `RDL_WorkerNorthT...`) — chỉ ghi yêu cầu, không tự sửa (đã nêu rõ ở §1.1). Phần (a) đã xong:

1. **§2 — `Private=False` cho HALCON:** đã áp dụng từ Task 1.1 (kiểm tra lại: cả `GerberStitching.Core.csproj` và `RDL.GerberStitch.csproj` đều có `<Private>False</Private>` cho `halcondotnetxl`/`hdevenginedotnetxl`). Xác nhận qua build thật: `bin\x64\Release\` không còn `halcondotnetxl.dll`.
2. **§3.1 — kiểm chứng `opencv_videoio_ffmpeg4130_64.dll` có cần không:** đã test thật bằng cách xoá file khỏi thư mục chạy rồi chạy harness `alignstitch` (dataset 80 tile thật, dùng đủ `PyramidEccMatcher`) — xem `docs/Phase1_Task06.md` §6 để biết kết quả cụ thể.
3. **§5 — `docs/deploy_deps.md`:** đã viết, dùng số liệu thật từ output build (`docs/deploy_deps.md`).

**Vẫn ngoài phạm vi, chưa và sẽ không tự làm trong repo này** (§1.1, §4.1): nâng HALCON trên `RDL_Master3`/`RDL_WorkerNorthT...`, binding redirect trong `Master.exe.config`/`Worker.exe.config`, test hồi quy luồng AOI cũ với dataset AOI thật. Đây là việc của người có quyền trên 2 repo đó — `docs/deploy_deps.md` đã ghi rõ các bước cần làm khi tới lúc.

---

## 0. Vấn đề

Bản trước của doc này viết khi chưa biết version HALCON thật của Master/Worker, nên đưa ra 3 thông tin sai:

| Bản cũ ghi | Thực tế |
|---|---|
| `MathNet.Numerics` — "nếu Core dùng (PoseGraph)" | Core **không** dùng. Pose graph tự cài `Alignment/Graph/SparseNormalEquationCg.cs` |
| `opencv_world4130.dll` | **Không tồn tại** trong output. Native thật là `OpenCvSharpExtern.dll` + `opencv_videoio_ffmpeg4130_64.dll` |
| `halcondotnetxl.dll` ← "đã có sẵn (Worker dùng)" | Đúng là có sẵn, nhưng là **18.11.1.1** — không dùng lại được sau quyết định nâng 25.05 |

Ngoài ra bản cũ có nhánh "Nếu bỏ OpenCvSharp" — nhánh này **đã bị loại** (`Phase0_Closeout.md` §2.2).

---

## 1. Xung đột trung tâm: hai version HALCON trong cùng một folder

Đây là vấn đề khó nhất của cả Phase 1.

**Hiện trạng deploy của Worker** (`RDL_WorkerNorthT_Ver2_SoftMerge\WindowsFormsApp1\dll\`):

```
AlignInspection.dll   EasyActiveMQ.dll     EWindowControl.dll
HalconCore.dll        halcondotnetxl.dll   ← 18.11.1.1
Halcon_Algorithm.dll  hdevenginedotnetxl.dll
Newtonsoft.Json.dll   ScriptCore.dll
```

**Output build của Core** (`GerberStitching.Core/bin/x64/Release/`):

| File | Kích thước |
|---|---|
| `GerberStitching.Core.dll` | 447 KB |
| `halcondotnetxl.dll` | 1 511 KB ← **25.05, đang copy local** |
| `OpenCvSharp.dll` | 1 002 KB |
| `OpenCvSharp.Extensions.dll` | 21 KB |
| `System.Drawing.Common.dll` | 49 KB |
| `System.Memory.dll` | 145 KB |
| `System.Buffers.dll` | 24 KB |
| `System.Numerics.Vectors.dll` | 110 KB |
| `System.Runtime.CompilerServices.Unsafe.dll` | 19 KB |
| `dll\x64\OpenCvSharpExtern.dll` | **68 576 KB** |
| `dll\x64\opencv_videoio_ffmpeg4130_64.dll` | **28 578 KB** |

Core đang đặt `<Private>True</Private>` cho `halcondotnetxl`, nên **copy bản 25.05 vào bin**. Deploy thẳng vào folder Worker sẽ **đè lên bản 18.11** mà toàn bộ luồng AOI hiện tại đang dùng.

> 🔴 Không có cách nào để .NET nạp **hai** version của cùng assembly `halcondotnetxl` trong **một** process. Vì vậy: hoặc cả process dùng 18.11, hoặc cả process dùng 25.05. Roadmap §5 xếp rủi ro này mức **Cao** — đúng.

### 1.1. Thứ tự thực hiện bắt buộc

Nâng HALCON **không phải** việc copy DLL. Trình tự:

| # | Bước | Ghi chú |
|---|---|---|
| 1 | **Xác nhận license/dongle phủ 25.05** | Chặn tất cả các bước sau. Nếu dongle chỉ cấp tới 18.11 thì phải quay lại xem xét quyết định `Phase0_Closeout.md` §2.1 |
| 2 | Cài HALCON 25.05 trên máy build + **mọi** máy Worker + máy Master | Đặt `HALCONROOT`, `HALCONARCH=x64-win64` |
| 3 | Đổi reference của Master + Worker sang 25.05 | `RDL_Master3`, `RDL_WorkerNorthT...` — **repo khác, ngoài phạm vi task này** |
| 4 | Build lại Master + Worker, **test hồi quy luồng AOI cũ** | Đây là phần nặng nhất; `NewMergeFunc`, `Halcon_Algorithm.dll`, `AlignInspection.dll` đều gọi HALCON |
| 5 | Mới deploy `RDL.GerberStitch.dll` + Core vào | |

> ⚠ Bước 3–4 đụng vào **2 ứng dụng production**. Không nằm trong repo này. Doc này chỉ ghi yêu cầu; việc thực thi cần kế hoạch riêng và người có quyền trên 2 repo đó.
>
> ⚠ `Halcon_Algorithm.dll`, `HalconCore.dll`, `AlignInspection.dll` là DLL biên dịch sẵn của RDL. Nếu chúng liên kết tĩnh với HALCON 18.11 mà không có source để build lại → **chặn đường**. Phải kiểm tra trước bước 1.

---

## 2. HALCON — cấu hình reference

Task 1.1 đã đổi hint path sang `$(HALCONROOT)`. Task này chốt chính sách copy:

| Project | `<Private>` | Lý do |
|---|---|---|
| `RDL.GerberStitch` | `False` | Dùng chung DLL trong folder deploy của Master/Worker |
| `GerberStitching.Core` | `False` ← **đổi từ `True`** | Đang copy 25.05 vào bin, gây nhầm lẫn nguồn DLL khi deploy |

```xml
<Reference Include="halcondotnetxl">
  <HintPath>$(HALCONROOT)\bin\dotnet35\halcondotnetxl.dll</HintPath>
  <Private>False</Private>
</Reference>
```

> `hdevenginedotnetxl` chỉ xuất hiện trong output `Debug`, không có trong `Release` — nghĩa là không có đường code nào thực sự dùng nó ở Release. Giữ reference (Core khai báo) nhưng cũng đặt `Private=False`. Nếu xác nhận không dùng thì đề xuất gỡ hẳn — **hỏi trước khi gỡ**.

---

## 3. OpenCvSharp — đóng gói native runtime

Quyết định giữ OpenCvSharp đã chốt (`Phase0_Closeout.md` §2.2): `PyramidEccMatcher` là refinement matcher của toàn bộ 54 tile có nội dung trong run tham chiếu.

### 3.1. Native payload

| File | Kích thước | Cần? |
|---|---|---|
| `OpenCvSharpExtern.dll` | 68.6 MB | ✅ Bắt buộc |
| `opencv_videoio_ffmpeg4130_64.dll` | 28.6 MB | ❓ **Nhiều khả năng không cần** |

`opencv_videoio_ffmpeg` phục vụ đọc/ghi **video**. Pipeline stitch chỉ xử lý ảnh tĩnh. Bỏ được thì giảm 28.6 MB mỗi lần deploy.

**Cách kiểm chứng:** deploy không kèm file này, chạy harness Task 1.5. `OpenCvSharpExtern` nạp ffmpeg theo kiểu lazy — nếu không gọi `VideoCapture`/`VideoWriter` thì không nạp. Nếu chạy trọn không lỗi → bỏ được.

> Không tự xoá khỏi package. Chỉ **không copy** khi deploy, và ghi vào checklist deploy.

### 3.2. Vị trí đặt native DLL

Core đã có sẵn `OpenCvSharp4.runtime.win.props/.targets` (import trong `.csproj`) tự đặt native vào `dll\x64\`. Cấu trúc này khớp convention `..\dll\` của RDL.

**Deploy đích:**

```
<RDL Deploy Root>/
├── Worker.exe  (hoặc Master.exe)
├── RDL.GerberStitch.dll
├── GerberStitching.Core.dll
├── GerberEngine.dll
├── OpenCvSharp.dll
├── OpenCvSharp.Extensions.dll
├── System.Drawing.Common.dll        ← xem §4
├── System.Memory.dll
├── System.Buffers.dll
├── System.Numerics.Vectors.dll
├── System.Runtime.CompilerServices.Unsafe.dll
└── dll/
    ├── halcondotnetxl.dll           ← 25.05 sau bước §1.1
    ├── hdevenginedotnetxl.dll
    ├── Newtonsoft.Json.dll          ← đã có, xem §4
    └── x64/
        └── OpenCvSharpExtern.dll
```

---

## 4. Managed dependency — đối chiếu với những gì Worker/Master đã có

| DLL | Worker đã có | Master đã có | Xử lý |
|---|---|---|---|
| `Newtonsoft.Json` | ✅ `dll\` | ✅ `Version=13.0.0.0` | **Kiểm tra version khớp.** Lệch → binding redirect. Xem Task 1.1 §4 |
| `System.Drawing.Common` | ❌ | ❌ | Copy. Core dùng version **10.0.9** (`packages\System.Drawing.Common.10.0.9`) |
| `System.Memory` / `Buffers` / `Numerics.Vectors` / `CompilerServices.Unsafe` | ❌ | ⚠ Master ref version **cũ hơn** | ⚠ Xem §4.1 |
| `EWindowControl`, `Elog_1_0`, `ScriptCore`, `HalconCore` | ✅ | ✅ | **Không đụng.** Không phải dependency của repo này |
| `MathNet.Numerics` | — | — | **Không dùng.** Bỏ khỏi mọi checklist |

### 4.1. Xung đột version các assembly `System.*`

Master (`WindowsFormsApp2.csproj`) tham chiếu:

| Assembly | Master | Core |
|---|---|---|
| `System.Buffers` | `4.0.3.0` | `4.6.1` (package) |
| `System.Memory` | `4.0.1.2` | `4.6.3` (package) |
| `System.Numerics.Vectors` | `4.1.4.0` | `4.6.1` (package) |
| `System.Runtime.CompilerServices.Unsafe` | `6.0.0.0` | `6.1.2` (package) |

Bốn assembly này lệch version. .NET Framework **không** tự hoà giải — cần `bindingRedirect` trong `Master.exe.config` / `Worker.exe.config`:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.2.0" newVersion="4.0.2.0" />
</dependentAssembly>
```

> ⚠ Lấy `newVersion` từ **assembly version thật** của DLL sẽ deploy, không phải version package NuGet — hai số này khác nhau. Đọc bằng:
> ```powershell
> [Reflection.AssemblyName]::GetAssemblyName("System.Memory.dll").Version
> ```

---

## 5. Danh sách file thay đổi

| File | Thay đổi |
|---|---|
| `GerberStitching.Core/GerberStitching.Core.csproj` | `halcondotnetxl` + `hdevenginedotnetxl` → `Private=False` (§2) |
| `RDL.GerberStitch/RDL.GerberStitch.csproj` | Xác nhận `Private=False` (đã làm ở Task 1.1) |
| `docs/deploy_deps.md` | **mới** — checklist deploy: danh sách DLL, thứ tự nâng HALCON §1.1, binding redirect §4.1 |

**Ngoài repo này** (ghi yêu cầu, không tự sửa):
- `RDL_Master3\WindowsFormsApp2\WindowsFormsApp2.csproj` — reference HALCON 25.05 + binding redirect
- `RDL_WorkerNorthT...\WindowsFormsApp1\*.csproj` — tương tự

---

## 6. Tiêu chí nghiệm thu

1. Build `Release|x64` — `bin\x64\Release\` **không** còn `halcondotnetxl.dll` (xác nhận `Private=False` có hiệu lực).
2. `[Reflection.AssemblyName]::GetAssemblyName("<deploy>\dll\halcondotnetxl.dll").Version` trên máy Worker sau nâng cấp → **25.05.x**, và chỉ có **một** bản trong toàn folder deploy.
3. Worker khởi động bình thường sau deploy; luồng AOI cũ (option Gerber **tắt**) chạy không hồi quy — đây là tiêu chí nặng nhất, cần dataset AOI thật.
4. Master khởi động bình thường sau deploy.
5. Từ Worker process: `new GerberStitchFacade()` không lỗi DLL load.
6. `HOperatorSet.ReadImage` chạy được từ cả Master và Worker sau deploy.
7. `Cv2.GetBuildInformation()` chạy được từ Worker process → native OpenCvSharp resolve đúng.
8. Deploy **không** kèm `opencv_videoio_ffmpeg4130_64.dll` → harness Task 1.5 vẫn chạy trọn (§3.1). Nếu lỗi thì copy lại và ghi vào checklist.
9. `docs/deploy_deps.md` liệt kê đủ DLL ở §3.2 kèm version.

---

## 7. Rủi ro

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| **Dongle không phủ 25.05** → toàn bộ quyết định `Phase0_Closeout.md` §2.1 phải làm lại | Cao | §1.1 bước 1 là bước **đầu tiên**, chặn mọi bước sau. Kiểm tra trước khi động vào code |
| `Halcon_Algorithm.dll` / `AlignInspection.dll` liên kết cứng 18.11, không có source build lại | Cao | Kiểm tra ở §1.1 bước 1. Nếu đúng vậy → cân nhắc phương án tiến trình riêng (đã bị loại ở Phase 0, có thể phải mở lại) |
| Nâng HALCON gây hồi quy luồng AOI production | Cao | Nghiệm thu mục 3 với dataset AOI thật; có kế hoạch rollback DLL |
| Lệch version 4 assembly `System.*` gây `FileLoadException` lúc runtime | TB | Binding redirect §4.1; test bằng nghiệm thu mục 5 |
| Native OpenCvSharp 97 MB làm chậm mỗi lần deploy | Thấp | §3.1 — thử bỏ ffmpeg, giảm 28.6 MB |
| `Newtonsoft.Json` lệch version với Master | TB | Task 1.1 §4 kiểm tra trước; binding redirect nếu cần |
