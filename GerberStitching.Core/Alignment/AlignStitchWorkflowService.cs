using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GerberViewer.Stitching.Imaging.ImageInterop;
using GerberViewer.Stitching.Imaging;
using GerberViewer.Stitching.Matching;
using GerberViewer.Stitching.Matching.OpenCv;
using GerberViewer.Stitching.Alignment.Evaluation;
using GerberViewer.Stitching.Mapping;
using GerberViewer.Stitching.Models;
using GerberViewer.Stitching.Stitching;
using GerberViewer.Stitching.Transforms;
using GerberViewer.Stitching.Configuration;
// [Tony] [Change time: 2026-08-11] [Purpose: Unconditional now - DebugPreviewWriter (Diagnostics namespace) is
// not #if DEBUG-gated and is called unconditionally below (guarded by config.EmitDebugPreview at runtime
// instead). DebugHtmlReportWriter.Write at line ~370 is still only reachable inside its own #if DEBUG block.]
using GerberViewer.Stitching.Diagnostics;
using OpenCvSharp;

namespace GerberViewer.Stitching.Alignment
{
    public interface IManualAlignmentProvider
    {
        ManualAlignmentResult RequestManualAlignment(ManualAlignmentRequest request,
                                                     CancellationToken cancellationToken);
    }

    public sealed class AlignStitchWorkflowService
    {
        public event EventHandler<MatchResultEventArgs> MatcherResultAvailable;

        private void ForwardMatcherResult(object sender, MatchResultEventArgs e)
        {
            var handler = MatcherResultAvailable;
            if (handler != null)
                handler(this, e);
        }
        // ==================== ALIGNMENT ====================
        // Direct and recovery matchers produce poses; stitching starts after this section.
        private readonly Func<ISampleAligner> _alignerFactory;
        private readonly IManualAlignmentProvider _manualProvider;
        private readonly IMatcherFactory _matcherFactory;
        private readonly OpenCvMatchInputAdapter _openCvInput = new OpenCvMatchInputAdapter();
        private readonly IWorkflowImageMappingService _mappingService;
        private readonly WorkflowStitchingService _stitchingService;
        private WorkflowImageCache _imageCache;

        public AlignStitchWorkflowService(Func<ISampleAligner> alignerFactory,
                                          IManualAlignmentProvider manualProvider = null,
                                          IMatcherFactory matcherFactory = null)
        {
            _alignerFactory = alignerFactory;
            _manualProvider = manualProvider;
            _matcherFactory = matcherFactory ?? new MatcherFactory();
            _mappingService = new WorkflowImageMappingService();
            _stitchingService = new WorkflowStitchingService();
        }

        public Task<AlignStitchWorkflowResult> RunAsync(AlignStitchConfig config, SampleManifest manifest,
                                                        IList<CapturedImageInfo> captured,
                                                        IProgress<WorkflowProgress> progress,
                                                        CancellationToken cancellationToken)
        {
            return Task.Run(
                () => RunCore(config ?? new AlignStitchConfig(), manifest, captured, progress, cancellationToken),
                cancellationToken);
        }

        internal Task<AlignStitchWorkflowResult> RunSimpleAsync(AlignStitchConfig config, SampleManifest manifest,
                                                                IList<CapturedImageInfo> captured,
                                                                IProgress<WorkflowProgress> progress,
                                                                CancellationToken cancellationToken)
        {
            return Task.Run(
                () => RunCore(config ?? new AlignStitchConfig(), manifest, captured, progress, cancellationToken, true),
                cancellationToken);
        }

        // Map the finalized tile state to OrderNodeState so the canvas displays the correct color during execution.
        private static OrderNodeState MapToNodeState(TileWorkflowState state)
        {
            if (state == null)
                return OrderNodeState.Failed;
            return OrderNodeStateMapper.FromPoseSource(state.Source);
        }

        // [Tony] [Change time: 2026-08-06] [Purpose: Record the time for a stage in ProcessingReport.StageTimings for
        // the Execute Time tab.]
        private static void Mark(ProcessingReport report, string stage, long elapsedMilliseconds, string detail)
        {
            report.StageTimings.Add(
                new StageTimingReport
                {
                    Stage = stage,
                    ElapsedMilliseconds = elapsedMilliseconds,
                    Detail = detail
                });
        }

        private AlignStitchWorkflowResult RunCore(AlignStitchConfig config, SampleManifest manifest,
                                                  IList<CapturedImageInfo> captured,
                                                  IProgress<WorkflowProgress> progress, CancellationToken ct,
                                                  bool simpleWorkflow = false)
        {
            AlignStitchConfigMapper.EnsureComposite(config);
            AlignStitchConfigMapper.SyncLegacy(config);
            // [Tony] [Change time: 2026-07-26] [Purpose: Make the stage call graph explicit: Validate/Map -> Direct ->
            // Neighbor -> Graph -> Stitch -> Report -> Publish.] [Tony] [Change time: 2026-08-06] [Purpose: Measure
            // the time for each stage of the workflow and display it on the Execute Time tab.]
            var swMapping = System.Diagnostics.Stopwatch.StartNew();
            var swDirect = new System.Diagnostics.Stopwatch();
            var swRecover = new System.Diagnostics.Stopwatch();
            var swNeighbor = new System.Diagnostics.Stopwatch();
            var swValidate = new System.Diagnostics.Stopwatch();
            var recoveredCount = 0;
            ct.ThrowIfCancellationRequested();
            var imageMap = _mappingService.ValidateAndMap(manifest, captured, ct);
            var report = ProcessingReport.Create(config, manifest);
            report.EffectiveConfig = AlignStitchConfigMapper.CreateSnapshot(config);
            report.Messages.Add("Neighbor graph modes: FailureRecovery=" +
                                (config.Recovery.RecoverFailedTiles ? "enabled" : "disabled") +
                                "; FullPoseReconciliation=" +
                                (config.Recovery.ReconcileAllMatchedTiles ? "enabled" : "disabled") + ".");
            report.Messages.Add("Stitching engine: " + config.StitchingEngine +
                                (" (OpenCV path remains available; HALCON ProjectiveMosaic uses " +
                                 "HOperatorSet.GenProjectiveMosaic when selected)."));
            report.Messages.Add(
                "Transform contract: HalconNccMatcher returns MovingImage-to-ReferenceImage transforms. Direct " +
                "alignment warps each captured MovingImage into its reference tile coordinate system, then composes " +
                "the tile ExpectedX/ExpectedY offset for global stitching.");
            var solved = new Dictionary<int, TileWorkflowState>();
            var tileByOrder = imageMap.TileByOrder;
            var capturedByOrder = imageMap.CapturedByOrder;
            var ordered = imageMap.OrderedCaptured;
            swMapping.Stop();
            Mark(report, "Mapping and Preprocessing", swMapping.ElapsedMilliseconds, "tiles=" + ordered.Count);

            using (_imageCache = new WorkflowImageCache()) using (
                var aligner = _alignerFactory == null ? null : _alignerFactory())
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var cap = ordered[i];
                    // [Tony] [Change time: 2026-08-06] [Purpose: Set the current tile to "Processing" so the canvas
                    // fills blue as soon as alignment begins.]
                    progress?.Report(new WorkflowProgress(i, ordered.Count, cap, "Direct camera-to-sample alignment",
                                                          OrderNodeState.Processing));
                    var tile = tileByOrder[cap.OrderIndex];
                    // [Tony] [Change time: 2026-08-10] [Purpose: swDirect/swRecover accumulate across every tile, so
                    // read them before and after this tile to get the duration of THIS tile alone for the host UI.
                    // The accumulated totals reported by Mark(...) below are unaffected.]
                    // [Claude] [Change time: 2026-08-11] [Purpose: Skip the per-tile before/after reads when
                    // CalculateTimeDetail=false; only stage-level totals are needed in that mode.]
                    var directBeforeMs = config.CalculateTimeDetail ? swDirect.ElapsedMilliseconds : 0L;
                    var recoverBeforeMs = config.CalculateTimeDetail ? swRecover.ElapsedMilliseconds : 0L;
                    swDirect.Start();
                    var state = SolveDirect(config, tile, cap, aligner, report, ct, !simpleWorkflow);
                    swDirect.Stop();
                    var tileDirectMs = config.CalculateTimeDetail ? swDirect.ElapsedMilliseconds - directBeforeMs : 0L;
#if DEBUG
                    var directAlignmentTransform =
                        state.Alignment == null || state.Alignment.CapturedToSampleTransform == null
                            ? null
                            : (double[,])state.Alignment.CapturedToSampleTransform.Clone();
                    state.DirectAlignmentTransform = directAlignmentTransform;
#endif
                    ReportMatcherResults(progress, cap, state.Alignment, "Direct Alignment");
                    if (simpleWorkflow && !state.IsStitchable)
                    {
                        var reason = "Direct alignment failed; retained the initial expected tile coordinate for " +
                                     "simple stitching.";
                        state = TileWorkflowState.From(cap, Translation(tile.ExpectedX, tile.ExpectedY),
                                                       PoseSource.ExpectedGridOffset, state.Alignment, reason);
                        state.IsFallbackPose = true;
                        state.IsStitchable = true;
                        report.Warnings.Add(TilePrefix(cap) + reason);
                    }
                    else if (!state.IsStitchable && config.Recovery.RecoverFailedTiles)
                    {
                        swRecover.Start();
                        state = Recover(config, tile, cap, solved, ordered, capturedByOrder, tileByOrder, report, ct,
                                        false);
                        swRecover.Stop();
                        recoveredCount++;
                        ReportMatcherResults(progress, cap, state.Alignment, "Neighbor Recovery");
                        // [Tony] [Change time: 2026-08-10] [Purpose: Surface this tile's recovery duration separately
                        // from its direct-alignment duration so the host UI can show the two phases on their own
                        // labels.]
                        progress?.Report(new WorkflowProgress(i, ordered.Count, cap, "Neighbor Recovery",
                                                              MapToNodeState(state),
                                                              config.CalculateTimeDetail
                                                                  ? swRecover.ElapsedMilliseconds - recoverBeforeMs
                                                                  : 0L));
                    }
                    else if (!state.IsStitchable)
                        state = ResolveFailedWithoutNeighbor(config, tile, cap, report, ct);
#if DEBUG
                    state.DirectAlignmentTransform = directAlignmentTransform;
#endif
                    solved[cap.OrderIndex] = state;
                    // [Tony] [Change time: 2026-08-06] [Purpose: Report the last state of the tile (after
                    // Direct/Recovery/Fallback) to ensure the canvas retains its correct colors, excluding the state
                    // from SolveDirect before recovery runs.] [Tony] [Change time: 2026-08-10] [Purpose: Report this
                    // tile's own direct-alignment duration; the recovery duration (0 when this tile never entered
                    // recovery) rides on the separate report above.]
                    progress?.Report(new WorkflowProgress(i, ordered.Count, cap, "Direct camera-to-sample alignment",
                                                          MapToNodeState(state), tileDirectMs));
                }
                foreach (var cap in simpleWorkflow ? Enumerable.Empty<CapturedImageInfo>() : ordered)
                {
                    ct.ThrowIfCancellationRequested();
                    TileWorkflowState state;
                    if (config.Recovery.RecoverFailedTiles && solved.TryGetValue(cap.OrderIndex, out state) &&
                        !state.IsStitchable)
                    {
                        var recoverBeforeMs2 = config.CalculateTimeDetail ? swRecover.ElapsedMilliseconds : 0L;
                        swRecover.Start();
                        var recovered = Recover(config, tileByOrder[cap.OrderIndex], cap, solved, ordered,
                                                capturedByOrder, tileByOrder, report, ct, true);
                        swRecover.Stop();
                        recoveredCount++;
#if DEBUG
                        recovered.DirectAlignmentTransform = state.DirectAlignmentTransform;
#endif
                        solved[cap.OrderIndex] = recovered;
                        ReportMatcherResults(progress, cap, solved[cap.OrderIndex].Alignment,
                                             "Neighbor Recovery (second pass)");
                        // [Tony] [Change time: 2026-08-06] [Purpose: Update canvas color after the second Recovery
                        // pass.] [Tony] [Change time: 2026-08-10] [Purpose: Carry this tile's second-pass recovery
                        // duration too.]
                        progress?.Report(new WorkflowProgress(
                            cap.OrderIndex, ordered.Count, cap, "Neighbor Recovery (second pass)",
                            MapToNodeState(solved[cap.OrderIndex]),
                            config.CalculateTimeDetail
                                ? swRecover.ElapsedMilliseconds - recoverBeforeMs2
                                : 0L));
                    }
                }
                // [Tony] [Change time: 2026-08-06] [Purpose: Ghi nhận thời gian Direct Alignment / Failure Recovery
                // Record the timing after both passes have completed.
                Mark(report, "Direct Alignment", swDirect.ElapsedMilliseconds, "tiles=" + ordered.Count);
                Mark(report, "Failure Recovery", swRecover.ElapsedMilliseconds,
                     config.Recovery.RecoverFailedTiles ? ("recovered=" + recoveredCount)
                                                        : "skipped (RecoverFailedTiles=false)");
                // [Tony] [Change time: 2026-08-04] [Purpose: The pose-graph optimizer needs the full neighbor-edge
                // graph; auto-enable measurement if the user left it off.]
                if (!simpleWorkflow && config.PoseGraph.Enabled && !config.Recovery.ReconcileAllMatchedTiles)
                {
                    config.Recovery.ReconcileAllMatchedTiles = true;
                    report.Warnings.Add("PoseGraph.Enabled requires Recovery.ReconcileAllMatchedTiles; it was off " +
                                        "and has been enabled for this run.");
                }
                swNeighbor.Start();
                if (!simpleWorkflow && config.Recovery.ReconcileAllMatchedTiles)
                    ReconcileNeighborGraph(config, solved, ordered, capturedByOrder, tileByOrder, report, progress, ct);
                swNeighbor.Stop();
                Mark(report, "Neighbor Graph", swNeighbor.ElapsedMilliseconds,
                     (!simpleWorkflow && config.Recovery.ReconcileAllMatchedTiles)
                         ? ("tiles=" + ordered.Count)
                         : "skipped (ReconcileAllMatchedTiles=false)");
            }
            _imageCache = null;

            ct.ThrowIfCancellationRequested();
            // Reconcile every output pose before applying the common output-coordinate translation.
            foreach (var state in solved.Values.Where(v => !v.IsStitchable && v.Source != PoseSource.Excluded))
            {
                var tile = tileByOrder[state.OrderIndex];
                state.GlobalPose = Translation(tile.ExpectedX, tile.ExpectedY);
            }
            swValidate.Start();
            ValidateGlobalPoses(config, solved, tileByOrder, report);
            swValidate.Stop();
            var offsetMessage =
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                              "Configured direct-alignment manual offset: X={0:R}, Y={1:R} pixels.",
                              config.DirectAlignment.ManualOffsetX, config.DirectAlignment.ManualOffsetY);
            report.Messages.Add(offsetMessage);

            foreach (var state in solved.Values.OrderBy(v => v.OrderIndex))
            {
                report.Poses.Add(state.ToPose());
                ApplyStateReport(report, state, tileByOrder[state.OrderIndex]);
            }

            var alignedCount = solved.Values.Count(v => v.AlignmentSucceeded);
            var stitchableCount = solved.Values.Count(v => v.IsStitchable);
            var fallbackCount = solved.Values.Count(v => v.IsFallbackPose);
            var excludedCount = solved.Values.Count(v => v.Source == PoseSource.Excluded);
            AddNeighborModeSummary(report, RecoveryEdgePurpose.FailureRecovery, config.Recovery.RecoverFailedTiles,
                                   solved.Values.Count(v => v.Source == PoseSource.NeighborAlignment));
            AddNeighborModeSummary(report, RecoveryEdgePurpose.FullPoseReconciliation,
                                   config.Recovery.ReconcileAllMatchedTiles,
                                   solved.Values.Count(v => v.Source == PoseSource.AnchorAdjusted));
            report.Succeeded = stitchableCount == ordered.Count && ordered.Count > 0;
            if (report.Succeeded)
                report.RunStatus =
                    fallbackCount > 0 ? AlignStitchRunStatus.CompletedWithFallback : AlignStitchRunStatus.Completed;
            else if (excludedCount > 0 && alignedCount + excludedCount == ordered.Count)
                report.RunStatus = AlignStitchRunStatus.CompletedWithExcludedTiles;
            else
                report.RunStatus = AlignStitchRunStatus.Failed;
            if (!report.Succeeded)
                report.Warnings.Add("One or more tiles could not be aligned; black placeholders were written at " +
                                    "their expected grid positions.");
            if (!string.IsNullOrWhiteSpace(config.OutputPath))
            {
                // ==================== STITCHING ====================
                var outputFileName = simpleWorkflow ? "Aligned_Stitched_Simple.tiff" : "Stitched.tiff";
                progress?.Report(
                    new WorkflowProgress(ordered.Count, ordered.Count, null, "Stitching: composing " + outputFileName));
                var outputStates = solved.Values.OrderBy(v => v.OrderIndex).ToList();
                // [Tony] [Change time: 2026-08-06] [Purpose: Measure Pose Graph Optimizer /
                // DirectPoseOutlierCorrector time for the Execute Time tab.]
                var swPoseGraph = System.Diagnostics.Stopwatch.StartNew();
                // [Tony] [Change time: 2026-08-04] [Purpose: Solve all tile poses jointly from neighbor evidence
                // instead of single-path propagation.]
                if (config.PoseGraph.Enabled)
                {
                    var optimizer = new GerberViewer.Stitching.Alignment.Graph.GlobalPoseGraphOptimizer();
                    report.PoseGraph = optimizer.Optimize(config, tileByOrder, outputStates, report.RecoveryEdges);
                    foreach (var w in report.PoseGraph.Warnings)
                        report.Warnings.Add(w);
                    report.Messages.Add("PoseGraph: enabled; DirectPoseOutlierCorrector skipped (pose graph " +
                                        "supersedes median-angle correction).");
                    progress?.Report(new WorkflowProgress(
                        ordered.Count, ordered.Count, null,
                        "Pose graph: edges=" + report.PoseGraph.EdgesUsed + "/" + report.PoseGraph.EdgesTotal +
                            "; seam med " + report.PoseGraph.BeforeResidualMedian.ToString("0.00") + " -> " +
                            report.PoseGraph.AfterResidualMedian.ToString("0.00") + " px."));
                }
                else
                {
                    // [Tony] [Change time: 2026-08-03] [Purpose: Correct anisotropic direct-pose outliers before the
                    // global safety validator and stitching boundary.]
                    var corrector = new DirectPoseOutlierCorrector();
                    report.DirectPoseCorrection = corrector.Correct(config, tileByOrder, outputStates);
                    foreach (var warning in report.DirectPoseCorrection.Warnings)
                        report.Warnings.Add(warning);
                    progress?.Report(new WorkflowProgress(
                        ordered.Count, ordered.Count, null,
                        "Direct pose correction: corrected=" + report.DirectPoseCorrection.Corrected.Count +
                            "; uncorrectable=" + report.DirectPoseCorrection.OutlierUncorrectable.Count + "."));
                }
                swPoseGraph.Stop();
                Mark(report, "Pose Graph Optimizer", swPoseGraph.ElapsedMilliseconds,
                     config.PoseGraph.Enabled
                         ? ("edges=" + report.PoseGraph.EdgesUsed + "/" + report.PoseGraph.EdgesTotal)
                         : "DirectPoseOutlierCorrector (PoseGraph disabled)");
                // Last safety gate: never hand an unchecked captured/source-to-global pose to either engine.
                swValidate.Start();
                var poseValidator = new GlobalPoseValidator();
                report.GlobalPoseValidation =
                    poseValidator.Validate(config, manifest, ordered, outputStates, report.RecoveryEdges);
                foreach (var diagnostic in report.GlobalPoseValidation.Tiles)
                {
                    var tileReport = report.TileReports.LastOrDefault(x => x.OrderIndex == diagnostic.OrderIndex);
                    if (tileReport != null)
                        tileReport.GlobalPoseDiagnostic = diagnostic;
                }
                progress?.Report(
                    new WorkflowProgress(ordered.Count, ordered.Count, null,
                                         "GlobalPose summary: valid=" + report.GlobalPoseValidation.IsValid +
                                             "; tiles=" + report.GlobalPoseValidation.Tiles.Count +
                                             "; outliers=" + report.GlobalPoseValidation.Errors.Count));
                swValidate.Stop();
                Mark(report, "Validate", swValidate.ElapsedMilliseconds,
                     "valid=" + report.GlobalPoseValidation.IsValid +
                         "; outliers=" + report.GlobalPoseValidation.Errors.Count);
                poseValidator.ThrowIfInvalid(report.GlobalPoseValidation);
#if DEBUG
                // Capture the finalized poses at the exact boundary before the stitching engine consumes them.
                DebugHtmlReportWriter.Write(config.OutputPath, manifest, outputStates, report.RecoveryEdges,
                                            report.DirectPoseCorrection, report.PoseGraph);
#endif
                // [Tony] [Change time: 2026-08-06] [Purpose: Measure the Stitching time (excluding the Save Image
                // portion, which is separated from StitchingExecutionReport.SaveElapsedMilliseconds) for the Execute
                // Time tab.]
                var swStitch = System.Diagnostics.Stopwatch.StartNew();
                report.FinalOutputPath = _stitchingService.Stitch(config, ordered, outputStates, outputFileName, ct);
                swStitch.Stop();
                report.StitchingExecution = _stitchingService.LastExecutionReport;
                var saveElapsedMs =
                    report.StitchingExecution != null ? report.StitchingExecution.SaveElapsedMilliseconds : 0;
                Mark(report, "Stitching", Math.Max(0, swStitch.ElapsedMilliseconds - saveElapsedMs),
                     report.StitchingExecution != null ? report.StitchingExecution.EffectiveEngine.ToString()
                                                       : string.Empty);
                Mark(report, "Save Image", saveElapsedMs,
                     string.IsNullOrWhiteSpace(report.FinalOutputPath) ? string.Empty
                                                                       : Path.GetFileName(report.FinalOutputPath));
                // [Tony] [Change time: 2026-08-04] [Purpose: Surface the HALCON blending-ignored diagnostic as a run
                // warning, not just a buried matrix diagnostic line.]
                if (report.StitchingExecution != null)
                    foreach (var diagnostic in report.StitchingExecution.MatrixDiagnostics.Where(
                                 d => d.Contains("has no blending parameter")))
                        report.Warnings.Add(diagnostic);
                // [Tony] [Change time: 2026-08-11] [Purpose: #if DEBUG -> cờ runtime. Giữ nguyên try/catch
                // best-effort: một artifact chẩn đoán hỏng KHÔNG được biến run stitch thành công thành thất bại.]
                if (config.EmitDebugPreview)
                {
                    try
                    {
                        var debugPreview = DebugPreviewWriter.WriteStitchedPreview(
                            config.OutputPath, report.FinalOutputPath, manifest, ordered, outputStates,
                            config.DirectAlignment.ManualOffsetX, config.DirectAlignment.ManualOffsetY);

                        report.DebugPreviewPath = debugPreview;

                        if (string.IsNullOrWhiteSpace(debugPreview))
                            report.Warnings.Add("Stitched preview was not created because the stitched image " +
                                                "or processed sample mask was unavailable.");
                    }
                    catch (Exception debugPreviewError)
                    {
                        report.Warnings.Add("Stitched preview was skipped: " + debugPreviewError.Message);
                    }
                }
            }
            else
            {
                // When OutputPath is absent, skip Pose Graph, Stitching, and Save Image. Validation remains useful
                // because ValidateGlobalPoses runs unconditionally above. Preserve the pipeline order: PoseGraph ->
                // Validate -> Stitching -> SaveImage.
                Mark(report, "Pose Graph Optimizer", 0, "not executed (no OutputPath)");
                Mark(report, "Validate", swValidate.ElapsedMilliseconds,
                     "partial (no stitching; only ValidateGlobalPoses ran)");
                Mark(report, "Stitching", 0, "not executed (no OutputPath)");
                Mark(report, "Save Image", 0, "not executed (no OutputPath)");
            }
            return new AlignStitchWorkflowResult { Report = report,
                                                   States = solved.Values.OrderBy(v => v.OrderIndex).ToList() };
        }

        private static void AddNeighborModeSummary(ProcessingReport report, RecoveryEdgePurpose purpose, bool enabled,
                                                   int adjusted)
        {
            var edges = report.RecoveryEdges.Where(x => x.Purpose == purpose).ToList();
            var reasons = edges.GroupBy(x => string.IsNullOrWhiteSpace(x.Reason) ? "unspecified" : x.Reason)
                              .Select(x => x.Key + " (" + x.Count() + ")");
            report.Messages.Add(purpose + ": mode=" + (enabled ? "enabled" : "disabled") +
                                "; attempted=" + edges.Count + "; accepted=" + edges.Count(x => x.Accepted) +
                                "; rejected=" + edges.Count(x => !x.Accepted) + "; adjusted=" + adjusted +
                                "; reasons=" + (edges.Count == 0 ? "none" : string.Join(" | ", reasons)) + ".");
        }

        private static void ReportMatcherResults(IProgress<WorkflowProgress> progress, CapturedImageInfo image,
                                                 SampleAlignmentResult result, string section)
        {
            if (result == null)
                return;
            PopulatePoseComponents(result);
            if (progress == null)
                return;
            if (!string.IsNullOrWhiteSpace(result.CoarseMatcherName))
                progress.Report(
                    new WorkflowProgress(image.OrderIndex + 1, 0, image,
                                         string.Format("Alignment/{0}/Coarse: {1}", section,
                                                       FormatMatchResult(result.CoarseMatcherName, result.Success,
                                                                         result.CoarseRawScore, result))));
            if (!string.IsNullOrWhiteSpace(result.RefinementMatcherName))
                progress.Report(
                    new WorkflowProgress(image.OrderIndex + 1, 0, image,
                                         string.Format("Alignment/{0}/Refinement: {1}", section,
                                                       FormatMatchResult(result.RefinementMatcherName, result.Success,
                                                                         result.RefinementRawScore, result))));
            progress.Report(new WorkflowProgress(
                image.OrderIndex + 1, 0, image,
                string.Format("Alignment/{0}/Result: success={1}, selected={2}, Dx={3:0.###}, Dy={4:0.###}, " +
                              "Ang={5:0.###}, rejection={6}",
                              section, result.Success, result.PipelineStage ?? "-", result.TranslationX,
                              result.TranslationY, result.RotationDeg, result.RejectionReason ?? "-")));
        }

        private static string FormatMatchResult(string matcherName, bool success, double rawScore,
                                                SampleAlignmentResult alignment)
        {
            return new MatchResult { MatcherName = matcherName,
                                     Success = success,
                                     RawScore = rawScore,
                                     TranslationX = alignment.TranslationX,
                                     TranslationY = alignment.TranslationY,
                                     RotationDeg = alignment.RotationDeg,
                                     FailureReason =
                                         success ? MatchFailureReason.None : MatchFailureReason.RuntimeFailure,
                                     FailureMessage = success ? null : alignment.RejectionReason }
                .ToString();
        }

        private static void PopulatePoseComponents(SampleAlignmentResult result)
        {
            if (result == null || !result.Success || !Homography.IsFinite(result.CapturedToSampleTransform))
                return;
            var transform = result.CapturedToSampleTransform;
            if (double.IsNaN(result.TranslationX))
                result.TranslationX = transform[0, 2];
            if (double.IsNaN(result.TranslationY))
                result.TranslationY = transform[1, 2];
            if (double.IsNaN(result.RotationDeg))
                result.RotationDeg = Math.Atan2(transform[1, 0], transform[0, 0]) * 180.0 / Math.PI;
            if (double.IsNaN(result.Scale))
                result.Scale = Math.Sqrt(transform[0, 0] * transform[0, 0] + transform[1, 0] * transform[1, 0]);
        }

        private TileWorkflowState SolveDirect(AlignStitchConfig config, SampleTileInfo tile, CapturedImageInfo cap,
                                              ISampleAligner aligner, ProcessingReport report, CancellationToken ct,
                                              bool allowAutomaticBlankPlacement)
        {
            ct.ThrowIfCancellationRequested();
            var metrics = GetSampleContent(config, tile);
            if (allowAutomaticBlankPlacement &&
                (metrics.ContentClass == SampleTileContentClass.ExactZero ||
                 metrics.ContentClass == SampleTileContentClass.LowTexture) &&
                config.AutoPlaceExactZeroSample)
            {
                var reason = "Exact-zero sample has no measurable alignment content; placed at deterministic " +
                             "expected grid pose.";
                AddReport(report, cap, null, reason);
                return TileWorkflowState.FromBlankSampleExpectedPose(cap, tile, metrics, reason);
            }
            if (aligner == null)
                return SolveDirectWithMatcherFactory(config, tile, cap, report, ct, metrics);
            using (var sample = LoadBitmap(tile.ExpectedPath)) using (var img = LoadBitmap(cap.FilePath))
            {
                var ctx = new SampleAlignmentContext { SampleTileId = cap.OrderIndex.ToString(),
                                                       SampleNccModelPath = tile.NccModelPath,
                                                       SampleNccModelRoi = ToNccRoi(tile),
                                                       NccReferenceOriginRow = tile.NccReferenceOriginRow,
                                                       NccReferenceOriginColumn = tile.NccReferenceOriginColumn,
                                                       NccModelSchemaVersion = tile.NccModelSchemaVersion,
                                                       SampleImage = sample,
                                                       CapturedImage = img,
                                                       ExpectedCapturedToSampleTransform = Homography.Identity(),
                                                       Options = ToOptions(config),
                                                       CancellationToken = ct };
                var r = aligner.Align(ctx);
                AddReport(report, cap, r, null);
                if (r.Success)
                {
                    var global = Multiply(DirectTileTranslation(config, tile), r.CapturedToSampleTransform);
                    var directState =
                        WithContent(TileWorkflowState.From(cap, global, PoseSource.SampleAlignment, r, null), metrics);
                    // [Tony] [Change time: 2026-08-04] [Purpose: Preserve the original direct pose as the pose-graph
                    // prior, in both Debug and Release.]
                    directState.OriginalDirectGlobalPose = (double[,])global.Clone();
                    return directState;
                }
                return WithContent(TileWorkflowState.From(cap, null, PoseSource.Failed, r, r.RejectionReason), metrics);
            }
        }

        private TileWorkflowState SolveDirectWithMatcherFactory(AlignStitchConfig config, SampleTileInfo tile,
                                                                CapturedImageInfo cap, ProcessingReport report,
                                                                CancellationToken ct, SampleTileContentMetrics metrics)
        {
            var pipeline = new MatcherPipeline(_matcherFactory, ForwardMatcherResult);
            MatcherKind ? coarse;
            try
            {
                coarse = AlignStitchConfigMapper.ToDirectCoarseMatcherKind(config);
            }
            catch (NotSupportedException ex)
            {
                return WithContent(TileWorkflowState.From(cap, null, PoseSource.Failed, null, ex.Message), metrics);
            }
            var refinement = AlignStitchConfigMapper.ToDirectRefinementMatcherKind(config);
            var directRequest = new MatchRequest { ReferenceImage = _imageCache.GetMono8(tile.ExpectedPath),
                                                   MovingImage = _imageCache.GetDirectMovingMono8(
                                                       cap.FilePath, config.DirectAlignment.Preprocessing.Contrast),
                                                   InitialMovingToReferenceTransform = Transform2D.Identity,
                                                   ReferenceNccModelPath = tile.NccModelPath,
                                                   ReferenceNccModelRoi = ToNccRoi(tile),
                                                   NccReferenceOriginRow = tile.NccReferenceOriginRow,
                                                   NccReferenceOriginColumn = tile.NccReferenceOriginColumn,
                                                   NccModelSchemaVersion = tile.NccModelSchemaVersion,
                                                   ReferenceShapeModelPath = tile.ShapeModelPath,
                                                   ReferenceShapeModelRoi = ToShapeRoi(tile),
                                                   ShapeReferenceOriginRow = tile.ShapeReferenceOriginRow,
                                                   ShapeReferenceOriginColumn = tile.ShapeReferenceOriginColumn,
                                                   ShapeModelSchemaVersion = tile.ShapeModelSchemaVersion,
                                                   ShapeOptions = config.DirectAlignment.Shape,
                                                   Options = AlignStitchConfigMapper.ToDirectMatcherOptions(config),
                                                   Purpose = MatchPurpose.CapturedToSample,
                                                   OrderIndex = cap.OrderIndex,
                                                   SampleTileId = cap.OrderIndex.ToString() };
            var result = pipeline.MatchDirect(
                directRequest, coarse, refinement, config.DirectAlignment.Policy.AllowCoarseOnlyAcceptance,
                config.DirectAlignment.Policy.AllowRefinementFromExpectedWhenCoarseFails, ct);
            DirectCandidateEvaluation evaluation = null;
            if (config.DirectAlignment.Evaluation.ReportOnlyEnabled ||
                config.DirectAlignment.Evaluation.SelectionPolicy == DirectSelectionPolicy.WeightedCommonMetric)
            {
                evaluation = new DirectCandidateEvaluator().Evaluate(
                    new DirectCandidateEvaluationRequest {
                        ReferenceImage = directRequest.ReferenceImage, MovingImage = directRequest.MovingImage,
                        ReferenceValidMask = directRequest.ReferenceMask, MovingValidMask = directRequest.MovingMask,
                        CoarseCandidate = result.CoarseCandidate, RefinementCandidate = result.RefinementCandidate,
                        Options = config.DirectAlignment.Evaluation
                    },
                    ct);
            }
            DirectCandidateSelector.Apply(result, evaluation, config.DirectAlignment.Evaluation);
            var match = result.SelectedCandidate;
            var alignment = new SampleAlignmentResult {
                Success = result.Success,
                Method = ToDirectAlignmentMethod(coarse, refinement, result),
                CapturedToSampleTransform =
                    result.Success && match != null ? match.MovingToReferenceTransform.ToArray() : null,
                NccScore = coarse == MatcherKind.HalconNcc && result.CoarseCandidate != null
                               ? result.CoarseCandidate.RawScore
                               : double.NaN,
                EccCorrelation = refinement == MatcherKind.PyramidEcc && result.RefinementCandidate != null
                                     ? result.RefinementCandidate.RawScore
                                     : double.NaN,
                CoarseMatcherName = result.CoarseCandidate == null ? null : result.CoarseCandidate.MatcherName,
                CoarseRawScore = result.CoarseCandidate == null ? double.NaN : result.CoarseCandidate.RawScore,
                CoarseNormalizedConfidence =
                    result.CoarseCandidate == null ? double.NaN : result.CoarseCandidate.NormalizedConfidence,
                RefinementMatcherName =
                    result.RefinementCandidate == null ? null : result.RefinementCandidate.MatcherName,
                RefinementRawScore =
                    result.RefinementCandidate == null ? double.NaN : result.RefinementCandidate.RawScore,
                SelectedMatcherName = match == null ? null : match.MatcherName,
                SelectedRawScore = match == null ? double.NaN : match.RawScore,
                SelectedCommonScore = result.SelectedCommonScore,
                SelectionReason = result.SelectionReason,
                CandidateEvaluation = evaluation,
                NccFailureReason =
                    result.CoarseCandidate == null || result.CoarseCandidate.Success
                        ? null
                        : result.CoarseCandidate.FailureReason + ": " + result.CoarseCandidate.FailureMessage,
                EccFailureReason =
                    result.RefinementCandidate == null || result.RefinementCandidate.Success
                        ? null
                        : result.RefinementCandidate.FailureReason + ": " + result.RefinementCandidate.FailureMessage,
                PipelineStage = result.SelectedStage,
                RejectionReason = result.Success ? null : result.FailureReason,
                TranslationX = match == null ? double.NaN : match.TranslationX,
                TranslationY = match == null ? double.NaN : match.TranslationY,
                RotationDeg = match == null ? double.NaN : match.RotationDeg,
                Scale = match == null ? double.NaN : match.Scale,
                OverlapRatio = match == null ? double.NaN : match.OverlapRatio
            };
            AddReport(report, cap, alignment, null);
            if (!alignment.Success)
                return WithContent(
                    TileWorkflowState.From(cap, null, PoseSource.Failed, alignment, alignment.RejectionReason),
                    metrics);
            var directGlobal = Multiply(DirectTileTranslation(config, tile), alignment.CapturedToSampleTransform);
            var directMatcherState = WithContent(
                TileWorkflowState.From(cap, directGlobal, PoseSource.SampleAlignment, alignment, null), metrics);
            // [Tony] [Change time: 2026-08-04] [Purpose: Preserve the original direct pose as the pose-graph prior, in
            // both Debug and Release.]
            directMatcherState.OriginalDirectGlobalPose = (double[,])directGlobal.Clone();
            return directMatcherState;
        }

        private static double[,] DirectTileTranslation(AlignStitchConfig config, SampleTileInfo tile)
        {
            return Translation(tile.ExpectedX + config.DirectAlignment.ManualOffsetX,
                               tile.ExpectedY + config.DirectAlignment.ManualOffsetY);
        }

        private static SampleAlignmentMethod ToDirectAlignmentMethod(MatcherKind? coarse, MatcherKind? refinement,
                                                                     MatcherPipelineResult result)
        {
            var refinementAccepted = result != null && result.RefinementCandidate != null &&
                                     result.RefinementCandidate.Success &&
                                     object.ReferenceEquals(result.SelectedCandidate, result.RefinementCandidate);
            if (refinementAccepted && refinement == MatcherKind.PyramidPhaseCorrelation)
                return SampleAlignmentMethod.PyramidPhaseCorrelation;
            if (refinementAccepted && refinement == MatcherKind.PyramidEcc)
            {
                if (coarse == MatcherKind.HalconNcc)
                    return SampleAlignmentMethod.NccThenPyramidEcc;
                if (coarse == MatcherKind.HalconShapeModel)
                    return SampleAlignmentMethod.ShapeThenPyramidEcc;
                return SampleAlignmentMethod.PyramidEcc;
            }
            if (coarse == MatcherKind.HalconShapeModel)
                return SampleAlignmentMethod.HalconShapeModel;
            if (coarse == MatcherKind.PyramidEcc)
                return SampleAlignmentMethod.PyramidEcc;
            if (coarse == MatcherKind.PyramidPhaseCorrelation)
                return SampleAlignmentMethod.PyramidPhaseCorrelation;
            return SampleAlignmentMethod.HalconNcc;
        }

        private TileWorkflowState Recover(AlignStitchConfig config, SampleTileInfo tile, CapturedImageInfo cap,
                                          Dictionary<int, TileWorkflowState> solved, IList<CapturedImageInfo> ordered,
                                          IDictionary<int, CapturedImageInfo> capturedByOrder,
                                          IDictionary<int, SampleTileInfo> tileByOrder, ProcessingReport report,
                                          CancellationToken ct, bool includeSuccessor)
        {
            if (AlignStitchConfigMapper.ToNeighborCoarseMatcherKind(config).HasValue)
            {
                // Measure every eligible adjacent anchor before selection. Rejected edges remain in the audit report.
                var measured = new List<Tuple<RecoveryCandidate, RecoveryEdgeReport>>();
                foreach (var candidate in BuildRecoveryCandidates(cap, solved, ordered, includeSuccessor))
                {
                    ct.ThrowIfCancellationRequested();
                    var edge = MatchNeighborEdge(config, tile, cap, candidate.Anchor.OrderIndex, capturedByOrder,
                                                 tileByOrder, RecoveryEdgePurpose.FailureRecovery, ct);
                    if (edge == null)
                        continue;
                    edge.TraversalDirectionPreferred = candidate.TraversalDirectionPreferred;
                    report.RecoveryEdges.Add(edge);
                    measured.Add(Tuple.Create(candidate, edge));
                }
                var ranked = NeighborCandidateRanker.Rank(measured.Select(x => x.Item2));
                if (ranked.Count > 0)
                {
                    PopulateCandidateGlobalPosesAndConsistency(config, measured, ranked, report, cap);
                    var selectedEdge = ranked[0];
                    var selected = measured.Single(x => object.ReferenceEquals(x.Item2, selectedEdge));
                    return CompleteRecoveryFromAnchor(cap, selected.Item1, selectedEdge, report);
                }
                report.Warnings.Add(TilePrefix(cap) +
                                    "Neighbor recovery attempted but no image-based candidate passed acceptance.");
            }
            else
                report.Warnings.Add(TilePrefix(cap) +
                                    "Neighbor recovery disabled; expected-grid substitution is blocked.");

            var expected = Translation(tile.ExpectedX, tile.ExpectedY);
            if (config.Recovery.EnableManualAlignment && _manualProvider != null)
            {
                var manual = _manualProvider.RequestManualAlignment(
                    new ManualAlignmentRequest { Captured = cap, SampleTile = tile, ExpectedGlobalPose = expected,
                                                 Diagnostics =
                                                     report.TileReports.Where(x => x.OrderIndex == cap.OrderIndex)
                                                         .Select(x => x.RejectionReason ?? x.FallbackReason)
                                                         .ToList() },
                    ct);
                if (manual != null)
                {
                    if (manual.CancelRun)
                        throw new OperationCanceledException("Manual alignment cancelled the run.", ct);
                    if (manual.Accepted)
                        return TileWorkflowState.From(cap, manual.GlobalPose, PoseSource.Manual, null, manual.Notes);
                    if (manual.Skipped)
                        return TileWorkflowState.From(cap, null, PoseSource.Excluded, null, manual.Notes);
                }
            }
            return TileWorkflowState.From(
                cap, null, PoseSource.Failed, null,
                "No verified direct alignment and no accepted image-based recovery/manual pose.");
        }

        private TileWorkflowState ResolveFailedWithoutNeighbor(AlignStitchConfig config, SampleTileInfo tile,
                                                               CapturedImageInfo cap, ProcessingReport report,
                                                               CancellationToken ct)
        {
            report.Warnings.Add(TilePrefix(cap) +
                                "Failed-tile neighbor recovery disabled; applying manual/excluded/failure policy.");
            var expected = Translation(tile.ExpectedX, tile.ExpectedY);
            if (config.Recovery.EnableManualAlignment && _manualProvider != null)
            {
                var manual = _manualProvider.RequestManualAlignment(
                    new ManualAlignmentRequest { Captured = cap, SampleTile = tile, ExpectedGlobalPose = expected,
                                                 Diagnostics =
                                                     report.TileReports.Where(x => x.OrderIndex == cap.OrderIndex)
                                                         .Select(x => x.RejectionReason ?? x.FallbackReason)
                                                         .ToList() },
                    ct);
                if (manual != null)
                {
                    if (manual.CancelRun)
                        throw new OperationCanceledException("Manual alignment cancelled the run.", ct);
                    if (manual.Accepted)
                        return TileWorkflowState.From(cap, manual.GlobalPose, PoseSource.Manual, null, manual.Notes);
                    if (manual.Skipped)
                        return TileWorkflowState.From(cap, null, PoseSource.Excluded, null, manual.Notes);
                }
            }
            return TileWorkflowState.From(cap, null, PoseSource.Failed, null,
                                          "Direct alignment failed and failed-tile neighbor recovery is disabled.");
        }

        private static TileWorkflowState CompleteRecoveryFromAnchor(CapturedImageInfo target,
                                                                    RecoveryCandidate candidate,
                                                                    RecoveryEdgeReport edge, ProcessingReport report)
        {
            var targetToAnchor = new Transform2D(edge.TargetToAnchorTransform);
            var global = new Transform2D(candidate.Anchor.GlobalPose).Multiply(targetToAnchor).ToArray();
            edge.Selected = true;
            edge.InheritedAnchorRotationDeg =
                Math.Atan2(candidate.Anchor.GlobalPose[1, 0], candidate.Anchor.GlobalPose[0, 0]) * 180 / Math.PI;
            edge.ComposedIntoGlobalPose = true;
            edge.CompositionAnchorGlobalPose = CloneMatrix(candidate.Anchor.GlobalPose);
            var alignment = new SampleAlignmentResult { Success = true,
                                                        Method = SampleAlignmentMethod.PyramidPhaseCorrelation,
                                                        CapturedToSampleTransform = edge.TargetToAnchorTransform,
                                                        NccScore = edge.PhaseScore,
                                                        EccCorrelation = double.NaN,
                                                        PipelineStage = edge.Matcher,
                                                        PreprocessingVariant = "neighbor-overlap-" + edge.Direction,
                                                        TranslationX = edge.TargetToAnchorTransform[0, 2],
                                                        TranslationY = edge.TargetToAnchorTransform[1, 2],
                                                        RotationDeg = Math.Atan2(edge.TargetToAnchorTransform[1, 0],
                                                                                 edge.TargetToAnchorTransform[0, 0]) *
                                                                      180 / Math.PI,
                                                        Scale = 1,
                                                        OverlapRatio = edge.OverlapRatio };
            edge.RankingReason = "Selected rank 1: " + NeighborCandidateRanker.RankingTuple(edge) + ".";
            AddReport(report, target, alignment,
                      "Recovered from selected anchor OrderIndex " + candidate.Anchor.OrderIndex + " via " +
                          edge.Direction + "; " + edge.RankingReason);
            return TileWorkflowState.From(target, global, PoseSource.NeighborAlignment, alignment,
                                          edge.Reason + " " + edge.RankingReason);
        }

        private static void PopulateCandidateGlobalPosesAndConsistency(
            AlignStitchConfig config, IList<Tuple<RecoveryCandidate, RecoveryEdgeReport>> measured,
            IList<RecoveryEdgeReport> ranked, ProcessingReport report, CapturedImageInfo target)
        {
            foreach (var item in measured.Where(x => x.Item2.Accepted))
                item.Item2.CandidateGlobalPose =
                    new Transform2D(item.Item1.Anchor.GlobalPose)
                        .Multiply(new Transform2D(item.Item2.MeasuredTargetToAnchorTransform))
                        .ToArray();
            for (var i = 0; i < ranked.Count; i++)
                for (var j = i + 1; j < ranked.Count; j++)
                {
                    var disagreement = PoseResidual(ranked[i].CandidateGlobalPose, ranked[j].CandidateGlobalPose);
                    ranked[i].MaximumTranslationDisagreement =
                        Maximum(ranked[i].MaximumTranslationDisagreement, disagreement.Item1);
                    ranked[j].MaximumTranslationDisagreement =
                        Maximum(ranked[j].MaximumTranslationDisagreement, disagreement.Item1);
                    ranked[i].MaximumRotationDisagreementDeg =
                        Maximum(ranked[i].MaximumRotationDisagreementDeg, disagreement.Item2);
                    ranked[j].MaximumRotationDisagreementDeg =
                        Maximum(ranked[j].MaximumRotationDisagreementDeg, disagreement.Item2);
                    if (disagreement.Item1 > config.NeighborAlignment.MaxCandidateDisagreementPixels ||
                        disagreement.Item2 > config.NeighborAlignment.MaxCandidateDisagreementRotationDeg)
                    {
                        var warning =
                            "Candidate pose disagreement: anchors " + ranked[i].AnchorOrderIndex + " and " +
                            ranked[j].AnchorOrderIndex + ", translation=" +
                            disagreement.Item1.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                            ", rotationDeg=" +
                            disagreement.Item2.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                            ". Rank 1 remains selected; no fusion.";
                        ranked[i].ConsistencyWarning = warning;
                        ranked[j].ConsistencyWarning = warning;
                        report.Warnings.Add(TilePrefix(target) + warning);
                    }
                }
        }

        private static double Maximum(double current, double value)
        {
            return double.IsNaN(current) ? value : Math.Max(current, value);
        }

        // Measures a pair only. It deliberately has no access to workflow state, so the same
        // operation is safe for failure recovery and full-pose reconciliation.
        private RecoveryEdgeReport MatchNeighborEdge(AlignStitchConfig config, SampleTileInfo targetTile,
                                                     CapturedImageInfo target, int anchorOrderIndex,
                                                     IDictionary<int, CapturedImageInfo> capturedByOrder,
                                                     IDictionary<int, SampleTileInfo> tileByOrder,
                                                     RecoveryEdgePurpose purpose, CancellationToken ct)
        {
            CapturedImageInfo anchorCap;
            SampleTileInfo anchorTile;
            if (!capturedByOrder.TryGetValue(anchorOrderIndex, out anchorCap) ||
                !tileByOrder.TryGetValue(anchorOrderIndex, out anchorTile))
                return null;
            var direction = NeighborTransformModel.Direction(anchorTile, targetTile);
            if (direction == null)
                return null;
#pragma warning disable 618
            var legacyRefinementWarning =
                config.NeighborAlignment.RefinementMatcher == NeighborRefinementMatcherKind.PyramidEcc
                    ? " Legacy Neighbor RefinementMatcher=PyramidEcc is deprecated and ignored; production is " +
                      "phase-only."
                    : string.Empty;
#pragma warning restore 618
            if (!AlignStitchConfigMapper.ToNeighborCoarseMatcherKind(config).HasValue)
                return CreateRecoveryEdge(anchorOrderIndex, target.OrderIndex, direction, "None",
                                          "anchor=" + anchorOrderIndex + ", target=" + target.OrderIndex +
                                              ", direction=" + direction + ": Neighbor coarse matcher is disabled.",
                                          null, null, null, null, 0, purpose, false);
            var anchorMat = _imageCache.GetMono8(anchorCap.FilePath);
            var targetMat = _imageCache.GetMono8(target.FilePath);
            var anchorOverlapRoi = AnchorRoi(anchorMat, anchorTile, targetTile, direction);
            var targetOverlapRoi = TargetRoi(targetMat, anchorTile, targetTile, direction);
            using (var anchorRoi = CropCopy(anchorMat, anchorOverlapRoi)) using (
                var targetRoi = CropCopy(targetMat, targetOverlapRoi))
            {
                double sampleForegroundRatio;
                double sampleEdgeDensity;
                CalculateSampleOverlapMetrics(targetTile, anchorTile, direction, out sampleForegroundRatio,
                                              out sampleEdgeDensity);
                var overlap = MatcherGeometryValidator.CalculateSameSizeOverlap(anchorRoi, targetRoi);
                var options = AlignStitchConfigMapper.ToNeighborMatcherOptions(config);
                MatchResult phase;
                using (var phaseMatcher =
                           _matcherFactory.Create(MatcherKind.PyramidPhaseCorrelation, ForwardMatcherResult)) phase =
                    phaseMatcher.Match(new MatchRequest { ReferenceImage = anchorRoi, MovingImage = targetRoi,
                                                          Options = options,
                                                          Purpose = MatchPurpose.TargetCapturedToAnchorCaptured,
                                                          SampleTileId = target.OrderIndex.ToString(),
                                                          OrderIndex = target.OrderIndex },
                                       ct);
                if (!phase.Success)
                {
                    var failedEdge = CreateRecoveryEdge(
                        anchorOrderIndex, target.OrderIndex, direction, "PyramidPhaseCorrelationMatcher",
                        "anchor=" + anchorOrderIndex + ", target=" + target.OrderIndex + ", direction=" + direction +
                            ": " + phase.FailureReason + ": " + phase.FailureMessage + legacyRefinementWarning,
                        NeighborTransformModel.ExpectedTargetToAnchor(anchorTile, targetTile).ToArray(), null, null,
                        phase, overlap, purpose, false);
                    failedEdge.SampleForegroundRatio = sampleForegroundRatio;
                    failedEdge.SampleEdgeDensity = sampleEdgeDensity;
                    return failedEdge;
                }

                var roiToFull =
                    RoiTransformToFull(phase.MovingToReferenceTransform, anchorOverlapRoi, targetOverlapRoi);
                var targetToAnchor = roiToFull;
                var expected = NeighborTransformModel.ExpectedTargetToAnchor(anchorTile, targetTile);
                var residual = NeighborTransformModel.Residual(expected, targetToAnchor);
                var acceptance =
                    NeighborMatchAcceptance.Validate(phase, expected, targetToAnchor, residual, options, overlap);
                var residualMatrix = residual.ToArray();
                var contextualReason =
                    "anchor=" + anchorOrderIndex + ", target=" + target.OrderIndex + ", direction=" + direction +
                    ": Residual Dx=" +
                    residualMatrix[0, 2].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) +
                    ", Dy=" +
                    residualMatrix[1, 2].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ". " +
                    acceptance.Reason + legacyRefinementWarning;
                var acceptedEdge =
                    CreateRecoveryEdge(anchorOrderIndex, target.OrderIndex, direction, "PyramidPhaseCorrelationMatcher",
                                       contextualReason, expected.ToArray(), targetToAnchor.ToArray(), residualMatrix,
                                       phase, overlap, purpose, acceptance.IsMatch);
                acceptedEdge.SampleForegroundRatio = sampleForegroundRatio;
                acceptedEdge.SampleEdgeDensity = sampleEdgeDensity;
                acceptedEdge.ResidualNorm = Math.Sqrt(residualMatrix[0, 2] * residualMatrix[0, 2] +
                                                      residualMatrix[1, 2] * residualMatrix[1, 2]);
                return acceptedEdge;
            }
        }

        private void CalculateSampleOverlapMetrics(SampleTileInfo targetTile, SampleTileInfo anchorTile,
                                                   string direction, out double foregroundRatio, out double edgeDensity)
        {
            var sample = _imageCache.GetMono8(targetTile.ExpectedPath);
            var roi = TargetRoi(sample, anchorTile, targetTile, direction);
            using (var crop = CropCopy(sample, roi)) using (var foreground = new Mat()) using (var edges = new Mat())
            {
                Cv2.Threshold(crop, foreground, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                Cv2.Canny(crop, edges, 50, 150);
                var pixels = Math.Max(1d, crop.Rows * (double)crop.Cols);
                foregroundRatio = Cv2.CountNonZero(foreground) / pixels;
                edgeDensity = Cv2.CountNonZero(edges) / pixels;
            }
        }

        private static IList<RecoveryCandidate> BuildRecoveryCandidates(CapturedImageInfo target,
                                                                        Dictionary<int, TileWorkflowState> solved,
                                                                        IList<CapturedImageInfo> ordered,
                                                                        bool includeSuccessor)
        {
            var result = new List<RecoveryCandidate>();
            TileWorkflowState anchor;
            if (solved.TryGetValue(target.OrderIndex - 1, out anchor) && anchor.IsStitchable)
                result.Add(new RecoveryCandidate(anchor, "traversal-predecessor", true));
            foreach (var s in solved.Values
                         .Where(x => x.IsStitchable && x.OrderIndex != target.OrderIndex &&
                                     (Math.Abs(x.Row - target.Row) + Math.Abs(x.Column - target.Column) == 1))
                         .OrderBy(x => Math.Abs(x.Row - target.Row) + Math.Abs(x.Column - target.Column))
                         .ThenBy(x => x.OrderIndex))
                if (!result.Any(r => r.Anchor.OrderIndex == s.OrderIndex))
                    result.Add(new RecoveryCandidate(s, "solved-grid-neighbor", false));
            if (includeSuccessor && solved.TryGetValue(target.OrderIndex + 1, out anchor) && anchor.IsStitchable &&
                !result.Any(r => r.Anchor.OrderIndex == anchor.OrderIndex))
                result.Add(new RecoveryCandidate(anchor, "traversal-successor-second-pass", true));
            return result;
        }

        private void ReconcileNeighborGraph(AlignStitchConfig config, Dictionary<int, TileWorkflowState> solved,
                                            IList<CapturedImageInfo> ordered,
                                            IDictionary<int, CapturedImageInfo> capturedByOrder,
                                            IDictionary<int, SampleTileInfo> tileByOrder, ProcessingReport report,
                                            IProgress<WorkflowProgress> progress, CancellationToken ct)
        {
            var pairs = new List<Tuple<CapturedImageInfo, CapturedImageInfo>>();
            var eligible =
                ordered
                    .Where(x => x != null && !string.IsNullOrWhiteSpace(x.FilePath) && File.Exists(x.FilePath) &&
                                solved.ContainsKey(x.OrderIndex) && solved[x.OrderIndex].Source != PoseSource.Excluded)
                    .ToList();
            foreach (var a in eligible.OrderBy(x => x.OrderIndex))
                foreach (var b in eligible.Where(x => x.OrderIndex > a.OrderIndex &&
                                                      Math.Abs(x.Row - a.Row) + Math.Abs(x.Column - a.Column) == 1))
                    pairs.Add(Tuple.Create(a, b)); // canonical order prevents the reverse duplicate

            foreach (var pair in pairs)
            {
                ct.ThrowIfCancellationRequested();
                var edge =
                    MatchNeighborEdge(config, tileByOrder[pair.Item2.OrderIndex], pair.Item2, pair.Item1.OrderIndex,
                                      capturedByOrder, tileByOrder, RecoveryEdgePurpose.FullPoseReconciliation, ct);
                if (edge == null)
                    continue;
                if (solved[pair.Item1.OrderIndex].HasValidPose)
                    edge.InheritedAnchorRotationDeg = Math.Atan2(solved[pair.Item1.OrderIndex].GlobalPose[1, 0],
                                                                 solved[pair.Item1.OrderIndex].GlobalPose[0, 0]) *
                                                      180 / Math.PI;
                if (edge.Accepted && solved[pair.Item1.OrderIndex].HasValidPose &&
                    solved[pair.Item2.OrderIndex].HasValidPose)
                {
                    var predicted = Multiply(solved[pair.Item1.OrderIndex].GlobalPose, edge.TargetToAnchorTransform);
                    var closure = PoseResidual(predicted, solved[pair.Item2.OrderIndex].GlobalPose);
                    if (closure.Item1 > config.NeighborAlignment.MaxCycleClosureErrorPixels)
                    {
                        edge.Accepted = false;
                        edge.Reason =
                            "Rejected by cycle/direct-pose closure check; residual=" +
                            closure.Item1.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " px.";
                    }
                }
                report.RecoveryEdges.Add(edge);
                progress?.Report(new WorkflowProgress(pair.Item2.OrderIndex + 1, ordered.Count, pair.Item2,
                                                      "Full pose reconciliation: " + pair.Item1.OrderIndex + "->" +
                                                          pair.Item2.OrderIndex + " " + edge.Reason));
            }

            // [Tony] [Change time: 2026-08-04] [Purpose: When the pose graph optimizer is enabled, only measure edges
            // here; it supersedes single-path anchor selection/propagation below.]
            if (config.PoseGraph.Enabled)
                return;

            var usable = report.RecoveryEdges
                             .Where(x => x.Purpose == RecoveryEdgePurpose.FullPoseReconciliation && x.Accepted &&
                                         x.TargetToAnchorTransform != null)
                             .ToList();
            var direct = solved.Values.Where(x => x.Source == PoseSource.SampleAlignment && x.HasValidPose)
                             .OrderByDescending(DirectConfidence)
                             .ThenBy(x => x.OrderIndex)
                             .FirstOrDefault();
            if (direct == null)
                direct = solved.Values.Where(x => x.IsStitchable && x.HasValidPose)
                             .OrderBy(x => x.OrderIndex)
                             .FirstOrDefault();
            if (direct == null)
                return;

            var poses = new Dictionary<int, double[,]> { { direct.OrderIndex, direct.GlobalPose } };
            var qualities = new Dictionary<int, double> { { direct.OrderIndex, double.PositiveInfinity } };
            var pending = new HashSet<int>(solved.Keys);
            pending.Remove(direct.OrderIndex);
            while (pending.Count > 0)
            {
                int bestNode = -1;
                double bestQuality = -1;
                double[,] bestPose = null;
                foreach (var edge in usable)
                {
                    double[,] candidatePose = null;
                    int node = -1;
                    if (poses.ContainsKey(edge.AnchorOrderIndex) && pending.Contains(edge.TargetOrderIndex))
                    {
                        node = edge.TargetOrderIndex;
                        candidatePose = Multiply(poses[edge.AnchorOrderIndex], edge.TargetToAnchorTransform);
                    }
                    else if (poses.ContainsKey(edge.TargetOrderIndex) && pending.Contains(edge.AnchorOrderIndex))
                    {
                        node = edge.AnchorOrderIndex;
                        candidatePose = Multiply(poses[edge.TargetOrderIndex],
                                                 new Transform2D(edge.TargetToAnchorTransform).Invert().ToArray());
                    }
                    if (node < 0)
                        continue;
                    var q = Math.Min(qualities.ContainsKey(edge.AnchorOrderIndex) ? qualities[edge.AnchorOrderIndex]
                                                                                  : qualities[edge.TargetOrderIndex],
                                     Math.Max(1e-9, edge.Quality));
                    if (q > bestQuality || (Math.Abs(q - bestQuality) < 1e-12 && node < bestNode))
                    {
                        bestNode = node;
                        bestQuality = q;
                        bestPose = candidatePose;
                    }
                }
                if (bestNode < 0)
                    break;
                poses[bestNode] = bestPose;
                qualities[bestNode] = bestQuality;
                pending.Remove(bestNode);
            }

            foreach (var entry in poses.Where(x => x.Key != direct.OrderIndex))
            {
                var state = solved[entry.Key];
                if (state.Source == PoseSource.Excluded)
                    continue;
                if (!state.HasValidPose || !state.IsStitchable)
                {
                    var reconciled =
                        WithContent(TileWorkflowState.From(capturedByOrder[entry.Key], entry.Value,
                                                           PoseSource.NeighborAlignment, state.Alignment,
                                                           "Connected to deterministic anchor " + direct.OrderIndex +
                                                               " by accepted reconciliation edges."),
                                    state.SampleContent);
#if DEBUG
                    reconciled.DirectAlignmentTransform = state.DirectAlignmentTransform;
#endif
                    solved[entry.Key] = reconciled;
                    continue;
                }
                var residual = PoseResidual(entry.Value, state.GlobalPose);
                if (residual.Item1 <= config.NeighborAlignment.MaxDirectPoseAdjustmentPixels &&
                    residual.Item2 <= config.NeighborAlignment.MaxDirectPoseAdjustmentRotationDeg &&
                    residual.Item1 > 1e-6)
                {
                    var tileReport = report.TileReports.LastOrDefault(x => x.OrderIndex == entry.Key);
                    if (tileReport == null)
                    {
                        tileReport =
                            new ProcessingTileReport
                            {
                                OrderIndex = entry.Key,
                                Row = state.Row,
                                Column = state.Column
                            };
                        report.TileReports.Add(tileReport);
                    }
                    tileReport.OriginalGlobalPose = CloneMatrix(state.GlobalPose);
                    tileReport.AdjustedGlobalPose = CloneMatrix(entry.Value);
                    tileReport.AdjustmentTranslationResidual = residual.Item1;
                    tileReport.AdjustmentRotationResidualDeg = residual.Item2;
                    tileReport.AdjustmentReason =
                        "Accepted highest-quality path from anchor " + direct.OrderIndex + ".";
                    state.GlobalPose = entry.Value;
                    state.Source = PoseSource.AnchorAdjusted;
                    state.Reason = tileReport.AdjustmentReason;
                }
            }
        }

        private static double DirectConfidence(TileWorkflowState state)
        {
            if (state.Alignment == null)
                return double.NegativeInfinity;
            if (!double.IsNaN(state.Alignment.CoarseNormalizedConfidence))
                return state.Alignment.CoarseNormalizedConfidence;
            if (!double.IsNaN(state.Alignment.EccCorrelation))
                return state.Alignment.EccCorrelation;
            return state.Alignment.NccScore;
        }

        private static Tuple<double, double> PoseResidual(double[,] a, double[,] b)
        {
            var dx = a[0, 2] - b[0, 2];
            var dy = a[1, 2] - b[1, 2];
            var aa = Math.Atan2(a[1, 0], a[0, 0]) * 180 / Math.PI;
            var ba = Math.Atan2(b[1, 0], b[0, 0]) * 180 / Math.PI;
            return Tuple.Create(Math.Sqrt(dx * dx + dy * dy), Math.Abs(aa - ba));
        }

        private static double[,] CloneMatrix(double[,] value)
        {
            return value == null ? null : (double[,])value.Clone();
        }

        private static void ValidateGlobalPoses(AlignStitchConfig config, Dictionary<int, TileWorkflowState> solved,
                                                IDictionary<int, SampleTileInfo> tiles, ProcessingReport report)
        {
            foreach (var state in solved.Values.Where(x => x.IsStitchable))
            {
                var p = state.GlobalPose;
                var scale = Homography.IsFinite(p) ? Math.Sqrt(p[0, 0] * p[0, 0] + p[1, 0] * p[1, 0]) : double.NaN;
                var rotation =
                    Homography.IsFinite(p) ? Math.Abs(Math.Atan2(p[1, 0], p[0, 0]) * 180 / Math.PI) : double.NaN;
                var tile = tiles[state.OrderIndex];
                var inCanvas = Homography.IsFinite(p) && Math.Abs(p[0, 2]) < 1000000000d &&
                               Math.Abs(p[1, 2]) < 1000000000d && tile.Width > 0 && tile.Height > 0;
                if (!Homography.IsFinite(p) || scale < config.NeighborAlignment.Acceptance.MinScale ||
                    scale > config.NeighborAlignment.Acceptance.MaxScale || rotation > 180 || !inCanvas)
                    throw new InvalidOperationException("Invalid reconciled GlobalPose for OrderIndex " +
                                                        state.OrderIndex + ".");
            }
            var accepted = report.RecoveryEdges.Where(x => x.Accepted).ToList();
            var connected = new HashSet<int>(solved.Values
                                                 .Where(x => x.Source == PoseSource.SampleAlignment ||
                                                             x.Source == PoseSource.Manual ||
                                                             x.Source == PoseSource.BlankSampleExpectedPose)
                                                 .Select(x => x.OrderIndex));
            bool changed;
            do
            {
                changed = false;
                foreach (var edge in accepted)
                {
                    if (connected.Contains(edge.AnchorOrderIndex) && connected.Add(edge.TargetOrderIndex))
                        changed = true;
                    if (connected.Contains(edge.TargetOrderIndex) && connected.Add(edge.AnchorOrderIndex))
                        changed = true;
                }
            } while (changed);
            foreach (var state in solved.Values.Where(x => x.Source == PoseSource.NeighborAlignment && x.IsStitchable))
                if (!connected.Contains(state.OrderIndex))
                    throw new InvalidOperationException(
                        "Neighbor-aligned tile is disconnected from the accepted edge graph: " + state.OrderIndex +
                        ".");
        }

        private static Rect AnchorRoi(Mat anchor, SampleTileInfo anchorTile, SampleTileInfo targetTile,
                                      string direction)
        {
            // The overlap size comes from manifest tile geometry, never from an arbitrary image fraction.
            var w = Math.Max(1, Math.Min(anchor.Cols, Math.Min(anchorTile.ExpectedX + anchorTile.Width,
                                                               targetTile.ExpectedX + targetTile.Width) -
                                                          Math.Max(anchorTile.ExpectedX, targetTile.ExpectedX)));
            var h = Math.Max(1, Math.Min(anchor.Rows, Math.Min(anchorTile.ExpectedY + anchorTile.Height,
                                                               targetTile.ExpectedY + targetTile.Height) -
                                                          Math.Max(anchorTile.ExpectedY, targetTile.ExpectedY)));
            if (direction == "right")
                return new Rect(anchor.Cols - w, 0, w, anchor.Rows);
            if (direction == "left")
                return new Rect(0, 0, w, anchor.Rows);
            if (direction == "bottom")
                return new Rect(0, anchor.Rows - h, anchor.Cols, h);
            return new Rect(0, 0, anchor.Cols, h);
        }

        private static Rect TargetRoi(Mat target, SampleTileInfo anchorTile, SampleTileInfo targetTile,
                                      string direction)
        {
            var w = Math.Max(1, Math.Min(target.Cols, Math.Min(anchorTile.ExpectedX + anchorTile.Width,
                                                               targetTile.ExpectedX + targetTile.Width) -
                                                          Math.Max(anchorTile.ExpectedX, targetTile.ExpectedX)));
            var h = Math.Max(1, Math.Min(target.Rows, Math.Min(anchorTile.ExpectedY + anchorTile.Height,
                                                               targetTile.ExpectedY + targetTile.Height) -
                                                          Math.Max(anchorTile.ExpectedY, targetTile.ExpectedY)));
            if (direction == "right")
                return new Rect(0, 0, w, target.Rows);
            if (direction == "left")
                return new Rect(target.Cols - w, 0, w, target.Rows);
            if (direction == "bottom")
                return new Rect(0, 0, target.Cols, h);
            return new Rect(0, target.Rows - h, target.Cols, h);
        }

        private static Transform2D RoiTransformToFull(Transform2D roiTargetToAnchor, Rect anchorRoi, Rect targetRoi)
        {
            return Transform2D.Translation(anchorRoi.X, anchorRoi.Y)
                .Multiply(roiTargetToAnchor)
                .Multiply(Transform2D.Translation(-targetRoi.X, -targetRoi.Y));
        }

        private static Mat CropCopy(Mat image, Rect roi)
        {
            using (var view = new Mat(image, roi)) return view.Clone();
        }

        private static RecoveryEdgeReport CreateRecoveryEdge(int anchorOrder, int targetOrder, string direction,
                                                             string matcher, string reason, double[,] expected,
                                                             double[,] measured, double[,] residual, MatchResult phase,
                                                             double overlap, RecoveryEdgePurpose purpose, bool accepted)
        {
            var phaseScore = phase == null ? double.NaN : phase.RawScore;
            return new RecoveryEdgeReport { Purpose = purpose,
                                            Accepted = accepted,
                                            AnchorOrderIndex = anchorOrder,
                                            TargetOrderIndex = targetOrder,
                                            Direction = direction,
                                            Matcher = matcher,
                                            Reason = reason,
                                            TargetToAnchorTransform = measured,
                                            ExpectedTargetToAnchorTransform = expected,
                                            MeasuredTargetToAnchorTransform = measured,
                                            ResidualTransform = residual,
                                            ResidualDx = residual == null ? double.NaN : residual[0, 2],
                                            ResidualDy = residual == null ? double.NaN : residual[1, 2],
                                            PairMeasuredRotationDeg = 0,
                                            PhaseScore = phaseScore,
                                            EccCorrelation = double.NaN,
                                            OverlapRatio = overlap,
                                            Quality = Math.Max(0, phaseScore) * Math.Max(0, overlap) };
        }

        private static Bitmap LoadBitmap(string p)
        {
            return new Bitmap(p);
        }
        private static SampleAlignmentOptions ToOptions(AlignStitchConfig c)
        {
            var d = c.DirectAlignment;
            return new SampleAlignmentOptions { MinOverlapRatio = d.Geometry.MinOverlapRatio,
                                                MaxAbsRotationDeg = d.Geometry.MaxAbsRotationDeg,
                                                AllowNccOnlyAcceptance = d.Policy.AllowCoarseOnlyAcceptance,
                                                NccMinScore = d.Ncc.MinScore,
                                                EccMinCorrelation = d.Ecc.MinCorrelation,
                                                MaxTranslationPixels = d.Geometry.MaxTranslationPixels,
                                                MinScale = d.Geometry.MinScale,
                                                MaxScale = d.Geometry.MaxScale,
                                                AllowEccFromExpectedWhenNccFails =
                                                    d.Policy.AllowRefinementFromExpectedWhenCoarseFails,
                                                PyramidLevels = d.Ecc.PyramidLevels,
                                                EccIterations = d.Ecc.MaxIterations,
                                                EccEpsilon = d.Ecc.Epsilon,
                                                Preprocessing =
                                                    new PreprocessingOptions { Contrast = d.Preprocessing.Contrast } };
        }
        private static OpenCvSharp.Rect? ToNccRoi(SampleTileInfo tile)
        {
            if (!tile.NccModelRoiX.HasValue || !tile.NccModelRoiY.HasValue || !tile.NccModelRoiWidth.HasValue ||
                !tile.NccModelRoiHeight.HasValue)
                return null;
            return new OpenCvSharp.Rect(tile.NccModelRoiX.Value, tile.NccModelRoiY.Value, tile.NccModelRoiWidth.Value,
                                        tile.NccModelRoiHeight.Value);
        }
        private static OpenCvSharp.Rect? ToShapeRoi(SampleTileInfo tile)
        {
            if (!tile.ShapeModelRoiX.HasValue || !tile.ShapeModelRoiY.HasValue || !tile.ShapeModelRoiWidth.HasValue ||
                !tile.ShapeModelRoiHeight.HasValue)
                return null;
            return new OpenCvSharp.Rect(tile.ShapeModelRoiX.Value, tile.ShapeModelRoiY.Value,
                                        tile.ShapeModelRoiWidth.Value, tile.ShapeModelRoiHeight.Value);
        }

        public static double[,] Translation(double x, double y)
        {
            return new[,] { { 1d, 0d, x }, { 0d, 1d, y }, { 0d, 0d, 1d } };
        }
        public static double[,] Multiply(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 3; x++)
                    for (int k = 0; k < 3; k++)
                        r[y, x] += a[y, k] * b[k, x];
            return r;
        }
        private SampleTileContentMetrics GetSampleContent(AlignStitchConfig config, SampleTileInfo tile)
        {
            if (!config.RevalidateSampleContent && !string.IsNullOrWhiteSpace(tile.SampleContentClass))
            {
                SampleTileContentClass parsed;
                if (Enum.TryParse(tile.SampleContentClass, out parsed))
                    return new SampleTileContentMetrics { ContentClass = parsed,
                                                          MinGray = tile.SampleMinGray,
                                                          MaxGray = tile.SampleMaxGray,
                                                          MeanGray = tile.SampleMeanGray,
                                                          StdDevGray = tile.SampleStdDevGray,
                                                          NonZeroPixelRatio = tile.SampleNonZeroPixelRatio };
            }
#if DEBUG
            if (tile.OrderIndex == 111 || tile.OrderIndex == 133)
            {
                var a = config.LowTextureStdDevThreshold;
            }
#endif
            return new SampleTileContentAnalyzer().Analyze(_imageCache.GetMono8(tile.ExpectedPath),
                                                           config.LowTextureStdDevThreshold);
        }

        private static TileWorkflowState WithContent(TileWorkflowState state, SampleTileContentMetrics metrics)
        {
            state.SampleContent = metrics;
            return state;
        }

        private static void ApplyStateReport(ProcessingReport report, TileWorkflowState state, SampleTileInfo tile)
        {
            var item = report.TileReports.LastOrDefault(x => x.OrderIndex == state.OrderIndex);
            if (item == null)
            {
                item =
                    new ProcessingTileReport
                    {
                        OrderIndex = state.OrderIndex,
                        Row = state.Row,
                        Column = state.Column
                    };
                report.TileReports.Add(item);
            }
            var m = state.SampleContent;
            if (m != null)
            {
                item.SampleContentClass = m.ContentClass.ToString();
                item.SampleMin = m.MinGray;
                item.SampleMax = m.MaxGray;
                item.SampleMean = m.MeanGray;
                item.SampleStdDev = m.StdDevGray;
                item.NonZeroPixelRatio = m.NonZeroPixelRatio;
            }
            item.PoseSource = state.Source;
            item.AlignmentSucceeded = state.AlignmentSucceeded;
            item.IsFallbackPose = state.IsFallbackPose;
            item.IsStitchable = state.IsStitchable;
            item.FallbackReason = state.IsFallbackPose ? state.Reason : item.FallbackReason;
            if (state.GlobalPose != null)
            {
                item.GlobalX = state.GlobalPose[0, 2];
                item.GlobalY = state.GlobalPose[1, 2];
                item.GlobalAngle = Math.Atan2(state.GlobalPose[1, 0], state.GlobalPose[0, 0]) * 180.0 / Math.PI;
                item.LocalDx = item.GlobalX - tile.ExpectedX;
                item.LocalDy = item.GlobalY - tile.ExpectedY;
                item.LocalAngle = item.GlobalAngle;
            }
        }

        private static void AddReport(ProcessingReport report, CapturedImageInfo cap, SampleAlignmentResult r,
                                      string fallback)
        {
            report.TileReports.Add(ProcessingTileReport.From(cap, r, fallback));
            if (r != null && !string.IsNullOrEmpty(r.Warning))
                report.Warnings.Add(TilePrefix(cap) + r.Warning);
        }
        private static string TilePrefix(CapturedImageInfo cap)
        {
            return "OrderIndex " + cap.OrderIndex + " Row " + cap.Row + " Column " + cap.Column + ": ";
        }

        private sealed class RecoveryCandidate
        {
            public RecoveryCandidate(TileWorkflowState anchor, string priority, bool traversalDirectionPreferred)
            {
                Anchor = anchor;
                Priority = priority;
                TraversalDirectionPreferred = traversalDirectionPreferred;
            }
            public TileWorkflowState Anchor { get; private set; }
            public string Priority { get; private set; }
            public bool TraversalDirectionPreferred { get; private set; }
        }
    }
}
