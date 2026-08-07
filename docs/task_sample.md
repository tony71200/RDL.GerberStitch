# Implement Task 1 — Tab "Execute Time" + đo thời gian theo giai đoạn (Tab 3)

- **Ngày:** 2026-08-06
- **Repository:** `tony71200/GerberViewer`
- **Branch làm việc:** `2026-08-04_Ver4_implement_claude`
- **Platform:** C# 7.3, .NET Framework 4.8, WinForms, **x64**
- **Yêu cầu gốc:** mục 1.1
- **Thứ tự thực thi:** làm **sau** `20250806_implement_task4.md` (Designer phải mở được trước khi sửa `.Designer.cs`).
- **Bắt buộc đọc trước khi sửa:**
  1. `.claude/AGENTS.md` (mục 3.1 — không đặt logic vào `.Designer.cs`)
  2. `flow_v4.md` §3 (Tab 3 flow), `specify_implement.md`
  3. File task này
- **Change record bắt buộc:** cập nhật `Log.html` và `Params.html`.
- Cập nhật vào history.html với các mục title (new, implement, fix bug), Nội dung thay đổi, Các bị thay đổi, Rút kinh nghiệm trong trường hợp fix bug
- **Comment thay đổi lớn:**
  ```csharp
  // [Claude] [Change time: 2026-08-06] [Purpose: ...]
  ```
- Không thêm NuGet package. Không thêm project test. Không tự chạy test.

---

## 0. Vấn đề

Tab 3 hiện chỉ có **một** `Stopwatch` bao toàn bộ run (`GerberViewer/Views/AlignStitchingControl.cs:252`), kết quả in vào đúng một dòng log qua `LogRunSummary(result, stopwatch.Elapsed, ...)` (`:313`) → chuỗi `"; elapsed=" + elapsed` (`:398-407`).

Hệ quả: không biết Direct Alignment tốn bao nhiêu so với Pose Graph Optimizer hay Stitching → không có cơ sở tối ưu pipeline.

Các stopwatch đã có trong Core đều ở **tầng khác**, không dùng được cho mục tiêu này:


| Đã có                                                                       | Đo gì                                                                                                  |
| ------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------- |
| `Matching/MatchResultCompletion.cs:9,15`                                       | `MatchResult.ProcessingTime` — từng lần match                                                         |
| `Alignment/Evaluation/DirectCandidateEvaluator.cs:65,165,178,184`              | `DirectCandidateEvaluation.ProcessingTime` — từng candidate                                            |
| `StitchingExecutionReport.ElapsedMilliseconds` (`Models/WorkflowModels.cs:48`) | **Chỉ** `HalconWarpThenTileOffsetEngine` set (`:36,:98`); engine mặc định và rebased để nguyên 0 |
| `Diagnostics/DebugHtmlReportWriter.cs`                                         | Không có timing nào cả — chỉ là bảng pose                                                        |

**Kết luận: chưa có cơ chế đo theo stage. Phải làm mới.**

---

## 1. Model thời gian (Core)

**File:** `GerberStitching.Core/Models/WorkflowModels.cs`

### 1.1 Thêm class mới (đặt cạnh `StitchingExecutionReport`, ~dòng 53)

```csharp
// [Claude] [Change time: 2026-08-06] [Purpose: Đo thời gian từng giai đoạn của workflow Tab 3 để hiển thị trên tab Execute Time.]
public sealed class StageTimingReport
{
    public string Stage { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Detail { get; set; }
}
```

`Detail` là chuỗi tự do mô tả khối lượng công việc của stage (`"tiles=80"`, `"edges=118/130"`, `"skipped (disabled)"`) — giúp đọc số đo có ngữ cảnh.

### 1.2 Thêm vào `ProcessingReport`

`ProcessingReport` khai báo ở `WorkflowModels.cs:161+`. Thêm ngay dưới `StitchingExecution`:

```csharp
public IList<StageTimingReport> StageTimings { get; set; } = new List<StageTimingReport>();
```

### 1.3 Thêm vào `StitchingExecutionReport` (phục vụ §3)

```csharp
public long SaveElapsedMilliseconds { get; set; }
```

---

## 2. Gắn móc đo trong `AlignStitchWorkflowService.RunCore`

**File:** `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs`, hàm `RunCore` (`:85-273`).

### 2.1 Nguyên tắc

- Dùng **`Stopwatch` cộng dồn** (`Start()`/`Stop()` nhiều lần) chứ **không** đo theo mốc đầu-cuối, vì Direct Alignment và Failure Recovery **lồng nhau trong cùng một vòng lặp** (`:111-144`) — không có mốc đầu-cuối tách bạch.
- Toàn bộ code đo phải nằm **ngoài** mọi `#if DEBUG` để bản Release cũng có số liệu.
- Helper cục bộ (private static trong class):
  ```csharp
  private static void Mark(ProcessingReport report, string stage, Stopwatch sw, string detail)
  {
      report.StageTimings.Add(new StageTimingReport
      {
          Stage = stage,
          ElapsedMilliseconds = sw.ElapsedMilliseconds,
          Detail = detail
      });
  }
  ```
- Thứ tự thêm vào `report.StageTimings` phải đúng **thứ tự pipeline** (grid hiển thị theo thứ tự này, không sort lại).

### 2.2 Bảng ranh giới 8 stage — đã trace xong, dùng nguyên


| # | Stage                         | Vị trí bắt đầu                                                                           | Vị trí kết thúc                      | Cách đo                                                                                                                                                                                                                                                 |
| - | ----------------------------- | --------------------------------------------------------------------------------------------- | ---------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1 | **Mapping and Preprocessing** | `:96` `ct.ThrowIfCancellationRequested()`                                                     | `:106` (sau khi gán `ordered`)          | Mốc đầu-cuối. Bao trọn`_mappingService.ValidateAndMap(...)` (`:97`) + `ProcessingReport.Create` + `CreateSnapshot`. `Detail = "tiles=" + ordered.Count`                                                                                              |
| 2 | **Direct Alignment**          | quanh`SolveDirect(...)` `:117`                                                                | —                                       | **Cộng dồn** `swDirect.Start()` trước dòng 117, `swDirect.Stop()` ngay sau. `Detail = "tiles=" + ordered.Count`                                                                                                                                      |
| 3 | **Failure Recovery**          | quanh`Recover(...)` `:135`, `ResolveFailedWithoutNeighbor(...)` `:139`, `Recover(...)` `:151` | —                                       | **Cộng dồn** `swRecover` qua cả 3 điểm (pass 1 inline + pass 2). `Detail = "recovered=" + <số tile qua Recover>`                                                                                                                                    |
| 4 | **Neighbor Graph**            | `:165`                                                                                        | `:166` sau `ReconcileNeighborGraph(...)` | Mốc đầu-cuối. Nếu điều kiện`!simpleWorkflow && config.Recovery.ReconcileAllMatchedTiles` false → ghi 0 ms, `Detail = "skipped (ReconcileAllMatchedTiles=false)"`                                                                                 |
| 5 | **Pose Graph Optimizer**      | `:215` `if (config.PoseGraph.Enabled)`                                                        | `:235` (hết khối `else`)               | Mốc đầu-cuối,**bao cả nhánh `else`** chạy `DirectPoseOutlierCorrector` (`:229-230`). `Detail` = `"edges=" + report.PoseGraph.EdgesUsed + "/" + report.PoseGraph.EdgesTotal` khi bật; `"DirectPoseOutlierCorrector (PoseGraph disabled)"` khi tắt |
| 6 | **Validate**                  | `:177` `ValidateGlobalPoses(...)` **và** `:237-246` `GlobalPoseValidator`                  | —                                       | **Cộng dồn** 2 đoạn. `Detail = "valid=" + report.GlobalPoseValidation.IsValid + "; outliers=" + report.GlobalPoseValidation.Errors.Count`                                                                                                             |
| 7 | **Stitching**                 | `:251` `_stitchingService.Stitch(...)`                                                        | `:252`                                   | Đo tổng quanh`Stitch(...)`, rồi **trừ đi** `report.StitchingExecution.SaveElapsedMilliseconds`. `Detail = report.StitchingExecution.EffectiveEngine.ToString()`                                                                                      |
| 8 | **Save Image**                | trong stitcher                                                                                | —                                       | `= report.StitchingExecution.SaveElapsedMilliseconds` (xem §3). `Detail = Path.GetFileName(report.FinalOutputPath)`                                                                                                                                      |

### 2.3 Xử lý nhánh không chạy

Khi `string.IsNullOrWhiteSpace(config.OutputPath)` (`:208`), stage 5-8 không chạy. **Vẫn phải ghi 4 dòng** với `ElapsedMilliseconds = 0` và `Detail = "not executed (no OutputPath)"` — để grid luôn đủ 8 dòng, người dùng không phải đoán stage nào bị thiếu.

Tương tự với `simpleWorkflow == true`: stage 4 bị bỏ qua → ghi 0 ms + lý do.

### 2.4 Xử lý ngoại lệ

Nếu `RunCore` ném exception giữa chừng (`OperationCanceledException` hoặc lỗi thật), `report.StageTimings` đã tích lũy tới đó vẫn nằm trong `report` — nhưng `report` **không** được trả về vì `RunCore` return ở `:272`. Chấp nhận: khi run lỗi, tab Execute Time giữ nguyên nội dung của run trước (hoặc rỗng). **Không** thêm try/finally để cứu — sẽ làm rối luồng và không thuộc phạm vi task.

---

## 3. Tách riêng stage "Save Image"

Thời gian ghi ảnh nằm sâu trong stitcher, không nhìn thấy từ `RunCore`. Cách lấy: cộng dồn vào `StitchingExecutionReport.SaveElapsedMilliseconds` (đã thêm ở §1.3), vì `options.ExecutionReport` được `WorkflowStitchingService` trả về qua `LastExecutionReport` (`WorkflowStitchingService.cs:32-35`) và gán vào `report.StitchingExecution` ở `AlignStitchWorkflowService.cs:252`.

### 3.1 `GerberStitching.Core/Stitching/GlobalTransformStitcher.cs`

**Đổi signature** (`:447`):

```csharp
// TRƯỚC
private static void SaveStandardTiff(Mat image, string path)

// SAU
private static void SaveStandardTiff(Mat image, string path, StitchingExecutionReport report)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (!Cv2.ImWrite(path, image))
        throw new IOException("OpenCV failed to write stitched TIFF: " + path);
    sw.Stop();
    if (report != null) report.SaveElapsedMilliseconds += sw.ElapsedMilliseconds;
}
```

**Cập nhật 4 call site:** `:138`, `:164`, `:192`, `:203` → thêm tham số `options.ExecutionReport`.

**Bọc `Publish(creatingPath, output)` ở `:208`** bằng cùng stopwatch, cộng dồn vào `options.ExecutionReport.SaveElapsedMilliseconds` — bước `.creating` → final là một phần của "Save Image" theo nghĩa người dùng hiểu.

> `options.ExecutionReport` được khởi tạo ở `:72`, luôn khác null tại các call site trên.

### 3.2 `GerberStitching.Core/Stitching/HalconProjectiveMosaicRebasedEngine.cs`

Hàm nhận sẵn `StitchingExecutionReport report` từ tham số (`:61`). Bọc `HOperatorSet.WriteImage(...)` ở `:214`:

```csharp
var sw = System.Diagnostics.Stopwatch.StartNew();
HOperatorSet.WriteImage(image, new HTuple(bigTiff ? "bigtiff none" : "tiff none"), new HTuple(0), new HTuple(path));
sw.Stop();
if (report != null) report.SaveElapsedMilliseconds += sw.ElapsedMilliseconds;
```

**Giữ nguyên license-check hiện có** — không tạo đường gọi HALCON mới, chỉ bọc stopwatch quanh lời gọi đã tồn tại (rule 3.3 `AGENTS.md`).

### 3.3 `GerberStitching.Core/Stitching/HalconWarpThenTileOffsetEngine.cs`

Engine này đã có `timer` set `report.ElapsedMilliseconds` (`:36`, `:98`). Tìm chỗ ghi ảnh tương ứng và bọc thêm `SaveElapsedMilliseconds` cùng cách.

---

## 4. UI — TabPage "Execute Time" trong `infotabControl`

### ⚠️ 4.0 Tên field

Field thật là **`infotabControl`** — chữ `t` **thường**, không phải `infoTabControl`. Khai báo tại `AlignStitchingControl.Designer.cs:476`.

Lưu ý có **hai** TabControl trong Tab 3: `infotabControl` (Panel1 của splitter, bên trái) và `resultTabControl` (Panel2, bên phải). Tab mới thuộc **`infotabControl`**, cùng chỗ với "List Map" và "Parameters".

### 4.1 Sửa `GerberViewer/Views/AlignStitchingControl.Designer.cs`

Chỉ thêm **khai báo control** — tuyệt đối không thêm `if`/`for`/logic (rule 3.1). Năm vị trí:

**(a) Khối `new`** — sau `:54`:

```csharp
this.tabPage_ExecuteTime = new System.Windows.Forms.TabPage();
this.executeTimeGrid = new System.Windows.Forms.DataGridView();
this.colStage = new System.Windows.Forms.DataGridViewTextBoxColumn();
this.colElapsedMs = new System.Windows.Forms.DataGridViewTextBoxColumn();
this.colStageDetail = new System.Windows.Forms.DataGridViewTextBoxColumn();
```

**(b) Khối `SuspendLayout`** — sau `:69`:

```csharp
this.tabPage_ExecuteTime.SuspendLayout();
((System.ComponentModel.ISupportInitialize)(this.executeTimeGrid)).BeginInit();
```

**(c) Gắn vào TabControl** — sau `:401` (`infotabControl.Controls.Add(this.tabPage_Params)`):

```csharp
this.infotabControl.Controls.Add(this.tabPage_ExecuteTime);
```

**(d) Khối định nghĩa page** — sau `:445`, copy đúng khuôn `tabPage_ListMap` (`:409-427`):

```csharp
// 
// tabPage_ExecuteTime
// 
this.tabPage_ExecuteTime.Controls.Add(this.executeTimeGrid);
this.tabPage_ExecuteTime.Location = new System.Drawing.Point(8, 39);
this.tabPage_ExecuteTime.Name = "tabPage_ExecuteTime";
this.tabPage_ExecuteTime.Padding = new System.Windows.Forms.Padding(3);
this.tabPage_ExecuteTime.Size = new System.Drawing.Size(284, 454);
this.tabPage_ExecuteTime.TabIndex = 2;
this.tabPage_ExecuteTime.Text = "Execute Time";
this.tabPage_ExecuteTime.UseVisualStyleBackColor = true;
// 
// executeTimeGrid
// 
this.executeTimeGrid.AllowUserToAddRows = false;
this.executeTimeGrid.AllowUserToDeleteRows = false;
this.executeTimeGrid.AllowUserToResizeRows = false;
this.executeTimeGrid.AutoSizeColumnsMode = System.Windows.Forms.DataGridViewAutoSizeColumnsMode.Fill;
this.executeTimeGrid.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
this.executeTimeGrid.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
    this.colStage, this.colElapsedMs, this.colStageDetail});
this.executeTimeGrid.Dock = System.Windows.Forms.DockStyle.Fill;
this.executeTimeGrid.Location = new System.Drawing.Point(3, 3);
this.executeTimeGrid.Name = "executeTimeGrid";
this.executeTimeGrid.ReadOnly = true;
this.executeTimeGrid.RowHeadersVisible = false;
this.executeTimeGrid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
this.executeTimeGrid.Size = new System.Drawing.Size(278, 448);
this.executeTimeGrid.TabIndex = 0;
// 
// colStage
// 
this.colStage.FillWeight = 130F;
this.colStage.HeaderText = "Stage";
this.colStage.Name = "colStage";
this.colStage.ReadOnly = true;
// 
// colElapsedMs
// 
this.colElapsedMs.FillWeight = 60F;
this.colElapsedMs.HeaderText = "Elapsed (ms)";
this.colElapsedMs.Name = "colElapsedMs";
this.colElapsedMs.ReadOnly = true;
// 
// colStageDetail
// 
this.colStageDetail.FillWeight = 150F;
this.colStageDetail.HeaderText = "Detail";
this.colStageDetail.Name = "colStageDetail";
this.colStageDetail.ReadOnly = true;
```

**(e) Khối `ResumeLayout` + khai báo field** — thêm vào `:467-469`:

```csharp
this.tabPage_ExecuteTime.ResumeLayout(false);
((System.ComponentModel.ISupportInitialize)(this.executeTimeGrid)).EndInit();
```

và vào khối field cuối file (`:474-481`):

```csharp
private System.Windows.Forms.TabPage tabPage_ExecuteTime;
private System.Windows.Forms.DataGridView executeTimeGrid;
private System.Windows.Forms.DataGridViewTextBoxColumn colStage;
private System.Windows.Forms.DataGridViewTextBoxColumn colElapsedMs;
private System.Windows.Forms.DataGridViewTextBoxColumn colStageDetail;
```

> Mẫu grid read-only đã có sẵn trong repo để đối chiếu: `GerberViewer/Views/TotalConfigParametersControl.Designer.cs:29-70`.

### 4.2 Sửa `GerberViewer/Views/AlignStitchingControl.cs`

Thêm method bind, theo đúng khuôn `TotalConfigParametersControl.BindSnapshot` (`TotalConfigParametersControl.cs:9-14`):

```csharp
// [Claude] [Change time: 2026-08-06] [Purpose: Hiển thị thời gian từng giai đoạn lên tab Execute Time.]
private void BindStageTimings(ProcessingReport report, TimeSpan totalElapsed)
{
    executeTimeGrid.Rows.Clear();
    if (report != null && report.StageTimings != null)
        foreach (var t in report.StageTimings)
            executeTimeGrid.Rows.Add(t.Stage, t.ElapsedMilliseconds, t.Detail);
    var rowIndex = executeTimeGrid.Rows.Add("TOTAL (measured run)", (long)totalElapsed.TotalMilliseconds, string.Empty);
    executeTimeGrid.Rows[rowIndex].DefaultCellStyle.Font =
        new Font(executeTimeGrid.Font, FontStyle.Bold);
}
```

**Gọi ở đâu:** trong `RunWorkflowAsync`, ngay sau `RebuildFinalStates(result);` (`:295`) — nghĩa là sau `await`, nên **đã ở UI thread**, không cần `Invoke`:

```csharp
_lastWorkflowResult = result;
RebuildFinalStates(result);
BindStageTimings(result.Report, stopwatch.Elapsed);   // <-- thêm dòng này
LogTileSummaries(result);
```

Dòng `TOTAL` dùng `stopwatch` sẵn có ở `:252` — để người dùng đối chiếu tổng 8 stage với tổng thật; chênh lệch chính là phần overhead ngoài `RunCore` (ghi report, comparison, publish).

**Clear grid khi bắt đầu run mới:** thêm `executeTimeGrid.Rows.Clear();` cạnh `prgAlignStitch.Value = 0;` (`:258`) để không hiểu nhầm số liệu run cũ là của run đang chạy.

### 4.3 Không cần sửa csproj

Task này chỉ sửa file đã có trong `<Compile Include>`. (Chỉ task4 mới thêm file mới.)

---

## 5. Ghi timing vào `processing_report.json`

**File:** `GerberViewer/Views/AlignStitchingControl.cs`, hàm `WriteProcessingReport` (`:576`).

Thêm khối `stageTimings` sau khối `stitchingExecution`, dùng đúng helper hand-rolled sẵn có (`EscapeJson` `:854`, `AppendJsonArray`):

```csharp
sb.AppendLine("  \"stageTimings\": [");
for (int i = 0; i < report.StageTimings.Count; i++)
{
    var t = report.StageTimings[i];
    sb.Append("    {\"stage\": \"").Append(EscapeJson(t.Stage))
      .Append("\", \"elapsedMs\": ").Append(t.ElapsedMilliseconds)
      .Append(", \"detail\": \"").Append(EscapeJson(t.Detail)).Append("\"}");
    sb.AppendLine(i < report.StageTimings.Count - 1 ? "," : string.Empty);
}
sb.AppendLine("  ],");
```

Mục đích: so sánh hiệu năng giữa các run mà không phải mở lại app.

---

## 6. Danh sách file thay đổi


| File                                                                    | Thay đổi                                                                                                    |
| ----------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------- |
| `GerberStitching.Core/Models/WorkflowModels.cs`                         | +`StageTimingReport`; + `ProcessingReport.StageTimings`; + `StitchingExecutionReport.SaveElapsedMilliseconds` |
| `GerberStitching.Core/Alignment/AlignStitchWorkflowService.cs`          | 8 móc đo trong`RunCore` + helper `Mark`                                                                     |
| `GerberStitching.Core/Stitching/GlobalTransformStitcher.cs`             | `SaveStandardTiff` +tham số report; 4 call site; bọc `Publish`                                              |
| `GerberStitching.Core/Stitching/HalconProjectiveMosaicRebasedEngine.cs` | Bọc stopwatch quanh`WriteImage` (`:214`)                                                                     |
| `GerberStitching.Core/Stitching/HalconWarpThenTileOffsetEngine.cs`      | Bọc stopwatch quanh chỗ ghi ảnh                                                                            |
| `GerberViewer/Views/AlignStitchingControl.Designer.cs`                  | +`tabPage_ExecuteTime`, `executeTimeGrid`, 3 column                                                           |
| `GerberViewer/Views/AlignStitchingControl.cs`                           | +`BindStageTimings`; gọi ở `:295`; clear grid ở `:258`; + `stageTimings` trong `WriteProcessingReport`     |

---

## 7. Tiêu chí nghiệm thu

1. Build sạch:
   ```bash
   msbuild GerberViewer.sln /p:Configuration=Debug /p:Platform=x64
   ```

   Build cả **Release|x64** để chắc code đo không nằm nhầm trong `#if DEBUG`.
2. Mở `AlignStitchingControl` ở Designer view — tab "Execute Time" hiện đúng vị trí thứ 3 trong `infotabControl`, không có lỗi.
3. Chạy một run đầy đủ (không simple) với dataset thật → tab Execute Time có **đủ 8 dòng + 1 dòng TOTAL**, đúng thứ tự pipeline.
4. Kiểm tra tính nhất quán: `Σ(8 stage) ≤ TOTAL`, và chênh lệch hợp lý (phần ghi report + comparison + publish nằm ngoài `RunCore`).
5. `Stitching` và `Save Image` đều `> 0` (trước đây `SaveElapsedMilliseconds` chưa tồn tại). `Stitching` **không** được âm — nếu âm nghĩa là trừ sai, xem lại §2.2 stage 7.
6. Chạy một run với `PoseGraph.Enabled = false` → dòng "Pose Graph Optimizer" vẫn hiện, `Detail` ghi `"DirectPoseOutlierCorrector (PoseGraph disabled)"`.
7. Chạy `btnTestSimpleWorkflow` → dòng "Neighbor Graph" hiện 0 ms kèm lý do, không bị thiếu dòng.
8. Mở `processing_report.json` trong thư mục run vừa publish → có mảng `stageTimings` hợp lệ JSON (parse thử bằng bất kỳ JSON viewer nào).
9. Chạy run thứ hai → grid được clear, không lẫn số liệu run trước.

---

## 8. Rủi ro


| Rủi ro                                                             | Giảm thiểu                                                                                                        |
| ------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| `Stitching` ra số âm do `SaveElapsedMilliseconds` lớn hơn tổng | Clamp`Math.Max(0, total - save)` và ghi cảnh báo vào `Detail`                                                   |
| Sửa signature`SaveStandardTiff` sót call site                     | Chỉ 4 call site, compiler sẽ báo hết — build là đủ để bắt                                                |
| Đo trong vòng lặp làm chậm run                                 | `Stopwatch.Start/Stop` ~20ns/lần, với vài trăm tile là không đáng kể                                       |
| `report.StitchingExecution` null khi không stitch                  | Stage 7-8 đã có nhánh "not executed" ở §2.3; vẫn phải null-check trước khi đọc`SaveElapsedMilliseconds` |
| Thứ tự dòng grid lộn xộn                                       | `StageTimings` là `IList`, append theo thứ tự pipeline, bind **không sort**                                     |
