# Deploy dependency checklist — `RDL.GerberStitch.dll`

**Ngày:** 2026-08-07
**Nguồn:** `docs/Phase1_Task03.md` §3–§4 — checklist thật, lấy từ output build `Release|x64` hiện tại.
**Phạm vi:** những gì cần copy khi đưa `RDL.GerberStitch.dll` vào folder deploy Master/Worker. **Không** bao gồm việc nâng HALCON trên 2 repo đó — xem `docs/Phase1_Task03.md` §1 (ngoài phạm vi repo này).

---

## 1. Managed DLL — copy cạnh `Master.exe`/`Worker.exe`

Lấy từ `RDL.GerberStitch/bin/x64/Release/` sau build (không phải `Debug/`):

| File | Kích thước | Ghi chú |
|---|---|---|
| `RDL.GerberStitch.dll` | 23 KB | Façade |
| `GerberStitching.Core.dll` | 451 KB | |
| `GerberEngine.dll` | 31 KB | |
| `OpenCvSharp.dll` | 1 002 KB | |
| `OpenCvSharp.Extensions.dll` | 22 KB | |
| `System.Drawing.Common.dll` | 49 KB | |
| `System.Memory.dll` | 145 KB | |
| `System.Buffers.dll` | 24 KB | |
| `System.Numerics.Vectors.dll` | 110 KB | |
| `System.Runtime.CompilerServices.Unsafe.dll` | 19 KB | |
| `Newtonsoft.Json.dll` | 526 KB | ⚠ **Kiểm tra version khớp Master trước khi copy đè** — xem §3 |

> `halcondotnetxl.dll`/`hdevenginedotnetxl.dll` **KHÔNG** nằm trong danh sách này — `RDL.GerberStitch.csproj`/`GerberStitching.Core.csproj` cố ý đặt `Private=False`, dùng bản đã có sẵn trong `dll\` của Worker/Master (§2).

## 2. HALCON — không copy, chỉ xác nhận version

`RDL.GerberStitch`/`GerberStitching.Core` build với `$(HALCONROOT)` trỏ tới HALCON **25.05**. Trước khi deploy, xác nhận:

1. Version `halcondotnetxl.dll` trong `dll\` của Worker/Master đích **đã là 25.05** (chưa nâng thì **dừng** — deploy sẽ chạy sai version, xem `docs/Phase1_Task03.md` §1.1 để biết trình tự nâng).
2. `HALCONARCH=x64-win64` đã đặt trên máy đích.

```powershell
[Reflection.AssemblyName]::GetAssemblyName("<deploy>\dll\halcondotnetxl.dll").Version
# Kỳ vọng: 25.5.x.x
```

## 3. `Newtonsoft.Json` — kiểm tra version trước khi copy đè

`RDL.GerberStitch.dll` build kèm `Newtonsoft.Json.dll` (từ `..\..\Lib_Supporter\dll\`, `526 KB`). Master hiện dùng `Version=13.0.0.0` (`WindowsFormsApp2.csproj`). Nếu bản đích khác version — **không copy đè**, dùng bản Master đã có (Newtonsoft.Json thường tương thích ngược trong cùng major version) hoặc thêm binding redirect.

## 4. Native OpenCvSharp

```
dll\x64\OpenCvSharpExtern.dll                68.6 MB   — bắt buộc
dll\x64\opencv_videoio_ffmpeg4130_64.dll     28.6 MB   — xem §5
```

## 5. `opencv_videoio_ffmpeg4130_64.dll` — kết quả kiểm chứng

`docs/Phase1_Task03.md` §3.1 đặt câu hỏi: có cần file này không, vì pipeline chỉ xử lý ảnh tĩnh, không phải video.

**Đã kiểm chứng bằng harness thật** (`docs/Phase1_Task06.md` §6, 2026-08-07): chạy `alignstitch` (dataset 80 tile thật, dùng đủ `PyramidEccMatcher`/OpenCV) với `opencv_videoio_ffmpeg4130_64.dll` **bị dời khỏi** thư mục chạy.

| Kết quả | |
|---|---|
| Run có hoàn tất không | ✅ Có — `Success=True`, không lỗi DLL load |
| Số liệu | `AlignedTiles=54`, `BlankTiles=26`, `FailedTiles=0` — khớp tuyệt đối với run có đủ ffmpeg dll |
| **Kết luận** | **Không cần** `opencv_videoio_ffmpeg4130_64.dll` khi deploy — bỏ được, giảm **28.6 MB** mỗi lần deploy Master/Worker |

> Kết quả đầy đủ nằm ở `docs/Phase1_Task06.md` §6 (chi tiết dataset, log).

## 6. `System.*` binding redirect (nếu deploy vào Master)

Master tham chiếu version cũ hơn 4 assembly `System.Buffers`/`System.Memory`/`System.Numerics.Vectors`/`System.Runtime.CompilerServices.Unsafe` — xem `docs/Phase1_Task03.md` §4.1 để lấy mẫu `<bindingRedirect>`. **Không tự sửa `Master.exe.config`/`Worker.exe.config`** — đó là file thuộc repo khác, ngoài phạm vi ở đây.

## 7. Checklist copy nhanh

- [ ] Build `RDL.GerberStitch.sln` cấu hình `Release|x64`
- [ ] Copy 10 managed DLL ở §1 (trừ `Newtonsoft.Json.dll` nếu version lệch — xem §3)
- [ ] Copy `dll\x64\OpenCvSharpExtern.dll`
- [ ] ~~Copy `dll\x64\opencv_videoio_ffmpeg4130_64.dll`~~ — **đã xác nhận không cần**, bỏ khỏi deploy (§5)
- [ ] Xác nhận `halcondotnetxl.dll` ở đích đã là 25.05 (§2) — **không** copy bản của repo này đè lên
- [ ] Thêm binding redirect nếu Master/Worker báo `FileLoadException` (§6)
- [ ] Test: `new GerberStitchFacade()` không lỗi DLL load từ process Worker/Master thật
