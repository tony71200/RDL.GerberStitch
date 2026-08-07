# Phase 1 — Task 1.4: Config mapping RDL ↔ `AlignStitchConfig`

**Ngày:** 2026-08-07
**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1.5 *(tăng từ 1 — phát sinh bẫy `ConfigVersion` ở §2)*
**Phụ thuộc:** Task 1.1 (`RdlAlignStitchOptions` đã đổi tên), Task 1.2 (façade `RunAlignStitch`)
**Platform:** C# 7.3, .NET Framework 4.8, **x64**

**Bắt buộc đọc trước khi sửa:**
1. `docs/Phase0_Closeout.md` §4.3 (mâu thuẫn blending), §1.2 (matcher thực dùng)
2. File task này, đặc biệt **§2 — bẫy `ConfigVersion`**

**Ràng buộc:** Không thêm NuGet package. Không thêm project test. Không tự chạy test.

---

## 0. Vấn đề

Bản trước của doc này tự nhận *"Bảng trên là ước lượng dựa trên convention — có thể cần adjust"*. Đối chiếu source: mô hình config bị sai **tầng**, và các giá trị số sai đủ để làm hỏng pipeline.

| Bản cũ | Thực tế trong Core |
|---|---|
| `AlignmentMethod` — 1 lựa chọn string | **4** enum riêng: `DirectCoarseMatcherKind`, `DirectRefinementMatcherKind`, `NeighborCoarseMatcherKind`, `NeighborRefinementMatcherKind` (`Configuration/AlignStitchStageOptions.cs:9-31`) |
| `MinMatchScore = 0.7` | `HalconNccOptions.MinScore = 0.10`; `EccOptions.MinCorrelation = 0.13`; `PhaseCorrelationOptions.MinResponse = 0.15` |
| `StitchingEngine` — 2 giá trị | **5** giá trị (`Models/WorkflowModels.cs:31-38`) |
| `SeamBlendMode = "Feather"` mặc định | Engine HALCON không có tham số blending |
| `MaxPoseCorrectionPx = 50` | Không tồn tại. Ngưỡng thật: `NeighborAlignment.MaxDirectPoseAdjustmentPixels = 8`, `MaxCycleClosureErrorPixels = 12` |

> 🔴 `MinMatchScore = 0.7` là sai lệch nguy hiểm nhất — cao gấp ~5–7× default thật. Code theo doc cũ sẽ loại gần hết tile.

**Sai lầm gốc:** doc cũ giả định phải **xây mới** một hệ config. Thực tế Core đã có hệ config trưởng thành (`AlignStitchConfig` + `AlignStitchConfigMapper` với `EnsureComposite` / `SyncLegacy` / `CloneForRun` / `CreateSnapshot`). Task này **map vào**, không xây lại.

---

## 1. Nguyên tắc: default của Core đã đúng

Đối chiếu default của Core với cấu hình đã cho ra `Stitched.tiff` hợp lệ (run `110300`):

| Thiết lập | Default Core | Run tham chiếu | Khớp? |
|---|---|---|---|
| `DirectAlignment.CoarseMatcher` | `HalconShapeModel` | `HalconShapeModelMatcher` | ✅ |
| `DirectAlignment.RefinementMatcher` | `PyramidEcc` | `PyramidEccMatcher` | ✅ |
| `NeighborAlignment.CoarseMatcher` | `PyramidPhaseCorrelation` | phase correlation | ✅ |
| `Stitching.Engine` | `HalconProjectiveMosaicRebased` | `HalconProjectiveMosaicRebased` | ✅ |
| `Recovery.AutoPlaceExactZeroSample` | `true` | 26 tile blank được đặt tự động | ✅ |
| `Recovery.RecoverFailedTiles` | `true` | `FailureRecovery=enabled` | ✅ |
| `Recovery.ReconcileAllMatchedTiles` | `true` | `FullPoseReconciliation=enabled` | ✅ |

**Kết luận: `RdlAlignStitchOptions` chỉ nên phơi ra những gì RDL thực sự cần đổi, và giữ nguyên phần còn lại của Core.** Không sao chép lại toàn bộ cây options — làm vậy là tạo ra một nguồn sự thật thứ hai, chắc chắn sẽ trôi lệch khỏi Core.

Ngoại lệ duy nhất: `Stitching.EnableBlending` (§3).

---

## 2. ⚠ Bẫy `ConfigVersion` — phải xử lý đúng, nếu không config bị ghi đè âm thầm

`AlignStitchConfig` mang **hai** bộ trường song song:
- Cấu trúc: `Input`, `DirectAlignment`, `NeighborAlignment`, `Recovery`, `PoseGraph`, `Stitching`, `Output`
- Phẳng (legacy, giữ để migrate config cũ đã serialize): `NccMinScore`, `EccMinCorrelation`, `MaxTranslationPixels`, `MaxAbsRotationDeg`, ...

`AlignStitchConfigMapper.EnsureComposite` (`:15`) hoà giải hai bộ đó **theo `ConfigVersion`**:

```csharp
if (c.ConfigVersion == 2) { /* migrate v2 -> v3 */ return; }
if (c.ConfigVersion >= 3) return;          // ← cấu trúc là nguồn sự thật, thoát sớm
// ConfigVersion 0/1: chép PHẲNG -> CẤU TRÚC
c.Input.ManifestPath = c.InputManifestPath;
c.DirectAlignment.Ncc.MinScore = c.NccMinScore;      // ...
```

**`new AlignStitchConfig()` có `ConfigVersion = 0`** (default của `int`). Nếu façade tạo config mới rồi gọi `EnsureComposite`, nhánh migration 0/1 sẽ chạy và **ghi đè** options cấu trúc bằng giá trị phẳng. Hai bộ default này **lệch nhau**:

| Thiết lập | Default cấu trúc | Default phẳng | Kết quả nếu để bẫy xảy ra |
|---|---|---|---|
| NCC min score | `DirectAlignment.Ncc.MinScore = 0.10` | `NccMinScore = 0.13` | → **0.13** (chặt hơn) |
| Max rotation | `Geometry.MaxAbsRotationDeg = 0.5` | `MaxAbsRotationDeg = 0.1` | → **0.1** (chặt hơn **5×**) — *tình cờ trùng giá trị RDL chốt ở §4, nhưng vẫn phải đặt tường minh* |
| ECC min correlation | `Ecc.MinCorrelation = 0.13` | `EccMinCorrelation = 0.13` | 0.13 (không đổi) |
| Max translation | `Geometry.MaxTranslationPixels = 300` | `300` | 300 (không đổi) |

Ngưỡng xoay siết từ 0.5° xuống 0.1° sẽ loại thêm tile **mà không có thông báo nào**.

### 2.1. Cách làm đúng

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Đặt ConfigVersion=3 trước EnsureComposite để cây options cấu trúc là nguồn sự thật, tránh nhánh migration 0/1 ghi đè bằng field phẳng legacy.]
var core = new AlignStitchConfig();
core.ConfigVersion = 3;                       // ← BẮT BUỘC, trước mọi thứ khác

// ... áp các override từ RdlAlignStitchOptions lên cây cấu trúc ...

AlignStitchConfigMapper.EnsureComposite(core); // giờ chỉ còn tác dụng null-guard
AlignStitchConfigMapper.SyncLegacy(core);      // đồng bộ cấu trúc -> phẳng
```

**Thứ tự bắt buộc:** đặt `ConfigVersion = 3` → override → `EnsureComposite` → `SyncLegacy`.

`SyncLegacy` (`:381`) chép **cấu trúc → phẳng**, cần gọi vì vẫn có code đọc field phẳng.

> Kiểm chứng bẫy này bằng nghiệm thu mục 3.

---

## 3. Blending — mâu thuẫn default của Core

Default của Core: `Stitching.EnableBlending = true`, `BlendMode = Feather`, `Engine = HalconProjectiveMosaicRebased`. Cặp này **luôn** sinh warning:

> *"EnableBlending was requested but `HOperatorSet.GenProjectiveMosaic` has no blending parameter; overlap uses hard overwrite."*

**Quyết định (`Phase0_Closeout.md` §4.3): default RDL = engine HALCON + `EnableBlending = false`.**

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: HALCON GenProjectiveMosaic không có tham số blending; bật blending chỉ sinh warning mà không có tác dụng.]
core.Stitching.Engine = StitchingEngine.HalconProjectiveMosaicRebased;
core.Stitching.EnableBlending = false;
```

Người dùng vẫn chọn được `Engine = OpenCv` + `EnableBlending = true` qua ini khi cần ảnh đẹp hơn để điều tra — **đánh đổi: khâu stitch chậm 17×** (13.3 s → 231.7 s).

> ⚠ **Đừng nhầm với `BlankFallbackOverlapPolicy`** (`Normal` / `PreserveExistingOverlap` / `WeightedBlend`, default `PreserveExistingOverlap`). Đó là knob riêng, chỉ chi phối cách tile blank fallback xử lý vùng chồng lấn — không phải blending seam tổng thể. **Giữ nguyên default.**

---

## 4. `RdlAlignStitchOptions`

Chỉ những gì RDL cần đổi. Mọi thứ khác thừa hưởng default Core.

```csharp
// [Claude] [Change time: 2026-08-07] [Purpose: Lớp config mỏng cho RDL; phần còn lại giữ default Core đã được chứng minh qua run 110300.]
public sealed class RdlAlignStitchOptions
{
    // ── Engine ──
    /// <summary>Engine stitch. Default HalconProjectiveMosaicRebased — nhanh hơn OpenCv 17×.</summary>
    public string StitchingEngine { get; set; } = "HalconProjectiveMosaicRebased";

    /// <summary>Bật blending seam. Default false: engine HALCON không hỗ trợ (xem §3).</summary>
    public bool EnableBlending { get; set; } = false;

    // ── Ngưỡng thường phải chỉnh theo dây chuyền ──
    /// <summary>Ngưỡng HALCON NCC. Default theo Core. KHÔNG đặt 0.7.</summary>
    public double NccMinScore { get; set; } = 0.10;

    /// <summary>Ngưỡng tương quan ECC (refinement). Default theo Core.</summary>
    public double EccMinCorrelation { get; set; } = 0.13;

    /// <summary>Dịch chuyển tối đa cho phép (px) khi khớp trực tiếp.</summary>
    public double MaxTranslationPixels { get; set; } = 300;

    /// <summary>
    /// Xoay tuyệt đối tối đa (độ). Chốt 0.1 cho RDL — siết hơn default cấu trúc (0.5)
    /// vì dây chuyền RDL đặt tile bằng cơ cấu cơ khí, sai lệch xoay thực tế rất nhỏ.
    /// Phải đặt TƯỜNG MINH: nếu để mặc định và dính bẫy ConfigVersion (§2) thì cũng ra 0.1,
    /// nhưng là do tai nạn chứ không phải chủ đích.
    /// </summary>
    public double MaxAbsRotationDeg { get; set; } = 0.1;

    // ── Output ──
    /// <summary>tiff | bmp. Tên file do Worker quyết theo chuẩn đặt tên RDL.</summary>
    public string OutputFormat { get; set; } = "tiff";

    // ── Fallback RDL ──
    /// <summary>true: stitch fail → Worker fallback sang NewMergeFunc. false → trả -300 về Master.</summary>
    public bool FallbackToLegacyMerge { get; set; } = false;
}
```

**Cố ý KHÔNG phơi ra** (giữ default Core, tránh nhân đôi nguồn sự thật):

| Nhóm | Lý do |
|---|---|
| Lựa chọn 4 matcher | Default đã khớp run đã chứng minh (§1). Đổi được thì cũng chỉ để thử nghiệm — làm qua Core config, không qua ini RDL |
| `Recovery.*` | 3 cờ quan trọng đều đã `true` |
| `PoseGraph.*` | Run tham chiếu: `edges=138/142`, `valid=True`, chỉ tốn 63 ms |
| `BlankFallbackOverlapPolicy` | Xem cảnh báo §3 |
| `BigTiffTileWidth/Height`, `TiffMode` | `TiffMode.Auto` tự chọn BigTiff khi cần — output thật 654 MB đã vượt ngưỡng TIFF chuẩn |

---

## 5. Ánh xạ `RdlAlignStitchOptions` → `AlignStitchConfig`

```csharp
// RDL.GerberStitch/Internal/CoreConfigMapper.cs
// [Claude] [Change time: 2026-08-07] [Purpose: Map config RDL sang AlignStitchConfig của Core, giữ đúng thứ tự để tránh bẫy ConfigVersion.]
internal static AlignStitchConfig ToCore(RdlAlignStitchOptions o, string manifestPath, string outputPath)
{
    var c = new AlignStitchConfig();
    c.ConfigVersion = 3;                                    // §2.1 — trước tiên

    c.Input.ManifestPath = manifestPath;
    c.Output.OutputPath  = outputPath;

    c.Stitching.Engine          = ParseEngine(o.StitchingEngine);
    c.Stitching.EnableBlending  = o.EnableBlending;

    c.DirectAlignment.Ncc.MinScore                = o.NccMinScore;
    c.DirectAlignment.Ecc.MinCorrelation          = o.EccMinCorrelation;
    c.DirectAlignment.Geometry.MaxTranslationPixels = o.MaxTranslationPixels;
    c.DirectAlignment.Geometry.MaxAbsRotationDeg    = o.MaxAbsRotationDeg;

    AlignStitchConfigMapper.EnsureComposite(c);
    AlignStitchConfigMapper.SyncLegacy(c);
    return c;
}
```

| `RdlAlignStitchOptions` | Đích trong Core |
|---|---|
| `StitchingEngine` | `Stitching.Engine` *(enum `StitchingEngine`)* |
| `EnableBlending` | `Stitching.EnableBlending` |
| `NccMinScore` | `DirectAlignment.Ncc.MinScore` |
| `EccMinCorrelation` | `DirectAlignment.Ecc.MinCorrelation` |
| `MaxTranslationPixels` | `DirectAlignment.Geometry.MaxTranslationPixels` |
| `MaxAbsRotationDeg` | `DirectAlignment.Geometry.MaxAbsRotationDeg` |
| `OutputFormat` | *(quyết định đuôi file khi ghi — không có trường tương ứng trong Core)* |
| `FallbackToLegacyMerge` | *(chỉ RDL dùng — Worker đọc, Core không biết)* |

> `CloneForRun(source, outputPath)` (`AlignStitchConfigMapper.cs:187`) đã làm sẵn việc nhân bản config + gán output path. Dùng nó khi cần chạy nhiều lô từ một config gốc, thay vì tự chép.

---

## 6. Đọc config từ ini RDL

```ini
[GerberAlignStitch]
Enable=true
GerberFilePath=Z:\Recipe\BoardA\design.gbr

; ── Engine ──
StitchingEngine=HalconProjectiveMosaicRebased
EnableBlending=false

; ── Ngưỡng (mặc định = default Core, chỉ đổi khi có lý do) ──
NccMinScore=0.10
EccMinCorrelation=0.13
MaxTranslationPixels=300
MaxAbsRotationDeg=0.1

; ── Output & fallback ──
OutputFormat=tiff
FallbackToLegacyMerge=false

; ── Grid config — dùng cho GenerateSampleManifest (Task 1.2 §3.1) ──
GridRows=8
GridColumns=10
OverlapValue=70
OverlapUnit=Pixel
ProcessedWidth=4096
ProcessedHeight=4096
StartOrder=TopLeftDown
CropOrder=Zigzag

; OnTheFly (default) | Pregenerate — xem Task 1.2 §2
ModelGeneration=OnTheFly
```

> Các giá trị grid ở trên là **default của Core** (`Configuration/GerberSampleConfig.cs`), cho ra `8 × 10 = 80` tile — đúng bằng dataset tham chiếu Phase 0. Dùng làm điểm khởi đầu hợp lý.
>
> ⚠ **Blocker 4 vẫn chưa tháo** (`Phase0_Closeout.md` §5): chưa xác nhận grid này khớp lưới chụp thật của dây chuyền RDL, và chưa có quy tắc map `ExpectedX/Y` ↔ toạ độ die của Master. Validate `Rows`/`Columns`/`ProcessedWidth`/`ProcessedHeight` **> 0** và đối chiếu với recipe trước khi chạy production.

**Cách đọc:** dùng đúng cơ chế đọc ini mà Master/Worker đang dùng (`GetPrivateProfileString` hoặc helper sẵn có), không tự viết parser mới.

**Quy tắc:** key thiếu → dùng default trong `RdlAlignStitchOptions`, không crash. Key sai giá trị → `Validate()` ném `ArgumentException` nêu rõ key nào.

```csharp
public static void Validate(RdlAlignStitchOptions o)
{
    // StitchingEngine phải parse được sang enum StitchingEngine (5 giá trị)
    // NccMinScore, EccMinCorrelation trong (0, 1]
    // MaxTranslationPixels > 0; MaxAbsRotationDeg > 0
    // OutputFormat thuộc { "tiff", "bmp" }
    // Cảnh báo (không chặn) nếu EnableBlending=true và Engine là HALCON — xem §3
}
```

---

## 7. Danh sách file thay đổi

| File | Thay đổi |
|---|---|
| `RDL.GerberStitch/Facade/RdlAlignStitchOptions.cs` | Thân class theo §4 |
| `RDL.GerberStitch/Facade/RdlAlignStitchOptionsReader.cs` | **mới** — đọc ini §6 |
| `RDL.GerberStitch/Facade/RdlAlignStitchOptionsValidator.cs` | **mới** — §6 |
| `RDL.GerberStitch/Internal/CoreConfigMapper.cs` | **mới** — §5 |
| `RDL.GerberStitch/RDL.GerberStitch.csproj` | + `<Compile Include>` |

**Không đụng:** file `.cs` nào trong `GerberStitching.Core`.

---

## 8. Tiêu chí nghiệm thu

1. Build sạch `Debug|x64` và `Release|x64`.
2. `ReadFromIni` đọc đúng mọi key ở §6; key thiếu → default, không crash.
3. **Kiểm chứng bẫy `ConfigVersion` (§2):** sau `ToCore(new RdlAlignStitchOptions(), ...)`, khẳng định:
   - `c.ConfigVersion == 3`
   - `c.DirectAlignment.Ncc.MinScore == 0.10` — **phép thử then chốt**. Nếu ra `0.13` nghĩa là bẫy đã xảy ra, giá trị bị field phẳng ghi đè.
   - `c.DirectAlignment.Geometry.MaxAbsRotationDeg == 0.1` — đúng giá trị RDL chốt. *(Lưu ý: bẫy cũng cho ra 0.1, nên riêng dòng này **không** phân biệt được đúng/tai nạn — dùng dòng `MinScore` ở trên để kết luận.)*
4. Config default → `RunAlignStitch` trên dataset 80 tile ra kết quả **giống run `110300`**: `BlankTileCount = 26`, `AlignedTileCount = 59`.
5. `EnableBlending = false` + engine HALCON → `report.Warnings` **không** còn cảnh báo blending.
6. `EnableBlending = true` + engine HALCON → `Validate()` cảnh báo, và run vẫn chạy được (không chặn).
7. `NccMinScore = 0.7` (giá trị sai của doc cũ) → số tile khớp **giảm rõ rệt** so với mục 4. Ghi lại con số làm bằng chứng vì sao 0.7 là sai.
8. `GridRows = 0` hoặc `ProcessedWidth = 0` trong ini → `Validate()` ném lỗi rõ ràng, không chạy tiếp.

---

## 9. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Quên đặt `ConfigVersion = 3` → ngưỡng bị siết âm thầm, tile bị loại không rõ lý do | §2.1 thứ tự bắt buộc; nghiệm thu mục 3 chốt bằng giá trị cụ thể |
| Phơi ra quá nhiều option → nhân đôi nguồn sự thật, trôi lệch khỏi Core | §4 — chỉ phơi 8 trường; bảng "cố ý không phơi" ghi rõ lý do |
| Người dùng đặt `EnableBlending=true` rồi thắc mắc sao stitch chậm 17× | §3 ghi rõ đánh đổi; `Validate()` cảnh báo (mục 6) |
| Grid config còn `0` do blocker 4 → chạy với lưới rỗng | §6 — validate khác 0; nghiệm thu mục 8 |
| Nhầm `BlankFallbackOverlapPolicy` với blending seam | Cảnh báo ở cuối §3 |
