# ECC Convergence Recovery and Alignment Verification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Recover ECC from poor identity initialization, reject repeated-pattern false convergence with independent structural evidence, and reproduce the 14 requested real-data cases.

**Architecture:** Keep the selected grayscale preprocessing output as the only ECC input. Add a bounded structural translation search that supplies multiple initialization seeds, run the existing pyramid ECC independently for every seed, then rank and classify finite candidates using edge coverage, chamfer distance, and ambiguity margin computed on contrast-adjusted verification images.

**Tech Stack:** Python 3 · NumPy · OpenCV Python 4.x · Tkinter · Pillow · `unittest`

**Spec:** [`docs/superpowers/specs/2026-08-16-ecc-convergence-and-verification-design.md`](../specs/2026-08-16-ecc-convergence-and-verification-design.md)

## Global Constraints

- Work directly on branch `Ver2_8`; do not create a worktree.
- Modify only `tools/ecc_sandbox/` and `docs/superpowers/`; do not modify the C# production pipeline.
- Preserve and integrate the user's uncommitted Otsu, morphology, and `addWeighted(0.7, 0.3)` preprocessing change.
- Exactly one of `FlattenAndEnhance` and `ToBinaryTraces` is selected; never run both in one preprocessing path.
- The structural representation supplies ECC initialization only; ECC always consumes the selected preprocessing output.
- Keep the existing Affine `median`/`min` normalization, signed rotation clamp, and `MovingImage -> ReferenceImage` result direction.
- Automatic results are `Verified`, `Uncertain`, or `Rejected`; never claim absolute correctness without trusted landmarks and TRE.
- Tests use synthetic arrays and do not automate the Tkinter GUI.
- Real-data verification uses the user-supplied seven coordinates, both preprocessing modes, `Contrast=150`, and saves under the supplied `result_test` directory.

## File Map

| File | Responsibility |
|---|---|
| `tools/ecc_sandbox/config.py` | Recovery and verification defaults |
| `tools/ecc_sandbox/preprocess.py` | Mutually exclusive preprocessing contract and preserved weighted-binary mode |
| `tools/ecc_sandbox/coarse_alignment.py` | Structural fields and bounded top-K translation seeds |
| `tools/ecc_sandbox/alignment_quality.py` | Edge metrics, ambiguity classification, and TRE statistics |
| `tools/ecc_sandbox/pyramid_ecc.py` | One ECC attempt plus multi-candidate orchestration |
| `tools/ecc_sandbox/app.py` | Exclusive preprocessing UI and diagnostic reporting |
| `tools/ecc_sandbox/experiment_runner.py` | Reproducible 14-case non-GUI runner |
| `tools/ecc_sandbox/tests/` | Synthetic regression tests |
| `tools/ecc_sandbox/README.md` | Behavior, limitations, commands, and output contract |

---

### Task 1: Enforce the preprocessing contract and add defaults

**Files:**
- Modify: `tools/ecc_sandbox/config.py`
- Modify: `tools/ecc_sandbox/preprocess.py`
- Create: `tools/ecc_sandbox/tests/__init__.py`
- Create: `tools/ecc_sandbox/tests/test_preprocess.py`

**Interfaces:**
- Produces: `build_variants(mono8, cfg, mode) -> dict[str, np.ndarray]`
- Produces config keys named exactly as in the approved spec.

- [ ] **Step 1: Write failing preprocessing tests**

Create tests that patch `flatten_and_enhance` and `to_binary_traces` with distinct constant arrays, then assert:

```python
flatten = preprocess.build_variants(image, config.DEFAULTS, "FlattenAndEnhance")
binary = preprocess.build_variants(image, config.DEFAULTS, "ToBinaryTraces")
self.assertTrue(np.array_equal(flatten["final"], flatten["flattened"]))
self.assertNotIn("binary", flatten)
self.assertTrue(np.array_equal(binary["final"], binary["binary"]))
self.assertNotIn("flattened", binary)
with self.assertRaisesRegex(ValueError, "Preprocess mode"):
    preprocess.build_variants(image, config.DEFAULTS, "Both")
```

Also test `to_binary_traces` on a fixed 32×32 input by recomputing Otsu, close, and
`cv2.addWeighted(input, 0.7, morph, 0.3, 0.0)` and asserting exact array equality.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
python -m unittest tools.ecc_sandbox.tests.test_preprocess -v
```

Expected: failure because `build_variants` does not accept the mode-string contract.

- [ ] **Step 3: Implement the exclusive mode API and defaults**

Change `build_variants` to:

```python
def build_variants(mono8, cfg, mode):
    out = {"raw": mono8}
    contrast = increase_contrast(mono8, cfg["Contrast"])
    out["contrast"] = contrast
    if mode == "FlattenAndEnhance":
        out["flattened"] = flatten_and_enhance(
            contrast, cfg["BackgroundSigma"], cfg["ClaheClipLimit"], cfg["ClaheTile"])
        out["final"] = out["flattened"]
    elif mode == "ToBinaryTraces":
        out["binary"] = to_binary_traces(
            contrast, cfg["AdaptiveBlockSize"], cfg["AdaptiveC"],
            cfg["CloseKernel"], cfg["CloseIterations"])
        out["final"] = out["binary"]
    else:
        raise ValueError("Preprocess mode phai la FlattenAndEnhance hoac ToBinaryTraces.")
    return out
```

Keep the user's Otsu/close/weighted implementation and update its obsolete “view only” docstring.
Add the exact recovery and verification defaults from the spec to `config.DEFAULTS`.

- [ ] **Step 4: Verify GREEN**

Run the focused test again and require all cases to pass.

- [ ] **Step 5: Commit Task 1**

Stage only `config.py`, `preprocess.py`, and the two test files. Commit:

```text
Enforce exclusive ECC preprocessing modes
```

---

### Task 2: Generate bounded structural translation seeds

**Files:**
- Create: `tools/ecc_sandbox/coarse_alignment.py`
- Create: `tools/ecc_sandbox/tests/test_coarse_alignment.py`

**Interfaces:**
- Produces: `build_distance_similarity(mono8, cfg) -> np.ndarray`
- Produces: `find_translation_seeds(reference_mono8, moving_mono8, cfg) -> list[dict]`
- Each seed dictionary contains `matrix`, `source`, and `coarse_score`; `matrix` is 3×3 `MovingImage -> ReferenceImage`.

- [ ] **Step 1: Write failing direction, bound, and NMS tests**

Use a synthetic image with non-repeated circles and lines, create moving with
`cv2.warpAffine(reference, [[1, 0, -24], [0, 1, 16]], ...)`, and assert that the best returned seed
maps moving back to reference within four full-resolution pixels. Add a second test with two separated
copies of a motif and assert no two retained peaks are closer than
`CoarseCandidateSeparationPixels`. Add a bound test asserting every `abs(tx)` and `abs(ty)` is no larger
than `MaxTranslationPixels`.

- [ ] **Step 2: Run the focused test and verify RED**

Run `python -m unittest tools.ecc_sandbox.tests.test_coarse_alignment -v`.
Expected: import failure because `coarse_alignment.py` does not exist.

- [ ] **Step 3: Implement structural fields and top-K search**

Implement Canny edges, capped `cv2.distanceTransform`, downsampling, reference padding, and
`cv2.matchTemplate(..., cv2.TM_CCOEFF_NORMED)`. Convert peak location to full-resolution translation:

```python
tx = (peak_x - coarse_bound_x) * downsample
ty = (peak_y - coarse_bound_y) * downsample
matrix = np.array([[1.0, 0.0, tx], [0.0, 1.0, ty], [0.0, 0.0, 1.0]])
```

Mask each selected peak with a filled zero circle before choosing the next peak. Return only finite,
in-bound, non-duplicate seeds sorted by descending `coarse_score`.

- [ ] **Step 4: Verify GREEN**

Run the focused test and require direction, bound, and NMS tests to pass.

- [ ] **Step 5: Commit Task 2**

Commit `coarse_alignment.py` and its test as:

```text
Add bounded structural ECC bootstrap
```

---

### Task 3: Add independent structural verification and TRE

**Files:**
- Create: `tools/ecc_sandbox/alignment_quality.py`
- Create: `tools/ecc_sandbox/tests/test_alignment_quality.py`

**Interfaces:**
- Produces: `measure_alignment(reference, moving, matrix, cfg) -> dict`
- Produces: `classify_candidates(candidates, cfg) -> dict`
- Produces: `compute_tre(matrix, moving_points, reference_points) -> dict`

- [ ] **Step 1: Write failing quality tests**

Create synthetic asymmetric edge structures and assert an exact transform has higher coverage and lower
chamfer than a displaced transform. Assert blank images return
`{"eligible": False, "reason": "InsufficientStructuralFeatures"}`. Construct two candidate dictionaries
with coverage `0.80` and `0.79`, translations separated by 20 pixels, and assert classification is
`Uncertain` with `RepeatedPatternAmbiguous`; change runner-up coverage to `0.70` and assert `Verified`.

For TRE, assert identity corresponding points produce RMS/median/p95/max zero and that a known translated
matrix produces the exact Euclidean errors calculated with NumPy.

- [ ] **Step 2: Run the focused test and verify RED**

Run `python -m unittest tools.ecc_sandbox.tests.test_alignment_quality -v`.
Expected: import failure because `alignment_quality.py` does not exist.

- [ ] **Step 3: Implement directional metrics and classification**

Use Canny edges, warp the moving edge mask with nearest-neighbor interpolation, and calculate directional
distances by sampling the opposite distance transform at edge pixels. Return:

```python
{
    "eligible": bool,
    "reference_edge_coverage": float,
    "moving_edge_coverage": float,
    "symmetric_edge_coverage": float,
    "symmetric_chamfer_p95": float,
    "reason": str_or_none,
}
```

Use the harmonic mean for coverage, the mean of directional p95 distances for chamfer, the exact defaults
from the spec, and deterministic ranking `(coverage desc, chamfer asc, raw_score desc)`. Preserve the best
finite matrix for `Uncertain`, but set `success=False`; set `success=True` only for `Verified`.

Implement TRE by homogeneous point transformation and NumPy percentile/statistics. Reject mismatched,
empty, or non-finite landmark arrays with `ValueError`.

- [ ] **Step 4: Verify GREEN**

Run the focused test and require all metric, ambiguity, and TRE tests to pass.

- [ ] **Step 5: Commit Task 3**

Commit the quality module and its test as:

```text
Verify ECC candidates with structural evidence
```

---

### Task 4: Orchestrate multi-candidate ECC without aborting on level failure

**Files:**
- Modify: `tools/ecc_sandbox/pyramid_ecc.py`
- Create: `tools/ecc_sandbox/tests/test_pyramid_ecc.py`

**Interfaces:**
- Produces: `_run_single_attempt(reference, moving, cfg, initial, source) -> dict`
- Changes: `match(reference, moving, cfg, initial_moving_to_reference=None, verification_reference=None, verification_moving=None) -> dict`
- Consumes: `coarse_alignment.find_translation_seeds` and `alignment_quality.measure_alignment/classify_candidates`.

- [ ] **Step 1: Write failing orchestration tests**

Retain regression tests for `_normalize_ecc_result`. Patch only the native-boundary
`_run_single_attempt` so the primary attempt returns `RuntimeFailure` and the first bootstrap attempt
returns a valid candidate. Assert `match` continues, exposes both attempts, and returns the later matrix.
Add a case where two valid distinct candidates have near-equal structural metrics and assert
`verification_status == "Uncertain"` and `success is False`. Add a case where all attempts fail and assert
the final message retains the level number and OpenCV error from the most informative failure.

- [ ] **Step 2: Run the focused test and verify RED**

Run `python -m unittest tools.ecc_sandbox.tests.test_pyramid_ecc -v`.
Expected: failure because `_run_single_attempt` and verification status do not exist.

- [ ] **Step 3: Extract one-attempt behavior without changing its math**

Move the current pyramid loop, inversion, Affine normalization, and geometry/correlation checks into
`_run_single_attempt`. Record `source`, seed matrix, per-level values, failure level, and full error text.
Do not catch one candidate's `cv2.error` outside this function.

- [ ] **Step 4: Implement candidate orchestration**

`match` runs identity or the supplied initial transform first. If the primary is not independently
eligible, append structural seeds, skip seed duplicates within one pixel, and call `_run_single_attempt`
for each. Measure valid candidates on `verification_reference/moving` when supplied, otherwise on ECC
inputs. Pass candidates to `classify_candidates`, preserve all attempt diagnostics, and return the winning
normalized matrix for `Verified` or `Uncertain`.

- [ ] **Step 5: Verify GREEN and full unit suite**

Run:

```powershell
python -m unittest discover -s tools/ecc_sandbox/tests -v
```

Require all tests to pass and no unhandled OpenCV exception.

- [ ] **Step 6: Commit Task 4**

Commit the matcher and test as:

```text
Recover ECC with verified multi-start candidates
```

---

### Task 5: Wire the UI and reproducible experiment runner

**Files:**
- Modify: `tools/ecc_sandbox/app.py`
- Create: `tools/ecc_sandbox/experiment_runner.py`
- Create: `tools/ecc_sandbox/tests/test_experiment_runner.py`

**Interfaces:**
- UI produces `self.preprocess_mode: tk.StringVar` with exactly two values.
- Runner exports `REQUESTED_COORDINATES` and `run_experiment(payload_path, images_dir, raster_path, extension, output_dir) -> list[dict]`.

- [ ] **Step 1: Write failing runner manifest/output tests**

Assert `REQUESTED_COORDINATES` equals the approved ordered seven-coordinate list. Patch pair loading and
matching at their I/O boundaries, run into a temporary directory, and assert exactly 14 rows are written
to JSON and CSV, with each coordinate represented once per preprocessing mode. Assert an empty extension
is normalized to `.bmp`.

- [ ] **Step 2: Run the focused test and verify RED**

Run `python -m unittest tools.ecc_sandbox.tests.test_experiment_runner -v`.
Expected: import failure because the runner does not exist.

- [ ] **Step 3: Replace preprocessing checkboxes and pass verification inputs**

Use one read-only combobox with values `FlattenAndEnhance` and `ToBinaryTraces`. Call:

```python
ref_v = pre.build_variants(reference, c, self.preprocess_mode.get())
mov_v = pre.build_variants(moving, c, self.preprocess_mode.get())
result = ecc.match(ref_v["final"], mov_v["final"], c,
                   verification_reference=ref_v["contrast"],
                   verification_moving=mov_v["contrast"])
```

Remove the obsolete binary “view only” warning. Extend the log with attempts, seed source, structural
metrics, ambiguity margin, and the exact three-state conclusion.

- [ ] **Step 4: Implement the runner and overlays**

Use the fixed coordinates and both modes, clone `config.DEFAULTS`, set `Contrast=150.0`, and leave every
other value unchanged. Serialize NumPy matrices and scalar types to JSON-native values. Write fixed-name
JSON/CSV files and `r{row}_c{col}_{mode}_before.jpg` plus `_after.jpg` when a matrix exists. Do not delete
or enumerate unrelated output files.

- [ ] **Step 5: Verify GREEN and full unit suite**

Run the focused runner test, then the full unittest discovery command.

- [ ] **Step 6: Commit Task 5**

Commit UI, runner, and runner test as:

```text
Expose verified ECC recovery in the sandbox
```

---

### Task 6: Document and run the supplied real-data experiment

**Files:**
- Modify: `tools/ecc_sandbox/README.md`
- External outputs: `H:/005_Project/AOI_2026_07_imp/20260813/result_test/`

**Interfaces:**
- Consumes the runner CLI paths documented in the spec.
- Produces fixed-name JSON, CSV, and overlay evidence.

- [ ] **Step 1: Update documentation**

Replace the obsolete binary warning with the exclusive-mode contract. Document recovery behavior,
three-state verification semantics, why ECC score is not absolute proof, TRE ground truth, runner command,
and every output filename pattern.

- [ ] **Step 2: Run static and unit verification**

Run `git diff --check` and the full unittest discovery command with the compatible OpenCV 4 environment.
Record the command, Python/OpenCV versions, pass count, and exit code.

- [ ] **Step 3: Run all 14 requested cases**

Run `experiment_runner.py` with:

```text
payload  = H:/005_Project/AOI_2026_07_imp/20260813/sample_4240_o207.json
images   = H:/005_Project/AOI_2026_07_imp/20260813/20260813 Fov Test 4240x4240
raster   = H:/005_Project/AOI_2026_07_imp/20260813/2-2 Gerber fix black.tiff
ext      = ""
output   = H:/005_Project/AOI_2026_07_imp/20260813/result_test
```

Confirm JSON and CSV each contain 14 completed records and every record has a status, attempts,
verification metrics or a specific insufficient-feature/failure reason.

- [ ] **Step 4: Inspect real overlays and summarize limitations**

Visually inspect representative `Verified`, `Uncertain`, and `Rejected` overlays. Report automatic
confidence honestly; do not relabel any result “absolute” without landmarks.

- [ ] **Step 5: Commit documentation**

Commit only `README.md` as:

```text
Document ECC recovery and verification workflow
```

- [ ] **Step 6: Final repository review**

Check branch, commits, `git status --short`, and scoped diff. Confirm `.claude/` remains unrelated and
unstaged. Confirm the user's preprocessing change is present in the committed implementation rather than
lost or overwritten.
