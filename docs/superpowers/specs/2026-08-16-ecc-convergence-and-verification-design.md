# ECC Convergence Recovery and Alignment Verification — Design

## Goal

Improve the Python ECC sandbox so that it:

- recovers from OpenCV `findTransformECC` non-convergence at a pyramid level;
- does not treat a high ECC correlation as proof that a repeated PCB pattern was aligned correctly;
- enforces exactly one preprocessing mode: `FlattenAndEnhance` or `ToBinaryTraces`;
- reports `Verified`, `Uncertain`, or `Rejected` with reproducible diagnostics;
- can rerun the requested 14 direct-alignment cases and save machine-readable and visual evidence.

This is an experimental Python-only change. It does not modify the C# production pipeline.

## Evidence and Root Cause

The baseline experiment used the supplied 4240 × 4240 payload, capture folder, and Gerber raster at
the seven requested coordinates, with `Contrast=150` and both preprocessing modes tested separately.
Only 3 of 14 runs were accepted. OpenCV reported that correlation was being minimized for several
cases at pyramid level 2.

The failure is not caused by level 2 alone. Representative failing pairs also failed with one pyramid
level and with Affine, Euclidean, and Translation motion models. The independently preprocessed Gerber
and camera images can begin with negative or near-zero correlation because the camera background and
illumination texture dominate the sparse Gerber traces.

A second, more serious problem is false convergence. A bounded coarse structural search followed by
ECC produced correlations near 0.99 for several pairs, but repeated pads and parallel traces allowed
geometrically different translations to obtain high scores. ECC correlation therefore remains an
optimizer score, not the final correctness criterion.

## Scope

Modify or add files only under:

- `tools/ecc_sandbox/`
- `docs/superpowers/`

Preserve the user's current `ToBinaryTraces` implementation: Otsu thresholding, morphological closing,
and `cv2.addWeighted(enhanced_mono8, 0.7, morph, 0.3, 0.0)`. The weighted result is the image passed to
ECC when that mode is selected.

Do not create a worktree. All implementation occurs on branch `Ver2_8`.

## Preprocessing Contract

The UI replaces the two independent checkboxes with one read-only selection named `Preprocess mode`:

- `FlattenAndEnhance`, default;
- `ToBinaryTraces`.

The modes are mutually exclusive:

- `FlattenAndEnhance`: contrast adjustment, illumination flattening, then CLAHE;
- `ToBinaryTraces`: contrast adjustment, Otsu thresholding, morphological closing, then weighted blending
  with the contrast-adjusted grayscale image. It does not run `FlattenAndEnhance` first.

The preprocessing API rejects an unknown mode. A compatibility wrapper may retain the old Boolean
arguments temporarily, but it must reject both enabled and both disabled rather than silently combining
them.

## Recovery Architecture

### Primary attempt

Run the existing coarse-to-fine ECC path from the supplied initial transform or identity. Preserve the
current Affine normalization (`median` or `min`), signed rotation clamp, matrix direction, and geometry
limits.

### Structural bootstrap

After the primary attempt, always generate bounded translation seeds. This is required even when the
primary attempt converges: without distinct runner-up candidates, a repeated-pattern false convergence
cannot be detected. A successful primary therefore does not suppress the ambiguity search:

1. Downsample both selected ECC inputs by `CoarseSearchDownsample=4`.
2. Extract Canny edges using thresholds `CoarseCannyLow=30` and `CoarseCannyHigh=90`.
3. Convert each edge map to a capped distance-similarity field with
   `CoarseDistanceCapPixels=48` in full-resolution units.
4. Pad the reference field by `ceil(MaxTranslationPixels / CoarseSearchDownsample)` and use normalized
   template correlation to score all translations inside the existing translation bound.
5. Keep `CoarseCandidateCount=5` local maxima. Suppress peaks within
   `CoarseCandidateSeparationPixels=48` full-resolution pixels of a better peak.
6. Convert each retained translation into a `MovingImage -> ReferenceImage` seed and run the normal ECC
   pyramid on the selected preprocessing output. The structural field supplies initialization only; it
   never replaces the selected ECC input.

Identity is always included as a candidate and duplicate seeds are removed. A structural seed within one
full-resolution pixel of the primary seed or an earlier structural seed is skipped. A failed candidate is
recorded and does not abort the remaining candidates. The configured upper bound is one primary attempt
plus `CoarseCandidateCount=5` distinct structural attempts.

## Independent Alignment Verification

Verification uses contrast-adjusted grayscale images, not the flattened/binary image on which ECC was
optimized. This keeps the verifier independent from the selected preprocessing mode.

For every finite, geometry-valid candidate:

1. Warp moving verification edges into reference coordinates.
2. Compute distance transforms in both directions.
3. Measure reference-to-moving and moving-to-reference edge coverage within
   `VerificationEdgeTolerancePixels=3`.
4. Define `symmetric_edge_coverage` as the harmonic mean of those two coverages.
5. Define `symmetric_chamfer_p95` as the mean of the two directional 95th-percentile edge distances.
6. Rank eligible candidates by `symmetric_edge_coverage`, then by lower
   `symmetric_chamfer_p95`, then by higher ECC correlation.

An eligible candidate must satisfy all existing finite-transform, translation, scale, and correlation
checks plus:

- `symmetric_edge_coverage >= VerificationMinEdgeCoverage`, default `0.20`;
- `symmetric_chamfer_p95 <= VerificationMaxChamferP95Pixels`, default `12.0`.

### Repeated-pattern ambiguity

Compare the best eligible candidate with the best geometrically distinct runner-up. Two transforms are
distinct when their translations differ by more than `VerificationSameTransformPixels=4` or their
rotations differ by more than `0.02` degrees.

The result is ambiguous when a distinct runner-up exists and:

```text
best.symmetric_edge_coverage - runner_up.symmetric_edge_coverage
    < VerificationMinCoverageMargin
```

where `VerificationMinCoverageMargin=0.03` by default.

### Result states

- `Verified`: at least one eligible candidate exists and the winner is not ambiguous.
- `Uncertain`: a finite geometry-valid ECC candidate exists, but structural thresholds fail or a distinct
  runner-up is ambiguous.
- `Rejected`: no finite geometry-valid candidate reaches `EccMinCorrelation`.

For compatibility, `result["success"]` is true only for `Verified`. New field
`result["verification_status"]` contains the three-state value. `failure_reason` distinguishes ECC
non-convergence, geometry rejection, structural rejection, and repeated-pattern ambiguity.

## Meaning of “Absolute” Alignment

No image-only score can prove absolute correctness on repeated PCB patterns. ECC, SSIM, chamfer distance,
and edge overlap can all give strong scores at a wrong repeated feature.

Absolute evaluation requires trusted corresponding landmarks, such as pad centers, trace endpoints, or
trace intersections. Given moving points `p_i`, reference points `q_i`, and the returned transform `M`,
Target Registration Error is:

```text
TRE_i = distance(transform(M, p_i), q_i)
```

Report TRE RMS, median, p95, and maximum. A result may be called ground-truth verified only when the
maximum TRE is within a user-defined physical or pixel tolerance. The current payload contains no trusted
landmark correspondences, so this implementation reports automatic confidence and never labels it
“absolute”. A pure TRE helper is included for later use when landmarks become available, without adding a
landmark-editing UI in this scope.

## Diagnostics and UI

The UI and result dictionary show:

- selected preprocessing mode;
- primary and bootstrap attempts, seed translation, per-level ECC values, and runtime error;
- final raw ECC correlation and normalized transform;
- both directional edge coverages, symmetric coverage, and symmetric chamfer p95;
- runner-up transform and coverage margin when present;
- `Verified`, `Uncertain`, or `Rejected` and a specific reason.

The preview still uses the exact authoritative normalized matrix returned in `result["matrix"]`.

## Experiment Runner

Add a non-GUI runner for the fixed regression dataset. It runs Direct mode for:

```text
(1,1), (3,0), (4,0), (3,1), (1,3), (4,2), (4,3)
```

and for each of the two mutually exclusive preprocessing modes with `Contrast=150`; all remaining values
come from `config.DEFAULTS`. Empty image extension resolves to `.bmp`, matching the UI.

The runner writes into the requested `result_test` directory:

- `experiment_results.json` with configuration, candidate diagnostics, and final matrices;
- `requested_cases_summary.csv` with one row per case;
- before/after overlay images for every case, including failures and uncertain results.

It must not overwrite unrelated files in `result_test`; only files with the runner's documented names are
replaced.

## Error Handling

- Catch OpenCV convergence exceptions per candidate, record the complete message, and continue.
- Reject non-finite seeds, transforms, correlations, and verification metrics.
- If either edge map has too few pixels for verification, return `Uncertain` with
  `InsufficientStructuralFeatures` instead of dividing by zero or accepting ECC alone.
- If every ECC attempt fails, retain the most informative OpenCV error and the level where it occurred.
- Do not automatically switch to the other preprocessing mode; the user's selected mode remains
  authoritative.

## Test Strategy

Use Python `unittest` with synthetic arrays and no GUI automation:

1. preprocessing accepts exactly one mode and preserves the weighted-binary contract;
2. coarse translation search returns the correct matrix direction and bounded top candidates;
3. one candidate failure does not stop later candidates;
4. the verifier accepts a unique aligned structure, marks two distinct near-equal transforms ambiguous,
   and rejects insufficient edges;
5. TRE statistics are correct for exact, translated, and non-uniform landmark errors;
6. Affine scale normalization and signed rotation behavior from the previous spec remain unchanged;
7. the experiment runner maps all seven coordinates to both modes and writes 14 result rows.

After unit verification, rerun the supplied real dataset. The real-data gate is not “all 14 must be
Verified”. Success means no unhandled level-2 exception, every case has complete diagnostics, obvious
repeated-pattern alternatives are not silently accepted, and outputs are saved for review.

## Out of Scope

- changes to `GerberStitching.Core`, the façade, or the C# production matcher;
- automatic switching between preprocessing modes;
- a landmark annotation UI;
- claiming absolute accuracy without landmark ground truth;
- changing the existing Affine scale-normalization and rotation-clamp contract.
