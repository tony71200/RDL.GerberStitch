# Hướng dẫn chạy `app.py` (ECC Sandbox) và `RDL.GerberStitch.Harness.exe`

Tài liệu này gộp hai công cụ dùng trong đợt hiệu chỉnh lưới chụp (grid pitch calibration,
xem [`docs/superpowers/plans/2026-08-15-grid-pitch-calibration.md`](superpowers/plans/2026-08-15-grid-pitch-calibration.md)):

- **`RDL.GerberStitch.Harness.exe`** — console app C#, chạy façade thật (`RDL.GerberStitch.dll`)
  với dữ liệu thật. Đây là công cụ **sản phẩm**, nằm trong `RDL.GerberStitch.sln`.
- **`tools/ecc_sandbox/app.py`** — công cụ Python **khảo sát**, nằm hoàn toàn ngoài `.sln`, dùng để
  dò tham số và mô phỏng thuật toán ECC trước khi đổi code C#. Không phải sản phẩm, không được
  Master/Worker dùng.

> Cả hai đều **do bạn tự chạy**. Agent không tự chạy `.exe` hay tự mở UI Tkinter (AGENTS.md §4) —
> chỉ build, đọc kết quả, và phân tích số liệu bạn cung cấp.

---

## Phần 1 — `RDL.GerberStitch.Harness.exe`

### 1.1 Build trước khi chạy

```bash
msbuild RDL.GerberStitch.sln /p:Configuration=Debug /p:Platform=x64
```

Cần biến môi trường `HALCONROOT` trỏ tới thư mục cài HALCON 25.05 Progress. Build xong, `.exe` nằm
ở:

```
RDL.GerberStitch.Harness\bin\x64\Debug\RDL.GerberStitch.Harness.exe
```

(hoặc `bin\x64\Release\...` nếu build Release).

### 1.2 Cấu hình — `global_config.json`

File này nằm **cạnh** `.exe` sau khi build (được copy tự động từ
`RDL.GerberStitch.Harness\global_config.json`). Có 4 section, mỗi section ứng với 1 mode:

```json
{
  "AlignStitch":      { "ManifestPath": "...", "ImagesPath": "...", "OutputPath": "..." },
  "AlignStitchMem":    { "PayloadPath": "...", "ImagesPath": "...", "OutputPath": "...",
                         "SettingsPath": "...", "RecipeSettingsPath": "..." },
  "CreateSample":      { "RasterImagePath": "...", "OutputPath": "..." },
  "CreateSampleMem":   { "RasterImagePath": "...", "TemplatePayloadPath": "...", "OutputPath": "...",
                         "CapturePitchX": ..., "CapturePitchY": ..., "CamResX": ..., "CamResY": ...,
                         "ImageWidth": ..., "ImageHeight": ..., "Rows": ..., "Columns": ...,
                         "StartOffsetX": ..., "StartOffsetY": ..., "TileWidth": ..., "TileHeight": ... }
}
```

Mọi giá trị trong `global_config.json` đều **có thể ghi đè bằng tham số dòng lệnh** — dùng
`global_config.json` cho các đường dẫn hay lặp lại, dùng CLI khi muốn đổi nhanh 1 giá trị (ví dụ
sweep `--tile`).

### 1.3 Bốn mode

Chọn mode bằng `--mode <tên>`. Mặc định là `alignstitch` nếu không truyền.

```bash
RDL.GerberStitch.Harness.exe --mode <alignstitch|alignstitchmem|createsample|createsamplemem> [tham số]
```

#### `createsamplemem` — dựng lưới, in bảng, xuất payload (Task 1–2)

Không cần ảnh chụp thật, chạy trong vài giây. Dùng để kiểm chứng công thức lưới và sinh
`sample_<fov>_o<overlap>.json`.

```bash
RDL.GerberStitch.Harness.exe --mode createsamplemem
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4192
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4240
RDL.GerberStitch.Harness.exe --mode createsamplemem --tile 4320
```

Tham số CLI: `--tile <px>` (mặc định = `CreateSampleMem.TileWidth`), `--tileh <px>` (mặc định = bằng
`--tile`), `--raster <path>`, `--template <path>`, `--out <thư mục>`.

**Đối chiếu quan trọng nhất** (xem Bước 1.9 trong plan):

```
StepX / StepY     = 4033.0    / 4033.0
CaptureOverlap    = 63 / 63        ← PHẢI giữ nguyên ở cả 4 lần chạy, không đổi theo --tile
TileOverlap       = 63 / 159 / 207 / 287   (đổi theo --tile)
Clamped tiles     = 0              ← hiện trạng cũ (trước Task 1) là 13
```

Nếu có `--out`/`--template` hợp lệ (đã có sẵn trong `global_config.json`), lệnh còn in dòng
`Payload = ...\sample_<fov>_o<overlap>.json` — đây là file bạn dùng cho mode `alignstitchmem` ở
dưới.

#### `alignstitchmem` — chạy align+stitch thật với payload in-memory (Task 3, 6)

Đây là mode chính để kiểm chứng `GridCalibrationProbe` (Task 3) và sweep 4 FOV (Task 6). Cần ảnh
chụp thật + raster Gerber thật.

```bash
RDL.GerberStitch.Harness.exe --mode alignstitchmem
```

Sửa `AlignStitchMem.PayloadPath` trong `global_config.json` để đổi payload test (hoặc truyền
`--payload <path>` trên CLI). Kết quả ghi ra thư mục `AlignStitchMem.OutputPath`, file quan trọng
nhất là `processing_report.json` bên trong.

**Đối chiếu cho Task 3** — chạy 2 lần:

1. Payload **lưới cũ** (bước 4096, trước Task 1–2) → kỳ vọng dừng sớm với lỗi chứa
   `GridMismatch`, không sinh `Stitched.tiff`.
2. Payload **mới** `sample_4096_o63.json` (từ mode `createsamplemem` ở trên) → kỳ vọng
   `processing_report.json` có:
   ```
   gridCalibration.status        = "Ok"
   gridCalibration.measuredStepX ≈ 4031.3
   gridCalibration.measuredStepY ≈ 4031.7
   gridCalibration.medianScore   > 0.8
   ```

**Đối chiếu cho Task 6** — chạy đủ 4 payload (`sample_4096_o63.json`,
`sample_4192_o159.json`, `sample_4240_o207.json`, `sample_4320_o287.json`), mỗi lần đổi
`PayloadPath` rồi chạy lại. Giữ nguyên `processing_report.json` của cả 4 lần (đổi tên/copy ra
thư mục riêng trước khi chạy lần kế, vì `OutputPath` giống nhau sẽ ghi đè).

Đọc nhanh 1 report bằng Python:

```bash
python -c "
import json
d = json.load(open(r'<duong_dan>\processing_report.json', encoding='utf-8-sig'))
g = d.get('gridCalibration'); p = d['poseGraph']
print('gridCalibration:', {k: g[k] for k in ('status','measuredStepX','measuredStepY','deltaX','deltaY')} if g else None)
print('edges:', {k: p[k] for k in p if k.startswith('edges')})
print('residual before/after max:', p['beforeResidualMax'], '->', p['afterResidualMax'], '  converged:', p['converged'])
"
```

#### `createsample` / `alignstitch` — mode cũ, không liên quan đợt hiệu chỉnh này

Dùng payload trên đĩa (`sample_manifest.json`) thay vì in-memory. Không cần cho Task 1–6, chỉ nêu
để biết chúng vẫn tồn tại. Xem `docs/Phase1_Task06.md` nếu cần.

### 1.4 Lỗi hay gặp

| Lỗi | Nguyên nhân | Cách fix |
|---|---|---|
| `halcondotnetxl` không resolve lúc build | Thiếu `HALCONROOT` | Set biến môi trường trỏ đúng thư mục cài HALCON 25.05 |
| `FileNotFoundException: Newtonsoft.Json` lúc chạy (không phải lúc build) | Thiếu reference copy-local | Đã fix ở Task 2 — nếu vẫn gặp, kiểm `Newtonsoft.Json.dll` có trong `bin\x64\Debug\` không |
| `Thiếu section "CreateSampleMem"...` | `global_config.json` cạnh `.exe` không có section đó | Kiểm file đã được copy đúng chưa, hoặc truyền đủ tham số qua CLI |

---

## Phần 2 — `tools/ecc_sandbox/app.py`

### 2.1 Cài đặt (một lần)

```bash
python -m pip install -r tools/ecc_sandbox/requirements.txt
```

Tkinter đi kèm sẵn Python trên Windows, không cần cài thêm.

### 2.2 Chạy

```bash
python tools/ecc_sandbox/app.py
```

Cửa sổ mở với panel tham số bên trái và 3 tab bên phải (**Tiền xử lý** / **Kết quả match** /
**Ma trận & log**). Mọi tham số ECC/tiền xử lý mặc định đúng bằng giá trị C# thật (đọc từ
`RDL.GerberStitch.Harness/align_stitch.ini` nếu tìm thấy, không phải mặc định của OpenCV).

### 2.3 Quy trình dùng

1. **Dữ liệu**: chọn `Payload JSON` (file `sample_<fov>_o<overlap>.json` sinh từ mode
   `createsamplemem` ở Phần 1), `Thư mục ảnh chụp`, và `Raster Gerber` (nếu dùng mode Direct).
2. **Cặp kiểm tra**: chọn `Direct` (tile Gerber ↔ ảnh chụp cùng vị trí) hoặc `Neighbor` (2 ảnh chụp
   kề nhau), nhập `Row`/`Column`.
3. **Tiền xử lý** / **PyramidECC**: chỉnh tham số nếu cần dò (mọi ô là ô nhập số, không phải thanh
   trượt — để chép nguyên văn giá trị đã thử vào bảng kết quả).
4. Bấm **CHẠY**. Đọc `rawScore`, ma trận 3×3, và kết luận ACCEPTED/REJECTED ở tab "Ma trận & log".

### 2.4 Ba thí nghiệm cần làm (Bước 5.8–5.10 của plan)

**(a) Kiểm chứng port đúng — quan trọng nhất.** Tắt `FlattenAndEnhance`, `Contrast = 100`, chọn
một tile mà `processing_report.json` (từ mode `alignstitchmem` ở Phần 1) có ghi `eccCorrelation`.
So `rawScore` sandbox với `eccCorrelation` thật — lệch dưới ~0.02 nghĩa là port đúng.

> Agent đã tự kiểm phần thuật toán bằng ground-truth test (dịch ảnh đi (+12,+5) px bằng tay, ECC
> tìm lại được (−12.07,−5.02), sai số <0.08px) — phần *logic* đã xác nhận đúng. Phần còn lại của
> bước này (so với `eccCorrelation` thật của **đúng tile bạn quan tâm**) vẫn cần bạn tự làm, vì cần
> đúng payload/ảnh/raster khớp nhau.

**(b) Thí nghiệm bước lưới.** Cùng 1 tile, chạy 2 lần chỉ đổi ô **Step ghi đè** (nền vàng, trong
nhóm "Cặp kiểm tra"): `4096` rồi `4031.5`. `rawScore` phải cao hơn rõ rệt ở `4031.5` — xác nhận
trực quan cho toàn bộ Task 1–3.

**(c) Dò tham số tiền xử lý.** Bật `FlattenAndEnhance`, quét `Background sigma` ∈ {31, 51, 81} và
`CLAHE clip limit` ∈ {2, 3, 4} trên 5–6 tile. Ghi bảng `rawScore` trước/sau. **Tiêu chí: ECC
correlation không được giảm** — nếu giảm, tổ hợp đó đang phá gradient ảnh, không đưa vào C#.

### 2.5 Cảnh báo quan trọng

**Không bao giờ đưa ảnh đã bật `ToBinaryTraces` vào ECC.** Checkbox đó trong UI chỉ để **xem** ảnh
nhị phân ở tab "Tiền xử lý" (có dòng nhắc đỏ ngay dưới lưới ảnh) — pipeline `match()` không bao giờ
nhận ảnh nhị phân làm input, vì gradient của nó bằng 0 hầu như mọi nơi khiến `cv2.findTransformECC`
không hội tụ.

### 2.6 Sự cố hay gặp

| Sự cố | Nguyên nhân | Cách fix |
|---|---|---|
| `UnicodeEncodeError` khi in ra console (không phải trong UI) | Console Windows dùng codepage cp1252, không in được tiếng Việt có dấu | Chạy với `set PYTHONIOENCODING=utf-8` trước khi gọi `python`, hoặc bỏ qua nếu chỉ là log debug ngoài UI |
| `THẤT BẠI: RuntimeFailure — ECC khong hoi tu` | Bình thường nếu 2 ảnh không thực sự chồng lên nhau (crop sai vị trí, hoặc `Row`/`Column` không đúng tile) | Kiểm lại `Row`/`Column`, thử mode Neighbor với 2 ảnh chắc chắn kề nhau để xác nhận UI hoạt động đúng trước |
| Không thấy tile nào ở `Row`/`Column` đã nhập | Payload JSON không có tile đó | Mở file JSON kiểm `GerberTiles[].Row/.Column`, hoặc dùng `OrderIndex` khác |

---

## Tóm tắt thứ tự khuyến nghị

1. Build C# (`msbuild ... x64`).
2. `--mode createsamplemem` (4 lần, `--tile` 4096/4192/4240/4320) → đối chiếu bảng in ra, lấy 4
   file payload.
3. `--mode alignstitchmem` với payload lưới cũ → xác nhận `GridMismatch`.
4. `--mode alignstitchmem` với `sample_4096_o63.json` → xác nhận `gridCalibration.status = Ok`.
5. `python tools/ecc_sandbox/app.py` → 3 thí nghiệm (2.4a–c).
6. `--mode alignstitchmem` với 4 payload còn lại → thu số liệu cho Task 6, báo lại kết quả.
