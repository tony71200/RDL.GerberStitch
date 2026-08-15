# Thiết kế — Hiệu chỉnh bước lưới chụp (Grid Pitch Calibration)

**Ngày:** 2026-08-15
**Nhánh:** `Ver2_1`
**Phạm vi:** chỉ trong solution `RDL.GerberStitch`. Không sửa RDL Master, không sửa Worker.
**Thay thế:** phần chẩn đoán §2, §3.2(b) và thứ tự hành động §9 của `RDL_GerberStitch_Findings.md`.

---

## 1. Nguyên nhân gốc (đã đo, không phải suy đoán)

### 1.1 Số đo

Đo trực tiếp trên ảnh chụp `.bmp` bằng `matchTemplate` (TM_CCOEFF_NORMED) ở full resolution,
bỏ qua hoàn toàn số liệu do pipeline tự báo:

| | n | mean | std | NCC |
|---|---|---|---|---|
| Bước ngang thật | 27 | **4031.3 px** | 0.54 | 0.85 – 0.99 |
| Bước dọc thật | 30 | **4031.7 px** | 0.51 | 0.53 – 0.98 |
| Lệch vuông góc | 57 | +3.3 / −3.7 px | 0.6 | → rotation **+0.047°** |

Dữ liệu: `H:\005_Project\AOI_2026_07_imp\20260720_Gerber_Align\20260725 Q168 2-1 org`
(80 ảnh, mỗi ảnh 4096×4096, mono8), raster `q168_Gerber2-1 to 2-4.tiff` (40418×32364).

Ánh xạ `orderIndex → (row, col)`: zigzag column-major,
`idx(r,c) = c*8 + (c chẵn ? r : 7−r)`.

### 1.2 Kiểm chứng độc lập từ hằng số recipe

`sample_prepare.json` chứa hai hằng số không nhất quán với nhau:

```
LensCalib         = 0.00064 mm = 0.64 µm/px   ← dùng khi đặt bước bàn máy
resolution_umPx_X = 0.65 µm/px                ← dùng khi quy đổi world → pixel ảnh
```

Bước bàn máy được đặt bằng đúng một FOV ở 0.64 µm/px: `4096 × 0.64 = 2621.4 µm`.
Quy đổi ra pixel ở 0.65 µm/px: `2621.4 / 0.65 = 4032.9 px`.

Đo được 4031.3 px. **Lệch 1.6 px trên 4032 (0.04%).**
Hai nguồn hoàn toàn độc lập cho cùng một đáp số.

⇒ Overlap 64.5 px giữa hai ảnh chụp **không do ai cấu hình**. Nó là hệ quả cơ học của
chênh lệch 1.56% giữa `LensCalib` và `resolution_umPx`.

### 1.3 Bug

Overlap cho các cấu hình FOV lớn được tính là `tile − 4096`, tức giả định các ảnh chụp
kề nhau **không chồng nhau**:

Bảng dưới nói về **`TileOverlap`** (`TileWidth − Step`, §3.2.1) — đúng đại lượng người dùng nhập.
`CaptureOverlap` thì luôn là 64.5 px ở mọi dòng, vì nó do bàn máy quyết định.

| Tile | Overlap khai | = `tile − 4096`? | `TileOverlap` đúng (`tile − 4031.5`) | Bước khai | Sai số/bước | Cộng dồn 9 bước |
|---|---|---|---|---|---|---|
| 4096 | 60 | — (lấy từ ini Master) | 64.5 | 4036 | **−4.5** | −40 px |
| 4192 | 96 | ✓ | **160.5** | 4096 | −64.5 | **−580 px** |
| 4240 | 144 | ✓ | **208.5** | 4096 | −64.5 | **−580 px** |
| 4320 | 224 | ✓ | **288.5** | 4096 | −64.5 | **−580 px** |

Giải thích trọn vẹn mọi triệu chứng:

- **Case 4096/60 gần đạt** — sai số chỉ −4.5 px/bước. Không phải may mắn: 60 là giá trị
  máy đo thật, gần đúng 64.5. Cộng dồn ~40 px là đủ để lệch nét line trắng ở node 12, 59.
- **Ba FOV lớn tệ như nhau, không phải "càng lớn càng tệ"** — cả ba cùng khai bước 4096.
  Report xác nhận: residual đồng nhất (+31.1 / +31.4 / +30.9), cùng 29 tile blank,
  cùng `edgesTotal=142`.

### 1.4 Vì sao báo cáo cũ kết luận ngược dấu

Pipeline tự báo residual **+31.1**; sự thật là **−64.5**. Lệch **+95.6 px**.

`AlignStitchWorkflowService.AnchorRoi` (dòng 1252) lấy bề rộng ROI từ hình học *tile*
(`4192 − 4096 = 96 px`). Phase correlation chạy trên dải chỉ rộng 96 px, trong khi board
có chu kỳ mạnh ở 84–185 px (đo autocorrelation: 84 px @ 0.81, 185 px @ 0.54)
→ **khóa nhầm một chu kỳ**.

Lập luận "std < 0.7 px nên không phải periodic aliasing" là ngược. Khóa nhầm chu kỳ trên
mạng tuần hoàn đều là chế độ lỗi ổn định nhất có thể — mọi cặp lệch đúng một chu kỳ như
nhau. Độ lệch chuẩn thấp ở đây là **bằng chứng buộc tội**, không phải chứng nhận.

### 1.5 Hai giả định đã kiểm tra và bác bỏ

| Giả định | Kết quả kiểm chứng |
|---|---|
| 29 tile blank do lưới tràn raster | **Sai.** Board trống thật. Tương quan độ phủ ảnh-chụp vs raster theo ô = **+0.881**. Ô (3,3),(3,4),(4,4),(5,5)… có độ phủ 0.0% trên *cả* ảnh chụp lẫn raster. |
| Lưới bị lệch hướng (`RotateMap=180`) | **Sai.** Tương quan hướng gốc +0.881; xoay 180° cho −0.212, lật ngang +0.723, lật dọc −0.239. Hướng hiện tại đúng. |

Hệ quả: đề xuất quality gate *"tỉ lệ BlankSample > 10% → fail"* sẽ đánh trượt một run hợp lệ
trên board này (36% trống là bình thường). **Không đưa tiêu chí đó vào.**

### 1.6 Nguồn công thức trong RDL_Master

`MapEngine/MapCore.cs:1154,1167` — lưới sinh trong toạ độ world rồi chia cho `camResX`:

```csharp
x = CaptureList[i].X * captureLayout.PitchX;      // PitchX = iPC_RcpInfo.CapturePitchX
info.FOV_Image.X = (int)(info.FOV_World.X / (float)camResX);
```

`RDL_Master/MainForm.cs:3243` — overlap là đại lượng **dẫn xuất**:

```csharp
double r = iPC_RcpInfo.CapturePitchX - ((iPC_RcpInfo.Image_Width) * rcp.CamResX);
layout.OverLapX = (int)(-1 * r / rcp.CamResX);    // ⇒ overlap = Image_Width − CapturePitchX/CamResX
```

`Ox` tính ở `MapCore.cs:1073` **không được dùng** trong phép tính vị trí (chỉ nằm trong khối
đã comment 1082–1088). Bước lưới thật = `CapturePitchX / CamResX`.

> Công thức "redistribute" (`n = ceil((span−tile)/(tile−overlapMin))+1`) suy ngược ở §7 báo cáo
> cũ khớp số là **trùng hợp**. Cơ chế thật chỉ là `(int)(i × PitchX/CamRes)`, cho đúng dãy
> `3998/3998/3999/…` trong `sample_prepare.json`.

---

## 2. Nguyên tắc thiết kế

1. **Sửa ở nguồn.** Lỗi nằm ở hình học lưới. Không để pose graph hấp thụ nó.
2. **Một biến một lần.** Sửa lưới, chạy lại 4 case, đo, rồi mới quyết bước tiếp.
3. **Overlap là output, không phải input.** Bịt cái bẫy đã gây ra vụ này.
4. **Không đổi hành vi matcher trong đợt này** (AGENTS.md §3.1 — `GerberStitching.Core` là code
   port, đổi hành vi pipeline phải xác nhận trước).

---

## 3. Component A — `CaptureGridCalculator`

**Vị trí:** `RDL.GerberStitch/Facade/CaptureGridCalculator.cs`
**Phụ thuộc:** không HALCON, không OpenCV, không I/O. Thuần số học — kiểm chứng được bằng mắt.

### 3.1 Input — `CaptureGridSpec`

| Trường | Kiểu | Nguồn | Ghi chú |
|---|---|---|---|
| `CapturePitchX/Y` | `double` | `iPC_RcpInfo.CapturePitchX/Y` | µm |
| `CamResX/Y` | `double` | `rcp.CamResX/Y` | µm/px (= `resolution_umPx / 1000` nếu nguồn là mm) |
| `ImageWidth/Height` | `int` | `iPC_RcpInfo.Image_Width/Height` | px ảnh chụp |
| `Rows`, `Columns` | `int` | `CaptureNumY`, `CaptureNumX` | |
| `StartOffsetX/Y` | `double` | `iPC_RcpInfo.StartOffsetX/Y` | px; `-1` ⇒ `ImageWidth/2` (`MainForm.cs:3233`) |
| `TileWidth/Height` | `int?` | — | `null` ⇒ `= ImageWidth/Height`. Ghi đè để nới cửa sổ tìm kiếm. |
| `Order` | `CaptureOrder` | `StartOrder` + `CropOrder` | khớp `SampleGeometryCalculator.PhysicalOrder` |

**Không có trường overlap.**

### 3.2 Output — `CaptureGridResult`

```
IList<TileRect> Tiles                  // OrderIndex liên tục 0..N-1
double StepXPx, StepYPx                // = CapturePitchX / CamResX  (double, không làm tròn)
double CaptureOverlapXPx, CaptureOverlapYPx   // = ImageWidth  − StepXPx
double TileOverlapXPx,    TileOverlapYPx      // = TileWidth   − StepXPx
int    RequiredWidth, RequiredHeight
IList<int> ClampedTileIndices          // tile bị cắt ở rìa raster
IList<string> Warnings
```

### 3.2.1 HAI loại overlap — không được lẫn

Đây chính là chỗ nhập nhằng đã gây ra vụ này, nên phải tách tên rõ ràng:

| Tên | Công thức | Ý nghĩa vật lý | Giá trị case này |
|---|---|---|---|
| **`CaptureOverlap`** | `ImageWidth − Step` | Hai **ảnh chụp** kề nhau chồng nhau bao nhiêu. Đại lượng vật lý thật, do bàn máy quyết định. Không phụ thuộc `TileWidth`. | **64.5 px** (mọi case) |
| **`TileOverlap`** | `TileWidth − Step` | Hai **cửa sổ crop** trên raster chồng nhau bao nhiêu. Thuần quy ước, đổi theo `TileWidth`. | 64.5 / 160.5 / 208.5 / 288.5 |

Nơi dùng từng loại:

| Nơi dùng | Loại |
|---|---|
| `OVerLapX/Y_EdgePath` trong JSON xuất ra | `CaptureOverlap` — khớp ngữ nghĩa Master (`MainForm.cs:3243`) |
| `MaxPoseCorrectionPixels` mặc định (§6.2) | `CaptureOverlap` |
| Ngưỡng `GridMismatchFailPixels` (§4.3) | `CaptureOverlap` |
| Hậu tố `o<overlap>` trong tên file (§5.2) | `TileOverlap` — vì nó phân biệt 4 case |
| Bề rộng ROI của Neighbor Recovery | `CaptureOverlap` (ngoài phạm vi đợt này — xem §7) |

### 3.3 Công thức

```
stepX        = CapturePitchX / CamResX
Sx           = StartOffsetX * CamResX                       // world µm
xWorld_c     = c * CapturePitchX - Sx
ExpectedX_c  = (int)(xWorld_c / CamResX)                    // truncate — GIỐNG Master
```

**Giữ nguyên `(int)` truncate.** Mục tiêu là khớp Master từng pixel (dãy `3998/3998/3999`),
vì Worker sẽ nhận `ExpectedX` từ Master thật. Không đổi sang `Math.Round` dù nó phân bố
đều hơn — làm vậy sẽ tạo lệch 1 px giữa hai đường sinh lưới, đúng loại lỗi khó truy.

### 3.4 Nới tile đối xứng

Hiện tại tile 4192 đặt tại `c*step`, cùng gốc với ảnh 4096 → 96 px biên tìm kiếm dồn hết
về phải/dưới. Đổi thành:

```
X = ExpectedX_c - (TileWidth - ImageWidth) / 2
W = TileWidth
```

Kẹp vào raster; tile bị kẹp thì ghi vào `ClampedTileIndices` + `Warnings`, **không** âm thầm
sinh tile teo như `SampleGeometryCalculator.Calculate` dòng 81–84 đang làm.

### 3.5 Kiểm tra phủ

```
RequiredWidth  = (Columns-1) * StepX + TileWidth
RequiredHeight = (Rows-1)    * StepY + TileHeight
```

Với step đúng 4031.5, tile 4096: `40378 × 32318` trên raster `40418 × 32364` → vừa khít,
**0 tile bị cắt** (hiện trạng: 13 tile bị cắt).

---

## 4. Component B — `GridCalibrationProbe`

**Vị trí:** `GerberStitching.Core/Alignment/GridCalibrationProbe.cs`
**Thời điểm:** chạy **trước** Direct Alignment.
**Vai trò:** chỉ đo và cảnh báo. **Không tự ghi đè lưới.** Nguồn sự thật vẫn là công thức recipe.

### 4.1 Thủ tục

Lấy mẫu ~16 cặp neighbor trực giao (không phải toàn bộ 142). Mỗi cặp:

- template = dải `ProbeTemplatePx = 64` px ở biên ảnh target
- vùng tìm = dải biên anchor, mở rộng `SearchMarginPx = 240` px quanh bước khai,
  biên vuông góc `±80` px
- `Cv2.MatchTemplate` + `TM_CCOEFF_NORMED`, lấy `minMaxLoc`

Lấy **median** trên các cặp có `NCC ≥ MinProbeScore = 0.35`.

Đây đúng thủ tục đã chạy trong phần chẩn đoán: 57/57 cặp, std 0.5 px, dưới 2 giây.

### 4.2 Output — mục `gridCalibration` trong `processing_report.json`

```
declaredStepX, declaredStepY
measuredStepX, measuredStepY
measuredRotationDeg
sampleCount, medianScore
deltaX, deltaY                 // measured − declared
status                         // Ok | Warning | Mismatch | Inconclusive
```

### 4.3 Chính sách

| Điều kiện | `status` | Hành động |
|---|---|---|
| \|Δ\| ≤ `GridWarnPixels` (2) | `Ok` | im lặng, chỉ ghi số |
| 2 < \|Δ\| ≤ `GridMismatchFailPixels` (16) | `Warning` | warning vào report + `AlignStitchResult.Warnings` |
| \|Δ\| > 16 | `Mismatch` | **fail sớm**, `runStatus = GridMismatch`, không chạy tiếp |
| `sampleCount < 4` hoặc `medianScore < 0.35` | `Inconclusive` | ghi nhận, **không fail** |

Nhánh `Inconclusive` bắt buộc phải có: board này có rows 6–7 trống hoàn toàn, vùng trống
không được gây fail.

Ngưỡng 16 px ≈ ¼ `CaptureOverlap` (64.5 px, §3.2.1), an toàn dưới `MaxEdgeResidualPixels = 40` hiện tại.
Dùng `CaptureOverlap` chứ không phải `TileOverlap` để ngưỡng không đổi theo `TileWidth`.
Với vụ vừa rồi (Δ = 64.5 px) probe sẽ dừng run ở giây thứ 2.

---

## 5. Component C — `createsamplemem` + output JSON

### 5.1 Façade

```csharp
public CaptureGridResult BuildCaptureGrid(CaptureGridSpec spec);
```

Trả `TileRect[]` — đúng kiểu mà `GenerateSampleManifestFromRects` và overload in-memory
`RunAlignStitch(sampleImagePath, IList<TileRect>, …)` đã nhận. **Không thêm bề mặt API nào khác.**

### 5.2 Ghi file JSON

`CaptureGridResult` được ghi ra file **cùng schema `sample_prepare.json`**:

```
sample_<fov>_o<overlap>.json
```

`<fov>` = `TileWidth`; `<overlap>` = **`TileOverlap`** làm tròn (`TileWidth − StepX`, xem §3.2.1).
Bộ 4 case:

```
sample_4096_o64.json
sample_4192_o160.json
sample_4240_o208.json
sample_4320_o288.json
```

> Tên file mang overlap **dẫn xuất**, không phải số khai (`O96/O144/O224` ở tên thư mục cũ).
> Cố ý — nhìn tên là biết giá trị đã đổi.

**Nguồn từng trường:**

| Nhóm | Nguồn |
|---|---|
| `GerberTiles[]` (`OrderIndex`, `Row`, `Column`, `ExpectedX`, `ExpectedY`, `Width`, `Height`) | `CaptureGridResult.Tiles` |
| `OVerLapX_EdgePath`, `OVerLapY_EdgePath` | **`CaptureOverlapX/YPx`** làm tròn (§3.2.1) — **không** phải `TileOverlap` |
| `Width_CaptureImages`, `Height_CaptureImages` | `spec.ImageWidth/Height` |
| `resolution_umPx_X/Y`, `LensCalib` | `spec.CamResX/Y` |
| `GerberSampleImagePath` | tham số |
| Còn lại (`JobName`, `SetUpName`, `RcpName`, `workmode`, `FilePath_AOI`, `Folder_CaptureImages`, `preProcessing`, các `Save*Folder`, …) | **copy nguyên từ file template truyền vào**, mặc định `sample_prepare.json` |

Nhờ copy template, file sinh ra dùng được ngay với mọi đường code đang đọc payload Master —
không phải schema riêng.

### 5.3 Harness `--mode createsamplemem`

Đọc spec từ `global_config.json`, gọi `BuildCaptureGrid`, in bảng:
step, overlap dẫn xuất, `RequiredWidth/Height` vs kích thước raster, danh sách tile bị kẹp.
Ghi file JSON. Tuỳ chọn chạy tiếp `RunAlignStitch` in-memory.

Cho phép sweep `TileWidth` 4096/4192/4240/4320 với **step cố định từ recipe** — chỉ cửa sổ
tìm kiếm to ra, lưới không đổi. Đây mới là phép so sánh có ý nghĩa.

---

## 6. Component D — Pose graph

**Không đổi thuật toán solver.** Không thêm cạnh chéo, không bật bậc tự do scale, không đổi
`MaxIterations`. Ba thứ đó trong báo cáo cũ đều là phản ứng với một tín hiệu đã hỏng.

### 6.1 Đo đủ 4 cạnh mỗi tile

Hiện trạng cần nói chính xác: `edgesTotal = 142 = 8×9 + 7×10` — đồ thị **đã** có đủ cạnh
4-neighbour, `edgesUsed = 142`, `edgesGatedOut = 0`. Vấn đề không ở số lượng cạnh.

Chỗ thiếu ở tầng **đo**: `FullPoseReconciliation` báo `attempted=142; accepted=22; rejected=120`.
Phần lớn 120 cạnh bị `cycle/direct-pose closure check` loại chỉ vì residual đúng bằng sai số
lưới hệ thống (log đầy dòng `residual=31.2 / 31.9 / 32.6 / 32.8 px`). Chúng vẫn vào pose graph
nhưng **không mang theo phép đo ảnh của chính nó**.

Yêu cầu:

1. Mỗi tile đo **tất cả** neighbor trực giao đang tồn tại — 4 ở giữa lưới, 3 ở cạnh, 2 ở góc.
   Mỗi cạnh có phép đo ảnh riêng, không suy từ pose của tile khác.
2. Mỗi cạnh giữ **trạng thái riêng** trong report:
   `Measured` (có phép đo, kèm score) / `ExpectedOnly` (không đo được, rơi về lưới) /
   `Rejected` (đo được nhưng bị loại, kèm lý do + số dư).
3. Pose graph nhận **mọi cạnh có phép đo thật**, để Huber xử lý outlier — thay vì loại cứng
   trước khi vào solver. Đây đúng tinh thần `UseRejectedEdges` đã có; cần xác minh xem hiện nó
   có giữ phép đo hay đã thay bằng expected.
4. Report thêm **bậc mỗi đỉnh**: `measuredEdgeCount / existingNeighborCount`. Nhìn ra ngay
   tile nào đang bị treo bằng 1 cạnh duy nhất — đúng loại tile như node 12 và 59.

> **Cần xác minh khi viết plan:** hành vi thực tế của `UseRejectedEdges` ở
> `GlobalPoseGraphOptimizer` / `PoseGraphProblem`. Có thể bước 3 đã đúng và chỉ cần sửa phần
> ghi report. Không đoán — đọc code rồi ghi kết luận vào plan.

### 6.2 `MaxPoseCorrectionPixels`

Đổi từ hằng số ma thuật (hiện `351.0`, đã từng phải nới tay lên 710) sang **suy từ `CaptureOverlap`**
(§3.2.1 — overlap vật lý, **không** đổi theo `TileWidth`):

```
mặc định = 2 × CaptureOverlapPx      // ≈ 130 px với CaptureOverlap 64.5
```

Vẫn override được qua `align_stitch.ini`. Lý do: một pose dịch quá 2 lần overlap thì chắc chắn
đã mất khớp — không cần tranh luận 351 hay 710.

### 6.3 Báo cáo trung thực

Hiện `converged = false` ở cả 6 run mà `runStatus` vẫn `CompletedWithFallback` — im lặng.
Thêm `converged == false` vào danh sách điều kiện sinh warning.

---

## 7. Ngoài phạm vi đợt này

| Hạng mục | Lý do hoãn |
|---|---|
| Bug ROI 96 px / chống alias | Đo lại sau khi sửa lưới rồi mới quyết (ROI sẽ rộng 160 px thay vì 96) |
| Tiền xử lý ảnh (§8 báo cáo cũ) | Chưa có bằng chứng cần, sau khi sửa lưới mới đo được |
| Siết `NccMinScore` → 0.40, `AngleExtentRad` → 2° | Đổi hành vi matcher — cần đo trước |
| Cạnh chéo pose graph, bậc tự do scale, `EccMotionModel = Affine` | Phản ứng với tín hiệu sai; không còn cơ sở |
| Quality gate "BlankSample > 10%" | **Loại hẳn** — sai với board này (36% trống hợp lệ) |

---

## 8. Tiêu chí nghiệm thu

Chạy lại cả 4 case (4096/4192/4240/4320) với lưới đã sửa:

| Metric | Hiện tại | Mục tiêu |
|---|---|---|
| Residual mỗi cạnh, \|mean\| | 64.5 px (thật) | **< 2 px** |
| Cạnh có phép đo thật | 22 / 142 | **> 100 / 142** |
| `beforeResidualMax` | 573.6 | **< 40** |
| Tile bị cắt ở rìa | 13 | **0** |
| Tile blank | 29 | **29** (giữ nguyên — hợp lệ) |
| `gridCalibration.status` | — | `Ok` |
| `poseGraph.converged` | `false` | đo lại, chưa đặt mục tiêu |

Bậc đỉnh: không tile nào có `measuredEdgeCount < 2`, trừ tile trong vùng trống thật.

---

## 9. File dự kiến chạm

| File | Thay đổi |
|---|---|
| `RDL.GerberStitch/Facade/CaptureGridCalculator.cs` | **mới** — `CaptureGridSpec`, `CaptureGridResult`, phép tính |
| `RDL.GerberStitch/Facade/CaptureGridJsonWriter.cs` | **mới** — ghi `sample_<fov>_o<overlap>.json` từ template |
| `RDL.GerberStitch/Facade/GerberStitchFacade.cs` | thêm `BuildCaptureGrid` |
| `GerberStitching.Core/Alignment/GridCalibrationProbe.cs` | **mới** |
| `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs` | gọi probe trước Direct Alignment; trạng thái cạnh; bậc đỉnh |
| `GerberStitching.Core/Alignment/Graph/PoseGraphOptions.cs` | `MaxPoseCorrectionPixels` suy từ overlap |
| `GerberStitching.Core/Models/WorkflowModels.cs` | mục `gridCalibration`, trạng thái cạnh trong report |
| `RDL.GerberStitch.Harness/Program.cs` | `--mode createsamplemem` |
| `RDL.GerberStitch.Harness/GlobalConfig.cs` | section `CreateSampleMem` |
| `docs/implement_code.html` | entry mới (bắt buộc theo CLAUDE.md) |

Build: `msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64`.
Không thêm test project (AGENTS.md §4) — kiểm thử do user chạy.

---

## Phụ lục — script tái lập phép đo

`matchTemplate` full-res, tham số đã dùng: `TW=64`, `PAD=80`, quét `dx ∈ [3860, 4096)`,
template lấy từ `B[512:3584, 0:64]`, vùng tìm `A[432:3664, 3860:4096]`.
Cặp ngang `(r,c)→(r,c+1)`, cặp dọc `(r,c)→(r+1,c)`, với `idx(r,c) = c*8 + (c chẵn ? r : 7−r)`.
Lọc `NCC > 0.35`. Kết quả: n=27 ngang / n=30 dọc, mean 4031.3 / 4031.7, std 0.54 / 0.51.
