# Thiết kế: Crop sample trong RAM + File setup tập trung

- **Ngày:** 2026-08-14
- **Phạm vi:** `RDL.GerberStitch` (façade) + `GerberStitching.Core` + `RDL_Worker/GerberAlignStitch`
- **Không đụng tới:** RDL_Master2 (payload `GerberCommonFileInfo` đã đủ dữ liệu, không cần đổi)

---

## 1. Vấn đề

Cơ chế hiện tại chia làm hai pha tách rời:

1. **Prepare/Crop** — Worker nhận `GerberSampleImagePath` + `GerberTiles[]` từ Master, gọi
   `GerberStitchFacade.GenerateSampleManifestFromRects`, cắt ảnh sample lớn thành 80 file
   `tiles/*.tiff` trên đĩa cộng `sample_manifest.json` và `processed_sample.tiff`.
2. **Align + Stitch** — `AlignStitchWorkflowService.RunAsync` đọc lại từng file tile từ đĩa qua
   `WorkflowImageCache.GetMono8(tile.ExpectedPath)`.

Bốn vấn đề đo được:

- **Ghi thừa 2.6 GB mỗi lần prepare.** `processed_sample.tiff` (1.31 GB) là bản copy y hệt ảnh
  gốc vì `PreprocessMode=None`, và **không có call site nào đọc lại nó** — chỉ được ghi tên vào
  manifest. Cộng thêm 80 tile (~1.31 GB) chỉ để pha sau đọc ngược lên RAM.
- **Cache đĩa không invalidate đúng.** `GerberStitchRunner.TryReuseManifest` chỉ so **số lượng
  tile** và sự tồn tại của file; nó **không so `ExpectedX/Y/Width/Height`**. Nếu Master đổi
  overlap mà số tile không đổi, lô sau sẽ tái dùng bộ tile cũ sai toạ độ, âm thầm, không báo lỗi.
  (Hiện `TryReuseManifest` chưa được kích hoạt trong Worker, nhưng khiếm khuyết vẫn nằm đó.)
- **Round-trip đĩa vô nghĩa.** Pixel đi từ RAM → đĩa → RAM mà không hề biến đổi.
- **Hai bản code đã lệch nhau** (xem §2).

## 2. Bước 0 — Hợp nhất hai bản code (điều kiện tiên quyết)

Bản `RDL.GerberStitch` + `GerberStitching.Core` nằm trong
`RDL_WorkerNorthT_Ver2_SoftMerge_2026_08_09` đã **fork khỏi repo này** và đi trước:

| Có ở Worker, thiếu ở repo này | Có ở repo này, thiếu ở Worker |
|---|---|
| `GerberStitchFacade.GenerateSampleManifestFromRects` | `AlignStitchConfig.CalculateTimeDetail` |
| `Facade/TileRect` | |
| `AlignStitchConfig.EmitDebugPreview` | |
| `Diagnostics/DebugPreviewWriter.cs` | |
| `ProcessingReport.DebugPreviewPath`, `AlignStitchConfig.EmitDebugPreview` (Core) | |
| `SampleGeometryCalculator.FromExplicitRects` | |

Gần như mọi file trong `GerberStitching.Core` đều khác nhau (đã diff bỏ CRLF).

**Bước 0 phải hoàn thành trước mọi việc khác:** merge chiều Worker → repo này, hợp nhất với
`CalculateTimeDetail`, build sạch ở x64. **Không đổi logic trong bước này** — chỉ hợp nhất. Sau
bước 0, repo `RDL.GerberStitch` là source of truth duy nhất; Worker reference DLL sinh ra từ đó.

## 3. Phần A — Crop sample trong RAM

### 3.1. Ràng buộc kỹ thuật đã xác minh

- **OpenCV không đọc được ảnh sample lớn.** `Cv2.ImRead` giới hạn 2³⁰ = 1 073 741 824 pixel;
  ảnh sample tham chiếu 40418 × 32364 = **1 308 090 552 pixel**, vượt ngưỡng. Việc đọc ảnh lớn +
  crop **bắt buộc** phải làm bằng HALCON. OpenCV chỉ nhận được các mảnh 4096² sau khi crop.
- **`Mat` là kiểu ảnh chung của toàn pipeline.** `MatchRequest.ReferenceImage`/`MovingImage` là
  `OpenCvSharp.Mat`; `SampleTileContentAnalyzer` và `CalculateSampleOverlapMetrics` cũng ăn `Mat`.
  Ngay cả matcher HALCON cũng tự convert `Mat` → `HImage` bên trong. Vì vậy tile crop được lưu
  dưới dạng **`Mat`**, không phải `HImage`/`HTuple` — lưu `HImage` sẽ buộc convert lại ở mỗi
  call site (4 chỗ × 80 tile × 16 MB copy), hoàn toàn thừa.
- **Crop cho pixel y hệt tile trên đĩa.** `PreprocessMode=None` trong đường FromRects, và
  `WorkflowImageCache.GetMono8` gọi `IncreaseContrast(image, 100d)` — hàm này `return` ngay khi
  `contrastPercent == 100`. Không có phép biến đổi nào giữa "crop" và "tile đọc từ đĩa".
- **Sample tile được dùng ở 4 chỗ, không phải chỉ Direct Alignment.** Cụ thể trong
  `AlignStitchWorkflowService.cs`: `:520` (nhánh aligner), `:564` (nhánh MatcherFactory),
  `:946` `CalculateSampleOverlapMetrics` — **nằm trong Neighbor Recovery**, `:1354`
  `GetSampleContent` (phân loại blank/low-texture). Vì vậy tile crop phải sống đến hết pha
  Recovery, không được thả sau Direct.

### 3.2. Trừu tượng mới

```csharp
internal interface ISampleTileSource : IDisposable
{
    Mat GetTile(int orderIndex);   // Mat mono8 của tile mẫu
}
```

Hai hiện thực:

| Impl | Dùng bởi | Hành vi |
|---|---|---|
| `DiskSampleTileSource` | GerberViewer Tab 2/3, đường tương thích ngược | Đọc `tile.ExpectedPath`; chính là `WorkflowImageCache` hôm nay, đổi tên/bọc lại |
| `InMemorySampleTileSource` | **Worker — mặc định** | Crop từ ảnh sample lớn, không chạm đĩa |

`AlignStitchWorkflowService` nhận `ISampleTileSource` qua constructor. Bốn call site nêu ở §3.1
đổi từ `_imageCache.GetMono8(tile.ExpectedPath)` sang `_tileSource.GetTile(tile.OrderIndex)`.

### 3.3. `InMemorySampleTileSource` — luồng thực thi

1. `HOperatorSet.ReadImage` ảnh sample lớn (HALCON, ~1.3 GB thường trú).
2. Lặp qua `TileRect[]` theo `OrderIndex`: `crop_rectangle1` → convert sang `Mat` mono8 qua
   `ImageInteropService` → nạp vào `Dictionary<int, Mat>`. `HImage` crop được dispose ngay sau
   khi convert.
3. **Dispose ảnh sample lớn ngay khi vòng crop kết thúc** — trước khi pha Align bắt đầu.
4. `Dispose()` cuối lô thả toàn bộ `Mat`.

Chọn crop **eager** (cắt hết một lượt) thay vì **lazy** (giữ ảnh lớn, crop khi cần) vì lazy buộc
giữ 1.3 GB ảnh lớn thường trú suốt Align+Recovery, cộng thêm cache tile — vượt ràng buộc peak.

**Không cache giữa các lô.** Mọi thứ được thả trong `finally` của `RunBatchAsync`, khớp với
`ReleaseMemory()` hiện có. Đọc lại 1.3 GB mỗi lô rẻ hơn nhiều so với việc sinh thêm một lớp
invalidate mới — chính lớp đó là nguồn của khiếm khuyết `TryReuseManifest`.

### 3.4. Hồ sơ bộ nhớ

| Giai đoạn | Hiện tại | Sau thay đổi |
|---|---|---|
| Prepare | HImage lớn 1.3 GB + ghi 2.6 GB ra đĩa | — (bỏ) |
| Crop | — | HImage lớn 1.3 GB **+** Mat crop tăng dần → đỉnh tức thời ~2.6 GB, rồi rơi về 1.3 GB |
| Align + Recovery | Mat cache 1.3 GB (tích dần khi đọc từ đĩa) | Mat 1.3 GB (đã có sẵn từ pha crop) |
| Stitching | **peak toàn cục 7.18 GB** | không đổi |

Đỉnh tức thời 2.6 GB ở pha crop xảy ra **trước** pha stitching, nên không đẩy peak toàn cục lên —
peak vẫn do stitching quyết định. Ràng buộc "không vượt peak hiện tại" được thoả.

### 3.5. Những gì bị bỏ

- `processed_sample.tiff` — 1.31 GB, bản copy không ai đọc (`SampleTileGenerator.cs:463`).
- 80 file `tiles/*.tiff` — 1.31 GB.
- **Toàn bộ `GerberStitchRunner.TryReuseManifest`** — không còn cache đĩa thì không còn bài toán
  invalidate, và khiếm khuyết "Master đổi overlap nhưng vẫn dùng tile cũ" biến mất theo.

`GenerateSampleManifestFromRects` và `SampleTileGenerator` **được giữ nguyên**, không bị xoá —
chúng là đường của `DiskSampleTileSource`, GerberViewer Tab 2/3, và chế độ debug (§3.6).

### 3.6. Chế độ debug

Khi `DebugMode=true` (đã có sẵn trong payload Master, đi qua `EmitDebugPreview`),
`InMemorySampleTileSource` ghi thêm **toàn bộ** tile crop ra đĩa cộng `sample_manifest.json`
(với `ExpectedPath` trỏ tới file vừa ghi). Đây là đường phụ, không phải đường chính — kết quả
align không phụ thuộc vào nó.

### 3.7. Rủi ro đã nhận diện

- **`ModelGeneration = Pregenerate` không tương thích.** `SampleTileInfo` mang
  `NccModelPath`/`ShapeModelPath` (model HALCON tiền sinh, gắn với file tile trên đĩa). Hiện
  Worker dùng `OnTheFly` nên hai trường này null và không ảnh hưởng. `InMemorySampleTileSource`
  phải **chặn tường minh** bằng thông báo lỗi rõ ràng khi gặp `Pregenerate`, thay vì để null-ref
  ở giữa lô.
- **`SampleManifest.Validate(requireFiles: true)`** kiểm tra `ExpectedPath` tồn tại trên đĩa
  (`Models/SampleManifest.cs:221-225`). Đường in-memory phải đi qua nhánh `requireFiles: false`,
  hoặc validate rect thay vì validate file.
- **Aliasing.** Tile crop được lưu là `Mat` **copy** (không phải ROI view của ảnh cha), vì
  `ModalityAwarePreprocessor` có các bước mutate ảnh. Copy giữ đúng ngữ nghĩa hiện tại và không
  làm tăng bộ nhớ so với cache hôm nay.
- **HALCON license.** Không thêm call site HALCON mới ngoài `read_image`/`crop_rectangle1` —
  cùng nhóm operator mà `GenerateSampleManifestFromRects` đã dùng. Repo vẫn chưa có gate license
  tập trung (AGENTS.md §3.3); giữ nguyên mẫu try/catch quanh `HalconImageInteropException`.

## 4. Phần B — File setup tập trung

### 4.1. Mục tiêu

Đưa toàn bộ ~118 tham số của pipeline ra một file JSON có chú thích tiếng Anh, thay cho 8 khoá
ini hiện tại.

Kiểm đếm thực tế từ code, **sau khi tách matcher ra registry riêng** (§4.3):

| Khối | Số tham số |
|---|---|
| `Matchers` — `HalconShapeModel` 19, `HalconNcc` 10, `PyramidEcc` 5, `PyramidPhaseCorrelation` 2 | ~36 |
| `DirectAlignment` (`Geometry` 6, `Policy` 2, `Evaluation` 14, pose-correction 4, 2 tham chiếu matcher, còn lại 3) | ~31 |
| `NeighborAlignment` (`Acceptance` 5, ngưỡng 5, 1 tham chiếu matcher) | 11 |
| `Recovery` (chỉ cờ policy) | 10 |
| `PoseGraph` | 19 |
| `Stitching` | 8 |
| `Output` (bỏ `OutputPath`) | 5 |
| **Tổng** | **~120** |

### 4.2. Vị trí và tầng ưu tiên

- **File chung toàn trạm:** `GerberAlignStitch.settings.json` cạnh Worker exe. Đường dẫn ghi đè
  được bằng khoá ini `[GerberAlignStitch] SettingsPath`.
- **File override theo recipe:** tuỳ chọn, đặt trong thư mục recipe. Chỉ chứa các khoá muốn đè;
  khoá vắng mặt lấy từ file chung.

Thứ tự áp dụng, sau thắng trước:

```
mặc định code  <  file chung  <  file override recipe  <  ini [GerberAlignStitch]  <  payload Master
```

### 4.3. Registry matcher — matcher tách riêng, stage tham chiếu theo tên

Cấu trúc slot hiện tại của Core lặp lại cùng một kiểu options ở nhiều stage, và có một chỗ đọc
chéo stage:

| Stage | Slot options matcher trong Core |
|---|---|
| `DirectAlignment` | `Geometry`, `Ncc`, `Shape`, `Ecc` |
| `NeighborAlignment` | `Geometry` (dựng từ `Acceptance`), `Phase`, `Ecc` *(obsolete, bị bỏ qua)* |
| `Recovery` | **không có** — dùng chung matcher của Neighbor |
| `PoseGraph` | không có, nhưng **đọc `NeighborAlignment.Phase.MinResponse`** (`GlobalPoseGraphOptimizer.cs:76-77`) để tính trọng số cạnh |

Vì vậy file tách matcher thành một registry riêng, **key theo loại matcher — mỗi loại đúng một bộ
tham số dùng chung cho mọi stage**; stage chỉ gọi tên loại:

```jsonc
// Matcher definitions. Each entry is one matcher kind, configured once and
// shared by every stage that references it below.
"Matchers": {
  "HalconNcc":        { "MinScore": 0.10, "NumLevels": 3, /* …10 params… */ },
  "HalconShapeModel": { "MinScore": 0.10, "SearchNumLevels": 5, "Greediness": 0.95, /* …19 params… */ },
  "PyramidEcc":       { "MinCorrelation": 0.13, "PyramidLevels": 3, /* …5 params… */ },
  "PyramidPhaseCorrelation": {
    // NOTE: PoseGraph also reads MinResponse from this matcher to weight its
    // edges. Changing it affects both neighbor matching and pose-graph solving.
    "MinResponse": 0.15, "PyramidLevels": 3
  }
},

"DirectAlignment":   { "CoarseMatcher": "HalconShapeModel", "RefinementMatcher": "PyramidEcc", /* geometry, policy, evaluation… */ },
"NeighborAlignment": { "CoarseMatcher": "PyramidPhaseCorrelation", /* acceptance, thresholds… */ },
"Recovery":          { /* policy flags only — matching runs on NeighborAlignment.CoarseMatcher */ }
```

**Tên entry trùng đúng tên hằng enum của Core** (`HalconNcc`, `HalconShapeModel`, `PyramidEcc`,
`PyramidPhaseCorrelation` — xem `DirectCoarseMatcherKind`, `MatcherKind`). Nhờ vậy giá trị mà
stage khai báo **chính là** giá trị enum cần gán, không có bảng dịch tên nào ở giữa.

**Loader phân giải theo 2 bước:** gán enum của stage từ tên → copy bộ tham số của loại đó vào
**mọi** slot Core tương ứng. Ví dụ `PyramidEcc` được copy vào cả `DirectAlignment.Ecc` lẫn
`NeighborAlignment.Ecc`; `PyramidPhaseCorrelation` copy vào `NeighborAlignment.Phase`.

Cấu trúc key-theo-loại **triệt tiêu** ràng buộc "hai matcher cùng loại trong một stage" — điều đó
không còn biểu diễn được. Hai ràng buộc còn lại loader vẫn phải cưỡng chế, báo lỗi rõ ràng thay
vì im lặng rơi về mặc định:

1. **Tên loại phải hợp lệ với vị trí.** `DirectAlignment.RefinementMatcher` chỉ nhận `PyramidEcc` /
   `PyramidPhaseCorrelation` / `None` (`DirectRefinementMatcherKind`); `NeighborAlignment.CoarseMatcher`
   chỉ nhận `PyramidPhaseCorrelation` / `None` (`NeighborCoarseMatcherKind`).
2. **Tên loại phải có mặt trong `Matchers`** — một stage trỏ tới loại chưa được định nghĩa là lỗi
   khi load.

**Đánh đổi đã chấp nhận:** một loại matcher chỉ có một bộ tham số cho toàn pipeline. Không thể đặt
`PyramidEcc` của Direct khác `PyramidEcc` dùng ở chỗ khác. Đây là chủ ý — đổi lại là không còn khả
năng để hai bộ tham số cùng loại lệch nhau mà không ai nhận ra.

**Recovery không có matcher riêng.** Cả pha khôi phục lỗi lẫn pha reconcile toàn cục đều đi qua
cùng một hàm `AlignStitchWorkflowService.MatchNeighborEdge`, gọi
`AlignStitchConfigMapper.ToNeighborMatcherOptions(config)`. Khối `Recovery` trong file chỉ chứa cờ
policy (`RecoverFailedTiles`, `ReconcileAllMatchedTiles`, …) quyết định **chạy cho tile nào**,
không quyết định dùng matcher gì. Chú thích trong file phải ghi rõ điều này. **Không có thay đổi
hành vi nào trong `GerberStitching.Core`** cho hạng mục config.

**`PoseGraph` cũng không có matcher riêng.** Nó đọc `MinResponse` từ
`NeighborAlignment.CoarseMatcher` (`GlobalPoseGraphOptimizer.cs:76-77`). Coupling này chỉ được
**ghi chú** ở cả hai chỗ trong file (entry `PyramidPhaseCorrelation` và khối `PoseGraph`), không
gỡ bằng code.

### 4.4. Cấu trúc file

Ngoài registry `Matchers` ở §4.3, phần còn lại giữ cấu trúc JSON **lồng nhau khớp 1-1** với cây
options của Core (`DirectAlignment.Geometry.MaxAbsRotationDeg` →
`"DirectAlignment": { "Geometry": { "MaxAbsRotationDeg": … } }`), deserialize thẳng bằng
Newtonsoft. Ngoài bước phân giải tên matcher ở §4.3, không có tầng mapping phẳng → lồng nào khác.

File chia **hai khối trong cùng một file**. Cả `Matchers` lẫn các stage đều có mặt ở cả hai khối:

- `"CommonlyTuned"` — khối trên, ~20 khoá hay chỉnh nhất (`MinScore` của matcher coarse,
  `MinCorrelation` của matcher refinement, `Geometry.MaxTranslationPixels`,
  `Geometry.MaxAbsRotationDeg`, `Policy.*`, `Stitching.Engine`,
  `Recovery.LowTextureStdDevThreshold`, `PoseGraph.Enabled`, …).
- `"Advanced"` — khối dưới, ~100 khoá còn lại, gồm định nghĩa đầy đủ của từng loại matcher.

**Quy tắc hợp nhất:** deep-merge `Advanced` trước, rồi `CommonlyTuned` đè lên. Các tham chiếu
matcher của stage (`CoarseMatcher`, `RefinementMatcher`) khai báo ở `Advanced`. Nếu một khoá xuất
hiện ở cả hai khối, `CommonlyTuned` thắng và ghi warning vào log.

Mỗi tham số kèm chú thích tiếng Anh gồm: **ý nghĩa**, **ảnh hưởng khi tăng/giảm**, và **giá trị
mặc định của Core** khi giá trị RDL khác nó. Ví dụ:

```jsonc
{
  // Pinned to 3. Do not change: ConfigVersion 0/1 triggers a legacy migration
  // that overwrites the structured groups below with flat legacy defaults.
  "ConfigVersion": 3,

  "CommonlyTuned": {
    "Matchers": {
      // Minimum HALCON shape-model match score. Do NOT set this to 0.7: that is
      // 5-7x the real default and rejects almost every tile.
      "HalconShapeModel": { "MinScore": 0.10 },
      "PyramidEcc":       { "MinCorrelation": 0.13 }
    },
    "DirectAlignment": {
      "Geometry": {
        // Reject a direct match whose translation exceeds this many pixels.
        // Raise it if stage placement is loose; lower it to reject
        // wrong-but-high-score matches earlier.
        "MaxTranslationPixels": 300,

        // RDL production value. Core default is 0.5 -- the RDL line has almost
        // no real rotational deviation, so 0.1 rejects bad matches earlier.
        "MaxAbsRotationDeg": 0.1
      }
    }
  },

  "Advanced": {
    "Matchers": {
      "HalconShapeModel": { "SearchNumLevels": 5, "Greediness": 0.95, /* …17 more… */ },
      "PyramidEcc":       { "PyramidLevels": 3, "MaxIterations": 80, /* …2 more… */ }
      /* HalconNcc, PyramidPhaseCorrelation… */
    },
    "DirectAlignment": { "CoarseMatcher": "HalconShapeModel", "RefinementMatcher": "PyramidEcc", /* … */ }
    /* NeighborAlignment, Recovery, PoseGraph, Stitching, Output… */
  }
}
```

### 4.5. Bốn cái bẫy mà file phải xử lý

1. **Field phẳng legacy không được có trong file.** `AlignStitchConfig` còn ~30 field phẳng
   (`NccMinScore`, `EnableNeighborRecovery`, …) tồn tại để migrate. `EnsureComposite` với
   `ConfigVersion` 0/1 sẽ lấy chúng **đè lên** tầng structured. File chỉ phơi tầng structured và
   ghim `ConfigVersion: 3`. Loader phải từ chối (lỗi rõ ràng) nếu file chứa một field phẳng.

2. **Field obsolete bị loại khỏi file, không phải chỉ chú thích.**
   `NeighborAlignment.RefinementMatcher` và `NeighborAlignment.Ecc` được đánh dấu
   `[Obsolete("deserialize-only and ignored")]` — Neighbor chạy phase-only. Để chúng trong file
   thì người vận hành sẽ chỉnh và không hiểu vì sao không có tác dụng. Hệ quả cho registry:
   `NeighborAlignment` chỉ có `CoarseMatcher`, không có khoá `RefinementMatcher`.

3. **Giá trị ship kèm là giá trị RDL production, không phải default của Core.** Hai chỗ đã lệch
   sẵn: `Stitching.EnableBlending` — Core `true`, RDL `false`; `Geometry.MaxAbsRotationDeg` —
   Core `0.5`, RDL `0.1`. Chú thích phải ghi rõ Core default là gì.

4. **Đường dẫn per-lô không nằm trong file.** `Input.ManifestPath`, `Input.CapturedFolderPath`,
   `Output.OutputPath` do Master cấp theo từng lô.

### 4.6. Chống mù với 5 tầng ưu tiên

Bắt buộc kèm hai cơ chế, nếu không thì 5 tầng là bài toán không debug được:

- **Log dump lúc bắt đầu lô** — in mọi giá trị **khác mặc định code**, kèm tầng đã set nó:
  ```
  [Gerber] DirectAlignment.Geometry.MaxAbsRotationDeg = 0.1     [file chung]
  [Gerber] Matchers.HalconShapeModel.MinScore         = 0.15    [ini]
  [Gerber] Stitching.Engine = HalconProjectiveMosaicRebased     [mặc định]
  ```
  Kèm một dòng ánh xạ loại matcher → stage ở đầu block
  (`HalconShapeModel -> DirectAlignment.CoarseMatcher`,
  `PyramidPhaseCorrelation -> NeighborAlignment.CoarseMatcher + Recovery + PoseGraph`) để tra
  ngược được về slot Core khi đối chiếu với `Debug_<date>.html`.
- **Cảnh báo khoá lạ** — khoá sai chính tả trong JSON ra warning vào log Worker, theo đúng mẫu
  `AlignStitchConfigIniReader.ReadFromIni(path, warningSink)` đang làm với ini.

### 4.7. File này chỉ được đọc, không bao giờ được ghi

Newtonsoft **đọc** được comment `//` và `/* */` nhưng **ghi** thì không giữ được — một lần
serialize ngược là mất sạch chú thích. Vì vậy chương trình tuyệt đối không ghi đè file setup.

Để phục vụ truy vết, ghi `effective_config.json` (không comment, giá trị đã hợp nhất đủ 5 tầng)
vào thư mục `AlignStitch_<timestamp>` của từng lô. Core đã có sẵn `EffectiveConfigSnapshot`.

## 5. Tiêu chí nghiệm thu

Chạy lại đúng bộ dữ liệu của lô tham chiếu `AlignStitch_20260813_154108` (80 tile, 54 aligned,
26 blank, 0 failed, 319.5 s):

1. **Bắt buộc — pose từng tile trùng bit-by-bit.** So bảng `Dx` / `Dy` / `DAngle` của từng tile
   trong `Debug_<date>.html` cũ và mới. Phải trùng tuyệt đối, vì crop cho pixel y hệt tile đọc
   từ đĩa (§3.1). Bất kỳ sai lệch nào cũng là bug, không phải nhiễu số.
2. `AlignedTileCount = 54`, `BlankTileCount = 26`, `FailedTileCount = 0`, `ErrorCode = 0`.
3. `PeakWorkingSetMB` **không vượt** 7 185 MB của lô tham chiếu.
4. Không còn `processed_sample.tiff` và thư mục `tiles/` khi `DebugMode=false`.
5. Tổng thời gian lô giảm (kỳ vọng: bằng phần thời gian prepare đã bỏ).
6. **Registry matcher phân giải đúng.** Đổi `MinScore` trong `Matchers.HalconShapeModel` → giá trị
   phải xuất hiện ở đúng `DirectAlignment.Shape.MinScore` trong `effective_config.json`. Đổi
   `Matchers.PyramidEcc.PyramidLevels` → phải xuất hiện ở **cả** `DirectAlignment.Ecc` lẫn
   `NeighborAlignment.Ecc`. Trỏ `NeighborAlignment.CoarseMatcher` tới `HalconNcc` → loader báo lỗi
   rõ ràng (không hợp lệ với vị trí), không chạy lô.
7. File setup: đổi `MaxAbsRotationDeg` trong file chung → log dump phản ánh đúng giá trị mới và
   ghi nguồn `[file chung]`; đặt cùng khoá trong ini → log dump ghi `[ini]` và giá trị ini thắng.

Việc chạy kiểm thử do user thực hiện (AGENTS.md §4).

## 6. Ghi log triển khai

Theo CLAUDE.md, mỗi bước thực thi có sửa `.cs`/`.csproj` phải **thêm một entry mới** vào
`docs/implement_code.html` (không ghi đè entry cũ): ngày, phạm vi, file đã đổi, đánh đổi thiết kế,
và mọi lỗi build/chạy kèm cách fix.

## 7. Thứ tự thực hiện

| # | Việc | Phụ thuộc |
|---|---|---|
| 0 | Merge Worker → repo này, build sạch x64 | — |
| 1 | Bỏ `processed_sample.tiff` (thắng nhanh, độc lập) | 0 |
| 2 | `ISampleTileSource` + `DiskSampleTileSource`, chuyển 4 call site sang interface | 0 |
| 3 | `InMemorySampleTileSource` + đường debug ghi tile | 2 |
| 4 | Overload façade `RunAlignStitch(sampleImagePath, TileRect[], …)` | 3 |
| 5 | Worker: bỏ Stage 1.5 và `TryReuseManifest`, gọi overload mới | 4 |
| 6 | Loader file setup JSON: registry `Matchers` + phân giải loại → slot, tầng ưu tiên, log dump, cảnh báo khoá lạ | 0 |
| 7 | Sinh file `GerberAlignStitch.settings.json` đầy đủ chú thích | 6 |

Toàn bộ kế hoạch **không có thay đổi hành vi nào trong `GerberStitching.Core`** — hạng mục config
chỉ là một tầng đọc/ánh xạ mới đặt trước cây options sẵn có, và phần A giữ nguyên pixel đầu vào
của mọi matcher. Đó là lý do tiêu chí nghiệm thu §5.1 (pose trùng bit-by-bit) áp dụng được cho cả
hai phần.

Hai nhánh 1–5 (crop) và 6–7 (config) chỉ chung phụ thuộc ở bước 0, làm song song được.

**Bước 2 là điểm kiểm tra an toàn:** nó giữ hành vi **không đổi gì** (vẫn đọc tile từ đĩa, chỉ
chuyển 4 call site sang interface). Nếu bước 2 làm lệch pose thì lỗi nằm ở refactor interface,
không phải ở cơ chế crop mới.
