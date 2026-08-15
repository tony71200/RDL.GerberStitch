# ECC / Preprocessing Sandbox

**Đây là công cụ khảo sát (survey tool), KHÔNG PHẢI code sản phẩm.** Nó nằm hoàn toàn ngoài
`RDL.GerberStitch.sln` — không phải project C#, không được build bằng `msbuild`, không được
Master/Worker reference. Mục đích: dò tham số tiền xử lý ảnh (Findings §8.3) và quan sát thuật
toán `PyramidEccMatcher` (align ảnh bằng ECC pyramid) **trước khi** đổi bất kỳ dòng code C#
nào trong `GerberStitching.Core`. Vòng lặp thử–sai ở đây mất vài giây; build + chạy lại cả lô
80 tile trong C# mất vài phút.

Các file trong `pyramid_ecc.py` là **port 1:1** (từng dòng, không viết lại thuật toán) của
`GerberStitching.Core/Matching/OpenCv/PyramidEccMatcher.cs`. Tham số mặc định trong `config.py`
lấy đúng từ code C# thật (`AlignStitchStageOptions.cs`, `MatcherOptions.cs`), **không phải**
mặc định của OpenCV.

## Cài đặt

Tkinter đi kèm sẵn với Python trên Windows, không cần cài thêm.

```bash
python -m pip install -r tools/ecc_sandbox/requirements.txt
```

## Chạy

```bash
python tools/ecc_sandbox/app.py
```

Cửa sổ mở với 3 tab bên phải (Tiền xử lý / Kết quả match / Ma trận & log) và panel tham số bên
trái. Quy trình dùng:

1. Chọn `Payload JSON` (`sample_<fov>_o<overlap>.json` từ Task 2), `Thư mục ảnh chụp`, và (nếu
   dùng mode Direct) `Raster Gerber`.
2. Chọn chế độ cặp kiểm tra:
   - **Direct**: crop raster Gerber tại đúng vết chân danh nghĩa của tile `(Row, Column)`, so với
     ảnh chụp cùng `OrderIndex`.
   - **Neighbor**: hai ảnh chụp kề nhau theo hướng `left/right/top/bottom`.
3. Chỉnh tham số tiền xử lý / PyramidECC (mọi ô đều là ô nhập số, không phải thanh trượt — để
   giá trị thử được có thể chép nguyên văn vào bảng kết quả).
4. Bấm **CHẠY**.

## Ý nghĩa ô "Step ghi đè"

Ô này (trong nhóm "Cặp kiểm tra", có nền vàng nổi bật để không nhầm với các tham số khác) là
**tham số DUY NHẤT không tương ứng với bất kỳ setting nào trong C#**. Nó cho phép tính lại vị trí
crop reference bằng `(Row * step, Column * step)` thay vì dùng `ExpectedX/ExpectedY` có sẵn trong
payload — dùng để so trực tiếp bước lưới `4096` (giả định ban đầu, sai) với `4031.5` (giá trị hiệu
chỉnh của Task 1–3) trên cùng một tile, không cần sinh lại payload. Để `0` để dùng đúng toạ độ đã
có trong payload.

## Cảnh báo quan trọng: KHÔNG đưa ảnh nhị phân (`ToBinaryTraces`) vào ECC

`preprocess.to_binary_traces` (bước 3+4 của Findings §8.3) chỉ dùng cho các thuật toán so khớp
hình dạng/đường viền như chamfer, Hausdorff, ICP, hoặc skeleton matching. **Không được** dùng kết
quả này làm input cho ECC (`pyramid_ecc.match`): ảnh nhị phân có gradient bằng 0 ở hầu hết mọi nơi
(trừ viền 1 pixel giữa vùng đen/trắng), nên `cv2.findTransformECC` sẽ không hội tụ hoặc hội tụ về
một cực trị vô nghĩa (Findings §8.2). Checkbox "ToBinaryTraces" trong UI chỉ để **xem** ảnh nhị
phân ở tab "Tiền xử lý" (có dòng nhắc màu đỏ ngay dưới lưới ảnh) — pipeline `match()` luôn nhận
`final` (là `flattened` nếu bật `FlattenAndEnhance`, ngược lại là `contrast`), không bao giờ nhận
`binary`.

## Ba điểm dễ port sai giữa OpenCV Python và OpenCvSharp C# (đã xử lý đúng trong `pyramid_ecc.py`)

1. `cv2.findTransformECC(templateImage=reference, inputImage=moving, ...)` — thứ tự này ngược
   trực giác (nhiều người quen viết `moving` trước).
2. Pyramid index 0 = **full resolution** (không phải nhỏ nhất). Vòng lặp chạy từ index lớn nhất
   (thô nhất) về 0 (mịn nhất).
3. `scale` trong `_to_warp_at_scale` chỉ nhân vào phần **translation** (`m[0,2]`, `m[1,2]`),
   không đụng phần xoay/scale của ma trận.

## Kiểm chứng port đúng (việc của user, xem `task-5-report.md`)

Chạy sandbox ở Direct, tắt `FlattenAndEnhance`, `Contrast = 100`, trên một tile có sẵn
`eccCorrelation` trong `processing_report.json`. So `rawScore` của sandbox với `eccCorrelation`.
Lệch dưới ~0.02 nghĩa là port đúng.
