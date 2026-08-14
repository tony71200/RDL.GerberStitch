# In-Memory Sample Crop + Settings File — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bỏ pha cắt tile mẫu ra đĩa; crop ảnh sample trong RAM bằng HALCON tại đầu lô, và đưa toàn
bộ tham số pipeline ra một file JSON có chú thích tiếng Anh.

**Architecture:** Thêm trừu tượng `ISampleTileSource` giữa `AlignStitchWorkflowService` và nguồn tile
mẫu. `DiskSampleTileSource` giữ hành vi cũ (đọc `tile.ExpectedPath`); `InMemorySampleTileSource` đọc
ảnh sample lớn bằng HALCON, crop hết một lượt sang `OpenCvSharp.Mat`, rồi dispose ảnh lớn trước khi
pha Align bắt đầu. Song song, một loader JSON mới đọc registry matcher key-theo-loại và ánh xạ vào
cây options sẵn có của Core, không đổi hành vi Core.

**Tech Stack:** C# 7.3 · .NET Framework 4.8 · x64 · HALCON 25.05 Progress (`halcondotnetxl`) ·
OpenCvSharp4 4.13 · Newtonsoft.Json

**Spec:** [`docs/superpowers/specs/2026-08-14-in-memory-sample-crop-and-settings-file-design.md`](../specs/2026-08-14-in-memory-sample-crop-and-settings-file-design.md)

## Global Constraints

- **Không có test project trong solution, và không được thêm.** AGENTS.md §4: agent không tự chạy
  test và không tự coi là đã pass. Vì vậy plan này **không dùng vòng TDD**. Mỗi task kết thúc bằng
  **build gate** (agent chạy được) và **verification gate** (user chạy, agent chỉ ghi rõ cần kiểm gì).
- **Build:** `msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64`. Cần biến môi
  trường `HALCONROOT`. Không build AnyCPU/x86.
- **Ranh giới façade:** Master/Worker chỉ gọi qua `RDL.GerberStitch.Facade.*`. Không rò rỉ kiểu nội
  bộ của `GerberStitching.Core`/`GerberEngine` ra khỏi façade.
- **Không đổi hành vi pipeline trong `GerberStitching.Core`.** Toàn bộ plan này phải giữ kết quả
  align không đổi bit nào. AGENTS.md §3.1: đổi hành vi pipeline phải hỏi user trước.
- **Không tự chế cơ chế license-check HALCON mới** (AGENTS.md §3.3). Operator HALCON mới chỉ được
  dùng `read_image` / `crop_rectangle1` — cùng nhóm mà `GenerateSampleManifestFromRects` đã dùng —
  và bọc theo mẫu try/catch quanh `HalconImageInteropException` sẵn có.
- **Ghi log triển khai — BẮT BUỘC:** mỗi task có sửa `.cs`/`.csproj` phải **thêm một entry mới** vào
  `docs/implement_code.html` (không ghi đè entry cũ): ngày, task, file đã đổi, đánh đổi thiết kế, và
  mọi lỗi build/chạy kèm cách fix.
- **Convention:** bám convention có sẵn trong file đang sửa. File mới: PascalCase cho
  class/method/public property, camelCase cho local, `_camelCase` cho private field. Code debug-only
  nằm trong `#if DEBUG`.
- **Lô tham chiếu để đối chiếu mọi kết quả:** `AlignStitch_20260813_154108` — 80 tile, 54 aligned,
  26 blank, 0 failed, 319 451 ms, `PeakWorkingSet` 7 185 MB.

## Bảng file

| File | Trách nhiệm | Task |
|---|---|---|
| `GerberStitching.Core/Imaging/SampleTileGenerator.cs` | Bỏ ghi `processed_sample.tiff` khi `PreprocessMode=None` | 1 |
| `GerberStitching.Core/Alignment/ISampleTileSource.cs` *(mới)* | Hợp đồng nguồn tile mẫu | 2 |
| `GerberStitching.Core/Alignment/DiskSampleTileSource.cs` *(mới)* | Impl đọc từ đĩa; bọc `WorkflowImageCache` | 2 |
| `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs` | 4 call site đổi sang `ISampleTileSource` | 2 |
| `GerberStitching.Core/Alignment/InMemorySampleTileSource.cs` *(mới)* | Crop HALCON → `Mat`, dispose ảnh lớn | 3 |
| `RDL.GerberStitch/Facade/GerberStitchFacade.cs` | Overload `RunAlignStitch` nhận ảnh sample + rect | 4 |
| `RDL_Worker/GerberAlignStitch/GerberStitchRunner.cs` | Bỏ Stage 1.5 + `TryReuseManifest` | 5 |
| `RDL.GerberStitch/Facade/Settings/MatcherRegistry.cs` *(mới)* | DTO registry matcher + phân giải loại → slot Core | 6 |
| `RDL.GerberStitch/Facade/Settings/AlignStitchSettingsFile.cs` *(mới)* | DTO file JSON (`CommonlyTuned`/`Advanced`) | 6 |
| `RDL.GerberStitch/Facade/Settings/AlignStitchSettingsReader.cs` *(mới)* | Đọc + deep-merge + validate + cảnh báo khoá lạ | 6 |
| `RDL.GerberStitch/Facade/Settings/ConfigLayerLog.cs` *(mới)* | Log dump giá trị khác mặc định + tầng nguồn | 6 |
| `RDL.GerberStitch/Facade/AlignStitchConfigIniReader.cs` | Nhận config nền; thêm khoá `SettingsPath` | 6, 8 |
| `GerberAlignStitch.settings.json` *(mới, cạnh Worker exe)* | ~120 tham số + chú thích tiếng Anh | 7 |
| `RDL.GerberStitch/Facade/AlignStitchConfig.cs` | Thêm `SettingsFilePath`, `RecipeSettingsPath`, `LogWarning` | 8 |

Hai nhánh chạy song song sau Task 0: **1→2→3→4→5** (crop) và **6→7** (config).

---

### Task 0: Hợp nhất hai bản code

**Files:**
- Modify: toàn bộ `GerberStitching.Core/` và `RDL.GerberStitch/` trong repo này
- Nguồn: `H:\005_Project\AOI_2026_07_imp\20260812_RDL\RDL_WorkerNorthT_Ver2_SoftMerge_2026_08_09\`

**Interfaces:**
- Consumes: —
- Produces: `GerberStitchFacade.GenerateSampleManifestFromRects(string, IList<TileRect>, string, string, CancellationToken)`, `RDL.GerberStitch.Facade.TileRect` (`OrderIndex`, `Row`, `Column`, `X`, `Y`, `Width`, `Height` — tất cả `int`), `AlignStitchConfig.EmitDebugPreview` (bool), `AlignStitchResult.DebugPreviewPath` (string), `GerberViewer.Stitching.Diagnostics.DebugPreviewWriter`, `SampleGeometryCalculator.FromExplicitRects`

Bản trong Worker đã fork và đi trước; repo này có `CalculateTimeDetail` mà Worker không có. Task này
hợp nhất, **không đổi logic nào**.

- [ ] **Step 1: Chụp lại danh sách file khác nhau để đối chiếu về sau**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL" && diff -rq --strip-trailing-cr RDL.GerberStitch/GerberStitching.Core RDL_WorkerNorthT_Ver2_SoftMerge_2026_08_09/GerberStitching.Core | grep -v "\.vs\|obj/\|bin/" > /tmp/core_diff.txt; wc -l /tmp/core_diff.txt
```

- [ ] **Step 2: Tạo branch làm việc**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && git checkout -b 2026-08-14_in-memory-crop
```

- [ ] **Step 3: Copy `GerberStitching.Core` từ Worker sang, trừ `bin`/`obj`/`.vs`**

Copy toàn bộ `.cs` + `.csproj` từ `RDL_WorkerNorthT_.../GerberStitching.Core/` đè lên
`RDL.GerberStitch/GerberStitching.Core/`. Không copy `bin/`, `obj/`, `.vs/`.

- [ ] **Step 4: Copy 3 file façade từ Worker sang**

`RDL_WorkerNorthT_.../RDL.GerberStitch/Facade/{GerberStitchFacade,AlignStitchConfig,AlignStitchConfigIniReader}.cs`
→ `RDL.GerberStitch/RDL.GerberStitch/Facade/`.

- [ ] **Step 5: Khôi phục `CalculateTimeDetail` — thứ duy nhất repo này có mà Worker không có**

Trong `RDL.GerberStitch/Facade/AlignStitchConfig.cs`, thêm lại thuộc tính (bản Worker đã mất nó):

```csharp
        /// <summary>
        /// true (default) computes and reports per-tile alignment/recovery duration (Detail mode).
        /// false skips the per-tile timing reads and only reports stage-level totals
        /// (Mapping, Direct Alignment, Failure Recovery, Neighbor Graph, Stitching)
        /// on ProcessingReport.StageTimings.
        /// </summary>
        public bool CalculateTimeDetail { get; set; } = true;
```

Trong `GerberStitchFacade.BuildCoreConfig`, thêm lại dòng ánh xạ ngay sau
`baseConfig.Stitching.EnableBlending = options.EnableBlending;`:

```csharp
            baseConfig.CalculateTimeDetail = options.CalculateTimeDetail;
```

- [ ] **Step 6: Kiểm `AlignStitchConfig.CalculateTimeDetail` tồn tại bên Core**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && grep -n "CalculateTimeDetail" GerberStitching.Core/Models/WorkflowModels.cs
```

Nếu không có, thêm vào `GerberViewer.Stitching.Models.AlignStitchConfig` (file
`GerberStitching.Core/Models/WorkflowModels.cs`, trong khối thuộc tính bool cuối class):

```csharp
        public bool CalculateTimeDetail { get; set; } = true;
```

rồi tìm chỗ đọc nó trong `AlignStitchWorkflowService` ở lịch sử git repo này
(`git log -S CalculateTimeDetail -- GerberStitching.Core`) và áp lại đúng nhánh đó.

- [ ] **Step 7: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Kỳ vọng: 0 error. Lỗi hay gặp khi merge kiểu này là **CS0104** (tên mơ hồ giữa
`GerberViewer.Stitching.Configuration.GerberSampleConfig` và `...Models.GerberSampleConfig`, giữa
`GerberEngine.ColorMode` và `System.Drawing.Imaging.ColorMode`) và **CS0012** (thiếu reference
`halcondotnetxl` khi `HALCONROOT` chưa đặt). Fix bằng tên đầy đủ, không bằng `using` mới.

- [ ] **Step 8: Ghi entry `docs/implement_code.html`**

Entry mới: ngày `2026-08-14`, task `Task 0 — merge Worker → repo`, danh sách file đổi, và **mọi lỗi
build gặp ở Step 7 kèm cách fix** (kể cả CS0104/CS0012 — đây chính là loại thông tin dễ mất nhất
giữa các phiên).

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "Merge the Worker fork of Core and the facade back into this repo

Brings in GenerateSampleManifestFromRects, TileRect, EmitDebugPreview and
DebugPreviewWriter from the Worker copy, and restores CalculateTimeDetail
which only existed here. No logic changes."
```

- [ ] **Step 10: Verification gate (user chạy)**

Chạy `RDL.GerberStitch.Harness.exe --mode alignstitch` trên bộ dữ liệu của lô tham chiếu. Kỳ vọng:
`aligned=54, blank=26, failed=0`. Task 0 không được đổi bất kỳ con số nào.

---

### Task 1: Bỏ ghi `processed_sample.tiff` khi `PreprocessMode=None`

**Files:**
- Modify: `GerberStitching.Core/Imaging/SampleTileGenerator.cs:123-124` (chỗ gọi `WriteProcessedSample`) và `:202` (`ProcessedSamplePath` trong `BuildManifest`)

**Interfaces:**
- Consumes: Task 0
- Produces: manifest có `ProcessedSamplePath` trỏ tới `SourceRasterPath` thay vì file copy 1.31 GB

`processed_sample.tiff` là bản copy y hệt ảnh gốc khi `PreprocessMode=None`. Nhưng **không xoá thẳng
được**: `DebugPreviewWriter.cs:28-47` và `SampleComparisonService.cs:120-152` đều đọc
`manifest.ProcessedSamplePath`. Giải pháp là trỏ nó về ảnh nguồn — hai consumer đó vẫn chạy đúng.

- [ ] **Step 1: Bọc lời gọi ghi file bằng điều kiện `PreprocessMode`**

Tại `SampleTileGenerator.cs:123-124`, thay:

```csharp
                var processedSampleFileName = "processed_sample.tiff";
                WriteProcessedSample(Path.Combine(temp, processedSampleFileName), run.ProcessedImage);
```

bằng:

```csharp
                // [Claude] [Change time: 2026-08-14] [Purpose: With PreprocessMode=None the processed sample is a
                // byte-identical copy of the source raster -- 1.31 GB written per prepare for nothing. Skip the write
                // and point the manifest at the source instead; DebugPreviewWriter and SampleComparisonService read
                // ProcessedSamplePath and work just as well against the source file.]
                var skipProcessedSampleCopy =
                    run.ConfigSnapshot.PreprocessMode == SamplePreprocessMode.None &&
                    !string.IsNullOrWhiteSpace(run.ConfigSnapshot.SourceRasterPath) &&
                    File.Exists(run.ConfigSnapshot.SourceRasterPath);
                if (!skipProcessedSampleCopy)
                    WriteProcessedSample(Path.Combine(temp, "processed_sample.tiff"), run.ProcessedImage);
```

- [ ] **Step 2: Cho `BuildManifest` biết đã bỏ qua hay chưa**

Đổi chữ ký `BuildManifest` để nhận thêm cờ. Tại `SampleTileGenerator.cs:202`, thay:

```csharp
                ProcessedSamplePath = Path.Combine(finalRoot, "processed_sample.tiff"),
```

bằng:

```csharp
                ProcessedSamplePath = skipProcessedSampleCopy
                                          ? run.ConfigSnapshot.SourceRasterPath
                                          : Path.Combine(finalRoot, "processed_sample.tiff"),
```

và truyền `skipProcessedSampleCopy` từ chỗ gọi vào `BuildManifest` như một tham số `bool`.

- [ ] **Step 3: Kiểm không còn chỗ nào giả định file tồn tại trong thư mục kết quả**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && grep -rn "processed_sample" --include=*.cs .
```

Kỳ vọng: chỉ còn 2 chỗ trong `SampleTileGenerator.cs` vừa sửa. Nếu có chỗ khác ghép đường dẫn bằng
tay thay vì đọc `manifest.ProcessedSamplePath`, sửa nó dùng manifest.

- [ ] **Step 4: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Step 5: Ghi entry `docs/implement_code.html`**

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Skip the processed_sample.tiff copy when PreprocessMode is None

The file was a byte-identical 1.31 GB copy of the source raster that
nothing read back. The manifest now points ProcessedSamplePath at the
source, which DebugPreviewWriter and SampleComparisonService accept."
```

- [ ] **Step 7: Verification gate (user chạy)**

Chạy prepare + align trên lô tham chiếu. Kỳ vọng: (a) không còn `processed_sample.tiff` trong thư
mục mẫu; (b) `aligned=54, blank=26, failed=0` không đổi; (c) bật `DebugMode=true` → `DebugPreview_*.jpg`
vẫn sinh ra bình thường.

---

### Task 2: `ISampleTileSource` + `DiskSampleTileSource` (hành vi không đổi)

**Files:**
- Create: `GerberStitching.Core/Alignment/ISampleTileSource.cs`
- Create: `GerberStitching.Core/Alignment/DiskSampleTileSource.cs`
- Modify: `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs` — dòng `50` (field), `142` (khởi tạo), `260` (dọn), `520`, `564`, `946`, `1354` (4 call site)

**Interfaces:**
- Consumes: Task 0
- Produces:
  - `public interface GerberViewer.Stitching.Alignment.ISampleTileSource : IDisposable`
    - `Mat GetTile(int orderIndex)` — trả `Mat` mono8 **thuộc sở hữu của source**, caller **không** dispose
    - `System.Drawing.Bitmap GetTileBitmap(int orderIndex)` — trả bản copy mới, **caller phải dispose**
  - `internal sealed class DiskSampleTileSource : ISampleTileSource` — ctor `DiskSampleTileSource(IDictionary<int, SampleTileInfo> tileByOrder, WorkflowImageCache cache)`

**Accessibility — bắt buộc đúng ngay từ đầu:** `ISampleTileSource` phải là **`public`** vì façade
(`RDL.GerberStitch`, assembly khác) truyền nó vào ctor `AlignStitchWorkflowService` ở Task 4. Ctor
overload nhận `ISampleTileSource` cũng phải `public`. `DiskSampleTileSource` giữ `internal` — chỉ Core
dùng. Đây là ngoại lệ có ý thức với AGENTS.md §3.1 ("không rò kiểu nội bộ Core ra khỏi façade"): façade
**không** phơi `ISampleTileSource` ra API công khai của nó, chỉ dùng nội bộ để dựng workflow.

Task này **không đổi hành vi gì**: vẫn đọc tile từ đĩa. Đây là điểm kiểm tra an toàn — nếu pose lệch
sau task này thì lỗi ở refactor, không phải ở cơ chế crop mới.

Lý do có `GetTileBitmap` riêng: call site `:520` (nhánh `aligner != null`, dùng bởi GerberViewer)
cần `System.Drawing.Bitmap` chứ không phải `Mat`, và nó đang bọc trong `using` nên phải nhận bản
copy sở hữu riêng. Ba call site còn lại dùng `Mat` không sở hữu, đúng như `WorkflowImageCache` hôm nay.

- [ ] **Step 1: Tạo `ISampleTileSource.cs`**

```csharp
using System;
using System.Drawing;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Decouple the workflow from "a sample tile is a file on disk".
    // The disk implementation keeps today's behaviour; the in-memory one crops the large sample raster at the
    // start of a lot, which removes the whole prepare-to-disk stage.]

    /// <summary>Supplies the sample tile image for one OrderIndex.</summary>
    // Public because the facade assembly constructs the workflow with a chosen implementation.
    public interface ISampleTileSource : IDisposable
    {
        /// <summary>Mono8 sample tile. The returned Mat is owned by the source and must NOT be disposed
        /// by the caller; it stays valid until the source itself is disposed.</summary>
        Mat GetTile(int orderIndex);

        /// <summary>Sample tile as a freshly allocated Bitmap. The CALLER owns and must dispose it.
        /// Used only by the legacy ISampleAligner branch, which takes System.Drawing types.</summary>
        Bitmap GetTileBitmap(int orderIndex);
    }
}
```

- [ ] **Step 2: Tạo `DiskSampleTileSource.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using GerberViewer.Stitching.Models;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Preserve the pre-existing behaviour exactly -- read
    // tile.ExpectedPath through WorkflowImageCache. GerberViewer Tab 2/3 and any caller that still supplies a
    // manifest with tile files on disk goes through this implementation.]

    internal sealed class DiskSampleTileSource : ISampleTileSource
    {
        private readonly IDictionary<int, SampleTileInfo> _tileByOrder;
        private readonly WorkflowImageCache _cache;

        /// <param name="cache">Not owned: the workflow owns and disposes it, as before.</param>
        public DiskSampleTileSource(IDictionary<int, SampleTileInfo> tileByOrder, WorkflowImageCache cache)
        {
            if (tileByOrder == null) throw new ArgumentNullException("tileByOrder");
            if (cache == null) throw new ArgumentNullException("cache");
            _tileByOrder = tileByOrder;
            _cache = cache;
        }

        public Mat GetTile(int orderIndex)
        {
            return _cache.GetMono8(PathFor(orderIndex));
        }

        public Bitmap GetTileBitmap(int orderIndex)
        {
            return new Bitmap(PathFor(orderIndex));
        }

        private string PathFor(int orderIndex)
        {
            SampleTileInfo tile;
            if (!_tileByOrder.TryGetValue(orderIndex, out tile))
                throw new KeyNotFoundException("No sample tile for OrderIndex " + orderIndex + ".");
            return tile.ExpectedPath;
        }

        // The cache belongs to the workflow, so this implementation owns nothing to release.
        public void Dispose()
        {
        }
    }
}
```

- [ ] **Step 3: Thêm field và khởi tạo trong `AlignStitchWorkflowService`**

Sau dòng `50` (`private WorkflowImageCache _imageCache;`) thêm:

```csharp
        private ISampleTileSource _tileSource;
```

Tại dòng `142`, chỗ `using (_imageCache = new WorkflowImageCache()) using (` — giữ nguyên, và ngay
**sau khi `tileByOrder` đã được dựng** trong cùng phương thức, thêm:

```csharp
                // [Claude] [Change time: 2026-08-14] [Purpose: Default to the disk source so this refactor changes
                // nothing. Task 3 injects InMemorySampleTileSource for the Worker path.]
                _tileSource = _externalTileSource ?? new DiskSampleTileSource(tileByOrder, _imageCache);
```

và thêm field + tham số ctor cho `_externalTileSource`:

```csharp
        private readonly ISampleTileSource _externalTileSource;
```

Bổ sung một ctor overload, giữ nguyên ctor cũ để mọi caller hiện tại không phải sửa:

```csharp
        public AlignStitchWorkflowService(Func<ISampleAligner> alignerFactory,
                                          IManualAlignmentProvider manualProvider,
                                          ISampleTileSource tileSource)
            : this(alignerFactory, manualProvider)
        {
            _externalTileSource = tileSource;
        }
```

Tại dòng `260` (`_imageCache = null;`) thêm ngay trước:

```csharp
                // Only dispose a source this service created; an injected one belongs to the caller.
                if (_externalTileSource == null && _tileSource != null) _tileSource.Dispose();
                _tileSource = null;
```

- [ ] **Step 4: Đổi call site `:520` (nhánh aligner)**

Thay:

```csharp
            using (var sample = LoadBitmap(tile.ExpectedPath)) using (var img = LoadBitmap(cap.FilePath))
```

bằng:

```csharp
            using (var sample = _tileSource.GetTileBitmap(tile.OrderIndex)) using (var img = LoadBitmap(cap.FilePath))
```

- [ ] **Step 5: Đổi call site `:564` (nhánh MatcherFactory)**

Thay `ReferenceImage = _imageCache.GetMono8(tile.ExpectedPath),` bằng:

```csharp
            var directRequest = new MatchRequest { ReferenceImage = _tileSource.GetTile(tile.OrderIndex),
```

- [ ] **Step 6: Đổi call site `:946` (Neighbor Recovery)**

Trong `CalculateSampleOverlapMetrics`, thay:

```csharp
            var sample = _imageCache.GetMono8(targetTile.ExpectedPath);
```

bằng:

```csharp
            var sample = _tileSource.GetTile(targetTile.OrderIndex);
```

- [ ] **Step 7: Đổi call site `:1354` (blank detection)**

Thay:

```csharp
            return new SampleTileContentAnalyzer().Analyze(_imageCache.GetMono8(tile.ExpectedPath),
                                                           config.LowTextureStdDevThreshold);
```

bằng:

```csharp
            return new SampleTileContentAnalyzer().Analyze(_tileSource.GetTile(tile.OrderIndex),
                                                           config.LowTextureStdDevThreshold);
```

- [ ] **Step 8: Kiểm không còn call site nào đọc `ExpectedPath` để lấy ảnh**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && grep -rn "ExpectedPath" --include=*.cs GerberStitching.Core/Alignment/
```

Kỳ vọng: chỉ còn `DiskSampleTileSource.cs`. `_imageCache.GetMono8` vẫn còn ở `:881-882` — **đúng**,
đó là ảnh **chụp** (`anchorCap.FilePath`, `target.FilePath`), không phải tile mẫu. Không đụng vào.

- [ ] **Step 9: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Step 10: Ghi entry `docs/implement_code.html`**

Ghi rõ đánh đổi: vì sao có **hai** phương thức (`GetTile` trả `Mat` không sở hữu, `GetTileBitmap` trả
`Bitmap` có sở hữu) thay vì một — call site `:520` là nhánh legacy dùng `System.Drawing` và đang bọc
trong `using`, gộp một phương thức sẽ sinh double-dispose hoặc rò `Mat`.

- [ ] **Step 11: Commit**

```bash
git add -A && git commit -m "Introduce ISampleTileSource and route the four sample-tile reads through it

DiskSampleTileSource reproduces the previous behaviour exactly, so this
change is behaviour-neutral. The captured-image reads at :881-882 keep
using the image cache directly -- they are captures, not sample tiles."
```

- [ ] **Step 12: Verification gate (user chạy) — ĐIỂM KIỂM TRA AN TOÀN**

Chạy lại lô tham chiếu và so `Debug_<date>.html` mới với bản của `AlignStitch_20260813_154108`:
bảng `Dx` / `Dy` / `DAngle` từng tile phải **trùng tuyệt đối**. Đây là refactor thuần — bất kỳ sai
lệch nào đều là bug của task này, và phải sửa xong trước khi sang Task 3.

---

### Task 3: `InMemorySampleTileSource`

**Files:**
- Create: `GerberStitching.Core/Alignment/InMemorySampleTileSource.cs`

**Interfaces:**
- Consumes: `ISampleTileSource` (Task 2); `IImageInteropService.ToMatCopy(HObject)` và
  `ToBitmapCopy(Mat)` từ `GerberViewer.Stitching.Imaging.ImageInterop`
- Produces:
  - `sealed class InMemorySampleTileSource : ISampleTileSource`
  - `static InMemorySampleTileSource CreateFromRaster(string rasterPath, IList<SampleTileRect> rects, string debugTileDirectory, CancellationToken ct)` — `debugTileDirectory` null = không ghi đĩa
  - `sealed class SampleTileRect { int OrderIndex; int Row; int Column; int X; int Y; int Width; int Height; }` trong namespace `GerberViewer.Stitching.Alignment`

Kiểu `SampleTileRect` là kiểu Core, tách biệt với `RDL.GerberStitch.Facade.TileRect` — façade dịch
giữa hai kiểu để không rò kiểu Core ra ngoài (AGENTS.md §3.1).

- [ ] **Step 1: Tạo `InMemorySampleTileSource.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GerberViewer.Stitching.Imaging.ImageInterop;
using HalconDotNet;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    /// <summary>One sample tile rectangle, in pixels on the sample raster.</summary>
    public sealed class SampleTileRect
    {
        public int OrderIndex { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    // [Claude] [Change time: 2026-08-14] [Purpose: Crop the sample tiles straight out of the large sample raster
    // instead of writing 80 TIFFs and reading them back. HALCON does the reading and cropping because OpenCV's
    // Cv2.ImRead refuses images above 2^30 pixels and the reference sample is 40418 x 32364 = 1.31e9 pixels.
    // Crops are stored as Mat because every consumer (MatchRequest, SampleTileContentAnalyzer,
    // CalculateSampleOverlapMetrics) takes Mat -- keeping HImage would force a conversion at each use.
    // The large image is disposed as soon as the crop loop ends, before alignment starts.]
    public sealed class InMemorySampleTileSource : ISampleTileSource
    {
        private readonly Dictionary<int, Mat> _tiles = new Dictionary<int, Mat>();

        private InMemorySampleTileSource()
        {
        }

        public static InMemorySampleTileSource CreateFromRaster(string rasterPath, IList<SampleTileRect> rects,
                                                                string debugTileDirectory,
                                                                System.Threading.CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rasterPath) || !File.Exists(rasterPath))
                throw new FileNotFoundException("Sample raster not found.", rasterPath);
            if (rects == null || rects.Count == 0)
                throw new ArgumentException("Tile rect list is empty.", "rects");

            var source = new InMemorySampleTileSource();
            var interop = new ImageInteropService();
            HObject large = null;
            try
            {
                HOperatorSet.ReadImage(out large, rasterPath);
                if (large == null || !large.IsInitialized())
                    throw new InvalidDataException("HALCON did not return a valid image: " + rasterPath);

                HTuple w = null, h = null;
                int sourceWidth, sourceHeight;
                try
                {
                    HOperatorSet.GetImageSize(large, out w, out h);
                    sourceWidth = w.I;
                    sourceHeight = h.I;
                }
                finally
                {
                    if (w != null) w.Dispose();
                    if (h != null) h.Dispose();
                }

                if (!string.IsNullOrWhiteSpace(debugTileDirectory))
                    Directory.CreateDirectory(debugTileDirectory);

                foreach (var rect in rects)
                {
                    ct.ThrowIfCancellationRequested();
                    Validate(rect, sourceWidth, sourceHeight);

                    HObject cropped = null;
                    try
                    {
                        // crop_rectangle1 takes an inclusive bottom-right corner.
                        HOperatorSet.CropRectangle1(large, out cropped, rect.Y, rect.X,
                                                    rect.Y + rect.Height - 1, rect.X + rect.Width - 1);
                        var mat = interop.ToMatCopy(cropped);
                        if (source._tiles.ContainsKey(rect.OrderIndex))
                        {
                            mat.Dispose();
                            throw new ArgumentException("Duplicate OrderIndex in tile rect list: " +
                                                        rect.OrderIndex, "rects");
                        }
                        source._tiles.Add(rect.OrderIndex, mat);

                        if (!string.IsNullOrWhiteSpace(debugTileDirectory))
                        {
                            var name = string.Format("Sample_R{0:00}_C{1:00}_O{2:000}.tiff",
                                                     rect.Row, rect.Column, rect.OrderIndex);
                            HOperatorSet.WriteImage(cropped, "tiff", 0, Path.Combine(debugTileDirectory, name));
                        }
                    }
                    finally
                    {
                        if (cropped != null && cropped.IsInitialized()) cropped.Dispose();
                    }
                }
            }
            catch
            {
                source.Dispose();
                throw;
            }
            finally
            {
                // Release the ~1.3 GB raster before alignment begins; nothing downstream needs it.
                if (large != null && large.IsInitialized()) large.Dispose();
            }
            return source;
        }

        private static void Validate(SampleTileRect r, int sourceWidth, int sourceHeight)
        {
            if (r.Width <= 0 || r.Height <= 0)
                throw new ArgumentOutOfRangeException("rects", "Tile " + r.OrderIndex + " has a non-positive size.");
            if (r.X < 0 || r.Y < 0 || r.X + r.Width > sourceWidth || r.Y + r.Height > sourceHeight)
                throw new ArgumentOutOfRangeException(
                    "rects", "Tile " + r.OrderIndex + " (" + r.X + "," + r.Y + " " + r.Width + "x" + r.Height +
                             ") falls outside the sample raster " + sourceWidth + "x" + sourceHeight + ".");
        }

        public Mat GetTile(int orderIndex)
        {
            Mat tile;
            if (!_tiles.TryGetValue(orderIndex, out tile))
                throw new KeyNotFoundException("No sample tile for OrderIndex " + orderIndex + ".");
            return tile;
        }

        public Bitmap GetTileBitmap(int orderIndex)
        {
            return new ImageInteropService().ToBitmapCopy(GetTile(orderIndex));
        }

        public void Dispose()
        {
            foreach (var tile in _tiles.Values)
                tile.Dispose();
            _tiles.Clear();
        }
    }
}
```

- [ ] **Step 2: Chặn tường minh `ModelGeneration = Pregenerate`**

Trong `AlignStitchWorkflowService`, ngay sau khi `_tileSource` được gán (Task 2 Step 3), thêm:

```csharp
                // Pregenerated NCC/shape models live next to the tile files on disk, so they cannot exist when the
                // tiles are cropped in memory. Fail loudly here rather than null-ref in the middle of a lot.
                if (_externalTileSource != null && manifest != null &&
                    string.Equals(manifest.ModelGeneration, "Pregenerate", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        "ModelGeneration=Pregenerate requires sample tile files on disk and is not supported by the " +
                        "in-memory tile source. Use ModelGeneration=OnTheFly.");
                }
```

- [ ] **Step 3: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Lỗi hay gặp: `HOperatorSet.CropRectangle1` cần `using HalconDotNet;` và reference `halcondotnetxl`
(đã có trong `GerberStitching.Core.csproj`); `Mat` mơ hồ giữa `OpenCvSharp.Mat` và kiểu khác nếu file
lỡ `using` thêm namespace — dùng tên đầy đủ thay vì thêm `using`.

- [ ] **Step 4: Ghi entry `docs/implement_code.html`**

Ghi rõ hai điều dễ quên khi đọc lại: (a) vì sao phải dùng HALCON để đọc ảnh lớn — giới hạn 2³⁰ pixel
của `Cv2.ImRead`; (b) vì sao lưu `Mat` chứ không phải `HImage`/`HTuple` — mọi consumer đều ăn `Mat`.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "Add InMemorySampleTileSource that crops the sample raster with HALCON

Reads the large sample with HALCON (OpenCV's ImRead rejects images over
2^30 pixels), crops every tile into an owned Mat, then disposes the raster
before alignment starts. Rejects ModelGeneration=Pregenerate explicitly."
```

---

### Task 4: Overload façade `RunAlignStitch` nhận ảnh sample + rect

**Files:**
- Modify: `RDL.GerberStitch/Facade/GerberStitchFacade.cs`

**Interfaces:**
- Consumes: `InMemorySampleTileSource.CreateFromRaster`, `SampleTileRect` (Task 3);
  `AlignStitchWorkflowService(Func<ISampleAligner>, IManualAlignmentProvider, ISampleTileSource)` (Task 2)
- Produces:
  ```csharp
  Task<AlignStitchResult> RunAlignStitch(
      string sampleImagePath, IList<TileRect> tiles, string capturedImagesFolder,
      AlignStitchConfig options, string outputRoot,
      IProgress<AlignStitchProgress> progress = null,
      CancellationToken cancellationToken = default(CancellationToken))
  ```

Overload mới **không đọc manifest từ đĩa** — nó dựng `SampleManifest` trong bộ nhớ từ `tiles`.
Overload cũ nhận `manifestPath` giữ nguyên, không đổi chữ ký.

- [ ] **Step 1: Tách phần thân dùng chung của `RunAlignStitch` hiện tại**

Rút phần từ `var runId = DateTime.Now...` đến hết thành một phương thức private:

```csharp
        private async Task<AlignStitchResult> RunCoreAsync(
            SampleManifest manifest, IList<CapturedImageInfo> images, string manifestPathForConfig,
            string capturedImagesFolder, AlignStitchConfig opts, string outputRoot,
            GerberViewer.Stitching.Alignment.ISampleTileSource tileSource,
            IProgress<AlignStitchProgress> progress, CancellationToken cancellationToken)
```

Bên trong, đổi đúng một dòng — chỗ tạo service:

```csharp
                var service = new AlignStitchWorkflowService(null, null, tileSource);
```

Overload cũ gọi `RunCoreAsync(..., tileSource: null, ...)`, nên `_externalTileSource == null` và
`DiskSampleTileSource` được dùng — hành vi cũ giữ nguyên.

- [ ] **Step 2: Thêm hàm dựng manifest trong bộ nhớ**

```csharp
        // [Claude] [Change time: 2026-08-14] [Purpose: The Worker no longer writes a manifest to disk, but Core still
        // needs a SampleManifest for tile geometry. ExpectedPath stays null: nothing reads it once the tile source is
        // in-memory, and SampleManifestSerializer.Validate is called with requireFiles:false accordingly.]
        private static SampleManifest BuildInMemoryManifest(string sampleImagePath, IList<TileRect> tiles,
                                                            int sourceWidth, int sourceHeight)
        {
            var ordered = tiles.OrderBy(t => t.OrderIndex).ToList();
            return new SampleManifest
            {
                ManifestVersion = SampleManifest.CurrentVersion,
                RootDirectory = Path.GetDirectoryName(sampleImagePath),
                SourceRasterPath = sampleImagePath,
                ProcessedSamplePath = sampleImagePath,
                SourceWidth = sourceWidth,
                SourceHeight = sourceHeight,
                ProcessedWidth = sourceWidth,
                ProcessedHeight = sourceHeight,
                CreatedUtc = DateTime.Now,
                ModelGeneration = SampleModelGenerationMode.OnTheFly.ToString(),
                Tiles = ordered.Select(t => new SampleTileInfo
                {
                    OrderIndex = t.OrderIndex,
                    Row = t.Row,
                    Column = t.Column,
                    ExpectedX = t.X,
                    ExpectedY = t.Y,
                    Width = t.Width,
                    Height = t.Height,
                    ExpectedPath = null
                }).ToList()
            };
        }
```

- [ ] **Step 3: Thêm overload công khai**

```csharp
        /// <summary>
        /// Runs Align -> Pose Graph -> Stitch for a whole lot, cropping the sample tiles from the sample raster in
        /// memory. No sample tiles or manifest are written to disk unless options.EmitDebugPreview is set.
        /// </summary>
        /// <param name="sampleImagePath">Sample raster (.tif/.tiff/.png/.bmp/.jpg). Gerber source files are rejected.</param>
        /// <param name="tiles">Explicit crop rectangles from the Master, one per captured image.</param>
        public async Task<AlignStitchResult> RunAlignStitch(
            string sampleImagePath, IList<TileRect> tiles, string capturedImagesFolder,
            AlignStitchConfig options, string outputRoot,
            IProgress<AlignStitchProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var opts = options ?? new AlignStitchConfig();
            try { opts.Validate(); }
            catch (Exception ex) { return AlignStitchResult.Fail("Invalid options: " + ex.Message); }

            if (string.IsNullOrWhiteSpace(sampleImagePath) || !File.Exists(sampleImagePath))
                return AlignStitchResult.Fail("Sample image not found: " + sampleImagePath);
            if (tiles == null || tiles.Count == 0)
                return AlignStitchResult.Fail("Tile rect list is empty.");
            if (string.IsNullOrWhiteSpace(capturedImagesFolder) || !Directory.Exists(capturedImagesFolder))
                return AlignStitchResult.Fail("Captured images folder not found: " + capturedImagesFolder);
            if (string.IsNullOrWhiteSpace(outputRoot))
                return AlignStitchResult.Fail("Output root is required.");

            int sourceWidth, sourceHeight;
            try
            {
                ReadRasterSize(sampleImagePath, out sourceWidth, out sourceHeight);
            }
            catch (Exception ex)
            {
                return AlignStitchResult.Fail("Sample image unreadable: " + ex.Message);
            }

            var manifest = BuildInMemoryManifest(sampleImagePath, tiles, sourceWidth, sourceHeight);

            var loadResult = new CapturedImageLoader().Load(capturedImagesFolder, manifest);
            if (!loadResult.Succeeded)
                return AlignStitchResult.Fail("Captured image mapping failed: " +
                                              string.Join("; ", loadResult.Errors));

            var coreRects = tiles.OrderBy(t => t.OrderIndex)
                                 .Select(t => new GerberViewer.Stitching.Alignment.SampleTileRect
                                 {
                                     OrderIndex = t.OrderIndex, Row = t.Row, Column = t.Column,
                                     X = t.X, Y = t.Y, Width = t.Width, Height = t.Height
                                 })
                                 .ToList();

            var debugTileDir = opts.EmitDebugPreview
                                   ? Path.Combine(outputRoot, "DebugSampleTiles_" +
                                                              DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                                   : null;
            if (debugTileDir != null)
            {
                // Spec 3.6: in debug mode the tiles AND a manifest land on disk, so a run can be replayed
                // through the disk tile source. requireFiles:false -- ExpectedPath is filled in below only
                // for the copies just written, and the in-memory run does not depend on them.
                Directory.CreateDirectory(debugTileDir);
                var debugManifest = BuildInMemoryManifest(sampleImagePath, tiles, sourceWidth, sourceHeight);
                foreach (var t in debugManifest.Tiles)
                {
                    t.ExpectedPath = Path.Combine(
                        debugTileDir, string.Format("Sample_R{0:00}_C{1:00}_O{2:000}.tiff",
                                                    t.Row, t.Column, t.OrderIndex));
                }
                SampleManifestSerializer.WriteValidated(
                    Path.Combine(debugTileDir, "sample_manifest.json"), debugManifest, false);
            }

            using (var tileSource = GerberViewer.Stitching.Alignment.InMemorySampleTileSource.CreateFromRaster(
                       sampleImagePath, coreRects, debugTileDir, cancellationToken))
            {
                return await RunCoreAsync(manifest, loadResult.Images, null, capturedImagesFolder, opts,
                                          outputRoot, tileSource, progress, cancellationToken);
            }
        }

        private static void ReadRasterSize(string path, out int width, out int height)
        {
            HObject image = null;
            HTuple w = null, h = null;
            try
            {
                HOperatorSet.ReadImage(out image, path);
                HOperatorSet.GetImageSize(image, out w, out h);
                width = w.I;
                height = h.I;
            }
            finally
            {
                if (w != null) w.Dispose();
                if (h != null) h.Dispose();
                if (image != null && image.IsInitialized()) image.Dispose();
            }
        }
```

- [ ] **Step 4: Thêm overload `CapturedImageLoader.Load` nhận manifest trong bộ nhớ**

`CapturedImageLoader.Load(folder, manifestPath)` hiện đọc lại manifest từ đĩa
(`GerberStitching.Core/Arrangement/CapturedImageLoader.cs:79-82`). Thêm overload nhận thẳng object,
và cho overload cũ gọi vào nó sau khi đọc file — không nhân đôi logic ánh xạ theo vị trí sort:

```csharp
        public CapturedImageLoadResult Load(string imageFolder, string manifestPath)
        {
            return Load(imageFolder, SampleManifestSerializer.Read(manifestPath));
        }

        public CapturedImageLoadResult Load(string imageFolder, SampleManifest manifest)
        {
            // ... phần thân cũ, bỏ dòng đọc manifest từ đĩa ...
        }
```

- [ ] **Step 5: Kiểm không có chỗ nào validate manifest với `requireFiles: true` trên đường mới**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && grep -rn "Validate(.*true)\|WriteValidated(.*true)" --include=*.cs GerberStitching.Core/
```

Mọi chỗ nằm trên đường in-memory phải dùng `requireFiles: false` — `ExpectedPath` là null và
`SampleManifest.cs:221-225` sẽ báo lỗi nếu bắt buộc có file.

- [ ] **Step 6: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Step 7: Ghi entry `docs/implement_code.html`**

Ghi rõ: `ExpectedPath = null` là hợp lệ trên đường in-memory, và mọi validate trên đường đó phải là
`requireFiles: false` — đây là chỗ dễ sinh lỗi "ExpectedPath missing at order N" khi ai đó sau này
đổi cờ.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "Add a RunAlignStitch overload taking the sample raster and tile rects

Builds the SampleManifest in memory with ExpectedPath left null and feeds
InMemorySampleTileSource to the workflow. The manifest-path overload is
unchanged and still uses the disk tile source."
```

- [ ] **Step 9: Verification gate (user chạy)**

Chạy `RDL.GerberStitch.Harness.exe` qua overload mới trên lô tham chiếu. Kỳ vọng: pose từng tile
trùng bit-by-bit với `AlignStitch_20260813_154108`; `aligned=54, blank=26, failed=0`;
`PeakWorkingSetMB` ≤ 7 185.

---

### Task 5: Worker bỏ Stage 1.5 và `TryReuseManifest`

**Files:**
- Modify: `RDL_WorkerNorthT_Ver2_SoftMerge_2026_08_09/RDL_Worker/GerberAlignStitch/GerberStitchRunner.cs` — bỏ khối `:271-327` (Stage 1.5), bỏ `TryReuseManifest` (`:550-588`), đổi lời gọi façade (`:414-421`), sửa `Validate` (`:453-495`)

**Interfaces:**
- Consumes: `GerberStitchFacade.RunAlignStitch(string, IList<TileRect>, string, AlignStitchConfig, string, IProgress<AlignStitchProgress>, CancellationToken)` (Task 4)
- Produces: `GerberStitchOutcome` không đổi — Form1 không phải sửa gì

- [ ] **Step 1: Xoá toàn bộ khối Stage 1.5**

Xoá từ comment `// ---------- Stage 1.5: sinh manifest từ rect Master gửi ----------` đến hết khối
`if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath)) { ... }` (`:271-327`),
kèm biến local `manifestPath`.

- [ ] **Step 2: Xoá phương thức `TryReuseManifest` (`:550-588`)**

Xoá cả `using Newtonsoft.Json.Linq;` nếu không còn chỗ nào dùng.

- [ ] **Step 3: Đổi lời gọi façade**

Thay khối `:414-421`:

```csharp
            var facade = new GerberStitchFacade();
            AlignStitchResult result = await facade.RunAlignStitch(
                manifestPath,
                request.CapturedImagesFolder,
                options,
                request.OutputRoot,
                progress,
                ct).ConfigureAwait(false);
```

bằng:

```csharp
            // [Tony] [Change time: 2026-08-14] [Purpose: Sample tiles are cropped in memory now, so there is no
            // prepare stage and no manifest on disk. TryReuseManifest is gone with it -- it only compared tile COUNT,
            // so a Master-side overlap change kept reusing stale tiles without a word.]
            var rects = request.Tiles
                .OrderBy(t => t.OrderIndex)
                .Select(t => new TileRect
                {
                    OrderIndex = t.OrderIndex, Row = t.Row, Column = t.Column,
                    X = t.ExpectedX, Y = t.ExpectedY, Width = t.Width, Height = t.Height
                })
                .ToList();

            var facade = new GerberStitchFacade();
            AlignStitchResult result = await facade.RunAlignStitch(
                request.SampleImagePath,
                rects,
                request.CapturedImagesFolder,
                options,
                request.OutputRoot,
                progress,
                ct).ConfigureAwait(false);
```

- [ ] **Step 4: Siết `Validate` — ảnh sample + rect giờ là bắt buộc**

Thay phần đầu `Validate` (`:455-473`):

```csharp
            bool hasGeometry = !string.IsNullOrWhiteSpace(request.SampleImagePath) &&
                               request.Tiles != null && request.Tiles.Length > 0;
            if (!hasGeometry)
                return "Master không gửi ảnh sample hoặc lưới tile (SampleImagePath/GerberTiles rỗng).";
            if (!File.Exists(request.SampleImagePath))
                return "Không tìm thấy ảnh sample: " + request.SampleImagePath;
            if (!IsRaster(request.SampleImagePath))
                return "Chỉ nhận ảnh raster; file Gerber gốc không được hỗ trợ: " + request.SampleImagePath;
```

- [ ] **Step 5: Bỏ log dòng manifest**

Tại `:256`, xoá `Log(_cb.LogInfo, "[Gerber] Manifest      = " + request.ManifestPath);` và thay bằng:

```csharp
            Log(_cb.LogInfo, "[Gerber] SampleImage   = " + request.SampleImagePath);
            Log(_cb.LogInfo, "[Gerber] Tiles         = " + request.Tiles.Length);
```

- [ ] **Step 6: Đánh dấu `GerberStitchRequest.ManifestPath` là không dùng nữa**

Giữ thuộc tính để `Form1` không vỡ, nhưng ghi rõ:

```csharp
        /// <summary>Không còn dùng từ 2026-08-14: tile mẫu được crop trong RAM, không có manifest trên đĩa.
        /// Giữ lại để Form1 biên dịch được; sẽ bỏ khi Form1 ngừng gán.</summary>
        [Obsolete("Sample tiles are cropped in memory; there is no manifest file.")]
        public string ManifestPath { get; set; }
```

- [ ] **Step 7: Build gate (solution Worker)**

```bash
msbuild RDL_Worker.sln /p:Configuration=Debug /p:Platform=x64
```

Chạy trong `H:\005_Project\AOI_2026_07_imp\20260812_RDL\RDL_WorkerNorthT_Ver2_SoftMerge_2026_08_09`.
Worker phải tham chiếu `RDL.GerberStitch.dll` vừa build ở Task 4. Lỗi hay gặp: cảnh báo CS0618 do
`[Obsolete]` ở Step 6 — nếu Worker bật `TreatWarningsAsErrors`, bọc chỗ gán bằng
`#pragma warning disable 618` thay vì gỡ attribute.

- [ ] **Step 8: Ghi entry `docs/implement_code.html`**

Ghi rõ vì sao `TryReuseManifest` bị xoá chứ không phải sửa: nó chỉ so **số lượng** tile và sự tồn
tại của file, không so `ExpectedX/Y/Width/Height` — Master đổi overlap mà giữ nguyên số tile sẽ tái
dùng nhầm bộ cũ, âm thầm.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "Drop the Worker prepare stage and TryReuseManifest

The facade now crops sample tiles in memory from the rects the Master
sends, so there is no manifest on disk to generate or reuse. Removing the
reuse path also removes its stale-tile hazard: it compared tile count
only, never the rect geometry."
```

- [ ] **Step 10: Verification gate (user chạy)**

Chạy lô thật qua Worker (F8 hoặc S050/T003 từ Master). Kỳ vọng:
1. Pose từng tile trùng bit-by-bit với `AlignStitch_20260813_154108`.
2. `aligned=54, blank=26, failed=0, ErrorCode=0`.
3. `PeakWorkingSetMB` ≤ 7 185.
4. Không còn thư mục mẫu (`GerberResult\<tên ảnh sample>\`) khi `DebugMode=false`.
5. Tổng thời gian lô giảm so với 319 451 ms.
6. Chạy **hai lô liên tiếp** cùng recipe — lô thứ hai phải chạy được (trước đây phụ thuộc
   `TryReuseManifest` để không vướng "Final run directory already exists").

---

### Task 6: Loader file setup JSON

**Files:**
- Create: `RDL.GerberStitch/Facade/Settings/AlignStitchSettingsFile.cs`
- Create: `RDL.GerberStitch/Facade/Settings/MatcherRegistry.cs`
- Create: `RDL.GerberStitch/Facade/Settings/AlignStitchSettingsReader.cs`
- Create: `RDL.GerberStitch/Facade/Settings/ConfigLayerLog.cs`
- Modify: `RDL.GerberStitch/Facade/AlignStitchConfigIniReader.cs` — thêm overload nhận config nền

**Interfaces:**
- Consumes: Task 0
- Produces:
  - `static GerberViewer.Stitching.Models.AlignStitchConfig AlignStitchSettingsReader.Read(string globalPath, string recipeOverridePath, Action<string> logWarning)`
  - `static void ConfigLayerLog.Dump(AlignStitchConfig effective, IDictionary<string,string> sourceByKey, Action<string> log)`
  - `static AlignStitchConfig AlignStitchConfigIniReader.ReadFromIni(string iniPath, AlignStitchConfig baseConfig, Action<string> logWarning)`

Thứ tự áp dụng: `mặc định code < file chung < override recipe < ini < payload Master`.

- [ ] **Step 1: Tạo `MatcherRegistry.cs` — DTO + phân giải loại → slot Core**

```csharp
using System;
using System.Collections.Generic;
using GerberViewer.Stitching.Configuration;
using GerberViewer.Stitching.Matching.Halcon;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Core keeps one options slot per (stage, matcher kind), which
    // duplicates the same knobs across DirectAlignment and NeighborAlignment. The settings file instead declares
    // each matcher KIND once and lets stages reference it by name; this class copies one definition into every
    // matching slot. Entry names are the Core enum constant names, so a stage value needs no translation.]

    /// <summary>Matcher definitions keyed by matcher kind name, as they appear in the "Matchers" block.</summary>
    public sealed class MatcherRegistry
    {
        public HalconNccOptions HalconNcc { get; set; }
        public HalconShapeModelOptions HalconShapeModel { get; set; }
        public EccOptions PyramidEcc { get; set; }
        public PhaseCorrelationOptions PyramidPhaseCorrelation { get; set; }

        /// <summary>Kind names legal in DirectAlignment.CoarseMatcher.</summary>
        private static readonly HashSet<string> DirectCoarseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "HalconNcc", "HalconShapeModel", "PyramidEcc", "PyramidPhaseCorrelation", "None" };

        /// <summary>Kind names legal in DirectAlignment.RefinementMatcher.</summary>
        private static readonly HashSet<string> DirectRefinementKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PyramidEcc", "PyramidPhaseCorrelation", "None" };

        /// <summary>Kind names legal in NeighborAlignment.CoarseMatcher. Neighbor production is phase-only.</summary>
        private static readonly HashSet<string> NeighborCoarseKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "PyramidPhaseCorrelation", "None" };

        /// <summary>Copies each declared matcher definition into every Core slot that uses that kind.</summary>
        public void ApplyTo(CoreConfig config)
        {
            if (HalconNcc != null) Copy(HalconNcc, config.DirectAlignment.Ncc);
            if (HalconShapeModel != null) config.DirectAlignment.Shape = HalconShapeModel;
            if (PyramidEcc != null)
            {
                Copy(PyramidEcc, config.DirectAlignment.Ecc);
                Copy(PyramidEcc, config.NeighborAlignment.Ecc);
            }
            if (PyramidPhaseCorrelation != null)
                Copy(PyramidPhaseCorrelation, config.NeighborAlignment.Phase);
        }

        public void ValidateReference(string stageKeyPath, string kindName, MatcherSlot slot)
        {
            if (string.IsNullOrWhiteSpace(kindName))
                throw new ArgumentException(stageKeyPath + " is empty; name a matcher kind or \"None\".");

            HashSet<string> legal;
            switch (slot)
            {
            case MatcherSlot.DirectCoarse: legal = DirectCoarseKinds; break;
            case MatcherSlot.DirectRefinement: legal = DirectRefinementKinds; break;
            default: legal = NeighborCoarseKinds; break;
            }
            if (!legal.Contains(kindName))
                throw new ArgumentException(stageKeyPath + " = \"" + kindName + "\" is not valid here. Allowed: " +
                                            string.Join(", ", new List<string>(legal).ToArray()) + ".");
            if (string.Equals(kindName, "None", StringComparison.OrdinalIgnoreCase)) return;
            if (!IsDeclared(kindName))
                throw new ArgumentException(stageKeyPath + " references matcher \"" + kindName +
                                            "\", which is not declared in the Matchers block.");
        }

        private bool IsDeclared(string kindName)
        {
            if (string.Equals(kindName, "HalconNcc", StringComparison.OrdinalIgnoreCase)) return HalconNcc != null;
            if (string.Equals(kindName, "HalconShapeModel", StringComparison.OrdinalIgnoreCase)) return HalconShapeModel != null;
            if (string.Equals(kindName, "PyramidEcc", StringComparison.OrdinalIgnoreCase)) return PyramidEcc != null;
            if (string.Equals(kindName, "PyramidPhaseCorrelation", StringComparison.OrdinalIgnoreCase)) return PyramidPhaseCorrelation != null;
            return false;
        }

        private static void Copy(HalconNccOptions from, HalconNccOptions to)
        {
            to.MinScore = from.MinScore; to.NumLevels = from.NumLevels;
            to.AngleStartRad = from.AngleStartRad; to.AngleExtentRad = from.AngleExtentRad;
            to.AngleStepRad = from.AngleStepRad; to.Metric = from.Metric;
            to.MaxMatches = from.MaxMatches; to.MaxOverlap = from.MaxOverlap;
            to.SubPixel = from.SubPixel; to.ModelRoiMarginPixels = from.ModelRoiMarginPixels;
        }

        private static void Copy(EccOptions from, EccOptions to)
        {
            to.MotionModel = from.MotionModel; to.PyramidLevels = from.PyramidLevels;
            to.MaxIterations = from.MaxIterations; to.Epsilon = from.Epsilon;
            to.MinCorrelation = from.MinCorrelation;
        }

        private static void Copy(PhaseCorrelationOptions from, PhaseCorrelationOptions to)
        {
            to.MinResponse = from.MinResponse; to.PyramidLevels = from.PyramidLevels;
        }
    }

    public enum MatcherSlot
    {
        DirectCoarse,
        DirectRefinement,
        NeighborCoarse
    }
}
```

- [ ] **Step 2: Tạo `AlignStitchSettingsFile.cs` — hình dạng file**

```csharp
using Newtonsoft.Json.Linq;

namespace RDL.GerberStitch.Facade.Settings
{
    /// <summary>
    /// Shape of GerberAlignStitch.settings.json. Both blocks may carry the same sections; Advanced is merged
    /// first and CommonlyTuned is layered on top, so the short block at the top of the file always wins.
    /// Kept as JObject rather than typed properties because the merge happens before deserialization.
    /// </summary>
    public sealed class AlignStitchSettingsFile
    {
        /// <summary>Must be 3. Versions 0/1 make Core's EnsureComposite overwrite the structured groups
        /// with flat legacy defaults.</summary>
        public int ConfigVersion { get; set; }

        public JObject CommonlyTuned { get; set; }
        public JObject Advanced { get; set; }
    }
}
```

- [ ] **Step 3: Tạo `AlignStitchSettingsReader.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    /// <summary>
    /// Reads GerberAlignStitch.settings.json (comments allowed) and maps it onto the Core config tree.
    /// The file is READ ONLY -- never serialize back over it, because Newtonsoft drops comments on write.
    /// </summary>
    public static class AlignStitchSettingsReader
    {
        /// <summary>Sections the file is allowed to contain. Anything else is a typo.</summary>
        private static readonly HashSet<string> KnownSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Matchers", "DirectAlignment", "NeighborAlignment", "Recovery", "PoseGraph", "Stitching", "Output" };

        /// <summary>Flat legacy fields that must never appear: Core's migration would let them overwrite the
        /// structured groups.</summary>
        private static readonly HashSet<string> BannedFlatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NccMinScore", "EccMinCorrelation", "MaxTranslationPixels", "MaxAbsRotationDeg", "MinScale", "MaxScale",
            "MinOverlapRatio", "AllowNccOnlyAcceptance", "AllowEccFromExpectedWhenNccFails", "EnableNeighborRecovery",
            "EnableAnchorInterpolation", "EnableDirectPoseOutlierCorrection", "RotationOutlierMadK",
            "AngleBandFloorDeg", "SignFlipGuardDeg", "AllowExpectedGridFallback",
            "RequireManualConfirmationForExpectedGrid", "StitchingEngine", "AlignmentMethod",
            "AutoPlaceExactZeroSample", "RevalidateSampleContent", "LowTextureStdDevThreshold",
            "AutoPlaceLowTextureSample", "BlankFallbackOverlapPolicy", "TiffMode", "BigTiffTileWidth",
            "BigTiffTileHeight", "PreviewUpdateInterval", "MaxPreviewMegapixels", "InputManifestPath",
            "CapturedFolderPath", "OutputPath"
        };

        /// <param name="globalPath">Station-wide settings file. Missing file is not an error.</param>
        /// <param name="recipeOverridePath">Optional per-recipe file holding only the keys to override.</param>
        /// <param name="logWarning">Called once per unknown key. Never null-checked away silently.</param>
        public static CoreConfig Read(string globalPath, string recipeOverridePath, Action<string> logWarning)
        {
            var config = new CoreConfig { ConfigVersion = 3 };

            var merged = new JObject();
            MergeFile(merged, globalPath, logWarning);
            MergeFile(merged, recipeOverridePath, logWarning);
            if (!merged.HasValues) return config;

            var registry = merged["Matchers"] == null
                               ? new MatcherRegistry()
                               : merged["Matchers"].ToObject<MatcherRegistry>();

            // Populate the stage trees first, so matcher references are readable, then overlay matcher params.
            PopulateStage(merged, "DirectAlignment", config.DirectAlignment);
            PopulateStage(merged, "NeighborAlignment", config.NeighborAlignment);
            PopulateStage(merged, "Recovery", config.Recovery);
            PopulateStage(merged, "PoseGraph", config.PoseGraph);
            PopulateStage(merged, "Stitching", config.Stitching);
            PopulateStage(merged, "Output", config.Output);

            registry.ValidateReference("DirectAlignment.CoarseMatcher",
                                       config.DirectAlignment.CoarseMatcher.ToString(), MatcherSlot.DirectCoarse);
            registry.ValidateReference("DirectAlignment.RefinementMatcher",
                                       config.DirectAlignment.RefinementMatcher.ToString(),
                                       MatcherSlot.DirectRefinement);
            registry.ValidateReference("NeighborAlignment.CoarseMatcher",
                                       config.NeighborAlignment.CoarseMatcher.ToString(),
                                       MatcherSlot.NeighborCoarse);

            registry.ApplyTo(config);
            return config;
        }

        private static void MergeFile(JObject target, string path, Action<string> logWarning)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            AlignStitchSettingsFile file;
            using (var reader = new JsonTextReader(new StreamReader(path)))
            {
                // JsonTextReader skips // and /* */ comments by default, which is what lets the file be annotated.
                file = new JsonSerializer().Deserialize<AlignStitchSettingsFile>(reader);
            }
            if (file == null) return;

            if (file.ConfigVersion != 0 && file.ConfigVersion != 3)
                throw new InvalidDataException(path + ": ConfigVersion must be 3, found " + file.ConfigVersion +
                                               ". Versions 0/1 trigger a legacy migration that overwrites the " +
                                               "structured option groups.");

            // Advanced first, CommonlyTuned on top: the short block at the top of the file wins.
            var settings = new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace };
            if (file.Advanced != null) { Check(file.Advanced, path, logWarning); target.Merge(file.Advanced, settings); }
            if (file.CommonlyTuned != null)
            {
                Check(file.CommonlyTuned, path, logWarning);
                WarnOnOverlap(file.Advanced, file.CommonlyTuned, path, logWarning);
                target.Merge(file.CommonlyTuned, settings);
            }
        }

        private static void Check(JObject block, string path, Action<string> logWarning)
        {
            foreach (var property in block.Properties())
            {
                if (BannedFlatKeys.Contains(property.Name))
                    throw new InvalidDataException(path + ": \"" + property.Name + "\" is a legacy flat field. " +
                                                   "Use the structured group instead; flat fields can overwrite it.");
                if (!KnownSections.Contains(property.Name) && logWarning != null)
                    logWarning(path + ": unknown section \"" + property.Name + "\" (check the spelling)");
            }
        }

        private static void WarnOnOverlap(JObject advanced, JObject common, string path, Action<string> logWarning)
        {
            if (advanced == null || logWarning == null) return;
            foreach (var leaf in common.Descendants())
            {
                var value = leaf as JValue;
                if (value == null) return;
                if (advanced.SelectToken(value.Path) != null)
                    logWarning(path + ": \"" + value.Path + "\" is set in both CommonlyTuned and Advanced; " +
                               "CommonlyTuned wins.");
            }
        }

        private static void PopulateStage(JObject merged, string sectionName, object target)
        {
            var section = merged[sectionName] as JObject;
            if (section == null) return;
            using (var reader = section.CreateReader())
            {
                new JsonSerializer { ObjectCreationHandling = ObjectCreationHandling.Reuse }.Populate(reader, target);
            }
        }
    }
}
```

- [ ] **Step 4: Tạo `ConfigLayerLog.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CoreConfig = GerberViewer.Stitching.Models.AlignStitchConfig;

namespace RDL.GerberStitch.Facade.Settings
{
    // [Claude] [Change time: 2026-08-14] [Purpose: Five precedence layers (code default < global file < recipe
    // override < ini < Master payload) are impossible to debug without this. Prints every value that differs from
    // the code default together with the layer that set it.]
    public static class ConfigLayerLog
    {
        /// <param name="sourceByKey">Key path -> layer name, filled in by whoever applied each layer.</param>
        public static void Dump(CoreConfig effective, IDictionary<string, string> sourceByKey, Action<string> log)
        {
            if (log == null) return;
            var defaults = new CoreConfig { ConfigVersion = 3 };

            log("[Gerber] --- effective config (values differing from code defaults) ---");
            log("[Gerber] matcher map: " + effective.DirectAlignment.CoarseMatcher +
                " -> DirectAlignment.CoarseMatcher; " + effective.DirectAlignment.RefinementMatcher +
                " -> DirectAlignment.RefinementMatcher; " + effective.NeighborAlignment.CoarseMatcher +
                " -> NeighborAlignment.CoarseMatcher + Recovery + PoseGraph");

            foreach (var line in Walk(string.Empty, effective, defaults, sourceByKey))
                log("[Gerber] " + line);
            log("[Gerber] --- end effective config ---");
        }

        private static IEnumerable<string> Walk(string prefix, object actual, object baseline,
                                                IDictionary<string, string> sourceByKey)
        {
            if (actual == null || baseline == null) yield break;
            foreach (var p in actual.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name;
                object a, b;
                try { a = p.GetValue(actual, null); b = p.GetValue(baseline, null); }
                catch { continue; }

                if (a != null && !p.PropertyType.IsPrimitive && !p.PropertyType.IsEnum &&
                    p.PropertyType != typeof(string) && p.PropertyType != typeof(decimal) &&
                    p.PropertyType.Namespace != null && p.PropertyType.Namespace.StartsWith("GerberViewer"))
                {
                    foreach (var line in Walk(path, a, b, sourceByKey)) yield return line;
                    continue;
                }

                var actualText = Format(a);
                if (actualText == Format(b)) continue;

                string layer;
                if (!sourceByKey.TryGetValue(path, out layer)) layer = "không rõ";
                yield return path.PadRight(56) + " = " + actualText + "   [" + layer + "]";
            }
        }

        private static string Format(object value)
        {
            if (value == null) return "null";
            var d = value as IFormattable;
            return d == null ? value.ToString() : d.ToString(null, CultureInfo.InvariantCulture);
        }
    }
}
```

- [ ] **Step 5: Cho `AlignStitchConfigIniReader` nhận config nền**

Trong `AlignStitchConfigIniReader.cs`, thay dòng `var config = new AlignStitchConfig();` bằng tham số:

```csharp
        /// <param name="baseConfig">Values already applied by lower layers (settings files). Null starts from
        /// the code defaults. INI keys override whatever is here, matching the documented precedence.</param>
        public static AlignStitchConfig ReadFromIni(string iniFilePath, AlignStitchConfig baseConfig,
                                                    Action<string> logWarning)
        {
            var config = baseConfig ?? new AlignStitchConfig();
            ...
        }
```

Giữ nguyên hai overload cũ (`ReadFromIni(path)` và `ReadFromIni(path, logWarning)`) bằng cách cho
chúng gọi vào overload mới với `baseConfig: null` — Worker hiện tại không phải sửa.

- [ ] **Step 6: Build gate**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Lỗi hay gặp: `HalconShapeModelOptions` nằm trong namespace `GerberViewer.Stitching.Matching.Halcon`
chứ không phải `...Configuration` — nếu CS0246, kiểm `using` ở `MatcherRegistry.cs`. `EccOptions` và
`PhaseCorrelationOptions` nằm trong `GerberViewer.Stitching.Configuration`.

- [ ] **Step 7: Ghi entry `docs/implement_code.html`**

Ghi rõ ba đánh đổi: (a) vì sao dùng `JObject` + `Populate` thay vì DTO đầy đủ — để deep-merge
`Advanced`/`CommonlyTuned` xảy ra **trước** deserialize, tránh phải viết logic "giá trị nào đã được
đặt tường minh"; (b) vì sao `BannedFlatKeys` ném lỗi chứ không cảnh báo — field phẳng có thể ghi đè
tầng structured qua `EnsureComposite`, hỏng âm thầm; (c) `Matchers` được áp **sau** stage, để tham
chiếu đọc được trước khi copy tham số.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "Add the JSON settings reader with a kind-keyed matcher registry

Merges the Advanced and CommonlyTuned blocks, resolves each stage's
matcher reference against the registry, and copies one definition per kind
into every Core slot that uses it. Rejects legacy flat fields outright and
warns on unknown sections."
```

---

### Task 7: Sinh file `GerberAlignStitch.settings.json`

**Files:**
- Create: `GerberAlignStitch.settings.json` (cạnh Worker exe; bản mẫu commit vào
  `RDL.GerberStitch/samples/GerberAlignStitch.settings.json`)

**Interfaces:**
- Consumes: `AlignStitchSettingsReader.Read` (Task 6)
- Produces: file cấu hình ~120 tham số

- [ ] **Step 1: Liệt kê giá trị mặc định thật của từng nhóm để chép đúng vào file**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch/GerberStitching.Core" && grep -n "public .*{ get; set; }" Configuration/AlignStitchStageOptions.cs Alignment/Graph/PoseGraphOptions.cs Matching/Halcon/HalconShapeModelOptions.cs Alignment/Evaluation/DirectCandidateEvaluator.cs
```

- [ ] **Step 2: Viết file với đủ 4 entry matcher và 6 stage**

Mỗi tham số một dòng chú thích tiếng Anh: **ý nghĩa**, **ảnh hưởng khi tăng/giảm**, và **Core default**
khi giá trị RDL khác nó. Hai chỗ đã biết là lệch, phải ghi chú:
`Stitching.EnableBlending` (Core `true`, RDL `false`) và `DirectAlignment.Geometry.MaxAbsRotationDeg`
(Core `0.5`, RDL `0.1`).

Không đưa vào file: `NeighborAlignment.RefinementMatcher`, `NeighborAlignment.Ecc` (obsolete,
deserialize-only), `Input.ManifestPath`, `Input.CapturedFolderPath`, `Output.OutputPath` (per-lô),
và mọi field phẳng legacy trong `BannedFlatKeys`.

**Khung dưới đây là bộ xương, không phải sản phẩm.** Mọi chỗ ghi `/* N keys */` phải được viết ra
đầy đủ từng khoá kèm chú thích — file giao nộp không được còn một dấu `/* … */` nào. Step 4 là chốt
chặn cho việc này.

Khung file:

```jsonc
{
  // Pinned to 3. Do not change: ConfigVersion 0/1 triggers a legacy migration
  // that overwrites the structured groups below with flat legacy defaults.
  "ConfigVersion": 3,

  // ==== Commonly tuned ====================================================
  // The handful of values that get adjusted on the line. Anything set here
  // overrides the same key in the Advanced block below.
  "CommonlyTuned": {
    "Matchers": {
      "HalconShapeModel": {
        // Minimum HALCON shape-model match score, 0..1. Raise it to reject
        // weak matches; lower it if sparse tiles stop matching at all.
        // Do NOT set this to 0.7: that is 5-7x the real default and rejects
        // almost every tile.
        "MinScore": 0.10
      },
      "PyramidEcc": {
        // Minimum ECC correlation for the refinement stage, 0..1.
        "MinCorrelation": 0.13
      }
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
    },
    "Stitching": {
      // RDL production value. Core default is true. HOperatorSet.GenProjectiveMosaic
      // has no blending parameter, so with the HALCON engine this only produces a
      // warning and overlaps stay hard-overwritten. For real blending switch Engine
      // to OpenCv, at roughly 17x slower stitching.
      "EnableBlending": false
    }
  },

  // ==== Advanced ==========================================================
  // Full definitions. Stage matcher references live here.
  "Advanced": {
    "Matchers": {
      "HalconNcc": { /* 10 keys */ },
      "HalconShapeModel": { /* 19 keys */ },
      "PyramidEcc": { /* 5 keys */ },
      "PyramidPhaseCorrelation": {
        // NOTE: PoseGraph also reads MinResponse from the matcher that
        // NeighborAlignment.CoarseMatcher points at, to weight its edges.
        // Changing it affects both neighbor matching and pose-graph solving.
        "MinResponse": 0.15,
        "PyramidLevels": 3
      }
    },
    "DirectAlignment":   { "CoarseMatcher": "HalconShapeModel", "RefinementMatcher": "PyramidEcc" /* … */ },
    "NeighborAlignment": { "CoarseMatcher": "PyramidPhaseCorrelation" /* … */ },
    // Policy flags only. Recovery matching runs on the matcher that
    // NeighborAlignment.CoarseMatcher references -- Core has no separate slot.
    "Recovery":          { /* 10 keys */ },
    "PoseGraph":         { /* 19 keys */ },
    "Stitching":         { /* 8 keys, minus EnableBlending above */ },
    "Output":            { /* 5 keys */ }
  }
}
```

- [ ] **Step 3: Kiểm file parse được và ConfigVersion đúng**

```bash
cd "H:/005_Project/AOI_2026_07_imp/20260812_RDL/RDL.GerberStitch" && python -c "import json,re,io; s=open('samples/GerberAlignStitch.settings.json',encoding='utf-8').read(); s=re.sub(r'//.*','',s); s=re.sub(r'/\*.*?\*/','',s,flags=16); d=json.loads(s); print('ConfigVersion',d['ConfigVersion']); print('sections',sorted(d['Advanced'].keys()))"
```

Kỳ vọng: `ConfigVersion 3` và đúng 7 section (`Matchers`, `DirectAlignment`, `NeighborAlignment`,
`Recovery`, `PoseGraph`, `Stitching`, `Output`).

- [ ] **Step 4: Đối chiếu số khoá với code**

Đếm khoá trong file và so với `grep` ở Step 1. Chênh lệch hợp lệ duy nhất là các mục đã cố ý loại
(`NeighborAlignment.RefinementMatcher`, `NeighborAlignment.Ecc`, 3 đường dẫn per-lô). Bất kỳ khoá
nào khác bị thiếu là thiếu sót — bổ sung.

- [ ] **Step 5: Ghi entry `docs/implement_code.html`**

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "Add the annotated GerberAlignStitch settings file

Every pipeline option in one commented JSON file: a matcher registry keyed
by kind plus six stage sections, split into a CommonlyTuned block and a
full Advanced block. Ships RDL production values, noting Core's default
wherever the two differ."
```

- [ ] **Step 7: Verification gate (user chạy)**

1. Đổi `Matchers.HalconShapeModel.MinScore` trong file chung → giá trị phải xuất hiện ở đúng
   `DirectAlignment.Shape.MinScore` trong `effective_config.json` của lô.
2. Đổi `Matchers.PyramidEcc.PyramidLevels` → phải xuất hiện ở **cả** `DirectAlignment.Ecc` lẫn
   `NeighborAlignment.Ecc`.
3. Trỏ `NeighborAlignment.CoarseMatcher` tới `"HalconNcc"` → lô **không chạy**, báo lỗi rõ ràng.
4. Đặt cùng một khoá ở cả file chung và ini → log dump ghi `[ini]` và giá trị ini thắng.
5. Thêm một khoá sai chính tả → warning trong log Worker, lô vẫn chạy.
6. Chạy lô tham chiếu với file mặc định → pose từng tile trùng bit-by-bit với
   `AlignStitch_20260813_154108`.

---

### Task 8: Nối file setup vào đường chạy + ghi `effective_config.json`

**Files:**
- Modify: `RDL.GerberStitch/Facade/Settings/AlignStitchSettingsReader.cs` — báo tầng nào set khoá nào
- Modify: `RDL.GerberStitch/Facade/AlignStitchConfig.cs` — thêm 2 đường dẫn file setup
- Modify: `RDL.GerberStitch/Facade/GerberStitchFacade.cs` — `BuildCoreConfig` áp 5 tầng, ghi `effective_config.json`
- Modify: `RDL_Worker/GerberAlignStitch/GerberStitchRunner.cs` — nạp đường dẫn file setup, nối log sink

**Interfaces:**
- Consumes: `AlignStitchSettingsReader.Read`, `ConfigLayerLog.Dump`, `MatcherRegistry` (Task 6);
  `AlignStitchConfigIniReader.ReadFromIni(string, AlignStitchConfig, Action<string>)` (Task 6 Step 5)
- Produces:
  - `AlignStitchConfig.SettingsFilePath` (string), `AlignStitchConfig.RecipeSettingsPath` (string),
    `AlignStitchConfig.LogWarning` (`Action<string>`)
  - `static CoreConfig AlignStitchSettingsReader.Read(string globalPath, string recipeOverridePath, Action<string> logWarning, out IDictionary<string,string> sourceByKey)`
  - `effective_config.json` trong mỗi thư mục `AlignStitch_<timestamp>`

Façade tự nạp file setup bên trong `BuildCoreConfig` — Worker chỉ đưa **đường dẫn**, không đụng tới
kiểu Core. Nhờ vậy toàn bộ thứ tự ưu tiên nằm gọn ở một chỗ duy nhất.

- [ ] **Step 1: Cho `AlignStitchSettingsReader.Read` báo tầng nguồn của từng khoá**

Đổi chữ ký và ghi lại đường dẫn khoá khi merge từng file:

```csharp
        public static CoreConfig Read(string globalPath, string recipeOverridePath, Action<string> logWarning,
                                      out IDictionary<string, string> sourceByKey)
        {
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sourceByKey = sources;

            var config = new CoreConfig { ConfigVersion = 3 };
            var merged = new JObject();
            MergeFile(merged, globalPath, "file chung", sources, logWarning);
            MergeFile(merged, recipeOverridePath, "override recipe", sources, logWarning);
            ...
        }
```

Trong `MergeFile`, sau khi merge mỗi block, ghi nguồn cho mọi khoá lá:

```csharp
        private static void RecordSources(JObject block, string layerName, IDictionary<string, string> sources)
        {
            if (block == null) return;
            foreach (var descendant in block.Descendants())
            {
                var value = descendant as JValue;
                if (value == null) continue;
                // JValue.Path is "DirectAlignment.Geometry.MaxAbsRotationDeg" -- the same shape
                // ConfigLayerLog.Walk builds, so the two line up without a translation table.
                sources[value.Path] = layerName;
            }
        }
```

Giữ overload 3 tham số cũ gọi vào overload mới và bỏ `sourceByKey` đi, để Task 6 không phải sửa lại.

- [ ] **Step 2: Thêm đường dẫn file setup vào config façade**

Trong `RDL.GerberStitch/Facade/AlignStitchConfig.cs`:

```csharp
        // ── Settings file ──

        /// <summary>
        /// Station-wide GerberAlignStitch.settings.json. Null or a missing file is not an error: the
        /// pipeline then runs on code defaults plus whatever the INI and the Master payload set.
        /// </summary>
        public string SettingsFilePath { get; set; }

        /// <summary>
        /// Optional per-recipe override file holding only the keys to change. Applied after
        /// SettingsFilePath and before the INI.
        /// </summary>
        public string RecipeSettingsPath { get; set; }

        /// <summary>
        /// Sink for configuration warnings (unknown key, duplicated key, ...) and for the
        /// effective-config dump. Null silences both; the Worker always supplies its logger.
        /// </summary>
        public Action<string> LogWarning { get; set; }
```

- [ ] **Step 3: Áp 5 tầng trong `BuildCoreConfig`**

Thay đoạn đầu của `BuildCoreConfig`:

```csharp
        private static GerberViewer.Stitching.Models.AlignStitchConfig BuildCoreConfig(
            AlignStitchConfig options, string manifestPath, string capturedFolderPath, string outputPath)
        {
            // [Claude] [Change time: 2026-08-14] [Purpose: One place for the whole precedence chain:
            // code defaults < global settings file < recipe override < INI < Master payload. The INI and the
            // Master payload both arrive already folded into `options` by the Worker, so they are applied last,
            // on top of whatever the settings files set.]
            IDictionary<string, string> sourceByKey;
            var baseConfig = Settings.AlignStitchSettingsReader.Read(
                options.SettingsFilePath, options.RecipeSettingsPath, options.LogWarning, out sourceByKey);

            baseConfig.Input.ManifestPath = manifestPath;
            baseConfig.Input.CapturedFolderPath = capturedFolderPath;

            var engine = AlignStitchConfig.ParseEngine(options.StitchingEngine);
            if (engine.HasValue) { baseConfig.Stitching.Engine = engine.Value; sourceByKey["Stitching.Engine"] = "ini"; }
            baseConfig.Stitching.EnableBlending = options.EnableBlending;
            baseConfig.CalculateTimeDetail = options.CalculateTimeDetail;
            baseConfig.EmitDebugPreview = options.EmitDebugPreview;

            baseConfig.DirectAlignment.Ncc.MinScore = options.NccMinScore;
            baseConfig.DirectAlignment.Shape.MinScore = options.NccMinScore;
            baseConfig.DirectAlignment.Ecc.MinCorrelation = options.EccMinCorrelation;
            baseConfig.DirectAlignment.Geometry.MaxTranslationPixels = options.MaxTranslationPixels;
            baseConfig.DirectAlignment.Geometry.MaxAbsRotationDeg = options.MaxAbsRotationDeg;
            baseConfig.DirectAlignment.Policy.AllowCoarseOnlyAcceptance = options.AllowCoarseOnlyAcceptance;
            baseConfig.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails =
                options.AllowRefinementFromExpectedWhenCoarseFails;

            foreach (var key in new[] { "Stitching.EnableBlending", "DirectAlignment.Ncc.MinScore",
                                        "DirectAlignment.Shape.MinScore", "DirectAlignment.Ecc.MinCorrelation",
                                        "DirectAlignment.Geometry.MaxTranslationPixels",
                                        "DirectAlignment.Geometry.MaxAbsRotationDeg",
                                        "DirectAlignment.Policy.AllowCoarseOnlyAcceptance",
                                        "DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails" })
                sourceByKey[key] = "ini";

            AlignStitchConfigMapper.EnsureComposite(baseConfig);
            Settings.ConfigLayerLog.Dump(baseConfig, sourceByKey, options.LogWarning);

            return AlignStitchConfigMapper.CloneForRun(baseConfig, outputPath);
        }
```

Lưu ý `DirectAlignment.Shape.MinScore` cũng được gán từ `options.NccMinScore`: matcher coarse mặc
định là `HalconShapeModel`, và khoá ini lịch sử tên là `NccMinScore` — doc `Processing_GerberAlignStitch.html`
đã ghi ini `NccMinScore = 0.10` chính là ngưỡng của `HalconShapeModelMatcher`. Không gán cả hai thì
đổi ini sẽ không có tác dụng.

- [ ] **Step 4: Ghi `effective_config.json` vào thư mục lô**

Trong `RunCoreAsync`, ngay sau khi `coreConfig` được dựng và `creatingDir` đã tồn tại:

```csharp
                // The settings file itself is never rewritten -- Newtonsoft drops comments on write. This is a
                // comment-free snapshot of the merged result, for audit only.
                try
                {
                    File.WriteAllText(Path.Combine(creatingDir, "effective_config.json"),
                                      Newtonsoft.Json.JsonConvert.SerializeObject(
                                          coreConfig, Newtonsoft.Json.Formatting.Indented));
                }
                catch (Exception ex)
                {
                    // An audit file must never be able to fail a lot.
                    if (opts.LogWarning != null)
                        opts.LogWarning("Could not write effective_config.json: " + ex.Message);
                }
```

- [ ] **Step 5: Worker nạp đường dẫn file setup**

Trong `GerberStitchRunner.RunBatchCoreAsync`, ngay sau khi `options` được đọc từ ini:

```csharp
            // [Tony] [Change time: 2026-08-14] [Purpose: Đường dẫn file setup lấy từ ini để trạm đổi được mà
            // không build lại; mặc định là file cạnh exe. Override theo recipe nằm trong thư mục recipe.]
            options.SettingsFilePath = ReadIniValue(request.IniPath, "SettingsPath");
            if (string.IsNullOrWhiteSpace(options.SettingsFilePath))
            {
                options.SettingsFilePath = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "GerberAlignStitch.settings.json");
            }
            options.RecipeSettingsPath = Path.Combine(
                Path.GetDirectoryName(request.SampleImagePath) ?? string.Empty,
                "GerberAlignStitch.settings.json");
            options.LogWarning = text => Log(_cb.LogWarning, "[Gerber] " + text);
```

Thêm `"SettingsPath"` vào `AlignStitchConfigIniReader.KnownKeys`, nếu không nó sẽ bị báo là khoá lạ.

- [ ] **Step 6: Build gate cả hai solution**

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

rồi

```bash
msbuild RDL_Worker.sln /p:Configuration=Debug /p:Platform=x64
```

- [ ] **Step 7: Ghi entry `docs/implement_code.html`**

Ghi rõ hai điều dễ mất: (a) `NccMinScore` trong ini gán cho **cả** `Ncc.MinScore` lẫn
`Shape.MinScore` — vì matcher coarse thật sự đang chạy là `HalconShapeModel` dù tên khoá ini nói
"Ncc"; (b) `effective_config.json` là snapshot chỉ-đọc, **không** được dùng để ghi ngược lên file
setup vì Newtonsoft làm mất comment.

- [ ] **Step 8: Commit**

```bash
git add -A && git commit -m "Wire the settings files into the run and snapshot the effective config

BuildCoreConfig now applies the whole precedence chain in one place --
defaults, global file, recipe override, INI, Master payload -- logs every
value that differs from a default with the layer that set it, and drops a
comment-free effective_config.json into each lot directory."
```

- [ ] **Step 9: Verification gate (user chạy)**

Chạy toàn bộ 6 mục kiểm ở Task 7 Step 7 — chúng chỉ thật sự kiểm được sau task này, vì trước đó
chưa có ai đọc file setup.

---

## Ghi chú thực thi

- Task 1→5 và Task 6→7→8 chỉ chung phụ thuộc ở Task 0, **làm song song được**. Riêng Task 8 Step 5
  sửa `GerberStitchRunner.cs` — cùng file với Task 5, nên hai task đó phải nối tiếp, không song song.
- **Task 2 Step 12 là cổng chặn:** không sang Task 3 khi pose chưa trùng bit-by-bit.
- **Task 6 và 7 chưa kiểm được bằng dữ liệu thật** — file setup chưa có ai đọc cho tới Task 8. Cổng
  nghiệm thu thật của nhánh config nằm ở Task 8 Step 9.
- Không có task nào đổi hành vi pipeline trong `GerberStitching.Core`. Nếu trong lúc làm phát hiện
  một thay đổi hành vi là **bắt buộc**, dừng và hỏi user (AGENTS.md §3.1, §6) thay vì tự quyết.
