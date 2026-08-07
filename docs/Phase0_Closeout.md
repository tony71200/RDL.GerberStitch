# Phase 0 — Closeout: Khảo sát & Spike

**Ngày chốt:** 2026-08-07
**Phase:** 0 — Khảo sát & Spike (roadmap ước lượng 3–4 PD)
**Trạng thái:** ✅ **ĐÓNG** — với 2 phần việc chuyển sang Phase 1 (xem §3)
**Nguồn bằng chứng:** 6 run thật trong `H:\005_Project\AOI_2026_07_imp\result_20260807\`

**Bắt buộc đọc trước khi dùng doc này:**
1. `docs/roadmap_GerberAlignStitch_integration.md` §3 Phase 0, §5 Rủi ro, §6 Blocker
2. `AGENTS.md` §3.3 (HALCON license — chưa có gate tập trung)

---

## 0. Vì sao cần doc này

Roadmap đặt exit gate Phase 0 là *"stitch được 1 dataset thật ngoài GerberViewer UI, chạy trọn trong một tiến trình; nắm được RAM/thời gian đỉnh của cả lô"*.

Thực tế đã có 6 run cho ra `Stitched.tiff` hợp lệ, nhưng **chưa có tài liệu nào ghi nhận kết quả**. Hệ quả: các quyết định phụ thuộc Phase 0 (task 1.1, 1.3, 1.4 đều ghi "Phụ thuộc: Phase 0.2 / Phase 0.5") không có nguồn để tra. Doc này chốt lại bằng số liệu.

---

## 1. Bằng chứng — 6 run trên cùng dataset 80 tile

| Run | Engine | Tổng | Ghi chú |
|---|---|---|---|
| `2_1/AlignStitch_20260807_090910` | OpenCv | 490 s | |
| `2_1/AlignStitch_20260807_092029` | OpenCv | 460 s | |
| `2_1/AlignStitch_20260807_093028` | OpenCv | 453 s | |
| `2_1/AlignStitchSimple_20260807_094104` | OpenCv | 481 s | simple workflow |
| `2_1/AlignStitch_20260807_105306` | HalconProjectiveMosaicRebased | 278 s | |
| **`AlignStitch_20260807_110300`** | **HalconProjectiveMosaicRebased** | **247 s** | ⭐ **run tham chiếu** |

Cả 6 run đều trả `succeeded = true`, `runStatus = CompletedWithFallback` (lý do ở §4.2).

### 1.1. Phân rã 8 stage — HALCON vs OpenCv

| Stage | HALCON (`110300`) | OpenCv (`090910`) |
|---|---|---|
| Mapping and Preprocessing | 11 ms | 6 ms |
| **Direct Alignment** | **222 653 ms** | 235 035 ms |
| Failure Recovery | 301 ms | 323 ms |
| Neighbor Graph | 1 833 ms | 1 855 ms |
| Pose Graph Optimizer | 63 ms | 56 ms |
| Validate | 16 ms | 14 ms |
| **Stitching** | **13 309 ms** | 231 731 ms |
| Save Image | 8 902 ms | 20 936 ms |
| **TỔNG** | **247 088 ms** | 489 956 ms |

**Ba kết luận:**

1. Engine HALCON nhanh hơn **17×** ở khâu stitch (13.3 s vs 231.7 s), giảm tổng thời gian gần một nửa.
2. **Direct Alignment chiếm 90% tổng thời gian** (222/247 s) và gần như không đổi giữa 2 engine. Đây mới là nút cổ chai thật — mọi nỗ lực tối ưu về sau phải nhắm vào đây, không phải khâu stitch.
3. `maxMatrixResidual = 5.29e-11`, `canonicalRebaseResidual = 5.29e-11` → phép rebase chính xác về mặt số học, không phải nguồn sai số.

### 1.2. Phân bố pose và matcher (run tham chiếu)

| poseSource | Số tile |
|---|---|
| `SampleAlignment` | 54 |
| `BlankSampleExpectedPose` | 26 |
| `NeighborAlignment` | 5 |
| **Tổng `tileReports`** | **85** |

| Vai trò matcher | Matcher thực dùng |
|---|---|
| Coarse | `HalconShapeModelMatcher` — 54 tile |
| Refinement | `PyramidEccMatcher` — 54 tile |
| Được **chọn** làm kết quả cuối | `PyramidEccMatcher` 29 tile / `HalconShapeModelMatcher` 20 tile |

### 1.3. Kích thước output

`Stitched.tiff` phụ thuộc engine: **654 MB** với engine `OpenCv` (run `090910`), nhưng **~1.3 GB** với engine `HalconProjectiveMosaicRebased` (run `110300`: 1 305 898 710 byte; xác nhận lại y hệt qua harness Task 1.6: 1 305 931 192 byte — chênh 32 KB do metadata). Vì `HalconProjectiveMosaicRebased` là engine **mặc định** của façade (`docs/Phase1_Task02.md`), **1.3 GB mới là con số đúng để tính dung lượng ổ chia sẻ** (blocker 3, §5) — không phải 654 MB như bản trước của mục này ghi nhầm.

---

## 2. Trạng thái 5 task Phase 0

| Task | Trạng thái | Kết luận |
|---|---|---|
| 0.1 Build Core độc lập | ✅ Xong | `GerberStitching.Core/bin/x64/{Debug,Release}` có DLL |
| 0.2 Thống nhất version HALCON | ✅ Đã quyết | **Nâng Master + Worker lên HALCON 25.05** |
| 0.3 Spike end-to-end | ✅ Đóng qua Task 1.6 | Harness headless chạy trọn qua façade — xem `docs/Phase1_Task06.md` §3 |
| 0.4 Đo tài nguyên | ✅ Đóng qua Task 1.6 | **RAM đỉnh = 6 616 MB** (~6.6 GB) trên dataset 80 tile, canvas ~40k×32k |
| 0.5 Quyết định OpenCvSharp | ✅ Đã quyết | **Giữ OpenCvSharp** |

### 2.1. Quyết định 0.2 — nâng lên HALCON 25.05

**Hiện trạng đo được:**

| Nơi | File | Version |
|---|---|---|
| Master | `RDL_Master3\dll\halcondotnetxl.dll` | **18.11.1.1** |
| Worker | `RDL_WorkerNorthT_Ver2_SoftMerge\WindowsFormsApp1\dll\halcondotnetxl.dll` | **18.11.1.1** |
| Core | hint path `...\MVTec\HALCON-25.05-Progress\bin\dotnet35\` | **25.05** |

Chênh ~7 năm — lớn hơn nhiều so với cách roadmap §2 mô tả ("cần thống nhất version").

**Phương án ngược lại đã cân nhắc và loại:** hạ Core về 18.11 là *khả thi về API surface* — toàn bộ 41 operator HALCON mà Core dùng (`CreateShapeModel`, `FindNccModel`, `GenProjectiveMosaic`, `TileImagesOffset`, `SetShapeModelParam`, ...) đều đã tồn tại từ trước 18.11. Loại vì tạo nợ kỹ thuật và khoá hệ thống vào bản 2018.

> ⚠ **Chưa thực thi.** Quyết định này mới là quyết định; việc nâng thật thuộc Task 1.3. Cần xác nhận license/dongle 25.05 phủ đủ số máy worker trước khi triển khai.

### 2.2. Quyết định 0.5 — giữ OpenCvSharp

Task 0.5 đặt câu hỏi *"giữ hay lược bỏ (nếu chỉ HALCON)"*. **Tiền đề "nếu chỉ HALCON" không thành lập.**

Bằng chứng: trong chính run engine HALCON (`110300`), matcher tinh chỉnh là `PyramidEccMatcher` (OpenCV) cho **toàn bộ 54 tile có nội dung**, và được chọn làm kết quả cuối cho **29 tile**. Đây không phải cấu hình tình cờ — nó là default của Core (`DirectAlignmentOptions.RefinementMatcher = PyramidEcc`).

Gỡ OpenCvSharp = mất tầng refinement của Direct Alignment. **Giữ**, và Task 1.3 phải đóng gói native runtime kèm theo.

---

## 3. Hai phần việc chuyển sang Phase 1 — ✅ đã đóng

Task 0.3 và 0.4 **không làm lại ở Phase 0** mà gộp vào harness Phase 1. Ban đầu dự định ở Task 1.5 (`Phase1_Task05.md`, spec-only); thực tế triển khai và chạy thật ở **Task 1.6** (`Phase1_Task06.md`) sau khi façade (Task 1.2) đã có API thật để gọi.

**Lý do gộp:** viết harness ở Phase 0 phải gọi thẳng vào Core; sang Phase 1 có façade lại phải viết lại để gọi façade. Gộp lại chỉ viết một lần.

| Việc còn thiếu | Đóng ở | Kết quả |
|---|---|---|
| Chạy pipeline trong tiến trình headless, ngoài GerberViewer UI | Task 1.6 | ✅ `RDL.GerberStitch.Harness.exe` chạy độc lập, dataset 80 tile thật, `Success=true` |
| Đo RAM đỉnh khi stitch cả lô | Task 1.6 | ✅ **6 616 MB** (~6.6 GB), canvas ~40k×32k — số liệu đầu tiên cho roadmap §5 rủi ro "Tài nguyên Worker" |

**Đối chiếu độ chính xác:** kết quả harness (`AlignedTiles=54`, `BlankTiles=26`, `FailedTiles=0`) **khớp chính xác** run tham chiếu `110300` (`SampleAlignment=54`, `BlankSampleExpectedPose=26`) — xác nhận façade tái tạo đúng hành vi pipeline gốc, không lệch do quá trình đóng gói.

---

## 4. Ba phát hiện ngược với giả định của roadmap

### 4.1. Không gỡ được OpenCvSharp

Đã trình bày ở §2.2. **Ảnh hưởng:** Task 1.3 phải đóng gói native runtime OpenCvSharp thay vì lược bỏ. Rủi ro *"OpenCvSharp kéo theo dù chỉ dùng HALCON"* trong roadmap §5 được đánh giá **Thấp** — giữ nguyên mức đó, nhưng phương án giảm thiểu "cân nhắc gỡ ở Phase 0.5 nếu sạch" **không còn hiệu lực**.

### 4.2. `CompletedWithFallback` là trạng thái bình thường của dataset này

Cả 6 run đều trả `CompletedWithFallback`. Nguyên nhân: 26/85 tile có `poseSource = BlankSampleExpectedPose`, với lý do ghi trong report:

> *"Exact-zero sample has no measurable alignment content; placed at deterministic expected grid pose."*

Đây là sample Gerber rỗng — vùng board không có đồng. Hành vi này do `RecoveryFallbackOptions.AutoPlaceExactZeroSample = true` (default của Core) điều khiển.

**✅ Đã xác nhận: đúng kỳ vọng, board có vùng trống. Không phải lỗi grid config.**

> ⚠ **Ảnh hưởng tới Phase 3 (task 3.7):** Worker **phải phân biệt "blank hợp lệ" với "match fail thật"** khi map sang mã lỗi RDL `-300`. Nếu coi mọi fallback pose là lỗi thì **mọi lô đều báo fail**. Dùng `PoseSource.BlankSampleExpectedPose` để nhận diện, không dùng `runStatus`.

### 4.3. Engine HALCON không hỗ trợ blending

Warning từ run thật:

> *"EnableBlending was requested but `HOperatorSet.GenProjectiveMosaic` has no blending parameter; overlap uses hard overwrite. Select StitchingEngine.OpenCv for Feat[her]..."*

Nguồn gốc: default của Core là `StitchingOptions.EnableBlending = true` + `BlendMode = Feather` **và** `Engine = HalconProjectiveMosaicRebased` — cặp giá trị luôn sinh warning.

**Quyết định:** để config quyết định. **Default cho RDL = engine HALCON + `EnableBlending = false`.** Đường OpenCv + Feather vẫn giữ, chọn được qua ini khi cần ảnh đẹp hơn để điều tra.

**Đánh đổi phải ghi rõ:** chọn blending = chấp nhận khâu stitch chậm **17×** (13.3 s → 231.7 s).

> Ghi chú: `StitchingOptions.BlankFallbackOverlapPolicy` (`Normal` / `PreserveExistingOverlap` / `WeightedBlend`, default `PreserveExistingOverlap`) là knob **riêng biệt**, chỉ chi phối cách tile blank fallback xử lý vùng chồng lấn — không phải blending seam tổng thể. Đừng nhầm hai thứ.

---

## 5. Blocker — trạng thái theo roadmap §6

| # | Blocker | Trạng thái |
|---|---|---|
| 1 | Version HALCON thống nhất | ✅ **Tháo** — đã quyết 25.05 (§2.1); còn khâu thực thi ở Task 1.3 |
| 2 | Dataset thật (tile ảnh + Gerber) | ✅ **Tháo** — 6 run trên dataset 80 tile |
| 3 | **Ổ chia sẻ** | ❌ **Còn treo** — chưa xác nhận đường dẫn/dung lượng cho manifest + model + `Stitched.tiff`. Tham chiếu: 1 file = **654 MB** |
| 4 | **Grid config** | ❌ **Còn treo** — chưa chốt rows/cols/overlap/px-per-mm để map `ExpectedX/Y` ↔ toạ độ die Master. **Chặn task 2.3** |

---

## 6. Open item

### 6.1. `tileReports = 85` nhưng `tiles = 80` — ✅ đã tháo gỡ (2026-08-07)

Trong run tham chiếu, stage `Mapping and Preprocessing` ghi `detail = "tiles=80"`, nhưng `ProcessingReport.TileReports` có **85** mục. Chênh **5** — đúng bằng số tile `NeighborAlignment`.

**Giả thuyết ban đầu (chưa xác minh trong source):** tile được recovery bị ghi report hai lần.

**Đã xác nhận đúng bằng dữ liệu thật.** `GerberStitchFacade.MapResult` (Task 1.2) dùng `AlignStitchWorkflowResult.States` — **không** dùng `TileReports` — để đếm `AlignedTileCount`/`BlankTileCount`. Chạy harness Task 1.6 trên đúng dataset của run `110300`: `States` cho **54 + 26 + 0 = 80**, khớp chính xác `manifest.Tiles.Count`, không có phần tử dư. Xác nhận: `AlignStitchWorkflowResult.States` là nguồn đếm đúng (1 tile = 1 state), `ProcessingReport.TileReports` mới là nơi có phần tử trùng lặp (khả năng cao do tile qua Neighbor Recovery được ghi report ở cả bước Direct lẫn bước Recovery).

### 6.2. Model HALCON — ✅ đã giải quyết (2026-08-07)

Khi viết doc Phase 1 phát hiện: code sinh `.ncm`/`.shm` trong `Imaging/SampleTileGenerator.cs` **đang bị comment**, và không có file model nào tồn tại trên đĩa. Các run Phase 0 chạy được là nhờ `HalconShapeModelProvider.GetOrCreate` tự `CreateShapeModel` trong bộ nhớ khi không thấy file (`Matching/Halcon/HalconShapeModelProvider.cs:95-109`).

Điều này lật giả định của roadmap task 2.2 / 2.4 ("Master sinh model, ghi ra ổ chia sẻ để Worker đọc").

**Đã xử lý:** thêm cờ `GerberSampleConfig.ModelGeneration` (`OnTheFly` mặc định | `Pregenerate`), ghi lựa chọn vào `sample_manifest.json`, và kích hoạt lại khối sinh model sau cờ đó. Chi tiết ở `Phase1_Task02.md` §2.

> ⚠ Chế độ `Pregenerate` **chưa được đo**. Mức tiết kiệm thật cần harness Task 1.5 so `Direct Alignment` giữa 2 chế độ trên cùng dataset.

### 6.3. HALCON license chưa có gate tập trung

Theo `AGENTS.md` §3.3: repo chưa có bước kiểm tra license HALCON trước khi gọi. Chỉ có xử lý lỗi *sau khi* license hết hạn (`ImageInteropService.cs`, `HalconImageInteropException.LicenseExpired`).

Việc nâng lên 25.05 (§2.1) làm vấn đề này cấp thiết hơn: nếu dongle không phủ 25.05, lỗi sẽ chỉ lộ ra lúc runtime trên máy worker. Cần chốt cơ chế khi triển khai Task 1.3.

---

## 7. Kết luận

Phase 0 **đóng**. Pipeline đã chứng minh chạy được trên dataset thật, engine HALCON là lựa chọn đúng, hai quyết định treo (0.2, 0.5) đã chốt kèm bằng chứng.

Chuyển sang Phase 1 với 3 điều kiện mang theo:

1. Task 1.3 thực thi việc nâng HALCON 25.05 và xác nhận license.
2. Task 1.5 đóng nốt phần headless + đo RAM.
3. Blocker 3 và 4 phải tháo trước khi bắt đầu Phase 2.
