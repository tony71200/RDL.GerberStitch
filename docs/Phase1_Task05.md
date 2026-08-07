# Phase 1 — Task 1.5: Harness headless & đo tài nguyên

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1
**Phụ thuộc:** Task 1.2 (façade có API), Task 1.3 (dependency resolve được ngoài IDE)
**Platform:** C# 7.3, .NET Framework 4.8, Console Application, **x64**

**Bắt buộc đọc trước khi làm:**
1. `docs/Phase0_Closeout.md` §1 (số liệu tham chiếu), §3 (vì sao task này tồn tại)
2. `AGENTS.md` §4 (agent không tự chạy test — user tự chạy harness)

**Ràng buộc:** Không thêm NuGet package. Không thêm project test. Không tự chạy test.

> **Task này KHÔNG phải project test.** Nó là **công cụ đo** — chạy pipeline thật, in số liệu, không có assertion, không có test runner, không chạy trong CI. Việc thực thi do user thực hiện thủ công.

---

## 0. Vấn đề

Roadmap task 0.3 yêu cầu chạy spike *"trong tiến trình giả lập worker"*, task 0.4 yêu cầu *"đo RAM đỉnh + thời gian khi stitch cả lô"*. Hiện trạng (`Phase0_Closeout.md` §2):

- ✅ Pipeline đã chạy đúng — 6 run, `Stitched.tiff` hợp lệ.
- ❌ Nhưng **mọi run đều qua GerberViewer UI**, chưa có tiến trình headless nào.
- ❌ Có stage timing đầy đủ, **không có số RAM nào**.

**Vì sao gộp vào Phase 1 thay vì làm ở Phase 0:** harness viết ở Phase 0 phải gọi thẳng vào Core; sang Phase 1 có façade lại phải viết lại. Gộp lại chỉ viết một lần, và chính harness đó là exit gate Phase 1 — roadmap ghi *"Master và Worker (project trống thử nghiệm) đều add-reference & gọi được façade"*. Harness **chính là** project đó.

Thiếu số RAM là vấn đề thật: roadmap §5 xếp *"Tài nguyên Worker: align+stitch cả lô, canvas ~40k×32k, RAM/thời gian lớn"* ở mức **Cao**, với cách giảm thiểu là *"Manager cấp phát RAM phù hợp"*. Không có số đo thì không cấp phát được.

---

## 1. Tạo project harness

```
RDL.GerberStitch.Harness/
├── Program.cs
├── ResourceSampler.cs
├── RunReportWriter.cs
├── Properties/AssemblyInfo.cs
└── RDL.GerberStitch.Harness.csproj
```

**Cấu hình:**
- Template: **Console Application (.NET Framework)**, target **4.8**
- Platform: **x64**, `Prefer32Bit = false` — bắt buộc, canvas lớn không chạy nổi trong 32-bit
- `ProjectReference` → `RDL.GerberStitch` **duy nhất**. Không reference thẳng `GerberStitching.Core` hay `GerberEngine` — nếu harness cần chúng thì façade chưa đủ kín, đó là tín hiệu phải sửa Task 1.2.

> Điểm này chính là phép thử ranh giới façade của `AGENTS.md` §3.1.

---

## 2. Luồng chạy

```
Program.Main(args)
  ├─ Đọc ini (RdlAlignStitchOptionsReader — Task 1.4)
  ├─ Bắt đầu ResourceSampler (§3)
  ├─ [tuỳ chọn] GenerateSampleManifest(gerberPath, grid, outputRoot)
  ├─ Quét thư mục ảnh → IList<RdlCapturedTile>  (§2.1)
  ├─ RunAlignStitch(manifestPath, tiles, options, outputPath, progress, ct)
  ├─ Dừng ResourceSampler
  └─ Ghi run_report.json + in bảng ra console (§4)
```

**Tham số dòng lệnh:**

| Tham số | Ý nghĩa |
|---|---|
| `--ini <path>` | File config (§6 Task 1.4). Bắt buộc |
| `--manifest <path>` | Manifest có sẵn. Bỏ qua thì chạy `GenerateSampleManifest` |
| `--images <dir>` | Thư mục ảnh tile đã chụp. Bắt buộc |
| `--out <dir>` | Thư mục ghi `Stitched.tiff` + `run_report.json`. Bắt buộc |
| `--repeat <n>` | Số lần chạy liên tiếp trong **cùng** process. Default 1. Dùng để soi rò RAM |

### 2.1. Gắn `OrderIndex` cho ảnh

Đây là chỗ dễ sai nhất. `OrderIndex` phải **khớp tuyệt đối** với `SampleTileInfo.OrderIndex` trong manifest — lệch thì ảnh bị gán sai vị trí **mà không có lỗi nào báo ra** (Task 1.2 §1.1).

Core có sẵn `Arrangement/NaturalSortService.cs` và `Arrangement/CapturedImageLoader.cs` cho việc này. Nhưng harness **không được** reference Core (§1).

> 🔴 **Thứ tự là rắn bò theo cột, không phải quét hàng ngang.** Với default `StartOrder=TopLeftDown` + `CropOrder=Zigzag`, Core duyệt **cột ngoài, hàng trong, đảo chiều ở cột lẻ** (`Imaging/SampleGridGeometry.cs:97-102` — xem Task 1.2 §3.1.1). Sắp xếp ảnh theo row-major sẽ gán sai toàn bộ `OrderIndex` **mà không có lỗi nào báo ra**, và kết quả stitch vẫn "trông có vẻ hợp lý".

**Cách làm:** harness **không** tự suy ra thứ tự. Đọc `sample_manifest.json`, lấy `Tiles[i].OrderIndex` cùng `Row`/`Column`, rồi ghép với ảnh chụp theo `Row`/`Column` — đó là khoá bền vững, không phụ thuộc quy ước đặt tên file. Nếu tên file ảnh chụp không mang được `Row`/`Column`, dùng natural sort làm phương án dự phòng nhưng **bắt buộc in bảng ánh xạ** `OrderIndex → (Row, Col) → tên file` để user đối chiếu bằng mắt trước khi tin kết quả.

> Nếu quy tắc gán `OrderIndex` phức tạp hơn thế, đó là dấu hiệu façade cần một hàm helper — báo lại để bổ sung vào Task 1.2, đừng để harness tự chế logic riêng.

---

## 3. Đo tài nguyên

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Lấy RAM đỉnh cho việc cấp phát của Manager; PeakWorkingSet64 không đủ vì cần cả diễn biến giữa chừng.]
internal sealed class ResourceSampler : IDisposable
{
    // Lấy mẫu mỗi 500 ms trên một thread nền:
    //   proc.WorkingSet64          — RAM vật lý đang giữ (gồm cả cấp phát native HALCON/OpenCV)
    //   proc.PrivateMemorySize64   — bộ nhớ riêng đã commit
    // Kết thúc đọc thêm:
    //   proc.PeakWorkingSet64      — đỉnh do OS ghi nhận
    //   proc.PeakPagedMemorySize64
    // Ghi lại: đỉnh, trung bình, và mốc thời gian đạt đỉnh
}
```

**Vì sao lấy mẫu định kỳ chứ không chỉ đọc `PeakWorkingSet64` lúc cuối:** cần biết **đỉnh rơi vào stage nào**. Nếu đỉnh xuất hiện lúc `Stitching` thì Manager cấp phát theo kích thước canvas; nếu đỉnh ở `Direct Alignment` thì theo số tile nạp đồng thời. Hai kết luận dẫn tới hai cách cấu hình khác nhau.

`WorkingSet64` bao gồm cấp phát native, nên bắt được cả `HObject` của HALCON lẫn `Mat` của OpenCV — đây là lý do dùng nó thay vì `GC.GetTotalMemory`, thứ chỉ thấy heap managed.

**Mốc so sánh:** `Stitched.tiff` = **654 MB**. RAM đỉnh chắc chắn lớn hơn nhiều lần — con số cụ thể chính là thứ task này cần trả lời.

---

## 4. Đầu ra

### 4.1. `run_report.json`

```json
{
  "runUtc": "2026-08-07T09:09:10Z",
  "tileCount": 80,
  "engine": "HalconProjectiveMosaicRebased",
  "totalElapsedMs": 247088,
  "stageTimings": [
    { "stage": "Direct Alignment", "elapsedMs": 222653, "detail": "tiles=80" }
  ],
  "resource": {
    "peakWorkingSetMb": 0,
    "peakPrivateMemoryMb": 0,
    "peakAtStage": "",
    "samples": []
  },
  "result": {
    "success": true,
    "tiffPath": "...",
    "tileCount": 80,
    "alignedTileCount": 59,
    "blankTileCount": 26,
    "failedTiles": []
  }
}
```

### 4.2. Bảng console

In ra bảng cùng định dạng với `Phase0_Closeout.md` §1.1 để **so trực tiếp** với run tham chiếu:

```
Stage                          Elapsed(ms)   Detail
─────────────────────────────────────────────────────────
Mapping and Preprocessing               11   tiles=80
Direct Alignment                   222 653   tiles=80
...
─────────────────────────────────────────────────────────
TOTAL                              247 088
Peak WorkingSet    : ____ MB  (tại stage: ____)
Peak PrivateMemory : ____ MB
```

---

## 5. Danh sách file thay đổi

| File | Thay đổi |
|---|---|
| `RDL.GerberStitch.Harness/RDL.GerberStitch.Harness.csproj` | **mới** |
| `RDL.GerberStitch.Harness/Program.cs` | **mới** — §2 |
| `RDL.GerberStitch.Harness/ResourceSampler.cs` | **mới** — §3 |
| `RDL.GerberStitch.Harness/RunReportWriter.cs` | **mới** — §4 |
| `RDL.GerberStitch.Harness/Properties/AssemblyInfo.cs` | **mới** |
| `RDL.GerberStitch.sln` | + project harness |
| `docs/Phase0_Closeout.md` | Cập nhật §2 task 0.4 với số RAM đo được |

**Không đụng:** `GerberStitching.Core`, `GerberEngine`.

---

## 6. Tiêu chí nghiệm thu

Đây đồng thời là **exit gate của Phase 1** và đóng nốt task 0.3 / 0.4.

1. Build sạch `Debug|x64` và `Release|x64`.
2. Harness chỉ reference `RDL.GerberStitch` — **không** có `using GerberViewer.Stitching.*` trong bất kỳ file nào. *(Phép thử ranh giới façade.)*
3. Chạy được từ **dòng lệnh**, ngoài Visual Studio, trên máy có `HALCONROOT` đặt đúng — **đóng task 0.3**.
4. Dataset 80 tile → `Stitched.tiff` **tương đương run `110300`**: cùng kích thước file, `blankTileCount = 26`, `alignedTileCount = 59`.
5. `run_report.json` có `peakWorkingSetMb > 0` và `peakAtStage` khác rỗng — **đóng task 0.4**.
6. Tổng thời gian trong khoảng **±20%** so với 247 s của run tham chiếu. Lệch nhiều hơn → điều tra trước khi chốt.
7. `--repeat 3` → RAM đỉnh của lần 3 **không** cao hơn lần 1 quá 10%. Cao hơn = rò tài nguyên, phải xử lý trước khi sang Phase 3 *(roadmap rủi ro "Rò RAM HObject/WorkflowImageCache", mức TB)*.
8. Số RAM đỉnh được cập nhật ngược vào `Phase0_Closeout.md` §2, task 0.4 chuyển từ 🔶 sang ✅.

---

## 7. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| `Private=False` (Task 1.3 §2) → harness không tìm thấy `halcondotnetxl.dll` lúc chạy | Copy DLL vào output harness bằng post-build, **hoặc** chạy harness từ folder deploy Worker. Không sửa ngược thành `Private=True` |
| `OrderIndex` gán sai → ảnh đúng vị trí sai, kết quả nhìn "hợp lý" nhưng sai | §2.1 in bảng ánh xạ để đối chiếu; nghiệm thu mục 4 so với số tile đã biết |
| Lấy mẫu 500 ms bỏ lỡ đỉnh ngắn | `PeakWorkingSet64` do OS ghi nhận bắt được đỉnh tức thời; lấy mẫu chỉ để biết đỉnh **ở stage nào** |
| Harness bị hiểu nhầm là project test → vi phạm `AGENTS.md` §4 | Ghi rõ ở đầu doc; không assertion, không test runner, không CI |
| Máy chạy harness không đủ RAM → OOM giữa chừng | Chạy trên máy worker thật hoặc tương đương; OOM cũng là dữ liệu — ghi lại cấu hình máy vào `run_report.json` |
