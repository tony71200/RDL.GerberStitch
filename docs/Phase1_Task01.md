# Phase 1 — Task 1.1: Tạo project wrapper `RDL.GerberStitch`

**Phase:** 1 — Đóng gói Core thành thư viện dùng chung
**PD ước lượng:** 1.5
**Phụ thuộc:** Phase 0 hoàn tất (build thành công, version HALCON đã chốt, quyết định OpenCvSharp đã có)

---

## Mục tiêu

Tạo project `RDL.GerberStitch` (Class Library, .NET Framework 4.8) làm **wrapper duy nhất** bọc `GerberStitching.Core` + `GerberEngine`. Toàn bộ Master/Worker chỉ reference DLL này — không reference trực tiếp Core hay GerberEngine.

---

## Hướng dẫn tạo project (bạn tự làm)

1. Trong solution RDL (hoặc solution phụ từ Phase 0), **Add → New Project**:
   - Template: **Class Library (.NET Framework)**
   - Name: `RDL.GerberStitch`
   - Target Framework: **.NET Framework 4.8**
   - Output path: theo convention RDL (vd `bin\Release\` hoặc `..\dll\`)

2. Add project reference:
   - `GerberStitching.Core`
   - `GerberEngine`
   (cả 2 đã có trong solution phụ Phase 0)

3. NuGet / reference:
   - `halcondotnetxl` — version đã chốt từ Phase 0.2
   - `OpenCvSharp4` 4.13 — nếu Phase 0.5 quyết định giữ
   - Các dependency transitively kéo theo từ Core (check `packages.config` hoặc `.csproj`)

4. Cấu trúc folder đề xuất:

```
RDL.GerberStitch/
├── Facade/
│   ├── GerberStitchFacade.cs          ← API chính (Task 1.2)
│   └── AlignStitchConfig.cs           ← Config mapping (Task 1.4)
├── Internal/                          ← re-export / adapter nếu cần
├── Properties/
│   └── AssemblyInfo.cs
├── RDL.GerberStitch.csproj
└── packages.config
```

5. `InternalsVisibleTo`: **không cần** — façade là public, pipeline Core giữ internal.

---

## Việc cần làm sau khi tạo project

### A. Ẩn chi tiết pipeline — chỉ expose façade

Trong `RDL.GerberStitch.csproj`, đảm bảo:
- Tất cả class từ `GerberStitching.Core` / `GerberEngine` được reference nhưng **không re-export** public.
- Chỉ namespace `RDL.GerberStitch.Facade` là public API.
- Nếu Core có class public mà Master/Worker không cần dùng trực tiếp → wrap lại hoặc dùng `[EditorBrowsable(Never)]`.

### B. Build verification

```
# Build xong phải thấy:
bin\Release\RDL.GerberStitch.dll
bin\Release\GerberStitching.Core.dll        ← copy local
bin\Release\GerberEngine.dll                ← copy local
bin\Release\halcondotnetxl.dll              ← copy local (hoặc GAC)
bin\Release\OpenCvSharp*.dll                ← nếu giữ
```

### C. Smoke test reference

Tạo 1 console project trống (hoặc dùng lại harness Phase 0.3):
- Add reference `RDL.GerberStitch.dll`
- Viết 2 dòng: `var facade = new GerberStitchFacade();` — build pass = OK
- Chưa cần gọi hàm thật — API façade viết ở Task 1.2

---

## Đầu vào

| Item | Nguồn |
|---|---|
| `GerberStitching.Core` project | Clone từ `tony71200/GerberViewer` branch `2026-08-04_Ver4_implement_claude` |
| `GerberEngine` project | Cùng repo trên |
| Version HALCON đã chốt | Output Phase 0.2 |
| Quyết định OpenCvSharp (giữ/bỏ) | Output Phase 0.5 |

## Đầu ra

| Item | Vị trí |
|---|---|
| Project `RDL.GerberStitch` | `<SolutionDir>\RDL.GerberStitch\` |
| DLL build thành công | `RDL.GerberStitch\bin\Release\RDL.GerberStitch.dll` |
| Smoke test pass | Console project reference được DLL, build không lỗi |

---

## Exit gate

- [x] Project `RDL.GerberStitch` build thành công trên máy build RDL
- [x] Output DLL kéo đúng các dependency (Core, GerberEngine, HALCON, OpenCvSharp nếu giữ)
- [x] Console project trống reference được và build pass
- [x] Không có class nội bộ của Core bị lộ public ngoài ý muốn
