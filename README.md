# RDL.GerberStitch

## English

A façade library that packages the **Gerber image alignment and stitching pipeline**, ported from `tony71200/GerberViewer` branch `2026-08-04_Ver4_implement_claude`, for use by the **RDL Master/Worker AOI** system. This repository covers Phases 0 and 1 of the integration roadmap.

> Core integration principle: **do not change the main AOI workflow**. Gerber Align/Stitch is added as a new `WorkModel` alongside the existing workflow. See the [English integration roadmap](docs/roadmap_GerberAlignStitch_integration_EN.md).

### Architecture

```text
RDL.GerberStitch (façade)  ──depends on──►  GerberStitching.Core, GerberEngine
RDL.GerberStitch.Harness   ──depends on──►  RDL.GerberStitch (+ GerberStitching.Core for createsample)
```

| Project | Responsibility |
|---|---|
| **`GerberEngine`** | Pure Gerber parser and renderer (`GerberParser`, `GerberRenderer`), with no HALCON or OpenCV dependency. |
| **`GerberStitching.Core`** | The complete alignment and stitching pipeline under the `GerberViewer.Stitching` namespace. Depends on HALCON 25.05 and OpenCvSharp4 4.13. |
| **`RDL.GerberStitch`** | The **only façade** referenced by Master/Worker. Its main methods are `GenerateSampleManifest`, `GenerateSampleManifestFromRaster`, and `RunAlignStitch`. |
| **`RDL.GerberStitch.Harness`** | A console application for exercising the façade with real data; it is not an NUnit/xUnit test project. |

Read [AGENTS.md](AGENTS.md) and [CLAUDE.md](CLAUDE.md) before changing any file.

### Align and Stitch workflow (Pose Graph, Version 4)

`GerberStitching.Core` implements the workflow described in [flow_v4.md](flow_v4.md) and [Flow_V4.html](Flow_V4.html). The entry point is `AlignStitchWorkflowService.RunAsync`, which calls `RunCore`.

```mermaid
flowchart LR
    A["① Input\nmanifest + captured"] --> B["② Preprocess\nvalidate + map"]
    B --> C["③ Direct Alignment\nHalconShapeModel → PyramidEcc"]
    C --> D["④ Failure Recovery\nneighbor rescue (2 passes)"]
    D --> E["⑤ Neighbor Graph\nmeasure every Manhattan-adjacent pair"]
    E -->|PoseGraph.Enabled| F["⑥ Pose-Graph Optimizer\nGauss-Newton + IRLS Huber"]
    E -->|legacy| G["Anchor + bottleneck propagation"]
    F --> H["⑦ Validate"]
    G --> H
    H --> I["⑧ Stitch\nGenProjectiveMosaic"]
    I --> J["⑨ Output\nStitched.tiff + report"]
```

The nine main stages are:

1. **Input** — validate the manifest and captured-image folder, then create the `.creating/` directory through `RunOutputLifecycle`.
2. **Preprocess** — call `AlignStitchConfigMapper.EnsureComposite`/`SyncLegacy` and map images to tiles by manifest `OrderIndex`.
3. **Direct Alignment** — run coarse `HalconShapeModel` matching and refine it with `PyramidEcc`. This stage consumes about 90% of total processing time.
4. **Failure Recovery** — rescue failed tiles in two neighbor passes: predecessors only, then predecessors and successors.
5. **Neighbor Graph** — measure every Manhattan-adjacent tile pair with `PyramidPhaseCorrelation` and record `RecoveryEdges`. With Pose Graph enabled, this stage measures only and does not propagate poses.
6. **Pose-Graph Optimizer** — solve all poses globally with Gauss-Newton and Huber IRLS. `MaxPoseCorrectionPixels` prevents application of implausibly large corrections.
7. **Validation** — validate scale, rotation, finite coordinates, canvas bounds, and tile connectivity.
8. **Stitching** — use `HOperatorSet.GenProjectiveMosaic` through the default `HalconProjectiveMosaicRebased` engine. HALCON does not provide seam blending here; this is a known limitation.
9. **Output** — write `Stitched.tiff` and `processing_report.json`, then publish `.creating/` as the final run directory.

Reference results for the 80-tile dataset are documented in [Phase0_Closeout.md](docs/Phase0_Closeout.md) and [Phase1_Task06.md](docs/Phase1_Task06.md):

| Metric | Measured value |
|---|---|
| Total time | 247–303 seconds; Direct Alignment accounts for about 90% |
| Peak RAM | About 6.5–6.6 GB for a roughly 40k × 32k canvas |
| Median seam residual before/after pose graph | 5.35 px → 0.38 px |
| `Stitched.tiff` size | About 1.3 GB with HALCON; about 654 MB with OpenCV |

### Façade implementation status

| Method | Status | Use |
|---|---|---|
| `GenerateSampleManifest(gerberFilePath, ...)` | ✅ Implemented and exercised with real data | Render an original Gerber file through `GerberEngineFacade`, then create tiles. |
| `GenerateSampleManifestFromRaster(rasterImagePath, ...)` | ✅ Implemented and exercised with real data | Read an already-rendered raster directly through HALCON without rendering it again. |
| `RunAlignStitch(manifestPath, capturedImagesFolder, ...)` | ✅ Implemented and exercised with real data | Run Align → Pose Graph → Stitch for a Worker batch. |

All three methods have been checked against a reference run with real data through `RDL.GerberStitch.Harness`. See [implement_code.html](docs/implement_code.html) for implementation decisions, tradeoffs, and resolved issues.

### Build

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Release /p:Platform=x64
```

- Set **`HALCONROOT`** to a HALCON 25.05 Progress installation.
- Build **x64 only**; do not build AnyCPU or x86.
- Required NuGet packages, including OpenCvSharp4 and System.Drawing.Common, are present under `packages/`.

### Run the harness with real data

```bat
RDL.GerberStitch.Harness\bin\x64\Release\RDL.GerberStitch.Harness.exe
```

The harness supports `--mode alignstitch` (default) and `--mode createsample`. Parameters come from command-line arguments or `global_config.json` beside the executable; command-line arguments take precedence. See [Phase1_Task06.md](docs/Phase1_Task06.md).

### Documentation

| File | Contents |
|---|---|
| [English integration roadmap](docs/roadmap_GerberAlignStitch_integration_EN.md) | Context, architectural decisions, and the six integration phases. |
| [Phase 0 closeout](docs/Phase0_Closeout.md) | Measurements from six real runs and Phase 0 decisions. |
| [Phase 1 tasks](docs/Phase1_Task01.md) | Task-by-task Phase 1 specifications; continue through `Phase1_Task06.md`. |
| [Deployment dependencies](docs/deploy_deps.md) | Dependency checklist for deploying the façade to Master/Worker. |
| [Implementation log](docs/implement_code.html) | Changed files, implementation notes, and issue resolutions. |
| [Version 4 flow](flow_v4.md) | Detailed Pose Graph pipeline flow, also available as [HTML](Flow_V4.html). |

### Open items

- Resolve Phase 0 blockers 3 and 4: shared-storage capacity for approximately 1.3 GB per batch and a grid configuration matching the production capture grid.
- Upgrade Master/Worker from `halcondotnetxl` 18.11.1.1 to HALCON 25.05 before deploying this façade.
- Measure the real performance benefit of pregenerated HALCON models (`SampleModelGenerationMode.Pregenerate`).
- Implement harness `--repeat N` support for observing memory usage across consecutive runs.

---

## Tiếng Việt

Façade library đóng gói pipeline **align + stitch ảnh Gerber** (port từ repo `tony71200/GerberViewer`, nhánh `2026-08-04_Ver4_implement_claude`) để hệ thống **RDL Master/Worker AOI** (solution khác, ngoài repo này) reference và gọi. Đây là phần **Phase 0/1** của roadmap tích hợp Gerber Align & Stitch vào luồng AOI hiện có.

> Nguyên tắc gốc của tích hợp: **không thay flow AOI chính** — Gerber Align/Stitch chỉ thêm vào như một `WorkModel` mới, song song với luồng hiện tại. Chi tiết đầy đủ: [`docs/roadmap_GerberAlignStitch_integration_EN.md`](docs/roadmap_GerberAlignStitch_integration_EN.md).

---

## Kiến trúc

```
RDL.GerberStitch (façade)  ──depends on──►  GerberStitching.Core, GerberEngine
RDL.GerberStitch.Harness   ──depends on──►  RDL.GerberStitch (+ GerberStitching.Core cho nhánh createsample)
```

| Project | Vai trò |
|---|---|
| **`GerberEngine`** | Parser/renderer file Gerber thuần tuý (`GerberParser`, `GerberRenderer`). Không phụ thuộc HALCON/OpenCV. |
| **`GerberStitching.Core`** | Namespace `GerberViewer.Stitching` — pipeline align + stitch đầy đủ (xem [Luồng xử lý](#luồng-xử-lý-align--stitch-pose-graph-ver-4) bên dưới). Phụ thuộc HALCON 25.05 + OpenCvSharp4 4.13. |
| **`RDL.GerberStitch`** | **Façade duy nhất** mà Master/Worker được reference. 3 hàm chính: `GenerateSampleManifest`, `GenerateSampleManifestFromRaster`, `RunAlignStitch`. |
| **`RDL.GerberStitch.Harness`** | Console app test façade với dữ liệu thật — không phải project test (NUnit/xUnit). |

Toàn bộ quy tắc bắt buộc khi sửa code nằm ở [`AGENTS.md`](AGENTS.md) / [`CLAUDE.md`](CLAUDE.md) — đọc trước khi thao tác trên bất kỳ file nào.

---

## Luồng xử lý Align + Stitch (Pose-Graph, Ver 4)

`GerberStitching.Core` triển khai đúng luồng mô tả trong [`flow_v4.md`](flow_v4.md) / [`Flow_V4.html`](Flow_V4.html) (bản trình bày trực quan, có mermaid diagram). Entry point: `AlignStitchWorkflowService.RunAsync` → `RunCore`.

```mermaid
flowchart LR
    A["① Input\nmanifest + captured"] --> B["② Preprocess\nvalidate + map"]
    B --> C["③ Direct Alignment\nHalconShapeModel → PyramidEcc"]
    C --> D["④ Failure Recovery\nneighbor rescue (2 pass)"]
    D --> E["⑤ Neighbor Graph\nđo mọi cặp Manhattan-kề"]
    E -->|PoseGraph.Enabled| F["⑥ Pose-Graph Optimizer\nGauss-Newton + IRLS Huber"]
    E -->|legacy| G["Anchor + bottleneck propagate"]
    F --> H["⑦ Validate"]
    G --> H
    H --> I["⑧ Stitch\nGenProjectiveMosaic"]
    I --> J["⑨ Output\nStitched.tiff + report"]
```

**9 giai đoạn chính:**

1. **Input** — validate manifest + captured folder, tạo `.creating/` (RunOutputLifecycle).
2. **Preprocess** — `AlignStitchConfigMapper.EnsureComposite`/`SyncLegacy`, map ảnh↔tile theo `OrderIndex`.
3. **Direct Alignment** — mỗi tile: coarse `HalconShapeModel` (HALCON `find_shape_model`) → refinement `PyramidEcc` (OpenCV `findTransformECC`). Đây là **giai đoạn chiếm ~90% tổng thời gian** (đo được thực tế: 222–285s / 247–303s tổng, xem [`docs/Phase0_Closeout.md`](docs/Phase0_Closeout.md)).
4. **Failure Recovery** — tile fail được cứu qua neighbor (2 pass: predecessor-only, rồi cả predecessor+successor).
5. **Neighbor Graph** — đo **toàn bộ** cặp tile kề Manhattan bằng `PyramidPhaseCorrelation`, ghi `RecoveryEdges`. Ver 4: nếu Pose-Graph bật, giai đoạn này **chỉ đo, không propagate**.
6. **Pose-Graph Optimizer** (điểm khác biệt của Ver 4 so với bản cũ) — giải pose toàn cục bằng Gauss-Newton + IRLS Huber trên toàn bộ node/edge cùng lúc, thay vì lan truyền tuần tự qua anchor. Có guard `MaxPoseCorrectionPixels` để không áp dụng nếu kết quả dịch quá xa.
7. **Validation** — kiểm tra scale/rotation/finite/canvas, connectivity giữa các tile.
8. **Stitching** — `HOperatorSet.GenProjectiveMosaic` (engine `HalconProjectiveMosaicRebased`, mặc định của façade). HALCON không hỗ trợ blending seam — biết trước, không phải bug.
9. **Output** — ghi `Stitched.tiff`, `processing_report.json`, publish `.creating/` → thư mục cuối.

**Số liệu tham chiếu thật** (dataset 80 tile, xem [`docs/Phase0_Closeout.md`](docs/Phase0_Closeout.md) và [`docs/Phase1_Task06.md`](docs/Phase1_Task06.md)):

| | Giá trị đo được |
|---|---|
| Tổng thời gian | 247–303 s (Direct Alignment chiếm ~90%) |
| RAM đỉnh | ~6.5–6.6 GB (canvas ~40k×32k) |
| Seam residual trước/sau pose-graph | median 5.35 px → 0.38 px |
| Kích thước `Stitched.tiff` | ~1.3 GB (engine HALCON), ~654 MB (engine OpenCv) |

---

## Trạng thái triển khai façade

| Hàm | Trạng thái | Dùng khi |
|---|---|---|
| `GenerateSampleManifest(gerberFilePath, ...)` | ✅ Đã triển khai + chạy thật | Input là file Gerber gốc (`.gbr`) — render qua `GerberEngineFacade` trước khi cắt tile |
| `GenerateSampleManifestFromRaster(rasterImagePath, ...)` | ✅ Đã triển khai + chạy thật | Input là ảnh raster **đã render sẵn** (`.tiff`) — đọc thẳng bằng HALCON, không render lại |
| `RunAlignStitch(manifestPath, capturedImagesFolder, ...)` | ✅ Đã triển khai + chạy thật | Worker chạy Align→PoseGraph→Stitch cho cả lô |

Cả 3 hàm đã được kiểm chứng bằng dữ liệu thật qua `RDL.GerberStitch.Harness` (không phải chỉ build được — đã **chạy** và **so khớp** với run tham chiếu). Chi tiết thiết kế, các đánh đổi, và lỗi/cách fix gặp phải trong lúc triển khai: [`docs/implement_code.html`](docs/implement_code.html).

---

## Build

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Release /p:Platform=x64
```

- Yêu cầu biến môi trường **`HALCONROOT`** trỏ tới HALCON 25.05 Progress.
- Target **x64** duy nhất — không build AnyCPU/x86.
- NuGet packages (OpenCvSharp4, System.Drawing.Common...) đã có sẵn trong `packages/`.

## Chạy thử với dữ liệu thật

```bash
RDL.GerberStitch.Harness\bin\x64\Release\RDL.GerberStitch.Harness.exe
```

2 mode: `--mode alignstitch` (mặc định) và `--mode createsample`. Tham số đọc từ CLI arg hoặc `global_config.json` cạnh `.exe` (arg luôn thắng). Chi tiết: [`docs/Phase1_Task06.md`](docs/Phase1_Task06.md).

---

## Tài liệu

| File | Nội dung |
|---|---|
| [`docs/roadmap_GerberAlignStitch_integration_EN.md`](docs/roadmap_GerberAlignStitch_integration_EN.md) | Roadmap tích hợp đầy đủ — bối cảnh, quyết định kiến trúc, 6 phase |
| [`docs/Phase0_Closeout.md`](docs/Phase0_Closeout.md) | Chốt Phase 0 bằng số liệu 6 run thật; các quyết định (HALCON 25.05, giữ OpenCvSharp) |
| [`docs/Phase1_Task01.md`](docs/Phase1_Task01.md) – [`Task06.md`](docs/Phase1_Task06.md) | Từng task Phase 1, đều có mục "Đã triển khai" đối chiếu kế hoạch ↔ code thật |
| [`docs/deploy_deps.md`](docs/deploy_deps.md) | Checklist dependency khi deploy `RDL.GerberStitch.dll` vào Master/Worker |
| [`docs/implement_code.html`](docs/implement_code.html) | Nhật ký triển khai — file đã đổi, lưu ý, lỗi & cách fix, theo từng lượt thực thi |
| [`flow_v4.md`](flow_v4.md) / [`Flow_V4.html`](Flow_V4.html) | Luồng kỹ thuật chi tiết pipeline Pose-Graph (Ver 4) trong `GerberStitching.Core` |
| [`AGENTS.md`](AGENTS.md) | Quy tắc bắt buộc cho mọi agent (Claude Code, Copilot, Codex...) khi sửa repo này |
| [`CLAUDE.md`](CLAUDE.md) | Hướng dẫn riêng cho Claude Code — build, kiến trúc, quy ước |

---

## Việc còn treo

- **Blocker 3/4 chưa tháo** (`docs/Phase0_Closeout.md` §5): dung lượng ổ chia sẻ thật (~1.3 GB/lô) và grid config khớp lưới chụp thật của dây chuyền RDL.
- **Nâng HALCON trên Master/Worker** — 2 app đó hiện dùng `halcondotnetxl` 18.11.1.1, cần nâng lên 25.05 trước khi deploy `RDL.GerberStitch.dll` (ngoài phạm vi repo này, xem `docs/Phase1_Task03.md` §1.1).
- **Model HALCON pregenerate** (`SampleModelGenerationMode.Pregenerate`) đã có code nhưng **chưa đo được** mức tiết kiệm thời gian thật.
- **`--repeat N`** trong harness để soi rò RAM qua nhiều lần chạy liên tiếp — chưa triển khai (`docs/Phase1_Task05.md`, mục SUPERSEDED).
