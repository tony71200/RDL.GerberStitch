# Pitch Diagnostics and Chamfer-Assisted Recovery — Design

## Goal

Extend the Python ECC sandbox (`tools/ecc_sandbox/`) so that it:

- measures the real camera grid pitch independently, using the sandbox's own tooling, on the
  three already-captured FOV datasets (4192, 4240, 4320) instead of waiting on a production
  `processing_report.json`;
- adds a matching method that does not depend on intensity gradients (unlike ECC) or on stable
  high-frequency content (unlike Phase Correlation), so thin or broken PCB traces can still be
  located when the seed transform is far off;
- lets the measured pitch be fed back into the sandbox's own seeding so direct-alignment cases
  that currently fail purely because of a large, systematic translation (not a matching-quality
  problem) get a real chance to converge;
- keeps all of this reversible and inert by default — nothing here changes production behavior
  unless a new config flag is explicitly set.

This is an experimental Python-only change, same constraint as the prior ECC-convergence-recovery
work. It does not modify `GerberStitching.Core`, the façade, or any C# production matcher, and it
does not modify Master's grid-generation logic (which lives outside this repo).

## Evidence

### 1. The 3-FOV experiment runner results reproduce Findings.md's pitch-error pattern

`experiment_runner.py` was run against three real captured datasets under
`H:\005_Project\AOI_2026_07_imp\20260813\` (4192x4192/o159, 4240x4240/o207, 4320x4320/o287, all
against raster `2-2 Gerber fix black.tiff`), 7 coordinates × 2 preprocessing modes = 14 cases each:

| FOV | Verified | Uncertain | Rejected |
|---|---|---|---|
| 4192x4192 | 0 | 11 | 3 |
| 4240x4240 | 0 | 4 | 10 |
| 4320x4320 | 0 | 4 | 10 |

0/42 cases reached `Verified`. Inspecting `translation_x/y`, `scale`, and `rotation_deg` on the
`Uncertain` cases (where ECC did converge with valid geometry) shows a pattern independent of, but
consistent with, `RDL_GerberStitch_Findings.md` §2–§3:

- **Translation grows with distance from the tile origin, not randomly.** Tile `(4,0)` needs
  `tx ≈ −275 to −281 px` across all three FOVs — close to the `MaxTranslationPixels=300` bound,
  which explains why it is the case most often `Rejected`/`RuntimeFailure`.
- **The sign of the residual flips between FOV sizes at the same tile.** `(1,3)` needs
  `tx ≈ +31.8 px` at 4192 but `tx ≈ −31.2 / −29.5 px` at 4240/4320. This is the same signature
  Findings §2.3 uses to discriminate between "wrong declared overlap" (residual scales with
  declared overlap: 32/80/160) and "wrong resolution factor" (residual constant: 32/32/32) — just
  observed here via direct Gerber-vs-capture alignment instead of `recoveryEdges` between
  neighboring captures.
- **`rotation_deg` is pinned at exactly ±0.1° (the `MaxAbsRotationDeg` clamp) in nearly every
  `Uncertain` case.** ECC is repeatedly trying to explain translation error with a spurious
  rotation and hitting the clamp — matching Findings §3.2(c) ("mỗi tile ước lượng rotation khác
  nhau" against a real global rotation of only ~0.079°).
- **`scale` sits at 0.94–0.98, not 1.0**, and drops further (to ~0.943) at `(3,0)` specifically for
  the larger 4240/4320 FOVs. This matches Findings §3.2(b)'s prediction: giving ECC an Affine scale
  degree of freedom does not fix a pitch error, it just gives the optimizer a second place to hide
  it.

**Conclusion:** the dominant failure mode in these 3 datasets is very likely the same grid-pitch
mismatch documented in Findings.md, not a weakness of ECC/Phase-Correlation as matching
algorithms. This shapes the whole design below: measure the pitch first (cheap, low-risk,
confirms or refutes the hypothesis with numbers from these exact datasets), then address matching
robustness for large/uncertain seeds (chamfer) and let the measured pitch correct the seed instead
of only reacting to failure with a bigger blind search.

### 2. All three datasets support a full-grid pitch measurement

Each dataset has 80/80 tiles captured (8 rows × 10 columns), matching the grid shape Findings.md
used for its `recoveryEdges` regression (§2.1). The sandbox does not yet have any
Neighbor-Alignment / Phase-Correlation code — `pairs.neighbor_pair` exists (loads two adjacent
captures) but nothing consumes it, and `pyramid_ecc.py` only implements ECC. This is a real gap,
not an oversight to preserve: without it, the sandbox cannot independently verify the pitch
hypothesis at all.

## Scope

Modify or add files only under:

- `tools/ecc_sandbox/`
- `docs/superpowers/`

Preserve everything already implemented in the prior spec
(`2026-08-16-ecc-convergence-and-verification-design.md`): the primary/structural-bootstrap
recovery architecture, independent structural verification, `Verified`/`Uncertain`/`Rejected`
classification, and the single-preprocessing-mode contract. This design adds to that pipeline, it
does not replace any part of it.

Do not create a worktree. All implementation occurs on branch `Ver2_8`.

## Part 1 — Pitch measurement (`pitch_diagnostics.py`)

A new, standalone module. Does not change `pyramid_ecc.py` or any existing file's behavior.

### Overlap ROI cropping

`crop_overlap_roi(image, anchor_tile, target_tile, direction)` ports the exact production ROI
formula from `AlignStitchWorkflowService.cs:1295-1329` (`AnchorRoi`/`TargetRoi`): the overlap
width/height is the intersection of `ExpectedX/Y + Width/Height` between the anchor and target
tile, and the crop is taken from the near edge of each image (e.g. for `direction="right"`, the
anchor crop is the rightmost `w` columns, the target crop is the leftmost `w` columns). Only
`right` and `bottom` directions are needed — each grid edge is measured exactly once; `left`/`top`
are the same edges read in the opposite order and are not separately computed.

### Residual measurement

`phase_correlate_shift(anchor_roi, moving_roi, cfg)` applies `flatten_and_enhance` (already in
`preprocess.py`) to both ROIs, multiplies by a Hann window, and calls `cv2.phaseCorrelate`. Per
Findings §8.2, phase correlation must not be run on thresholded/binarized input — only the
flattened, still-continuous-gradient image.

Because the ROI is defined so that a zero-residual alignment already means "these two crops line
up exactly," the raw shift `cv2.phaseCorrelate` returns **is** the residual — no separate expected
transform needs to be subtracted, unlike Findings' `recoveryEdges` analysis which had to subtract
`expectedTargetToAnchorTransform` because it worked with un-cropped full-frame alignment data.

`neighbor_edges_for_grid(payload)` enumerates all `right` and `bottom` adjacent tile pairs across
the full grid (8×10 → 142 pairs for these datasets).

`measure_pitch(payload, images_dir, extension, cfg)` runs `phase_correlate_shift` over every pair,
groups results by direction, and reports `{direction: {n, mean_dx, mean_dy, std_dx, std_dy}}` —
the same statistic as Findings §2.1's per-direction table, computed independently from these three
datasets rather than from `processing_report.json`.

### CLI and A/B discrimination

```
python pitch_diagnostics.py --payload ... --images ... --ext .bmp --output result_test/pitch_<FOV>.json
```

No `--raster` argument — this stage compares captures to each other, not to the Gerber raster.

When run for all three FOVs, the tool additionally prints the discrimination check from Findings
§2.3: if `mean_dx` (right direction) scales with the declared overlap across the three datasets
(≈32/80/160), that supports hypothesis (A) — wrong declared overlap; if it stays roughly constant
(≈32/32/32) across all three, that supports hypothesis (B) — wrong resolution factor. This does
not fix anything by itself; it produces the number needed to decide what Part 3's
pitch-corrected seeding should actually be set to for a follow-up experiment, and separately, what
Master should be told to fix (outside this repo).

## Part 2 — Chamfer-assisted recovery (`chamfer_alignment.py`)

### Why this and not ICP-skeleton

Directional chamfer matching only needs "edge point close to some edge point," not intensity
gradient (ECC) or stable frequency content (Phase Correlation), so it tolerates broken/thin traces
and large seed error. ICP-skeleton would add a real dependency (`ximgproc` thinning, not in
`opencv-python`) and still converges locally like ECC — it doesn't solve the "seed too far away"
failure mode this design targets, so it is out of scope for this round.

### Candidate search

`find_chamfer_candidates(reference_mono8, moving_mono8, cfg)`:

1. Build a small rotation grid in `[-MaxAbsRotationDeg, +MaxAbsRotationDeg]`, step
   `ChamferAngleStepDeg` (new, default `0.02`) — about 10 angles at the current default rotation
   limit, so this stays cheap.
2. For each angle, rotate the reference image and rebuild
   `coarse_alignment.build_distance_similarity`, then locate translation peaks against the
   moving image's distance-similarity field via the same NCC (`cv2.matchTemplate`) approach
   `find_translation_seeds` already uses. This step is a cheap coarse filter, not the final score.
3. Score every surviving `(angle, dx, dy)` candidate with the **same bidirectional
   distance-transform metric already implemented in `alignment_quality.measure_alignment`** —
   reused directly, not reimplemented, so there is exactly one definition of "how close is close
   enough" in the codebase.
4. Keep the top `ChamferCandidateCount` (new, default `5`) distinct candidates
   (`ChamferSeparationPixels`, new, default `48.0`, mirrors
   `CoarseCandidateSeparationPixels`), as `MovingImage -> ReferenceImage` seed matrices tagged
   `source="chamfer_bootstrap"`.

### Integration into `pyramid_ecc.match()`

After the existing `find_translation_seeds` structural-bootstrap loop, also call
`find_chamfer_candidates` and run each surviving seed through the existing `_run_single_attempt`
(same deduplication against `used_matrices`, same ECC sub-pixel polish, same
`geometry_valid`/`attempts` bookkeeping `structural_bootstrap` seeds already get). No new branch
is added to `classify_candidates` or the verification pipeline — a chamfer-seeded attempt is
indistinguishable, once in the `attempts` list, from any other attempt. Chamfer solves the seeding
problem (getting ECC's basin of convergence to actually contain the right answer); ECC still does
the final sub-pixel refinement, since by that point the seed is close enough for ECC's gradient
descent to work as designed.

## Part 3 — Feeding the measured pitch back into seeding

### Pitch-corrected seeding (primary mechanism)

New optional config, default `0.0` (no behavior change unless set):

```
PitchCorrectionPxPerStepX = 0.0
PitchCorrectionPxPerStepY = 0.0
```

When non-zero, `pairs.direct_pair` adds `column * PitchCorrectionPxPerStepX +
row * PitchCorrectionPxPerStepY` to the raster crop origin before cropping the reference tile —
i.e. it applies Part 1's measured per-step residual directly to the reference crop, so the
matcher starts from a corrected estimate instead of having to search its way out of a
systematic, growing offset. This is a sandbox-only experimental knob for verifying the pitch
hypothesis with real alignment outcomes (does `(4,0)`'s ~280px error collapse to a few px once
corrected?); it is not a proposal to patch Master, whose grid generation lives outside this repo.

### Controlled expanded search (fallback mechanism, only when needed)

If, even with pitch-corrected seeding, a case still has zero `geometry_valid` attempts after
primary + structural bootstrap + chamfer bootstrap, one extra round is allowed:

```
ExpandedSearchFactor = 2.0
ExpandedSearchMaxRounds = 1
MaxTranslationPixelsHardCap = 800.0
```

Round 2 (only, capped by `ExpandedSearchMaxRounds`) reruns structural + chamfer bootstrap with
`MaxTranslationPixels` multiplied by `ExpandedSearchFactor` (capped at
`MaxTranslationPixelsHardCap`), and `CoarseCandidateSeparationPixels` /
`ChamferSeparationPixels` scaled by the same factor, so a wider search does not proportionally
increase the risk of locking onto a repeated PCB pattern. This only triggers on total failure of
round 1, so the default (already fast, already precise) path is unchanged when round 1 succeeds.

## Error Handling

- `crop_overlap_roi` on tiles with degenerate overlap (`w` or `h` <= 0) raises rather than
  returning an empty crop that would silently corrupt the phase-correlation result.
- A single failed neighbor pair in `measure_pitch` (missing capture file, degenerate ROI) is
  recorded and skipped; it does not abort the remaining 141 pairs — same "one failure does not
  abort the batch" contract as the existing structural bootstrap.
- `find_chamfer_candidates` follows the same non-finite/degenerate-input rejection contract as
  `coarse_alignment.find_translation_seeds` (raises `ValueError` for bad input; caller already
  catches this the same way it catches `find_translation_seeds` failures today).
- Round 2 of the expanded search only runs when round 1 produced zero `geometry_valid` attempts;
  it never runs when round 1 already found at least one valid (even if `Uncertain`) candidate, so
  it cannot itself introduce a repeated-pattern false positive into an otherwise-successful case.

## Test Strategy

Python `unittest`, synthetic arrays, no GUI automation, no real dataset required for the tests
themselves:

1. `crop_overlap_roi` matches the C# `AnchorRoi`/`TargetRoi` formula on a small table of
   hand-computed tile coordinates, for all four directions.
2. `phase_correlate_shift` recovers a known injected shift on a synthetic image with edges.
3. `measure_pitch` groups by direction and computes the correct mean/std on a synthetic set of
   edges with a constant injected residual.
4. `find_chamfer_candidates` recovers a known rotation+translation on a synthetic traces image;
   one failed candidate does not stop the remaining candidates (mirrors the existing
   `test_primary_failure_does_not_abort_bootstrap_success` pattern).
5. Pitch-corrected seeding: `direct_pair` applies the configured per-step offset when set, and is
   byte-identical to current behavior when `PitchCorrectionPxPerStepX/Y == 0.0`.
6. Expanded search: round 2 only runs when round 1 has zero `geometry_valid` attempts; it is
   skipped entirely when round 1 already has an eligible candidate.

After unit verification, rerun the same three real datasets (4192/4240/4320) used in the evidence
section above and compare `Verified`/`Uncertain`/`Rejected` counts and, specifically, whether
`(4,0)`'s translation error collapses once `PitchCorrectionPxPerStepX/Y` is set to Part 1's
measured value. This before/after comparison against a concrete case (not just "more tests pass")
is the actual success signal for this design.

## Out of Scope

- Changes to `GerberStitching.Core`, the façade, or the C# production matcher.
- Any change to Master's grid/`ExpectedX/Y` generation — that code lives outside this repo;
  `PitchCorrectionPxPerStepX/Y` is a sandbox experiment knob for verifying the hypothesis with
  real alignment outcomes, not a production fix.
- ICP-on-skeleton matching (would need a new dependency and does not address the seed-distance
  failure mode this design targets).
- Automatic switching between preprocessing modes (unchanged from prior spec).
- A landmark annotation UI or claiming absolute (ground-truth) accuracy (unchanged from prior
  spec).
