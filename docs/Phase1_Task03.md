# Phase 1 — Task 1.3: Chuẩn hoá dependency & đóng gói runtime

**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1
**Phụ thuộc:** Task 1.1 (project tạo xong), Phase 0.2 (version HALCON chốt), Phase 0.5 (quyết định OpenCvSharp)

---

## Mục tiêu

Đảm bảo `RDL.GerberStitch.dll` khi deploy vào folder Master/Worker sẽ resolve được **tất cả** native + managed dependency mà không xung đột với DLL hiện có. Theo convention RDL: native DLL nằm trong `..\dll\`.

---

## Nơi thực hiện

```
<RDL Deploy Root>/
├── Master.exe
├── Worker.exe
├── RDL.GerberStitch.dll          ← managed, cùng folder exe
├── GerberStitching.Core.dll      ← managed, cùng folder exe
├── GerberEngine.dll              ← managed, cùng folder exe
└── dll/                          ← native DLL convention RDL
    ├── halcondotnetxl.dll        ← đã có sẵn (Worker dùng)
    ├── hdevenginecpp.dll         ← HALCON native
    ├── OpenCvSharpExtern.dll     ← nếu giữ OpenCvSharp
    └── opencv_world4130.dll      ← nếu giữ OpenCvSharp
```

---

## Việc cần làm

### A. HALCON hint path

Worker RDL đã dùng HALCON → DLL native đã nằm trong `..\dll\` hoặc GAC. Cần xác nhận:

1. **Check Worker hiện tại load HALCON từ đâu:**
   ```
   - GAC? → Assembly.GetAssembly(typeof(HOperatorSet)).Location
   - Local bin? → check bin\halcondotnetxl.dll
   - ..\dll\? → check relative path
   ```

2. **Nếu Core dùng version khác (đã align ở Phase 0.2):** copy đúng version vào vị trí Worker đang load. Không để 2 version tồn tại song song.

3. **HALCON environment variable:**
   - `HALCONROOT` — phải trỏ đúng version đã chốt
   - `HALCONARCH` — `x64-win64` (hoặc theo máy Worker)
   - Ghi note nếu cần update system env trên máy deploy

4. **`RDL.GerberStitch.csproj` — hint path:**
   ```xml
   <Reference Include="halcondotnetxl">
     <HintPath>..\dll\halcondotnetxl.dll</HintPath>
     <Private>False</Private>  <!-- không copy local, dùng chung với Worker -->
   </Reference>
   ```

### B. OpenCvSharp runtime (nếu giữ — theo Phase 0.5)

OpenCvSharp4 NuGet kéo theo:
- `OpenCvSharp4` (managed) — vào bin
- `OpenCvSharp4.runtime.win` (native) — `OpenCvSharpExtern.dll` + `opencv_world4130.dll`

**Cần làm:**

1. Xác nhận native DLL đi kèm NuGet tương thích x64 (Worker chạy x64)
2. Copy native DLL vào `..\dll\`:
   ```
   dll\OpenCvSharpExtern.dll
   dll\opencv_world4130.dll
   ```
3. Hoặc dùng `<NativeLibraryPath>` trong app.config / runtime config để trỏ vào `..\dll\`
4. **Test:** gọi 1 hàm OpenCvSharp đơn giản (vd `Cv2.GetBuildInformation()`) từ Worker process → không crash = OK

### C. Nếu bỏ OpenCvSharp (theo Phase 0.5)

Nếu Phase 0.5 quyết định gỡ:
1. Fork/patch `GerberStitching.Core` → loại bỏ reference `OpenCvSharp4` trong `.csproj`
2. Conditional compile hoặc xoá file dùng OpenCvSharp (list từ Phase 0.5)
3. Build lại Core → confirm không thiếu reference
4. **Không cần deploy OpenCvSharp native DLL** → giảm 1 nguồn xung đột

### D. Dependency khác (transitively từ Core)

Check `packages.config` hoặc `PackageReference` trong `GerberStitching.Core.csproj`:

| Package | Cần xử lý |
|---|---|
| `Newtonsoft.Json` | Likely đã có trong RDL — check version conflict, dùng binding redirect nếu cần |
| `System.Drawing.Common` | .NET 4.8 đã có, no-op |
| `MathNet.Numerics` | Nếu Core dùng (PoseGraph) → copy managed DLL, không có native |
| Các package khác | List ra, check từng cái xem RDL đã có chưa |

**Binding redirect** (nếu cần, trong `Worker.exe.config` / `Master.exe.config`):
```xml
<dependentAssembly>
  <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" />
  <bindingRedirect oldVersion="0.0.0.0-13.0.0.0" newVersion="13.0.0.0" />
</dependentAssembly>
```

### E. Build output verification

Sau khi cấu hình xong, build `RDL.GerberStitch` và verify:

```powershell
# Liệt kê tất cả DLL output
dir bin\Release\*.dll | Select-Object Name, Length

# Check managed dependency resolve
# Dùng ILSpy hoặc:
[System.Reflection.Assembly]::LoadFrom("bin\Release\RDL.GerberStitch.dll").GetReferencedAssemblies()
```

Confirm danh sách DLL:
- [x] `RDL.GerberStitch.dll`
- [x] `GerberStitching.Core.dll`
- [x] `GerberEngine.dll`
- [x] `halcondotnetxl.dll` (hoặc GAC)
- [x] `OpenCvSharp4.dll` + native (nếu giữ)
- [x] Transitive packages (Json.NET, MathNet, ...)

---

## Đầu vào

| Item | Nguồn |
|---|---|
| Version HALCON đã chốt | Output Phase 0.2 |
| Quyết định OpenCvSharp | Output Phase 0.5 |
| Cấu trúc folder `..\dll\` của RDL | Máy deploy Worker/Master hiện tại |
| `packages.config` của Core | Repo GerberViewer |

## Đầu ra

| Item | Vị trí |
|---|---|
| `RDL.GerberStitch.csproj` cập nhật hint path | `<SolutionDir>\RDL.GerberStitch\` |
| Native DLL deploy script / checklist | Ghi vào note hoặc `deploy_deps.md` |
| Binding redirect (nếu cần) | `Worker.exe.config`, `Master.exe.config` |

---

## Exit gate

- [ ] Build `RDL.GerberStitch` clean, không warning assembly reference
- [ ] Deploy vào folder Worker: Worker khởi động bình thường, flow AOI cũ không ảnh hưởng
- [ ] Deploy vào folder Master: Master khởi động bình thường
- [ ] Gọi `new GerberStitchFacade()` từ Worker process — không crash DLL load
- [ ] HALCON operator (`HOperatorSet.ReadImage`) chạy được từ cả 2 app sau deploy
