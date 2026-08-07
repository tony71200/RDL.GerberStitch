# Phase 1 — Task 1.4: Config mapping RDL ↔ `AlignStitchConfig`

**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1
**Phụ thuộc:** Task 1.1 (project tạo xong), Task 1.2 (DTO `AlignStitchConfig` đã define)

---

## Mục tiêu

Viết class `AlignStitchConfig` + helper đọc config từ file ini/recipe RDL → map sang các tham số mà `AlignStitchWorkflowService.RunAsync` (Core) cần. Ưu tiên HALCON engine, default value hợp lý cho môi trường RDL.

---

## Nơi tạo file

```
RDL.GerberStitch/
└── Facade/
    ├── AlignStitchConfig.cs           ← class config chính
    └── AlignStitchConfigReader.cs     ← helper đọc từ ini/recipe RDL
```

---

## Config class

```csharp
public class AlignStitchConfig
{
    // ── Alignment ──
    /// <summary>
    /// Phương pháp align: "HalconNcc" | "HalconShapeModel"
    /// Default: "HalconNcc"
    /// </summary>
    public string AlignmentMethod { get; set; } = "HalconNcc";

    /// <summary>
    /// Ngưỡng match score tối thiểu (0.0 – 1.0).
    /// Tile dưới ngưỡng → MatchFailureReason, không align.
    /// </summary>
    public double MinMatchScore { get; set; } = 0.7;

    /// <summary>
    /// Giới hạn pixel sai lệch tối đa giữa pose align và ExpectedX/Y.
    /// Vượt ngưỡng → guard reject (MaxPoseCorrectionPixels trong Core).
    /// </summary>
    public double MaxPoseCorrectionPx { get; set; } = 50.0;

    // ── Stitching ──
    /// <summary>
    /// Engine stitch: "HalconProjectiveMosaicRebased" | "HalconTileOffset"
    /// Default: "HalconProjectiveMosaicRebased" (chất lượng cao hơn).
    /// </summary>
    public string StitchingEngine { get; set; } = "HalconProjectiveMosaicRebased";

    /// <summary>
    /// Blending mode seam: "Feather" | "None"
    /// </summary>
    public string SeamBlendMode { get; set; } = "Feather";

    // ── Output ──
    /// <summary>
    /// Format ảnh output: "tiff" | "bmp"
    /// </summary>
    public string OutputFormat { get; set; } = "tiff";

    /// <summary>
    /// Tên file output (không kèm extension).
    /// Nếu null → dùng naming convention RDL: "Stitched_{batchId}"
    /// </summary>
    public string OutputFileName { get; set; }

    // ── Resource ──
    /// <summary>
    /// Giới hạn RAM (MB) cho stitch canvas. 0 = không giới hạn.
    /// Dùng để Worker tự kiểm tra trước khi allocate canvas lớn.
    /// </summary>
    public int MaxCanvasRamMb { get; set; } = 0;

    // ── Fallback ──
    /// <summary>
    /// Nếu true: khi stitch fail → Worker tự fallback sang NewMergeFunc (merge cũ).
    /// Nếu false: fail → trả lỗi -300 về Master.
    /// </summary>
    public bool FallbackToLegacyMerge { get; set; } = false;
}
```

---

## Mapping từ Core config

Bảng mapping giữa `AlignStitchConfig` (façade) ↔ config nội bộ Core:

| Façade property | Core internal property / class | Ghi chú |
|---|---|---|
| `AlignmentMethod` | `WorkflowConfig.AlignmentMethod` enum | Map string → enum |
| `MinMatchScore` | `AlignerConfig.MinScore` | Truyền vào `ISampleAligner` |
| `MaxPoseCorrectionPx` | `AlignerConfig.MaxPoseCorrectionPixels` | Guard check |
| `StitchingEngine` | `WorkflowConfig.StitchingEngine` enum | Map string → enum |
| `SeamBlendMode` | `StitcherConfig.BlendMode` | Truyền vào `WorkflowStitchingService` |
| `OutputFormat` | `StitcherConfig.OutputFormat` | `.tiff` / `.bmp` |

> **Lưu ý:** Kiểm tra tên property chính xác trong source Core (branch `2026-08-04_Ver4_implement_claude`). Bảng trên là ước lượng dựa trên convention — có thể cần adjust.

---

## Config Reader — đọc từ ini/recipe RDL

```csharp
public static class AlignStitchConfigReader
{
    /// <summary>
    /// Đọc config từ file ini RDL (section [GerberAlignStitch]).
    /// Return default config nếu section không tồn tại.
    /// </summary>
    public static AlignStitchConfig ReadFromIni(string iniFilePath)
    {
        // Đọc các key từ section [GerberAlignStitch]:
        //   AlignmentMethod=HalconNcc
        //   MinMatchScore=0.7
        //   MaxPoseCorrectionPx=50
        //   StitchingEngine=HalconProjectiveMosaicRebased
        //   SeamBlendMode=Feather
        //   OutputFormat=tiff
        //   MaxCanvasRamMb=0
        //   FallbackToLegacyMerge=false
        //
        // Key không có → dùng default value trong class
    }
}
```

### Format ini RDL mẫu (section mới thêm vào file config hiện có)

```ini
[GerberAlignStitch]
Enable=true
GerberFilePath=Z:\Recipe\BoardA\design.gbr
AlignmentMethod=HalconNcc
MinMatchScore=0.7
MaxPoseCorrectionPx=50
StitchingEngine=HalconProjectiveMosaicRebased
SeamBlendMode=Feather
OutputFormat=tiff
MaxCanvasRamMb=4096
FallbackToLegacyMerge=false

; Grid config (cũng đọc ở đây, dùng cho GenerateSampleManifest)
GridRows=8
GridCols=10
GridOverlapPx=50
GridPxPerMm=25.4
```

---

## Nơi lấy code tham khảo

| Cần tham khảo | Nguồn |
|---|---|
| Cách RDL đọc ini hiện tại | File config reader trong Master/Worker (tìm `IniFile` / `IniParser` hoặc `GetPrivateProfileString`) |
| Config class của Core | `GerberStitching.Core/Configuration/` hoặc `WorkflowConfig.cs` — xem enum `AlignmentMethod`, `StitchingEngine` |
| Default value hợp lý | Report spike Phase 0.3 (config nào chạy ra kết quả tốt) |

---

## Validation

```csharp
public static class AlignStitchConfigValidator
{
    public static void Validate(AlignStitchConfig cfg)
    {
        // AlignmentMethod phải là "HalconNcc" hoặc "HalconShapeModel"
        // MinMatchScore trong [0.0, 1.0]
        // MaxPoseCorrectionPx > 0
        // StitchingEngine phải là "HalconProjectiveMosaicRebased" hoặc "HalconTileOffset"
        // OutputFormat phải là "tiff" hoặc "bmp"
        // Throw ArgumentException nếu invalid, kèm message rõ key nào sai
    }
}
```

---

## Đầu vào

| Item | Nguồn |
|---|---|
| Config Core (enum, default) | `GerberStitching.Core/Configuration/` |
| Format ini hiện tại của RDL | Master/Worker config reader |
| Kết quả spike Phase 0.3 | Config nào cho kết quả stitch tốt → dùng làm default |

## Đầu ra

| Item | Vị trí |
|---|---|
| `AlignStitchConfig.cs` | `RDL.GerberStitch/Facade/AlignStitchConfig.cs` |
| `AlignStitchConfigReader.cs` | `RDL.GerberStitch/Facade/AlignStitchConfigReader.cs` |
| `AlignStitchConfigValidator.cs` | `RDL.GerberStitch/Facade/AlignStitchConfigValidator.cs` |
| Mẫu section ini | Document / ghi vào `README.md` của project |

---

## Exit gate

- [ ] `AlignStitchConfigReader.ReadFromIni(path)` đọc đúng tất cả key từ file ini mẫu
- [ ] Key thiếu → dùng default, không crash
- [ ] Key sai value → `Validate()` throw rõ ràng
- [ ] Harness Phase 0.3 chạy lại với config đọc từ ini (thay vì hardcode) → kết quả `Stitched.tiff` giống nhau
- [ ] Config mapping sang Core internal config đúng — verify bằng log hoặc debug
