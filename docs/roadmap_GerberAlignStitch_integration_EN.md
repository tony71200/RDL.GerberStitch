# Roadmap — Integrating Gerber Align & Stitching into RDL Master/Worker

**Date:** 2026-08-05
**Source:** `tony71200/GerberViewer` branch `2026-08-04_Ver4_implement_claude` → `GerberStitching.Core` library
**Principle:** DO NOT change the main flow. Add **one new option/WorkModel** ("GerberAlignStitch") that runs alongside the existing AOI flow.

---

## 1. Architecture Decisions (confirmed)

| Question | Decision |
|---|---|
| Where does it run | **The Worker executes the entire Align + Stitch for the whole batch.** The Master generates the manifest (at Prepare) and sends images that are already position-mapped; **the Master does NOT execute align/stitch** — it only **receives the path of the stitched image** and then continues with inspection/flow |
| Where is Sample Tile generation (Stage A) | **Embedded in the Master**, executed during **Prepare (S002)** |
| Matcher/Stitch engine | **HALCON-first** (aligned with the existing Worker stack: HalconNcc / HalconShapeModel matcher + HalconProjectiveMosaic/TileOffset stitcher) |

### New Responsibility Split

```
MASTER (Prepare)                          WORKER (runs the full RunCore for the batch)   MASTER (receives result only)
────────────────                          ─────────────────────────────────────────      ─────────────────────────────
Gerber → SampleTileGenerator              receives manifest + THE ENTIRE batch of         receives the Stitched.tiff path
  → SampleManifest (+ Halcon models)        images + mapped ExpectedX/Y                    (does NOT execute align/stitch)
sends manifest + position-mapped images   → Align each tile (ISampleAligner.Align)        → continues inspection / flow,
                                          → GlobalPoseGraphOptimizer (global)                 reports back to IPC
                                          → WorkflowStitchingService.Stitch()
                                          → Stitched.tiff (written to shared drive)
                                          returns the image path to Master (queue)
```

> **Key technical point:** inside `GerberStitching.Core`, `RunCore` already combines Align→Graph→Stitch. Under this model, **the Worker runs the full `RunCore`** (or `AlignStitchWorkflowService.RunAsync`) for the whole batch — **no pipeline-splitting is required**, which sharply lowers refactor risk. In exchange, **the dispatch model changes**: a single Worker now needs the **entire batch of tiles** (not one tile at a time, as in the current AOI dispatch loop) — see Phase 3.

---

## 2. Compatibility Assessment (favorable factors)

| Factor | Result |
|---|---|
| Target framework | `GerberStitching.Core` = **.NET Framework 4.8**, matches Master (4.8) & Worker (4.8) ✅ |
| HALCON | Core uses `halcondotnetxl 25.5`; Worker uses `halcondotnetxl` → **version alignment required** ⚠ |
| OpenCV | Core pulls in `OpenCvSharp4 4.13` (NuGet). Even if only the HALCON path is used, OpenCvSharp still ships as a hard reference of Core ⚠ |
| Monolithic API usage | Worker calls `AlignStitchWorkflowService.RunAsync` directly (Align→Graph→Stitch) — **not a pipeline split**, used exactly as originally designed ✅ |
| Worker's current merge | `NewMergeFunc.MergeImages_Run_*` uses `HOperatorSet.TileImagesOffset` — Gerber align/stitch is an upgraded variant, attached as a new WorkModel ✅ |
| Messaging | Master only receives an **image path** (string) — no need to serialize `double[,]`; only new cmd/IDs are added to `HandShakeInfo` ✅ |

**Top risks:** (a) **dispatch model**: a Worker now needs the entire batch of images to stitch, unlike the current per-die dispatch loop; (b) aligning the HALCON version between Core and Worker; (c) resources: a Worker running align+stitch for a whole batch (large canvas ~40k×32k) requires significant RAM/time — the Manager must allocate accordingly.

---

## 3. Roadmap by Phase

> Estimated in **person-days (PD)**, assuming **1 developer** proficient in C#/HALCON/RDL. Total ~**36–45 PD (~7.5–9 weeks)**. Can be shortened with 2 developers working in parallel (Master is lighter / Worker is the core effort) starting from Phase 3.

### Phase 0 — Discovery & Spike (3–4 PD)
**Goal:** get `GerberStitching.Core` building inside the RDL environment and run an end-to-end offline trial.

| Task | Detail | PD |
|---|---|---|
| 0.1 | Clone Core + GerberEngine into a side solution, build independently on the RDL build machine | 0.5 |
| 0.2 | **Align HALCON version**: compare Core's `halcondotnetxl` (25.5) with the Worker's; pick one version, verify license/dongle | 1 |
| 0.3 | Full-run spike: call `AlignStitchWorkflowService.RunAsync` (Align→Graph→Stitch) with a sample dataset (manifest + the entire batch of images) inside a simulated worker process → confirm a correct Stitched.tiff is produced | 1.5 |
| 0.4 | Resource measurement: peak RAM + time when stitching a full batch (large canvas) on one worker machine → basis for Manager sizing | 0.5 |
| 0.5 | Decide on OpenCvSharp: keep or strip out (if HALCON-only) — assess the effort to remove it | 0.5 |

**Exit gate:** one real dataset stitched outside the GerberViewer UI, running end-to-end in a single process; peak RAM/time for a full batch is known.

---

### Phase 1 — Package Core as a Shared Library (4–5 PD)
**Goal:** turn Core into a stable DLL with trimmed dependencies, referenced by both Master and Worker.

| Task | Detail | PD |
|---|---|---|
| 1.1 | Create a `RDL.GerberStitch` wrapper project bundling `GerberStitching.Core` + `GerberEngine`; hide pipeline details behind a small set of façade functions | 1.5 |
| 1.2 | Minimal façade API: `GenerateSampleManifest(gerberPath, gridCfg) → manifest` (used by the Master); `RunAlignStitch(manifest, capturedImages, cfg) → tiffPath` (used by the Worker, wrapping `AlignStitchWorkflowService.RunAsync`) | 1.5 |
| 1.3 | Standardize dependencies: bundle the OpenCvSharp runtime + HALCON hint path following RDL's `..\dll\` layout | 1 |
| 1.4 | Write the RDL↔`AlignStitchConfig` mapping config (HALCON-first: `AlignmentMethod=HalconNcc`/ShapeModel, `StitchingEngine=HalconProjectiveMosaicRebased`) | 1 |

**Exit gate:** both Master and Worker (in a scratch test project) can add-reference and call the façade successfully.

---

### Phase 2 — Master: Generate Sample Manifest at Prepare (6–7 PD)
**Goal:** when the IPC sends **S002 Prepare**, the Master generates sample tiles from the Gerber file and builds the manifest, **in parallel** with the existing recipe flow (new branch, only when the option is enabled).

| Task | Detail | PD |
|---|---|---|
| 2.1 | Add option configuration: read the `GerberAlignStitch.Enable` flag + Gerber path + grid config from ini/recipe | 1 |
| 2.2 | Inside `Receive_S002_001`: if the option is enabled → call `GenerateSampleManifest` (SampleTileGenerator + Halcon NCC/Shape model per tile) | 2 |
| 2.3 | Map `SampleManifest.Tiles[i].ExpectedX/Y` to the existing die/map coordinates (`RunningDieDict`, `MapCore`) — ensure the mapped positions match | 2 |
| 2.4 | Save the manifest + models (`.shm`/`.ncm`) to a shared folder for the Worker to read; add the path into `CommonFileInfo` | 1 |
| 2.5 | Status/timeout: if manifest generation fails → return `S002-999` using the existing mechanism, no hangs | 0.5 |

**Exit gate:** Prepare with the option enabled → manifest + models exist on the shared drive; the legacy AOI flow (option off) is unchanged.

---

### Phase 3 — Worker: Align + Stitch the Full Batch (12–15 PD) ⭐ core effort
**Goal:** add `WorkModel.GerberAlignStitch`; a single Worker receives the **manifest + the entire batch of images** (already position-mapped) → runs the full **Align → Pose-Graph → Stitch** pipeline → writes `Stitched.tiff` to the shared drive → returns the **path** to the Master.

| Task | Detail | PD |
|---|---|---|
| 3.1 | Add `GerberAlignStitch` to `enum WorkModel` (JSONFormat.cs) + a new branch inside the `RunTask_NorthT_FrameInspect` `switch(workMode)` | 1 |
| 3.2 | **Batch-level dispatch**: a branch that accepts a `TaskInfo` carrying the manifest path + the list of images (instead of a single die). The Worker builds an `IList<CapturedImageInfo>` from the mapped images + `ExpectedX/Y` | 2 |
| 3.3 | Worker loads the manifest + HALCON models (`HalconShapeModelProvider`/`HalconNccModelProvider`) from the path in `CommonFileInfo`; caches models | 2 |
| 3.4 | Call the `RunAlignStitch(manifest, captured, cfg)` façade → wraps `AlignStitchWorkflowService.RunAsync` (per-tile Align + `GlobalPoseGraphOptimizer` + `WorkflowStitchingService.Stitch`, HALCON engine) | 2.5 |
| 3.5 | Progress/cancel: wire `IProgress<WorkflowProgress>` into the Worker's UI/log; support `StopAsk`/`CancellationToken` | 1.5 |
| 3.6 | Write `Stitched.tiff` to the shared drive following RDL naming conventions; **return the path (string)** to the Master via `WorkerResponseTaskResult`, with a new ID (e.g. `S711-001-1xx`) | 1.5 |
| 3.7 | Handle failure/low-texture/guard cases (`MaxPoseCorrectionPixels`): map `MatchFailureReason`/`Applied=false` to RDL error codes (`-300`), fall back to legacy `NewMergeFunc` if configured | 2 |
| 3.8 | Resource management: large canvas (~40k×32k) — monitor RAM, dispose `HObject`/models/`WorkflowImageCache` at the right points | 1.5 |

**Exit gate:** one Worker receives a full batch (manifest + images) → produces a valid `Stitched.tiff` with seam residual within target (goal < 2px, per the original report); returns the correct path to the Master.

---

### Phase 4 — Master: Receive the Stitched Image Path (4–5 PD)
**Goal:** the Master **does NOT execute align/stitch**. It only receives the **`Stitched.tiff` path** from the Worker, validates it, then forwards it to the next inspection/flow step and reports to IPC.

| Task | Detail | PD |
|---|---|---|
| 4.1 | Listener in `Listening_ReceiverWorkerResponseTaskResult`: receive the `S711-001-1xx` message carrying the **stitched image path** (string) per batch | 1 |
| 4.2 | Validate the path exists/is readable on the shared drive; record it into batch state (`InLine_ResultJson` or an equivalent collection) | 1 |
| 4.3 | Forward the stitched image into the next inspection/flow step (reuse existing mechanisms) | 1 |
| 4.4 | Report completion status to IPC (`S900`/`S711-002`); timeout if the Worker does not respond within the threshold | 1 |
| 4.5 | Fallback: if the Worker reports a stitch failure (`-300`) → route the error to IPC or trigger the legacy merge, per configuration | 1 |

**Exit gate:** the Master correctly receives the stitched image path, validates it, and forwards it to inspection; no align/stitch step runs on the Master.

---

### Phase 5 — Protocol & Configuration (3–4 PD)
**Goal:** standardize messaging and make the option cleanly toggleable.

| Task | Detail | PD |
|---|---|---|
| 5.1 | Define a new handshake ID group for the Gerber flow (e.g. `S710` dispatch align/stitch batch, `S711` report stitched image path) in `HandShakeID` — without touching existing codes | 1 |
| 5.2 | Add DTOs to `JSONFormat`: a `TaskInfo` carrying **manifest path + image list** (Master→Worker) and a message carrying the **stitched image path** (Worker→Master); no `double[,]` serialization needed | 1 |
| 5.3 | Global enable/disable flag on both Master and Worker: option OFF → legacy behavior **byte-for-byte** unchanged | 1 |
| 5.4 | Update `CommonFileInfo` to carry the manifest/model path | 0.5 |

**Exit gate:** the option can be toggled via config; when off, all 3 apps behave exactly as today.

---

### Phase 6 — Testing, Performance, Documentation (4–5 PD)
| Task | Detail | PD |
|---|---|---|
| 6.1 | Real dataset test (e.g. 8×10 tiles) end-to-end across the Master+Manager+N Worker cluster | 1.5 |
| 6.2 | Measure seam residual vs. the legacy merge baseline; measure align/stitch time | 1 |
| 6.3 | Regression test the legacy AOI flow (option OFF) — confirm no regression | 1 |
| 6.4 | Update `flow_master.md`/`flow_work.md`/`General_flow.md` + CLAUDE/SKILL docs for the new option | 1 |
| 6.5 | Check for HALCON RAM leaks across long runs with multiple batches | 0.5 |

**Exit gate:** runs stably across several consecutive batches; documentation updated; no AOI regression.

---

## 4. Timeline Summary

| Phase | Content | PD | Weeks (1 dev) |
|---|---|---|---|
| 0 | Discovery & spike | 3–4 | 1 |
| 1 | Package Core as a shared library | 4–5 | 1 |
| 2 | Master generates manifest (Prepare) | 6–7 | 1.5 |
| 3 | Worker align + stitch full batch ⭐ | 12–15 | 2.5–3 |
| 4 | Master receives stitched image path | 4–5 | 1 |
| 5 | Protocol & configuration | 3–4 | 1 |
| 6 | Testing & documentation | 4–5 | 1 |
| **Total** | | **36–45 PD** | **~7.5–9 weeks** |

**Critical path:** Phase 0 → 1 → 3 (Worker align+stitch). Phase 2 (Master manifest generation) and Phase 4 (Master receiving the path) are lighter and can run in parallel with Phase 3 given 2 developers. Effort is concentrated on the Worker; the Master is essentially reduced to orchestration + result reception.

---

## 5. Risks & Mitigations

| Risk | Level | Mitigation |
|---|---|---|
| **Dispatch model change**: one Worker needs the entire batch of images (unlike the current per-die dispatch loop) | High | Phase 3.2 designs a separate dispatch branch for batches; the legacy AOI dispatch loop is left untouched |
| **Worker resource load**: align+stitch for the full batch, canvas ~40k×32k, significant RAM/time | High | Manager allocates RAM accordingly (tune `AotoWorkerNum` for this mode); cap the number of Workers running the Gerber mode concurrently |
| HALCON version mismatch (Core 25.5 vs. Worker) | High | Align versions early in Phase 0.2; if the Worker cannot be upgraded, downgrade Core instead |
| A Worker dies mid-batch → the whole stitch result is lost | Medium | Timeout on the Master side (4.4) + allow the batch to be reassigned to another Worker |
| OpenCvSharp is pulled in even though only HALCON is used | Low | Keep the reference, bundle the runtime; consider removing it in Phase 0.5 if it can be done cleanly |
| RAM leaks from `HObject`/`WorkflowImageCache` on long runs | Medium | Proper disposal (3.8) + leak testing (6.5) |
| Three-app setup + duplicate namespaces cause confusion | Low | Give the wrapper project a clear name, `RDL.GerberStitch` |

---

## 6. Prerequisites (blockers)

1. **HALCON version** must be aligned between the GerberViewer Core and RDL Worker (license/dongle for 25.5?).
2. **Real dataset** (captured tile images + the corresponding Gerber file) for the Phase 0 spike and Phase 6 testing.
3. **Shared drive** (like `RemoteDevie=Z`) with enough space to store the manifest + models + Stitched.tiff.
4. Confirm the **grid configuration** (tile rows/columns, overlap, px/mm) needed to map `ExpectedX/Y` to the Master's die coordinates.

---

## 7. Boundaries NOT to Touch (keeping this "just an added option")

- Do not modify the default S001/S002/S003 flow — only **add a branch when the option is enabled**.
- Do not replace the legacy `NewMergeFunc` — keep it as a fallback.
- Do not change existing `HandShakeInfo`/codes — only **add** new codes (`S710/S711`).
- Do not change the existing AOI load-balancing/dispatch — the Gerber batch-dispatch path is a **separate route**, and does not interfere with the per-die loop.
- **The Master does not execute align/stitch** — all computation lives on the Worker; the Master only generates the manifest (Prepare) and receives the stitched image path for subsequent inspection.
