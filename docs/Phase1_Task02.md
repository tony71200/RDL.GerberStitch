# Phase 1 — Task 1.2: Façade API tối thiểu

**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1.5
**Phụ thuộc:** Task 1.1 (project `RDL.GerberStitch` đã build)

---

## Mục tiêu

Viết 2 hàm façade chính trong `RDL.GerberStitch.Facade.GerberStitchFacade`:

1. **`GenerateSampleManifest`** — Master gọi khi Prepare (S002)
2. **`RunAlignStitch`** — Worker gọi khi nhận lô

Cả 2 hàm bọc logic nội bộ của `GerberStitching.Core`, Master/Worker không cần biết pipeline bên trong.

---

## Nơi tạo file

```
RDL.GerberStitch/
└── Facade/
    ├── GerberStitchFacade.cs        ← file chính task này
    ├── Models/
    │   ├── GridConfig.cs            ← DTO grid config
    │   ├── CapturedImageInfo.cs     ← DTO ảnh + toạ độ
    │   └── StitchResult.cs          ← DTO kết quả
    └── AlignStitchConfig.cs         ← (Task 1.4)
```

---

## API Signature

### 1. `GenerateSampleManifest` — dùng ở Master

```csharp
/// <summary>
/// Sinh SampleManifest + HALCON models (.shm/.ncm) từ file Gerber.
/// Master gọi hàm này tại Prepare (S002).
/// </summary>
/// <param name="gerberFilePath">Đường dẫn file Gerber (.gbr/.ger)</param>
/// <param name="gridCfg">Cấu hình grid: rows, cols, overlap, px/mm</param>
/// <param name="outputDir">Folder output ghi manifest + model files</param>
/// <returns>Đường dẫn file manifest JSON đã ghi</returns>
public string GenerateSampleManifest(
    string gerberFilePath,
    GridConfig gridCfg,
    string outputDir)
```

**Bên trong bọc:**
- `SampleTileGenerator` (từ Core) → sinh sample tiles từ Gerber
- Tạo HALCON NCC/Shape model per tile → ghi `.shm` / `.ncm` vào `outputDir`
- Serialize `SampleManifest` ra JSON → ghi vào `outputDir`
- Return đường dẫn manifest JSON

**Đầu vào:**

| Param | Mô tả | Ví dụ |
|---|---|---|
| `gerberFilePath` | File Gerber gốc | `Z:\Recipe\BoardA\design.gbr` |
| `gridCfg` | Grid config | `{ Rows=8, Cols=10, OverlapPx=50, PxPerMm=25.4 }` |
| `outputDir` | Folder output trên ổ chia sẻ | `Z:\GerberStitch\BatchXXX\manifest\` |

**Đầu ra:**

| Item | Vị trí |
|---|---|
| File manifest JSON | `<outputDir>\SampleManifest.json` |
| HALCON model files | `<outputDir>\tile_R0C0.shm`, `tile_R0C1.ncm`, ... |
| Return value | Đường dẫn đầy đủ tới `SampleManifest.json` |

---

### 2. `RunAlignStitch` — dùng ở Worker

```csharp
/// <summary>
/// Chạy trọn Align → PoseGraph → Stitch cho cả lô.
/// Worker gọi hàm này khi nhận batch dispatch.
/// Bọc AlignStitchWorkflowService.RunAsync từ Core.
/// </summary>
/// <param name="manifestPath">Đường dẫn manifest JSON (từ Master)</param>
/// <param name="capturedImages">Danh sách ảnh đã chụp + toạ độ ExpectedX/Y</param>
/// <param name="config">Config align/stitch (HALCON engine)</param>
/// <param name="outputDir">Folder ghi Stitched.tiff</param>
/// <param name="progress">Optional: callback progress</param>
/// <param name="cancellationToken">Optional: cancellation</param>
/// <returns>StitchResult chứa đường dẫn ảnh output + metadata</returns>
public async Task<StitchResult> RunAlignStitch(
    string manifestPath,
    IList<CapturedImageInfo> capturedImages,
    AlignStitchConfig config,
    string outputDir,
    IProgress<WorkflowProgress> progress = null,
    CancellationToken cancellationToken = default)
```

**Bên trong bọc:**
1. Deserialize manifest từ `manifestPath`
2. Load HALCON models (`HalconShapeModelProvider` / `HalconNccModelProvider`) từ folder manifest
3. Build input cho `AlignStitchWorkflowService.RunAsync`:
   - Map `CapturedImageInfo` → internal `CapturedImageInfo` của Core
   - Set config (engine = HALCON, method = NCC/ShapeModel)
4. Gọi `AlignStitchWorkflowService.RunAsync` (Align per-tile → `GlobalPoseGraphOptimizer` → `WorkflowStitchingService.Stitch`)
5. Output `Stitched.tiff` → ghi vào `outputDir`
6. Return `StitchResult`

**Đầu vào:**

| Param | Mô tả | Ví dụ |
|---|---|---|
| `manifestPath` | Manifest JSON từ Master | `Z:\GerberStitch\BatchXXX\manifest\SampleManifest.json` |
| `capturedImages` | List ảnh + toạ độ đã map | `[{Path="Z:\img\R0C0.bmp", ExpectedX=0, ExpectedY=0}, ...]` |
| `config` | Config engine | `{ AlignmentMethod=HalconNcc, StitchingEngine=HalconProjectiveMosaicRebased }` |
| `outputDir` | Folder ghi kết quả | `Z:\GerberStitch\BatchXXX\output\` |

**Đầu ra:**

| Item | Vị trí |
|---|---|
| `Stitched.tiff` | `<outputDir>\Stitched.tiff` |
| Return `StitchResult` | `{ TiffPath, SeamResidualPx, ElapsedMs, TileCount, FailedTiles[] }` |

---

## DTO Definitions

### `GridConfig`

```csharp
public class GridConfig
{
    public int Rows { get; set; }
    public int Cols { get; set; }
    public int OverlapPx { get; set; }
    public double PxPerMm { get; set; }
}
```

### `CapturedImageInfo`

```csharp
public class CapturedImageInfo
{
    /// <summary>Đường dẫn ảnh tile đã chụp (bmp/tiff)</summary>
    public string ImagePath { get; set; }

    /// <summary>Toạ độ X kỳ vọng (pixel, đã map từ die coordinate)</summary>
    public double ExpectedX { get; set; }

    /// <summary>Toạ độ Y kỳ vọng (pixel, đã map từ die coordinate)</summary>
    public double ExpectedY { get; set; }

    /// <summary>Row index trong grid (0-based)</summary>
    public int Row { get; set; }

    /// <summary>Column index trong grid (0-based)</summary>
    public int Col { get; set; }
}
```

### `StitchResult`

```csharp
public class StitchResult
{
    public bool Success { get; set; }

    /// <summary>Đường dẫn đầy đủ tới Stitched.tiff</summary>
    public string TiffPath { get; set; }

    /// <summary>Seam residual trung bình (pixel)</summary>
    public double SeamResidualPx { get; set; }

    /// <summary>Thời gian xử lý (ms)</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Tổng số tile đã xử lý</summary>
    public int TileCount { get; set; }

    /// <summary>Danh sách tile align thất bại (nếu có)</summary>
    public List<FailedTileInfo> FailedTiles { get; set; }

    /// <summary>Mã lỗi RDL nếu fail toàn bộ (-300 = stitch fail)</summary>
    public int ErrorCode { get; set; }
}

public class FailedTileInfo
{
    public int Row { get; set; }
    public int Col { get; set; }
    public string Reason { get; set; }  // map từ MatchFailureReason
}
```

---

## Nơi lấy code bọc (mapping Core → Façade)

| Façade method | Core class cần bọc | File trong Core |
|---|---|---|
| `GenerateSampleManifest` | `SampleTileGenerator` | `GerberStitching.Core/SampleTile/SampleTileGenerator.cs` |
| `GenerateSampleManifest` | `HalconNccModelProvider` / `HalconShapeModelProvider` | `GerberStitching.Core/Alignment/Halcon/` |
| `RunAlignStitch` | `AlignStitchWorkflowService.RunAsync` | `GerberStitching.Core/Workflow/AlignStitchWorkflowService.cs` |
| `RunAlignStitch` | `GlobalPoseGraphOptimizer` | `GerberStitching.Core/PoseGraph/GlobalPoseGraphOptimizer.cs` |
| `RunAlignStitch` | `WorkflowStitchingService.Stitch` | `GerberStitching.Core/Stitching/WorkflowStitchingService.cs` |

> **Lưu ý:** Kiểm tra lại tên file/namespace chính xác trong repo clone Phase 0. Bảng trên dựa trên cấu trúc branch `2026-08-04_Ver4_implement_claude`.

---

## Error handling

- `GenerateSampleManifest`: nếu Gerber parse lỗi / model generation fail → throw `GerberStitchException` (custom exception, message rõ ràng) — Master bắt để trả `S002-999`
- `RunAlignStitch`: nếu align fail 1 vài tile → ghi vào `FailedTiles[]`, vẫn stitch phần còn lại (partial success). Nếu fail quá ngưỡng hoặc stitch fail toàn bộ → `Success=false`, `ErrorCode=-300`
- Không throw unhandled — tất cả lỗi gói trong `StitchResult` hoặc `GerberStitchException`

---

## Exit gate

- [ ] `GenerateSampleManifest` gọi được từ console test, output manifest JSON + model files vào folder chỉ định
- [ ] `RunAlignStitch` gọi được từ harness Phase 0.3 (thay thế call trực tiếp vào Core) — output `Stitched.tiff` giống kết quả Phase 0
- [ ] Cả 2 hàm có XML doc comment đầy đủ
- [ ] DTO `GridConfig`, `CapturedImageInfo`, `StitchResult` compile sạch, serializable (cho Phase 5 dùng JSON)
