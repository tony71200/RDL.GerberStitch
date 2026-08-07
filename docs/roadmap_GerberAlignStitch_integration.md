# Roadmap — Tích hợp Align & Stitching Gerber vào RDL Master/Worker

**Ngày lập:** 2026-08-05
**Nguồn:** `tony71200/GerberViewer` branch `2026-08-04_Ver4_implement_claude` → thư viện `GerberStitching.Core`
**Nguyên tắc:** KHÔNG thay flow chính. Thêm **1 option/WorkModel mới** ("GerberAlignStitch") song song với luồng AOI hiện tại.

---

## 1. Quyết định kiến trúc (chốt theo trao đổi)

| Vấn đề | Quyết định |
|---|---|
| Nơi chạy chính | **Worker thực thi toàn bộ Align + Stitch cho cả lô.** Master sinh manifest (Prepare) + gửi ảnh đã mapping vị trí; **Master KHÔNG thực thi align/stitch** — chỉ **nhận đường dẫn ảnh đã stitch** rồi tiếp tục inspection/flow sau đó |
| Sinh Sample Tile (giai đoạn A) | **Nhúng vào Master**, thực hiện khi **Prepare (S002)** |
| Matcher/Stitch engine | **Ưu tiên HALCON** (đồng bộ stack Worker: HalconNcc / HalconShapeModel matcher + HalconProjectiveMosaic/TileOffset stitcher) |

### Phân chia trách nhiệm mới

```
MASTER (Prepare)                          WORKER (chạy trọn RunCore cho cả lô)      MASTER (chỉ nhận kết quả)
────────────────                          ──────────────────────────────────       ─────────────────────────
Gerber → SampleTileGenerator              nhận manifest + TOÀN BỘ ảnh lô +          nhận đường dẫn Stitched.tiff
  → SampleManifest (+ Halcon models)        ExpectedX/Y đã map                       (KHÔNG thực thi align/stitch)
phát manifest + ảnh đã mapping vị trí      → Align từng tile (ISampleAligner.Align)  → tiếp tục inspection / flow
                                          → GlobalPoseGraphOptimizer (global)          sau đó, trả IPC
                                          → WorkflowStitchingService.Stitch()
                                          → Stitched.tiff (ghi ổ chia sẻ)
                                          trả đường dẫn ảnh về Master (queue)
```

> **Điểm mấu chốt kỹ thuật:** trong `GerberStitching.Core`, `RunCore` gộp Align→Graph→Stitch. Với mô hình này, **Worker chạy trọn `RunCore`** (hoặc `AlignStitchWorkflowService.RunAsync`) cho cả lô — **không cần tách pipeline**, giảm mạnh rủi ro refactor. Đổi lại, **mô hình dispatch thay đổi**: một Worker cần **toàn bộ tile của lô** (không phải 1 tile lẻ như vòng dispatch AOI hiện tại) — xem Phase 3.

---

## 2. Đánh giá tương thích (thuận lợi)

| Yếu tố | Kết quả |
|---|---|
| Target framework | `GerberStitching.Core` = **.NET 4.8**, khớp Master (4.8) & Worker (4.8) ✅ |
| HALCON | Core dùng `halcondotnetxl 25.5`; Worker dùng `halcondotnetxl` → cần **thống nhất version** ⚠ |
| OpenCV | Core kéo `OpenCvSharp4 4.13` (NuGet). Nếu chỉ dùng HALCON path vẫn phải mang theo OpenCvSharp (Core reference cứng) ⚠ |
| API dùng nguyên khối | Worker gọi thẳng `AlignStitchWorkflowService.RunAsync` (Align→Graph→Stitch) — **không phải tách pipeline**, dùng đúng như thiết kế gốc ✅ |
| Merge hiện tại của Worker | `NewMergeFunc.MergeImages_Run_*` dùng `HOperatorSet.TileImagesOffset` — align/stitch Gerber là bản nâng cấp, gắn như WorkModel mới ✅ |
| Bản tin | Master chỉ nhận **đường dẫn ảnh** (chuỗi) — không cần serialize `double[,]`; chỉ thêm cmd/ID mới trong `HandShakeInfo` ✅ |

**Rủi ro cao nhất:** (a) **mô hình dispatch**: một Worker cần toàn bộ ảnh của lô để stitch, khác vòng dispatch per-die hiện tại; (b) thống nhất version HALCON giữa Core và Worker; (c) tài nguyên: Worker chạy align+stitch cả lô (canvas lớn ~40k×32k) cần RAM/thời gian đáng kể — Manager phải cấp phát phù hợp.

---

## 3. Roadmap theo Phase

> Ước lượng theo **người-ngày (PD)**, giả định **1 dev** thạo C#/HALCON/RDL. Tổng ~**36–45 PD (~7.5–9 tuần)**. Có thể rút nếu 2 dev chạy song song (Master phần nhẹ / Worker phần trọng tâm) từ Phase 3.

### Phase 0 — Khảo sát & Spike (3–4 PD)
**Mục tiêu:** dựng được `GerberStitching.Core` trong môi trường RDL, chạy thử end-to-end offline.

| Task | Chi tiết | PD |
|---|---|---|
| 0.1 | Clone Core + GerberEngine vào solution phụ, build độc lập trên máy build RDL | 0.5 |
| 0.2 | **Thống nhất version HALCON**: so `halcondotnetxl` của Core (25.5) vs Worker; chọn 1 version, kiểm license/dongle | 1 |
| 0.3 | Spike chạy trọn: gọi `AlignStitchWorkflowService.RunAsync` (Align→Graph→Stitch) với 1 dataset mẫu (manifest + cả lô ảnh) trong tiến trình giả lập worker → xác nhận ra Stitched.tiff đúng | 1.5 |
| 0.4 | Đo tài nguyên: RAM đỉnh + thời gian khi stitch cả lô (canvas lớn) trên 1 máy worker → làm cơ sở cấu hình Manager | 0.5 |
| 0.5 | Quyết định OpenCvSharp: giữ hay lược bỏ (nếu chỉ HALCON) — đánh giá công gỡ | 0.5 |

**Exit gate:** stitch được 1 dataset thật ngoài GerberViewer UI, chạy trọn trong một tiến trình; nắm được RAM/thời gian đỉnh của cả lô.

---

### Phase 1 — Đóng gói Core thành thư viện dùng chung (4–5 PD)
**Mục tiêu:** biến Core thành DLL ổn định, gọn dependency, để Master & Worker cùng reference.

| Task | Chi tiết | PD |
|---|---|---|
| 1.1 | Tạo project `RDL.GerberStitch` (wrapper) bọc `GerberStitching.Core` + `GerberEngine`; ẩn chi tiết pipeline sau vài hàm façade | 1.5 |
| 1.2 | Façade API tối thiểu: `GenerateSampleManifest(gerberPath, gridCfg) → manifest` (dùng ở Master); `RunAlignStitch(manifest, capturedImages, cfg) → tiffPath` (dùng ở Worker, bọc `AlignStitchWorkflowService.RunAsync`) | 1.5 |
| 1.3 | Chuẩn hoá dependency: đóng gói OpenCvSharp runtime + HALCON hint path theo cấu trúc `..\dll\` của RDL | 1 |
| 1.4 | Viết config mapping RDL↔`AlignStitchConfig` (ưu tiên HALCON: `AlignmentMethod=HalconNcc`/ShapeModel, `StitchingEngine=HalconProjectiveMosaicRebased`) | 1 |

**Exit gate:** Master và Worker (project trống thử nghiệm) đều add-reference & gọi được façade.

---

### Phase 2 — Master: sinh Sample Manifest khi Prepare (6–7 PD)
**Mục tiêu:** khi IPC gửi **S002 Prepare**, Master sinh sample tile từ Gerber và chuẩn bị manifest, **song song** flow recipe hiện tại (thêm nhánh khi option bật).

| Task | Chi tiết | PD |
|---|---|---|
| 2.1 | Thêm cấu hình option: đọc cờ `GerberAlignStitch.Enable` + đường dẫn Gerber + grid config từ ini/recipe | 1 |
| 2.2 | Trong `Receive_S002_001`: nếu option bật → gọi `GenerateSampleManifest` (SampleTileGenerator + Halcon NCC/Shape model per tile) | 2 |
| 2.3 | Ánh xạ `SampleManifest.Tiles[i].ExpectedX/Y` ↔ toạ độ die/map hiện có (`RunningDieDict`, `MapCore`) — đảm bảo vị trí mapping khớp | 2 |
| 2.4 | Lưu manifest + model (`.shm`/`.ncm`) ra thư mục chia sẻ để Worker đọc; đưa đường dẫn vào `CommonFileInfo` | 1 |
| 2.5 | Trạng thái/timeout: nếu sinh manifest lỗi → trả `S002-999` như cơ chế hiện có, không treo | 0.5 |

**Exit gate:** Prepare với option bật → có manifest + model trên đĩa chia sẻ; flow AOI cũ (option tắt) không đổi.

---

### Phase 3 — Worker: Align + Stitch trọn lô (12–15 PD) ⭐ trọng tâm
**Mục tiêu:** thêm `WorkModel.GerberAlignStitch`; một Worker nhận **manifest + toàn bộ ảnh lô** (đã map vị trí) → chạy trọn **Align → Pose-Graph → Stitch** → ghi `Stitched.tiff` ra ổ chia sẻ → trả **đường dẫn** về Master.

| Task | Chi tiết | PD |
|---|---|---|
| 3.1 | Thêm `GerberAlignStitch` vào `enum WorkModel` (JSONFormat.cs) + nhánh trong `RunTask_NorthT_FrameInspect` `switch(workMode)` | 1 |
| 3.2 | **Dispatch cả lô**: nhánh nhận `TaskInfo` mang manifest path + danh sách ảnh (thay vì 1 die lẻ). Worker dựng `IList<CapturedImageInfo>` từ ảnh đã map + `ExpectedX/Y` | 2 |
| 3.3 | Worker load manifest + Halcon model (`HalconShapeModelProvider`/`HalconNccModelProvider`) từ path trong `CommonFileInfo`; cache model | 2 |
| 3.4 | Gọi façade `RunAlignStitch(manifest, captured, cfg)` → bọc `AlignStitchWorkflowService.RunAsync` (Align per-tile + `GlobalPoseGraphOptimizer` + `WorkflowStitchingService.Stitch`, engine HALCON) | 2.5 |
| 3.5 | Progress/cancel: nối `IProgress<WorkflowProgress>` vào UI/log Worker; hỗ trợ `StopAsk`/`CancellationToken` | 1.5 |
| 3.6 | Ghi `Stitched.tiff` ra ổ chia sẻ theo chuẩn đặt tên RDL; **trả đường dẫn (chuỗi)** về Master qua `WorkerResponseTaskResult`, ID mới (vd `S711-001-1xx`) | 1.5 |
| 3.7 | Xử lý fail/low-texture/guard (`MaxPoseCorrectionPixels`): map `MatchFailureReason`/`Applied=false` sang mã lỗi RDL (`-300`), fallback merge cũ `NewMergeFunc` nếu cấu hình | 2 |
| 3.8 | Quản lý tài nguyên: canvas lớn (~40k×32k) — kiểm RAM, dispose `HObject`/model/`WorkflowImageCache` đúng chỗ | 1.5 |

**Exit gate:** 1 Worker nhận trọn lô (manifest + ảnh) → ra `Stitched.tiff` hợp lệ với seam residual đạt ngưỡng (mục tiêu < 2px như report gốc); trả đúng đường dẫn về Master.

---

### Phase 4 — Master: nhận đường dẫn ảnh đã stitch (4–5 PD)
**Mục tiêu:** Master **KHÔNG thực thi align/stitch**. Chỉ nhận **đường dẫn `Stitched.tiff`** từ Worker, xác thực, rồi chuyển tiếp cho bước inspection/flow sau đó và trả IPC.

| Task | Chi tiết | PD |
|---|---|---|
| 4.1 | Listener trong `Listening_ReceiverWorkerResponseTaskResult`: nhận bản tin `S711-001-1xx` mang **đường dẫn ảnh đã stitch** (chuỗi) theo lô | 1 |
| 4.2 | Xác thực đường dẫn tồn tại/đọc được trên ổ chia sẻ; ghi vào trạng thái lô (`InLine_ResultJson` hoặc collection tương đương) | 1 |
| 4.3 | Chuyển tiếp ảnh đã stitch cho bước inspection/flow kế tiếp (tái dùng cơ chế hiện có) | 1 |
| 4.4 | Phát trạng thái hoàn tất về IPC (mã `S900`/`S711-002`); timeout nếu Worker không trả sau ngưỡng | 1 |
| 4.5 | Fallback: nếu Worker báo lỗi stitch (`-300`) → định tuyến lỗi về IPC hoặc kích hoạt merge cũ theo cấu hình | 1 |

**Exit gate:** Master nhận đúng đường dẫn ảnh đã stitch, xác thực và chuyển tiếp inspection; không có bước align/stitch nào chạy trên Master.

---

### Phase 5 — Giao thức & Config hoá (3–4 PD)
**Mục tiêu:** chuẩn hoá bản tin và bật/tắt option sạch sẽ.

| Task | Chi tiết | PD |
|---|---|---|
| 5.1 | Định nghĩa nhóm mã handshake mới cho luồng Gerber (vd `S710` phát lô align/stitch, `S711` báo đường dẫn ảnh đã stitch) trong `HandShakeID` — không đụng mã cũ | 1 |
| 5.2 | Thêm DTO vào `JSONFormat`: `TaskInfo` mang **manifest path + danh sách ảnh** (Master→Worker) và bản tin **đường dẫn ảnh đã stitch** (Worker→Master); không cần serialize `double[,]` | 1 |
| 5.3 | Cờ bật/tắt toàn cục ở cả Master/Worker: option OFF → hành vi cũ **byte-for-byte** | 1 |
| 5.4 | Cập nhật `CommonFileInfo` mang thêm manifest/model path | 0.5 |

**Exit gate:** bật/tắt option qua config; khi tắt, 3 app hoạt động như hiện tại.

---

### Phase 6 — Kiểm thử, hiệu năng, tài liệu (4–5 PD)
| Task | Chi tiết | PD |
|---|---|---|
| 6.1 | Test dataset thật (vd 8×10 tile) end-to-end trên cụm Master+Manager+N Worker | 1.5 |
| 6.2 | Đo seam residual, so với baseline merge cũ; đo thời gian align/stitch | 1 |
| 6.3 | Test hồi quy flow AOI cũ (option OFF) — đảm bảo không hồi quy | 1 |
| 6.4 | Cập nhật `flow_master.md`/`flow_work.md`/`General_flow.md` + CLAUDE/SKILL cho option mới | 1 |
| 6.5 | Kiểm rò RAM HALCON khi chạy dài nhiều lô | 0.5 |

**Exit gate:** chạy ổn định ≥ vài lô liên tiếp; tài liệu cập nhật; không hồi quy AOI.

---

## 4. Bảng tổng hợp thời gian

| Phase | Nội dung | PD | Tuần (1 dev) |
|---|---|---|---|
| 0 | Spike & khảo sát | 3–4 | 1 |
| 1 | Đóng gói Core dùng chung | 4–5 | 1 |
| 2 | Master sinh manifest (Prepare) | 6–7 | 1.5 |
| 3 | Worker align + stitch trọn lô ⭐ | 12–15 | 2.5–3 |
| 4 | Master nhận đường dẫn ảnh đã stitch | 4–5 | 1 |
| 5 | Giao thức & config | 3–4 | 1 |
| 6 | Test & tài liệu | 4–5 | 1 |
| **Tổng** | | **36–45 PD** | **~7.5–9 tuần** |

**Đường găng (critical path):** Phase 0 → 1 → 3 (Worker align+stitch). Phase 2 (Master sinh manifest) và Phase 4 (Master nhận path) nhẹ hơn, có thể chạy song song với Phase 3 nếu có 2 dev. Trọng tâm dồn vào Worker; Master gần như chỉ điều phối + nhận kết quả.

---

## 5. Rủi ro & giảm thiểu

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| **Mô hình dispatch đổi**: 1 Worker cần toàn bộ ảnh lô (khác vòng dispatch per-die) | Cao | Phase 3.2 thiết kế nhánh dispatch riêng cho lô; không đụng vòng dispatch AOI cũ |
| **Tài nguyên Worker**: align+stitch cả lô, canvas ~40k×32k, RAM/thời gian lớn | Cao | Manager cấp phát RAM phù hợp (điều chỉnh `AotoWorkerNum` cho mode này); giới hạn số Worker chạy song song mode Gerber |
| Version HALCON lệch (Core 25.5 vs Worker) | Cao | Phase 0.2 thống nhất version sớm; nếu không nâng được Worker, downgrade Core |
| 1 Worker chết giữa lô → mất cả kết quả stitch | TB | Timeout ở Master (4.4) + cho phép giao lại lô cho Worker khác |
| OpenCvSharp kéo theo dù chỉ dùng HALCON | Thấp | Giữ reference, đóng gói runtime; cân nhắc gỡ ở Phase 0.5 nếu sạch |
| Rò RAM HObject/`WorkflowImageCache` khi chạy dài | TB | Dispose chuẩn (3.8) + test rò (6.5) |
| Đội hình 3 app + namespace trùng gây nhầm | Thấp | Đặt project wrapper tên rõ `RDL.GerberStitch` |

---

## 6. Việc cần chuẩn bị trước (blocker)

1. **Version HALCON** thống nhất giữa GerberViewer Core và Worker RDL (license/dongle 25.5?).
2. **Dataset thật** (tile ảnh chụp + Gerber tương ứng) để spike Phase 0 và test Phase 6.
3. **Ổ chia sẻ** (như `RemoteDevie=Z`) đủ chỗ ghi manifest + model + Stitched.tiff.
4. Xác nhận **grid config** (số hàng/cột tile, overlap, px/mm) để map `ExpectedX/Y` ↔ toạ độ die của Master.

---

## 7. Ranh giới KHÔNG đụng tới (đảm bảo "chỉ thêm option")

- Không sửa flow S001/S002/S003 mặc định — chỉ **rẽ nhánh khi option bật**.
- Không thay `NewMergeFunc` cũ — giữ làm fallback.
- Không đổi `HandShakeInfo`/mã cũ — chỉ **thêm** mã mới (`S710/S711`).
- Không đổi cân bằng tải/dispatch AOI hiện có — nhánh phát lô Gerber là **đường dispatch riêng**, không chen vào vòng per-die.
- **Master không thực thi align/stitch** — toàn bộ tính toán nằm ở Worker; Master chỉ sinh manifest (Prepare) và nhận đường dẫn ảnh đã stitch để inspection sau đó.
