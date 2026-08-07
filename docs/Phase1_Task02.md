# Phase 1 — Task 1.2: Façade API tối thiểu

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 2 *(tăng từ 1.5 — phát sinh quyết định model pre-gen ở §2)*
**Phụ thuộc:** Task 1.1 (façade đã `public`, DTO đã đổi tên)
**Platform:** C# 7.3, .NET Framework 4.8, **x64**

**Bắt buộc đọc trước khi sửa:**
1. `docs/Phase0_Closeout.md` §4.2 (blank tile), §6.1 (open item đếm tile)
2. `AGENTS.md` §3.1 (ranh giới façade — không đổi hành vi pipeline Core khi chưa hỏi)
3. File task này, đặc biệt **§2 — quyết định chặn đường**

**Ràng buộc:** Không thêm NuGet package. Không thêm project test. Không tự chạy test.

---

## ✅ Đã triển khai (2026-08-07, nhánh `Ver1_0`)

Đã viết `Facade/GerberStitchFacade.cs` + `Facade/AlignStitchConfig.cs` thật, build sạch, và **chạy thành công với dữ liệu thật** qua harness (`docs/Phase1_Task06.md`) — dataset 80 tile tại `H:\005_Project\AOI_2026_07_imp\data\GerberSample_20260806_084849` + `H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\20260725 Q168 2-1 org`.

So với kế hoạch §1–§4 dưới đây, có **5 điểm thiết kế lại** — phát sinh từ việc đọc trực tiếp `AlignStitchingControl.RunWorkflowAsync` và `CreateGerberSampleControl` trong repo GerberViewer (theo yêu cầu tham khảo code sản xuất trước khi viết façade):

1. **Không tạo `RdlCapturedTile`/danh sách DTO ảnh thủ công.** Core đã có sẵn `GerberViewer.Stitching.Arrangement.CapturedImageLoader.Load(imageFolder, manifestPath, w, h)` — tự đọc lại manifest, natural-sort tên file trong thư mục, ghép theo vị trí **index** với `manifest.Tiles` (đã đảm bảo liên tục 0..N-1 theo `OrderIndex`), trả `IList<CapturedImageInfo>` đã validate (số lượng khớp, không trùng khoá, kích thước ảnh khớp nhau). Đây chính là cơ chế production dùng ở `AlignStitchingControl`/`CreateGerberSampleControl`. `RunAlignStitch` vì vậy nhận **thư mục ảnh** (`string capturedImagesFolder`), không nhận `IList<RdlCapturedTile>` — đơn giản hơn thiết kế cũ, và tránh đúng rủi ro "OrderIndex lệch không báo lỗi" mà §1 dưới đây cảnh báo.
2. **Không tạo `RdlGridConfig`.** `GenerateSampleManifest` nhận thẳng `GerberViewer.Stitching.Configuration.GerberSampleConfig` của Core — đây là DTO thuần dữ liệu (không HObject/HALCON handle), tạo một bản sao gần giống hệt (`RdlGridConfig`) là dư thừa. Đánh đổi: façade lộ ra 1 kiểu của Core, hơi ngược với AGENTS.md §3.1; chấp nhận có chủ đích vì DTO này an toàn để chạm trực tiếp.
3. **Giữ tên `AlignStitchConfig.cs`/`class AlignStitchConfig`, không đổi sang `RdlAlignStitchOptions`.** Xem `docs/Phase1_Task01.md` mục "Đã triển khai" — tên trần trong namespace bao quanh không xung đột; mapper nội bộ (`GerberStitchFacade.BuildCoreConfig`) viết đủ tên `GerberViewer.Stitching.Models.AlignStitchConfig` khi cần chạm kiểu của Core, đúng cách `AlignStitchingControl.CloneConfigForRun` đã làm.
4. **`BuildCoreConfig` dùng đúng pattern production**, không tự chế: đặt `ConfigVersion = 3` ngay khi khởi tạo (né bẫy §2 của `docs/Phase1_Task04.md`), set `Input.ManifestPath`/`CapturedFolderPath`, ghi đè 6 trường override từ `AlignStitchConfig` façade lên cây cấu trúc, gọi `AlignStitchConfigMapper.EnsureComposite` rồi `AlignStitchConfigMapper.CloneForRun(baseConfig, outputPath)` — **`CloneForRun` đã tự đặt lại `ConfigVersion = 3` + `SyncLegacy` ở cuối**, xác nhận qua đọc trực tiếp `AlignStitchConfigMapper.cs:187-233`. Đây đúng chuỗi gọi `AlignStitchingControl.CloneConfigForRun(source, creatingDir)` dùng trong `RunWorkflowAsync`.
5. **Publish kết quả bằng `RunOutputLifecycle.Publish`/`Cleanup` có sẵn trong Core** (`Stitching/RunOutputLifecycle.cs`) — không tự viết lại pattern `.creating` → di chuyển vào folder cuối. `RunAlignStitch` tạo `outputRoot\AlignStitch_<timestamp>\.creating\`, gọi `RunAsync`, rồi `Publish` — giống hệt `finalRunDir`/`creatingDir` trong `AlignStitchingControl.RunWorkflowAsync`.

**Không triển khai trong task này** (nằm ngoài phạm vi 2 file được yêu cầu): bộ JSON `processing_report.json` chi tiết mà `AlignStitchingControl.WriteProcessingReport` ghi (130 dòng, phục vụ debug/so sánh run — thuộc về công cụ chẩn đoán của GerberViewer, không phải hợp đồng Master/Worker). `AlignStitchResult` (façade) đã đủ để Worker báo cáo về Master theo đúng field đã thiết kế ở §4.1 dưới đây.

**Sửa 1 phát hiện mới:** `GerberSampleConfig` bị trùng tên **compile-error thật** (`CS0104`), không chỉ là rủi ro lý thuyết như §3.1 mô tả — `using GerberViewer.Stitching.Models;` (cho `AlignStitchConfig`, `CapturedImageInfo`...) và tham số kiểu `Configuration.GerberSampleConfig` cùng có mặt trong `GerberStitchFacade.cs` khiến bản build đầu tiên fail ngay. Đã sửa bằng cách viết đủ tên `GerberViewer.Stitching.Configuration.GerberSampleConfig` tại điểm khai báo tham số. Tương tự, `ColorMode` cũng trùng tên giữa `GerberEngine.ColorMode` và `System.Drawing.Imaging.ColorMode` (BCL) — sửa bằng full-qualify.

---

## 0. Vấn đề

Bản trước của doc này mô tả API dựa trên **suy đoán**, tự nó ghi chú *"Bảng trên dựa trên cấu trúc branch `2026-08-04_Ver4_implement_claude`"*. Đối chiếu source thật: **4/5 đường dẫn sai**, và signature được tự nghĩ ra.

| Bản cũ ghi | Đường dẫn thật |
|---|---|
| `Core/SampleTile/SampleTileGenerator.cs` | `Core/Imaging/SampleTileGenerator.cs` |
| `Core/Alignment/Halcon/` | `Core/Matching/Halcon/` |
| `Core/Workflow/AlignStitchWorkflowService.cs` | `Core/Alignment/AlignStitchWorkflowService.cs` |
| `Core/PoseGraph/GlobalPoseGraphOptimizer.cs` | `Core/Alignment/Graph/GlobalPoseGraphOptimizer.cs` |
| `Core/Stitching/WorkflowStitchingService.cs` | ✅ đúng |

Sai lệch nghiêm trọng hơn nằm ở **mô hình dữ liệu** — xem §1.

---

## 1. Đính chính mô hình dữ liệu: `ExpectedX/Y` không nằm trên ảnh chụp

Bản cũ định nghĩa DTO `CapturedImageInfo { ImagePath, ExpectedX, ExpectedY, Row, Col }`, khớp với cách roadmap mô tả *"Master gửi ảnh đã mapping vị trí + ExpectedX/Y"*.

**Thực tế trong Core:**

```csharp
// Models/WorkflowModels.cs:160 — KHÔNG có ExpectedX/ExpectedY
public sealed class CapturedImageInfo
{
    public string FilePath { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
    public int OrderIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string NaturalSortKey { get; set; }
    public string SourceMetadata { get; set; }
    public double RobotX { get; set; }
    public double RobotY { get; set; }
    public DateTime CapturedUtc { get; set; }
    public OrderNodeState State { get; set; } = OrderNodeState.Pending;
}
```

Toạ độ kỳ vọng nằm trong **manifest**, không phải trên ảnh:

```csharp
// Models/SampleManifest.cs:35 — ExpectedX/Y là int, thuộc SampleTileInfo
public sealed class SampleTileInfo
{
    public int OrderIndex { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
    public string ExpectedPath { get; set; }
    public int ExpectedX { get; set; }
    public int ExpectedY { get; set; }
    // ... NccModelPath, ShapeModelPath, ROI, origin, SampleContentClass, thống kê gray
}
```

Và hai bên được ghép **chỉ bằng `OrderIndex`**:

```csharp
// Mapping/WorkflowImageMap.cs:34-44
var tileByOrder    = manifest.Tiles.ToDictionary(t => t.OrderIndex);
var capturedByOrder = captured.ToDictionary(c => c.OrderIndex);
// duplicate OrderIndex  -> InvalidOperationException
// thiếu captured cho tile -> InvalidOperationException
```

### 1.1. Hệ quả cho hợp đồng Master→Worker

Hợp đồng **đơn giản hơn** roadmap mô tả:

| | Roadmap mô tả | Thực tế cần |
|---|---|---|
| Master gửi | manifest + ảnh **đã kèm ExpectedX/Y** | manifest (đã chứa ExpectedX/Y) + ảnh **chỉ cần gắn `OrderIndex`** |
| Worker dựng | `CapturedImageInfo` với toạ độ | `CapturedImageInfo` với `OrderIndex` khớp manifest |

> ⚠ **Ảnh hưởng Phase 3.2 và Phase 5.2:** DTO `TaskInfo` Master→Worker **không cần** mang toạ độ per-ảnh. Chỉ cần *manifest path* + danh sách `(OrderIndex, đường dẫn ảnh)`. Ghi lại để dùng khi viết doc Phase 3/5.
>
> **Rủi ro cần chặn:** `OrderIndex` lệch giữa Master và Worker sẽ khiến ảnh bị gán sai vị trí **mà không báo lỗi** (chỉ báo lỗi khi thiếu hoặc trùng). Façade phải validate `captured.Count == manifest.Tiles.Count` và tập `OrderIndex` khớp nhau **trước** khi gọi Core.

---

## 2. ✅ Model HALCON: đã chốt — thêm cờ cho người dùng chọn

> **Trạng thái: ĐÃ TRIỂN KHAI (2026-08-07).** Phần dưới giữ lại phân tích để hiểu vì sao, và ghi cách dùng.

### 2.0. Quyết định và những gì đã sửa

Thay vì chọn cứng một trong hai phương án, **thêm một cờ cấu hình để người dùng quyết định từng lần chạy**, và **ghi lựa chọn đó vào `sample_manifest.json`** để Worker biết manifest có kèm model hay không.

**Ba file Core đã sửa:**

| File | Thay đổi |
|---|---|
| `Configuration/GerberSampleConfig.cs` | + `enum SampleModelGenerationMode { OnTheFly = 0, Pregenerate = 1 }`<br>+ `[DataMember] public SampleModelGenerationMode ModelGeneration` — default `OnTheFly` |
| `Models/SampleManifest.cs` | + `[DataMember(Order = 17, EmitDefaultValue = false)] public string ModelGeneration` |
| `Imaging/SampleTileGenerator.cs` | Bỏ comment khối sinh model, đặt sau cờ `pregenerateModels`; `BuildManifest` tra `nccMetadata` **null-safe** và ghi `ModelGeneration` vào manifest |

```csharp
// Configuration/GerberSampleConfig.cs
public enum SampleModelGenerationMode
{
    /// Không ghi .ncm/.shm. Provider tự CreateModel trong bộ nhớ lúc chạy.
    /// Mặc định — khớp các run đã kiểm chứng ở Phase 0.
    OnTheFly = 0,

    /// Phân tích nội dung tile; tile Matchable thì ghi .ncm + .shm và điền
    /// đường dẫn/ROI/origin vào manifest để Worker ReadModel thay vì CreateModel.
    Pregenerate = 1
}
```

**Ba điểm cần biết khi dùng:**

1. **Default là `OnTheFly`** — hành vi hiện hành không đổi. Manifest sinh ra vẫn để trống `NccModelPath`/`ShapeModelPath`, các trường thống kê gray = 0, `SampleContentClass` = null. Không có hồi quy với các run Phase 0.
2. **`Pregenerate` chỉ ghi model cho tile `Matchable`.** Tile `ExactZero` / `UniformNonZero` / `LowTexture` không có model — đúng ý đồ, vì tạo shape model trên vùng không có nội dung là vô nghĩa. Manifest của những tile đó vẫn để trống đường dẫn, và provider sẽ tự xử lý.
3. **Manifest cũ không có trường `ModelGeneration`** → deserialize ra `null`, hiểu là `OnTheFly`. Tương thích ngược.

**Vì sao đáng làm:** `Direct Alignment` chiếm **90% tổng thời gian** (222/247 s — `Phase0_Closeout.md` §1.1), và chi phí `CreateShapeModel` cho từng tile nằm trong đó, **lặp lại ở mọi lô**. Với `Pregenerate`, chi phí này trả một lần ở Master lúc Prepare thay vì mọi lô ở Worker.

> ⚠ **Chưa có số đo cho `Pregenerate`.** Mức tiết kiệm thật phải đo bằng harness Task 1.5 — chạy cùng dataset ở cả 2 chế độ rồi so `Direct Alignment`. Đừng giả định trước là sẽ nhanh hơn: `ReadShapeModel` từ đĩa cũng tốn thời gian, và lợi ích phụ thuộc tỉ lệ tile `Matchable` (dataset tham chiếu chỉ 54/80).

### 2.1. Hiện trạng trước khi sửa *(bối cảnh)*

Roadmap task 2.2 và 2.4 giả định Master sinh `.shm`/`.ncm` per tile rồi ghi ra ổ chia sẻ cho Worker đọc. Thực tế:

1. **Code sinh model trong Core đang bị comment.** `Imaging/SampleTileGenerator.cs:84-91` — cả khối `AnalyzeContent` / `WriteNccModel` / `WriteShapeModel` bị vô hiệu hoá. Hàm `WriteNccModel` (`:216`) và `WriteShapeModel` (`:260`) vẫn còn nhưng **không ai gọi**. Do đó `nccMetadata` luôn rỗng → `BuildManifest` sinh manifest **không có** `NccModelPath`/`ShapeModelPath`.
2. **Không có file model nào tồn tại.** Quét `result_20260807\` và `GerberViewer\`: **0** file `.shm`, **0** file `.ncm`.
3. **Pipeline vẫn chạy đúng.** Provider tự tạo model khi không có file:

```csharp
// Matching/Halcon/HalconShapeModelProvider.cs:95-109 (rút gọn)
if (!string.IsNullOrWhiteSpace(request.ReferenceShapeModelPath)
    && File.Exists(request.ReferenceShapeModelPath))
    HOperatorSet.ReadShapeModel(request.ReferenceShapeModelPath, out modelId);
else
    HOperatorSet.CreateShapeModel(/* ... tạo tại chỗ từ ảnh sample ... */);
```

**Kết luận:** run tham chiếu `110300` dùng `HalconShapeModelMatcher` cho 54 tile bằng model **tạo trong bộ nhớ lúc chạy**, không đọc file.

### 2.2. Vì sao điều này quan trọng

`Direct Alignment` chiếm **90% tổng thời gian** (222/247 s — `Phase0_Closeout.md` §1.1). Chi phí `CreateShapeModel` cho từng tile nằm trong đó và **lặp lại ở mọi lô**. Sinh model sẵn một lần ở Master chính là ý đồ tối ưu của roadmap.

### 2.3. Hai chế độ — so sánh

| | **`OnTheFly`** *(default)* | **`Pregenerate`** |
|---|---|---|
| Master sinh | Tile ảnh + manifest | Thêm `.ncm` + `.shm` cho tile Matchable |
| Worker mỗi lô | `CreateShapeModel` tại chỗ | `ReadShapeModel` từ đĩa |
| Ổ chia sẻ | Chỉ manifest + ảnh tile | Thêm ~2 file/tile Matchable |
| Manifest | Đường dẫn model = null, thống kê gray = 0 | Có đường dẫn + ROI + origin + thống kê gray |
| Trạng thái kiểm chứng | ✅ 6 run Phase 0 | ⚠ Chưa đo — xem cảnh báo §2.0 |

`GenerateSampleManifest` (§3.1) **không đổi signature** giữa 2 chế độ. Chế độ đọc từ `RdlGridConfig.ModelGeneration` và truyền xuống `GerberSampleConfig.ModelGeneration`.

---

## 3. API façade

Toàn bộ đặt trong `RDL.GerberStitch.Facade.GerberStitchFacade` (đã `public sealed` từ Task 1.1).

### 3.1. `GenerateSampleManifest` — Master gọi khi Prepare (S002)

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Master sinh sample tile + manifest từ Gerber tại Prepare, không cần biết pipeline Core.]
/// <summary>
/// Render file Gerber, cắt thành lưới sample tile, ghi manifest.
/// </summary>
/// <returns>Đường dẫn file manifest JSON đã ghi.</returns>
public string GenerateSampleManifest(
    string gerberFilePath,
    RdlGridConfig grid,
    string outputRoot,
    CancellationToken cancellationToken = default(CancellationToken))
```

**Chuỗi gọi bên trong** — 5 bước, tất cả đều là API đã tồn tại:

| # | Gọi | File Core / GerberEngine |
|---|---|---|
| 1 | `GerberEngineFacade.LoadLayer(gerberFilePath)` | `GerberEngine/GerberEngine.cs:66` |
| 2 | `GerberEngineFacade.RenderCombined(RenderOptions)` → `Bitmap` | `GerberEngine/GerberEngine.cs:123` |
| 3 | `Bitmap` → `HObject` | `Core/Imaging/ImageInterop/ImageInteropService.cs` |
| 4 | `SamplePreparationService.Prepare(hObject, GerberSampleConfig, ct)` → `PreparedSampleRun` | `Core/Imaging/PreparedSampleRun.cs:64` |
| 5 | `SampleTileGenerator.GenerateAsync(run, outputRoot, ct, progress)` → `SampleCropResult` | `Core/Imaging/SampleTileGenerator.cs:23` |

Signature thật của bước 5 — **dùng đúng, không tự chế**:

```csharp
public Task<SampleCropResult> GenerateAsync(
    PreparedSampleRun preparedRun,
    string outputRoot,
    CancellationToken cancellationToken,
    IProgress<SampleCropProgress> progress)

// SampleCropResult { string OutputDirectory; string ManifestPath; bool Completed; }
```

`GenerateSampleManifest` trả về `SampleCropResult.ManifestPath`.

> **`PreparedSampleRun` là `IDisposable`** và giữ 2 `HObject` (`SourceImage`, `ProcessedImage`). Bọc trong `using`, nếu không sẽ rò native memory HALCON qua từng lần Prepare.

**Ánh xạ `RdlGridConfig` → `GerberSampleConfig` của Core:**

> ⚠ Có **hai** class tên `GerberSampleConfig`. Cái đúng là bản trong **`Configuration/`** — đó là kiểu mà `SamplePreparationService.Prepare` nhận (`Imaging/PreparedSampleRun.cs` có `using GerberViewer.Stitching.Configuration;`). Bản trong `Models/WorkflowModels.cs:64` nghèo trường hơn và **không** dùng ở đường này.

```csharp
// Configuration/GerberSampleConfig.cs — bản ĐÚNG, kèm default thật
public sealed class GerberSampleConfig
{
    public string SourceRasterPath { get; set; }
    public string OutputDirectory { get; set; }
    public int Rows { get; set; } = 8;
    public int Columns { get; set; } = 10;                       // "Columns", không phải "Cols"
    public OrderMode CropOrder { get; set; } = OrderMode.Zigzag;
    public StartOrder StartOrder { get; set; } = StartOrder.TopLeftDown;
    public bool InvertImage { get; set; } = false;
    public double OverlapValue { get; set; } = 70;
    public OverlapUnit OverlapUnit { get; set; } = OverlapUnit.Pixel;   // Pixel | Percent
    public SamplePreprocessMode PreprocessMode { get; set; } = SamplePreprocessMode.None;
    public bool KeepAspectRatio { get; set; } = true;
    public SampleOutputFormat OutputFormat { get; set; } = SampleOutputFormat.Tiff;
    public SampleModelGenerationMode ModelGeneration { get; set; }      // ← thêm ở §2
    public string TileNamePattern { get; set; } = "Sample_R{row:00}_C{col:00}_O{order:000}";
    public int ProcessedWidth { get; set; } = 4096;
    public int ProcessedHeight { get; set; } = 4096;
}
```

**Default `Rows=8, Columns=10` = 80 tile — đúng bằng dataset tham chiếu Phase 0.** Overlap là `OverlapValue` + `OverlapUnit` (70 px), **không** phải `PxPerMm` như DTO cũ bịa ra.

### 3.1.1. Thứ tự tile — rắn bò theo cột

`StartOrder.TopLeftDown` + `CropOrder.Zigzag` (cả hai đều là default) tạo ra đường đi **rắn bò từ trên xuống theo từng cột, đảo chiều ở cột lẻ**:

```csharp
// Imaging/SampleGridGeometry.cs:97-102
bool vertical = c.StartOrder == StartOrder.TopLeftDown || c.StartOrder == StartOrder.BottomRightUp;
// vertical: duyệt cột ngoài, hàng trong
for (int cc = 0; cc < c.Columns; cc++) {
    var rows = Enumerable.Range(0, c.Rows)...;
    if (c.CropOrder == OrderMode.Zigzag && cc % 2 == 1) rows.Reverse();   // ← đảo chiều cột lẻ
    foreach (var row in rows) yield return Tuple.Create(row, col);
}
```

`OrderIndex` được gán theo đúng thứ tự duyệt này, và `ExpectedX/Y` = **toạ độ góc tile khi cắt khỏi ảnh sample** (`BuildManifest`: `ExpectedX = t.Rectangle.X`). Sau khi Direct Alignment → Neighbor Alignment → Pose Graph xong, khâu stitch dựng lại vị trí toàn cục dựa trên `ExpectedX/Y` cộng với pose đã hiệu chỉnh.

> 🔴 **Hệ quả cho harness và cho Worker:** thứ tự ảnh chụp phải khớp đường rắn bò này, **không** phải thứ tự quét hàng ngang thông thường. Sắp xếp nhầm sang row-major sẽ gán sai toàn bộ `OrderIndex` mà không có lỗi nào báo ra. Xem Task 1.5 §2.1.

### 3.2. `RunAlignStitch` — Worker gọi khi nhận lô

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Worker chạy trọn Align→PoseGraph→Stitch cho cả lô qua một lời gọi.]
public async Task<RdlStitchResult> RunAlignStitch(
    string manifestPath,
    IList<RdlCapturedTile> capturedTiles,
    RdlAlignStitchOptions options,
    string outputPath,
    IProgress<RdlStitchProgress> progress = null,
    CancellationToken cancellationToken = default(CancellationToken))
```

**Bên trong:**

1. Deserialize `SampleManifest` từ `manifestPath`.
2. **Validate** `capturedTiles` ↔ `manifest.Tiles` theo `OrderIndex` (xem §1.1) — sai thì trả `RdlStitchResult` lỗi, **không** để Core ném exception.
3. Map `RdlCapturedTile` → `CapturedImageInfo` (gán `FilePath`, `OrderIndex`, `Row`, `Column`).
4. Map `RdlAlignStitchOptions` → `AlignStitchConfig` của Core — **chi tiết ở Task 1.4**.
5. Gọi:

```csharp
// Alignment/AlignStitchWorkflowService.cs:58 — signature thật
public Task<AlignStitchWorkflowResult> RunAsync(
    AlignStitchConfig config,
    SampleManifest manifest,
    IList<CapturedImageInfo> captured,
    IProgress<WorkflowProgress> progress,
    CancellationToken cancellationToken)
```

6. Map `AlignStitchWorkflowResult` → `RdlStitchResult`.

> `RunAsync` **không** nhận `outputPath` riêng — đường dẫn output nằm trong `config.OutputPath` / `config.Output.OutputPath`. Façade nhận `outputPath` rồi gán vào config, giữ chữ ký façade dễ dùng cho Worker.
>
> Core có sẵn `AlignStitchConfigMapper.CloneForRun(source, outputPath)` (`Configuration/AlignStitchConfigMapper.cs:187`) làm đúng việc này — **dùng lại, không tự viết**.

---

## 4. DTO façade

Quy ước tiền tố `Rdl` (lý do ở Task 1.1 §2 — tránh trùng type public của Core).

```
RDL.GerberStitch/Facade/
├── GerberStitchFacade.cs          ← 2 hàm ở §3
├── RdlAlignStitchOptions.cs       ← Task 1.4
└── Models/
    ├── RdlGridConfig.cs
    ├── RdlCapturedTile.cs
    ├── RdlStitchResult.cs
    └── RdlStitchProgress.cs
```

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: DTO grid cho Master; ánh xạ 1-1 sang Configuration.GerberSampleConfig của Core.]
public sealed class RdlGridConfig
{
    public int Rows { get; set; } = 8;
    public int Columns { get; set; } = 10;
    /// <summary>Độ chồng lấn. Đơn vị theo OverlapUnit.</summary>
    public double OverlapValue { get; set; } = 70;
    /// <summary>"Pixel" | "Percent".</summary>
    public string OverlapUnit { get; set; } = "Pixel";
    /// <summary>Kích thước tile sau xử lý (px).</summary>
    public int ProcessedWidth { get; set; } = 4096;
    public int ProcessedHeight { get; set; } = 4096;
    /// <summary>Thứ tự quét. "TopLeftDown" + Zigzag = rắn bò theo cột (§3.1.1).</summary>
    public string StartOrder { get; set; } = "TopLeftDown";
    /// <summary>"Zigzag" | "Raster".</summary>
    public string CropOrder { get; set; } = "Zigzag";
    /// <summary>"OnTheFly" (default) | "Pregenerate" — xem §2.</summary>
    public string ModelGeneration { get; set; } = "OnTheFly";
}

// [Claude] [Change time: 2026-08-07] [Purpose: Ảnh chụp gắn OrderIndex — toạ độ kỳ vọng lấy từ manifest, không truyền kèm.]
public sealed class RdlCapturedTile
{
    public string FilePath { get; set; }
    /// <summary>Khoá ghép với SampleTileInfo.OrderIndex. Bắt buộc đúng.</summary>
    public int OrderIndex { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
}

public sealed class RdlStitchProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Stage { get; set; }
}
```

### 4.1. `RdlStitchResult` — chú ý cách đếm tile

```csharp
public sealed class RdlStitchResult
{
    public bool Success { get; set; }
    public string TiffPath { get; set; }
    public long ElapsedMs { get; set; }

    /// <summary>Tổng tile theo manifest. KHÔNG lấy từ TileReports.Count — xem ghi chú.</summary>
    public int TileCount { get; set; }

    /// <summary>Tile khớp bằng ảnh (PoseSource.SampleAlignment / NeighborAlignment).</summary>
    public int AlignedTileCount { get; set; }

    /// <summary>Tile sample rỗng, đặt theo grid pose danh định. KHÔNG phải lỗi.</summary>
    public int BlankTileCount { get; set; }

    /// <summary>Tile thất bại thật sự.</summary>
    public IList<RdlFailedTile> FailedTiles { get; set; } = new List<RdlFailedTile>();

    /// <summary>Mã lỗi RDL. 0 = OK, -300 = stitch fail.</summary>
    public int ErrorCode { get; set; }

    public IList<string> Warnings { get; set; } = new List<string>();
}

public sealed class RdlFailedTile
{
    public int OrderIndex { get; set; }
    public int Row { get; set; }
    public int Column { get; set; }
    public string Reason { get; set; }
}
```

**Hai quy tắc bắt buộc khi map kết quả:**

1. **`TileCount` lấy từ `manifest.Tiles.Count`**, không lấy `report.TileReports.Count`. Lý do: run tham chiếu có `TileReports` = **85** trong khi thực tế **80** tile (`Phase0_Closeout.md` §6.1 — chênh 5 chưa lý giải được). Dùng nhầm sẽ báo sai số về Master.

2. **Blank tile không phải lỗi.** Phân loại theo `PoseSource`, không theo `runStatus`:

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Tách blank hợp lệ khỏi fail thật; mọi run đều CompletedWithFallback nên không dùng runStatus để kết luận.]
foreach (var pose in report.Poses)
{
    if (pose.Source == PoseSource.SampleAlignment || pose.Source == PoseSource.NeighborAlignment)
        result.AlignedTileCount++;
    else if (pose.Source == PoseSource.BlankSampleExpectedPose)
        result.BlankTileCount++;   // hợp lệ — board có vùng trống
    else
        result.FailedTiles.Add(/* ... */);
}
```

> 🔴 Nếu coi `runStatus == CompletedWithFallback` là lỗi thì **mọi lô đều báo fail** — cả 6 run Phase 0 đều trả trạng thái này (`Phase0_Closeout.md` §4.2).

---

## 5. Xử lý lỗi

- `GenerateSampleManifest`: lỗi parse Gerber / prepare / crop → ném `GerberStitchException` (exception mới, đặt trong `Facade/`). Master bắt để trả `S002-999`.
- `RunAlignStitch`: **không ném** ra ngoài. Mọi lỗi gói trong `RdlStitchResult` (`Success=false`, `ErrorCode=-300`, `FailedTiles`). Worker chạy nền, exception thoát ra sẽ khó truy vết.
- `OperationCanceledException` do `CancellationToken`: cho phép ném ra — đây là hành vi mong đợi khi `StopAsk`, không phải lỗi.

---

## 6. Danh sách file thay đổi

| File | Thay đổi |
|---|---|
| `RDL.GerberStitch/Facade/GerberStitchFacade.cs` | + `GenerateSampleManifest`, + `RunAlignStitch` |
| `RDL.GerberStitch/Facade/GerberStitchException.cs` | **mới** |
| `RDL.GerberStitch/Facade/Models/RdlGridConfig.cs` | **mới** |
| `RDL.GerberStitch/Facade/Models/RdlCapturedTile.cs` | **mới** |
| `RDL.GerberStitch/Facade/Models/RdlStitchResult.cs` | **mới** (+ `RdlFailedTile`) |
| `RDL.GerberStitch/Facade/Models/RdlStitchProgress.cs` | **mới** |
| `RDL.GerberStitch/Internal/CoreResultMapper.cs` | **mới** — map `AlignStitchWorkflowResult` → `RdlStitchResult` (§4.1) |
| `RDL.GerberStitch/RDL.GerberStitch.csproj` | + `<Compile Include>` cho các file trên |

**Đã sửa trong `GerberStitching.Core` theo quyết định §2 (2026-08-07):**

| File | Thay đổi |
|---|---|
| `Configuration/GerberSampleConfig.cs` | + `enum SampleModelGenerationMode`; + property `ModelGeneration` (default `OnTheFly`) |
| `Models/SampleManifest.cs` | + `ModelGeneration` (Order 17, `EmitDefaultValue = false`) |
| `Imaging/SampleTileGenerator.cs` | Kích hoạt sinh model sau cờ `pregenerateModels`; `BuildManifest` tra metadata null-safe + ghi `ModelGeneration` |

**Không đụng:** `GerberEngine`, và mọi file Core khác.

---

## 7. Tiêu chí nghiệm thu

1. Build sạch `Debug|x64` và `Release|x64`.
2. Từ console project: gọi được cả 2 hàm façade, **không** cần `using GerberViewer.Stitching.*`.
3. `GenerateSampleManifest` với 1 file Gerber thật → ra thư mục tile + `SampleManifest.json` đọc được, `manifest.Tiles.Count == Rows * Columns`.
4. `RunAlignStitch` với manifest + 80 ảnh của dataset Phase 0 → `Stitched.tiff` **giống kết quả run `110300`** (so kích thước file và `maxMatrixResidual`).
5. `RdlStitchResult` của run đó cho: `TileCount = 80`, `BlankTileCount = 26`, `AlignedTileCount = 59`, `FailedTiles.Count = 0`, `Success = true`, `ErrorCode = 0`.
6. Truyền `capturedTiles` thiếu 1 `OrderIndex` → trả `Success=false` kèm message rõ, **không** ném exception.
7. Truyền `capturedTiles` trùng `OrderIndex` → tương tự mục 6.
8. Chạy 2 lần liên tiếp trong cùng process → RAM không tăng tích luỹ (chứng minh `PreparedSampleRun`/`HObject` được dispose).
9. **Chế độ model (§2):**
   - `ModelGeneration = "OnTheFly"` → thư mục tiles **không** có `.ncm`/`.shm`; manifest có `"modelGeneration": "OnTheFly"`, mọi `nccModelPath`/`shapeModelPath` = null. Kết quả stitch **giống hệt** run `110300`.
   - `ModelGeneration = "Pregenerate"` → tile `Matchable` có đủ `.ncm` + `.shm`; manifest điền đường dẫn + ROI + `sampleContentClass`. Số file model = số tile Matchable (dataset tham chiếu: **54**, không phải 80).
   - Manifest cũ (không có trường `modelGeneration`) vẫn đọc được, hiểu là `OnTheFly`.
10. So `Direct Alignment` giữa 2 chế độ trên **cùng** dataset — ghi lại con số. Đây là dữ liệu chưa ai có (§2.0).

---

## 8. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| **§2 chưa quyết** → không code được phần model của `GenerateSampleManifest` | Làm theo phương án A trước; signature không đổi khi chuyển B |
| `OrderIndex` lệch → ảnh gán sai vị trí, **không có lỗi báo ra** | Validate ở façade (§3.2 bước 2), không phó mặc Core |
| Dùng `TileReports.Count` làm `TileCount` → sai số báo về Master | §4.1 quy tắc 1; nghiệm thu mục 5 chốt con số |
| Coi mọi fallback pose là lỗi → mọi lô báo fail | §4.1 quy tắc 2; phân loại theo `PoseSource` |
| Rò native memory HALCON qua `PreparedSampleRun` | `using`; nghiệm thu mục 8 |
| `RdlGridConfig` đặt sai trường → phải bịa công thức quy đổi | §3.1 — bám đúng `GerberSampleConfig`; chờ blocker 4 để có giá trị thật |
