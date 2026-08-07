# Design — Đóng Phase 0 & viết lại bộ task doc Phase 1

**Ngày:** 2026-08-07
**Repository:** `RDL.GerberStitch` (branch `Ver01`)
**Phạm vi:** chỉ `docs/` — không sửa code C# nào trong task này
**Nguồn đối chiếu:**
- `docs/roadmap_GerberAlignStitch_integration.md` (bản VI)
- Kết quả spike: `H:\005_Project\AOI_2026_07_imp\result_20260807\` (6 run)
- Master: `H:\005_Project\AOI_202607_Base\origin\RDL_Master3`
- Worker: `H:\005_Project\AOI_202607_Base\origin\RDL_WorkerNorthT_Ver2_SoftMerge\WindowsFormsApp1`

---

## 1. Vấn đề

Roadmap coi Phase 0 gồm 5 task (0.1–0.5) với exit gate là "stitch được 1 dataset thật ngoài GerberViewer UI, nắm được RAM/thời gian đỉnh". Đối chiếu với hiện trạng:

- Phase 0 **chưa được chốt bằng văn bản** — không có doc nào ghi nhận kết quả 6 run spike đã chạy.
- Bộ doc `Phase1_Task01–04.md` được viết **trước khi** có code Core thật trong repo, nên chứa nhiều thông tin sai lệch so với source hiện tại. Chính các doc đó tự ghi chú "Bảng trên là ước lượng dựa trên convention — có thể cần adjust".
- Sai lệch nguy hiểm nhất là các **giá trị số** và **đường dẫn file**: nếu dev code đúng theo doc thì pipeline sẽ hỏng hoặc không compile.

Hệ quả: bất kỳ ai bắt tay vào Phase 1 hôm nay đều sẽ code theo một bản mô tả không khớp code thật.

---

## 2. Bằng chứng Phase 0 (từ 6 run thật)

Tất cả run nằm trong `result_20260807/`, cùng dataset **80 tile**.

| Run | Engine | Tổng | Ghi chú |
|---|---|---|---|
| `2_1/AlignStitch_...090910` | OpenCv | 490 s | |
| `2_1/AlignStitch_...092029` | OpenCv | 460 s | |
| `2_1/AlignStitch_...093028` | OpenCv | 453 s | |
| `2_1/AlignStitchSimple_...094104` | OpenCv | 481 s | simple workflow |
| `2_1/AlignStitch_...105306` | HalconProjectiveMosaicRebased | 278 s | |
| `AlignStitch_...110300` | HalconProjectiveMosaicRebased | **247 s** | run tham chiếu |

**Phân rã thời gian, run tham chiếu `110300` vs run OpenCv `090910`:**

| Stage | HALCON | OpenCv |
|---|---|---|
| Mapping and Preprocessing | 11 ms | 6 ms |
| **Direct Alignment** | **222 653 ms** | 235 035 ms |
| Failure Recovery | 301 ms | 323 ms |
| Neighbor Graph | 1 833 ms | 1 855 ms |
| Pose Graph Optimizer | 63 ms | 56 ms |
| Validate | 16 ms | 14 ms |
| **Stitching** | **13 309 ms** | 231 731 ms |
| Save Image | 8 902 ms | 20 936 ms |
| **Tổng** | **247 088 ms** | 489 956 ms |

Kết luận rút ra:
1. Engine HALCON nhanh hơn **17×** ở khâu stitch (13.3 s vs 231.7 s) và giảm tổng thời gian gần một nửa.
2. **Direct Alignment chiếm 90% tổng thời gian** (222/247 s) và gần như không đổi giữa 2 engine → đây mới là nút cổ chai thật, không phải khâu stitch.
3. `maxMatrixResidual ≈ 5.3e-11` → phép rebase chính xác về mặt số học, không phải nguồn sai số.

**Phân bố pose (run `110300`):**

| poseSource | Số tile |
|---|---|
| `SampleAlignment` | 54 |
| `BlankSampleExpectedPose` | 26 |
| `NeighborAlignment` | 5 |
| **Tổng `tileReports`** | **85** |

> **Chưa lý giải:** `tileReports` có 85 mục trong khi stage `Mapping and Preprocessing` ghi `tiles=80`. Chênh 5 mục đúng bằng số tile `NeighborAlignment`, gợi ý các tile được recovery bị ghi report hai lần — nhưng **chưa xác minh trong source**. Cần làm rõ trước khi façade dùng `tileReports.Count` làm `TileCount` trả về Master, nếu không con số báo cáo sẽ sai. Ghi vào `Phase0_Closeout.md` như một open item.

**Matcher thực dùng (run HALCON):** coarse = `HalconShapeModelMatcher` (54 tile), refinement = `PyramidEccMatcher` (54 tile). Matcher được *chọn* cuối: `PyramidEccMatcher` 29 tile, `HalconShapeModelMatcher` 20 tile.

---

## 3. Trạng thái Phase 0 và quyết định đã chốt

| Task | Trạng thái | Quyết định |
|---|---|---|
| 0.1 Build Core độc lập | ✅ Xong | `GerberStitching.Core/bin/x64/{Debug,Release}` có DLL |
| 0.2 Thống nhất HALCON | 🔸 Đã quyết, chưa thực thi | **Nâng Master + Worker lên HALCON 25.05** |
| 0.3 Spike end-to-end | 🔶 Một phần | Pipeline chạy đúng, nhưng vẫn trong GerberViewer UI → **gộp phần headless vào Phase 1 (Task 1.5)** |
| 0.4 Đo tài nguyên | 🔶 Một phần | Có stage timing, **thiếu RAM đỉnh** → đo trong Task 1.5 |
| 0.5 Quyết định OpenCvSharp | ✅ Đã quyết | **Giữ OpenCvSharp** |

**Cơ sở của quyết định 0.2 (nâng lên 25.05):** Master và Worker hiện cùng dùng `halcondotnetxl` **18.11.1.1** (`RDL_Master3\dll\halcondotnetxl.dll`, `RDL_WorkerNorthT...\dll\halcondotnetxl.dll`), Core dùng **25.05**. Chênh ~7 năm. Phương án ngược lại (hạ Core về 18.11) khả thi về API surface — 41 operator Core dùng đều có từ trước 18.11 — nhưng đã bị loại để tránh nợ kỹ thuật.

**Cơ sở của quyết định 0.5 (giữ OpenCvSharp):** ngay trong run engine HALCON, matcher tinh chỉnh là `PyramidEccMatcher` (OpenCV) cho toàn bộ 54 tile có nội dung, và được chọn làm kết quả cuối cho 29 tile. Gỡ OpenCvSharp sẽ mất tầng refinement. Tiền đề của task 0.5 ("nếu chỉ dùng HALCON") không thành lập.

---

## 4. Ba phát hiện ngược với giả định của roadmap

### 4.1. Không gỡ được OpenCvSharp
Đã trình bày ở §3. Ảnh hưởng: Task 1.3 phải đóng gói native runtime OpenCvSharp thay vì lược bỏ.

### 4.2. `CompletedWithFallback` là trạng thái bình thường của dataset này
Cả 6 run đều trả `runStatus = CompletedWithFallback`, nguyên nhân là 26/85 tile có `poseSource = BlankSampleExpectedPose` — sample Gerber rỗng (không có đồng), được đặt theo grid pose danh định với lý do ghi rõ: *"Exact-zero sample has no measurable alignment content; placed at deterministic expected grid pose."*

**Đã xác nhận: đây là đặc tính thật của board (có vùng trống), không phải lỗi grid config.**

Hệ quả cho Phase 3: Worker **phải phân biệt "blank hợp lệ" với "match fail thật"** khi map sang mã lỗi RDL `-300`. Nếu coi mọi fallback pose là lỗi thì mọi lô đều báo fail. Ghi nhận này thuộc phạm vi task 3.7, sẽ được nhắc lại khi viết doc Phase 3.

### 4.3. Engine HALCON không hỗ trợ blending
Warning từ run thật: *"EnableBlending was requested but `HOperatorSet.GenProjectiveMosaic` has no blending parameter; overlap uses hard overwrite."*

Đây là mâu thuẫn cấu hình: Task 1.4 cũ đặt default `SeamBlendMode = "Feather"` **và** `StitchingEngine = "HalconProjectiveMosaicRebased"` — cặp giá trị luôn sinh warning.

**Quyết định:** để config quyết định, **default = HALCON engine + `SeamBlendMode = None`**. Đường OpenCv + Feather vẫn giữ, chọn được qua ini khi cần ảnh đẹp hơn để điều tra. Đánh đổi phải ghi rõ trong doc: chọn blending = chấp nhận stitch chậm 17×.

---

## 5. Sai lệch cụ thể trong `Phase1_Task01–04.md`

### 5.1. Task 1.2 — đường dẫn Core sai 4/5 dòng

| Doc cũ ghi | Đường dẫn thật |
|---|---|
| `Core/SampleTile/SampleTileGenerator.cs` | `Core/Imaging/SampleTileGenerator.cs` |
| `Core/Alignment/Halcon/` | `Core/Matching/Halcon/` |
| `Core/Workflow/AlignStitchWorkflowService.cs` | `Core/Alignment/AlignStitchWorkflowService.cs` |
| `Core/PoseGraph/GlobalPoseGraphOptimizer.cs` | `Core/Alignment/Graph/GlobalPoseGraphOptimizer.cs` |
| `Core/Stitching/WorkflowStitchingService.cs` | ✅ đúng |

Signature cũng tự nghĩ ra. Signature thật:

```csharp
// Alignment/AlignStitchWorkflowService.cs:58
public Task<AlignStitchWorkflowResult> RunAsync(
    AlignStitchConfig config,
    SampleManifest manifest,
    IList<CapturedImageInfo> captured,
    IProgress<WorkflowProgress> progress,
    CancellationToken cancellationToken)

// Imaging/SampleTileGenerator.cs:23
public Task<SampleCropResult> GenerateAsync(
    PreparedSampleRun preparedRun,
    string outputRoot,
    CancellationToken cancellationToken,
    IProgress<SampleCropProgress> progress)
```

### 5.2. Xung đột tên DTO façade ↔ Core

Core **đã có** các type public trùng tên với DTO mà Task 1.2/1.4 đề xuất tạo mới:

| Type Core đã có | Vị trí |
|---|---|
| `AlignStitchConfig` | `Models/WorkflowModels.cs:72` |
| `CapturedImageInfo` | `Models/WorkflowModels.cs:160` |
| `GerberSampleConfig` | `Models/WorkflowModels.cs:64` |
| `AlignStitchConfigMapper` | `Configuration/AlignStitchConfigMapper.cs:13` |

Vì `RDL.GerberStitch` reference Core, tạo `RDL.GerberStitch.Facade.AlignStitchConfig` sẽ gây ambiguous reference, buộc phải dùng alias ở mọi call site.

**Hướng giải quyết trong bản viết lại:** DTO façade dùng tiền tố phân biệt (`RdlAlignStitchOptions`, `RdlCapturedTile`, `RdlStitchResult`). Đồng thời ghi rõ Core đã có sẵn `AlignStitchConfigMapper` với `EnsureComposite` / `CloneForRun` / `CreateSnapshot` — façade **map vào** hạ tầng đó, không viết lại từ đầu.

### 5.3. Task 1.4 — mô hình config sai tầng, giá trị số sai nguy hiểm

| Doc cũ | Thực tế trong Core |
|---|---|
| `AlignmentMethod` — 1 lựa chọn | **4** enum riêng: `DirectCoarseMatcherKind`, `DirectRefinementMatcherKind`, `NeighborCoarseMatcherKind`, `NeighborRefinementMatcherKind` (`Configuration/AlignStitchStageOptions.cs:9–31`) |
| `MinMatchScore = 0.7` | `HalconNccOptions.MinScore = 0.10`; `EccOptions.MinCorrelation = 0.13`; `PhaseCorrelationOptions.MinResponse = 0.15` |
| `StitchingEngine` — 2 giá trị | **5** giá trị: `OpenCv`, `HalconProjectiveMosaic`, `HalconProjectiveMosaicRebased`, `HalconWarpThenTileOffsetExperimental`, `HalconThenOpenCvFallback` (`Models/WorkflowModels.cs:31–38`) |
| `SeamBlendMode = "Feather"` default | Engine HALCON không có tham số blending (xem §4.3) |

`MinMatchScore = 0.7` là sai lệch nguy hiểm nhất: cao gấp ~5× default thật, nếu áp dụng sẽ loại gần hết tile.

Ngoài ra Core còn có các options group mà doc cũ không nhắc: `CommonGeometryOptions` (`MaxTranslationPixels = 300`, `MaxAbsRotationDeg = 0.5`, `MinScale/MaxScale = 0.95/1.05`, `MinTextureStdDev = 2`, `MinOverlapRatio = 0.01`), `AlignmentPreprocessingOptions`, `InputPathOptions`.

### 5.4. Task 1.3 — dependency không khớp thực tế

- Liệt kê `MathNet.Numerics` — Core **không** dùng; pose graph tự cài `Alignment/Graph/SparseNormalEquationCg.cs`.
- Liệt kê `opencv_world4130.dll` — không có trong output build. Native thật: `OpenCvSharpExtern.dll` + `opencv_videoio_ffmpeg4130_64.dll` (`GerberStitching.Core/bin/x64/Debug/dll/x64/`).
- Ghi "`halcondotnetxl.dll` ← đã có sẵn (Worker dùng)" — DLL đó là 18.11.1.1, không dùng lại được sau quyết định nâng 25.05.

### 5.5. Task 1.1 — mô tả việc đã xong, bỏ sót việc chưa xong

Doc cũ mô tả "tạo project mới" và đánh dấu exit gate `[x]`. Thực tế project đã tồn tại trong `RDL.GerberStitch.sln`, nhưng còn 3 vấn đề mở:

1. `RDL.GerberStitch/bin/x64/Debug/` **rỗng** — project chưa từng build ra output, dù exit gate ghi đã pass.
2. Hint path HALCON trong cả `RDL.GerberStitch.csproj` và `GerberStitching.Core.csproj` trỏ `C:\Users\USER\AppData\Local\Programs\MVTec\HALCON-25.05-Progress\bin\dotnet35\` — đường dẫn máy cá nhân, sẽ hỏng trên máy build RDL.
3. `Newtonsoft.Json` trỏ `..\..\Lib_Supporter\dll\Newtonsoft.Json.dll` — nằm ngoài repo, không kiểm soát được version.

---

## 6. Deliverable

Sáu file trong `docs/`. **Không sửa code C# trong task này.**

| File | Loại | Nội dung |
|---|---|---|
| `Phase0_Closeout.md` | mới | §2–§4 của spec này ở dạng doc chốt phase: bảng bằng chứng 6 run, 5 task với trạng thái + quyết định, 3 phát hiện ngược giả định, 2 blocker còn treo |
| `Phase1_Task01.md` | viết lại | Chuyển từ "tạo project" → "hoàn thiện project đã có": 3 vấn đề ở §5.5 |
| `Phase1_Task02.md` | viết lại | Façade API bám signature thật (§5.1); đặt tên DTO tránh xung đột (§5.2) |
| `Phase1_Task03.md` | viết lại | Dependency theo quyết định 25.05 + giữ OpenCvSharp; danh sách native lấy từ output build thật (§5.4) |
| `Phase1_Task04.md` | viết lại | Config mirror cây options thật; default lấy từ run `110300`; xử lý mâu thuẫn blending (§4.3, §5.3) |
| `Phase1_Task05.md` | mới | Harness headless — đóng 0.3/0.4, đồng thời là exit gate Phase 1 |

### 6.1. Định dạng doc

Theo `docs/task_sample.md`:

1. Header block: Ngày, Phase, PD ước lượng, Phụ thuộc, Bắt buộc đọc trước, Ràng buộc (không thêm NuGet, không thêm project test, không tự chạy test).
2. `0. Vấn đề` — nêu vấn đề kèm bằng chứng `file:line`.
3. Các mục đánh số — bước thực hiện, code cụ thể.
4. `Danh sách file thay đổi` — bảng.
5. `Tiêu chí nghiệm thu` — checklist đánh số.
6. `Rủi ro` — bảng kèm cách giảm thiểu.

**Bỏ so với mẫu:** khối "Change record bắt buộc: cập nhật `Log.html` / `Params.html` / `history.html`". Đó là convention của repo GerberViewer; repo này không có các file đó.

**Giữ so với mẫu:** quy ước comment cho thay đổi lớn — `// [Claude] [Change time: YYYY-MM-DD] [Purpose: ...]`.

### 6.2. Task 1.5 — harness headless (doc mới)

**Mục tiêu:** một console app x64 gọi façade `RDL.GerberStitch`, chạy lại dataset 80 tile, ghi ra file: RAM đỉnh, stage timing, đường dẫn `Stitched.tiff`.

**Vì sao gộp vào Phase 1 thay vì làm ở Phase 0:** viết harness ở Phase 0 sẽ phải gọi thẳng vào Core, rồi Phase 1 lại viết lại để gọi façade. Gộp lại chỉ viết một lần, và chính harness đó trở thành exit gate của Phase 1 ("Master/Worker add-reference & gọi được façade").

**Đo RAM:** `Process.GetCurrentProcess().PeakWorkingSet64` sau khi run xong, cộng thêm lấy mẫu định kỳ trong lúc chạy để bắt đỉnh giữa chừng. Con số này là đầu vào để Manager cấp phát RAM cho Worker (rủi ro "Tài nguyên Worker" mức Cao trong roadmap §5).

**Không thuộc phạm vi:** harness là công cụ đo, **không phải** unit test. Theo `AGENTS.md` §4, không thêm project test và không tự chạy kiểm thử.

### 6.3. Blocker còn treo (ghi vào `Phase0_Closeout.md`)

Giữ nguyên cách đánh số của roadmap §6. Blocker **1** (version HALCON) và **2** (dataset thật) đã tháo. Còn treo:

3. **Ổ chia sẻ** — chưa xác nhận đường dẫn/dung lượng cho manifest + model + `Stitched.tiff`. Tham chiếu kích thước thật: một `Stitched.tiff` = **654 MB**.
4. **Grid config** — chưa chốt rows/cols/overlap/px-per-mm để map `ExpectedX/Y` ↔ toạ độ die của Master. **Chặn task 2.3.**

---

## 7. Phạm vi KHÔNG làm

- Không sửa file C# nào — task này chỉ sinh/sửa tài liệu trong `docs/`.
- Không viết doc Phase 2 / Phase 3 (đã thống nhất làm sau).
- Không thực hiện việc nâng HALCON 25.05 — đó là việc của Task 1.3 khi triển khai.
- Không chạy build hay kiểm thử — theo `AGENTS.md` §4, user tự kiểm thử thủ công.

---

## 8. Tiêu chí nghiệm thu của chính task này

1. `docs/Phase0_Closeout.md` tồn tại, chứa đủ: bảng 6 run, bảng phân rã 8 stage của run tham chiếu, trạng thái 5 task Phase 0 kèm quyết định, 3 phát hiện ngược giả định, 2 blocker còn treo (số 3 và 4), và open item `tileReports = 85` vs `tiles = 80`.
2. `Phase1_Task01–04.md` được viết lại; mọi đường dẫn file Core trong doc **khớp** repo hiện tại; mọi giá trị số default **khớp** source Core.
3. `docs/Phase1_Task05.md` tồn tại, mô tả harness headless + cách đo RAM đỉnh.
4. Không doc nào còn chứa: `MinMatchScore = 0.7`, `MathNet.Numerics`, `opencv_world4130.dll`, hay các đường dẫn Core ở §5.1.
5. Không file `.cs` nào bị sửa (`git status` chỉ hiện thay đổi trong `docs/`).
