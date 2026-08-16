# ECC Affine Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Affine the Python ECC sandbox default, normalize affine X/Y scale through a UI-selectable `median` or `min` policy (default `min`), and clamp signed rotation to `MaxAbsRotationDeg`.

**Architecture:** Let OpenCV optimize through all pyramid levels, then post-process the final `MovingImage -> ReferenceImage` matrix once. For Affine, a pure helper extracts raw scales and rotation, chooses a uniform scale, clamps rotation while preserving its sign, rebuilds the linear 2x2 block as a similarity transform, and preserves translation. For Euclidean, it rebuilds the rotation block only when clamping is required; Translation is unchanged. The UI and log expose the normalization policy and raw/final geometry.

**Tech Stack:** Python 3 · NumPy · OpenCV Python · Tkinter

**Spec:** [`docs/superpowers/specs/2026-08-16-ecc-affine-normalization-design.md`](../specs/2026-08-16-ecc-affine-normalization-design.md)

## Global Constraints

- Work directly on branch `Ver2_7`; do not create a worktree.
- Modify only the Python ECC sandbox and its README; do not modify C# or the production pipeline.
- Preserve the user's unrelated uncommitted changes in `tools/ecc_sandbox/preprocess.py`.
- Preserve the user's intended Affine default already started in `tools/ecc_sandbox/config.py`, while repairing that file's incomplete dictionary edit.
- Do not create a test project or automated tests. Per repository `AGENTS.md`, execution/testing is performed manually by the user.
- Do not run the GUI or claim runtime verification passed.

## File Map

| File | Responsibility |
|---|---|
| `tools/ecc_sandbox/config.py` | Valid default configuration: Affine motion and `min` scale normalization |
| `tools/ecc_sandbox/pyramid_ecc.py` | Extract, normalize, report, and validate final affine geometry |
| `tools/ecc_sandbox/app.py` | Select normalization policy and show raw/final diagnostics |
| `tools/ecc_sandbox/README.md` | Explain sandbox-only behavior and manual checks |

---

### Task 1: Normalize the final affine transform

**Files:**
- Modify: `tools/ecc_sandbox/config.py:5-49`
- Modify: `tools/ecc_sandbox/pyramid_ecc.py:52-142`

**Interfaces:**
- Consumes: configuration keys `EccMotionModel`, `AffineNormalize`, `MaxAbsRotationDeg`
- Produces: `_normalize_ecc_result(matrix, motion_model, mode, max_abs_rotation_deg) -> (matrix, diagnostics)`
- Produces diagnostics: `raw_scale_x`, `raw_scale_y`, `raw_rotation_deg`, `affine_normalize`, `rotation_clamped`

- [ ] **Step 1: Repair and finalize sandbox defaults**

Keep all existing configuration keys inside one valid `DEFAULTS` dictionary and set:

```python
"EccMotionModel": "Affine",
"AffineNormalize": "min",
```

Remove the incomplete `_NORMALIZE_DEFAULT_MAX_SCALE_SPREAD_NOTE` tuple fragment. Keep preprocessing defaults and matcher limits unchanged.

- [ ] **Step 2: Add the pure affine-normalization helper**

Implement directly above `match()`:

```python
def _normalize_ecc_result(matrix, motion_model, mode, max_abs_rotation_deg):
    m = np.asarray(matrix, dtype=float)
    scale_x = math.hypot(m[0, 0], m[1, 0])
    scale_y = math.hypot(m[0, 1], m[1, 1])
    raw_rotation_deg = math.degrees(math.atan2(m[1, 0], m[0, 0]))

    if motion_model == "Affine":
        if mode == "median":
            uniform_scale = (scale_x + scale_y) / 2.0
        elif mode == "min":
            uniform_scale = min(scale_x, scale_y)
        else:
            raise ValueError("AffineNormalize phai la 'median' hoac 'min'.")
    else:
        uniform_scale = scale_x

    if not all(math.isfinite(v) for v in (scale_x, scale_y, raw_rotation_deg, uniform_scale)):
        raise ValueError("Affine result chua gia tri khong huu han.")
    if uniform_scale <= 1e-12:
        raise ValueError("Affine result co scale suy bien.")

    rotation_limit = abs(float(max_abs_rotation_deg))
    rotation_deg = max(-rotation_limit, min(raw_rotation_deg, rotation_limit))
    angle = math.radians(rotation_deg)
    c = math.cos(angle) * uniform_scale
    s = math.sin(angle) * uniform_scale
    should_rebuild = motion_model == "Affine" or abs(rotation_deg - raw_rotation_deg) > 1e-12
    normalized = np.array([
        [c, -s, m[0, 2]],
        [s,  c, m[1, 2]],
        [0.0, 0.0, 1.0],
    ])
    if not should_rebuild:
        normalized = m.copy()
    diagnostics = {
        "raw_scale_x": scale_x,
        "raw_scale_y": scale_y,
        "raw_rotation_deg": raw_rotation_deg,
        "affine_normalize": mode,
        "rotation_clamped": abs(rotation_deg - raw_rotation_deg) > 1e-12,
    }
    return normalized, diagnostics
```

- [ ] **Step 3: Apply normalization after ECC and before result extraction**

Immediately after `moving_to_reference = np.linalg.inv(full_ref_to_mov)`, call the helper for every motion model. It normalizes scale only for Affine, clamps rotation for Affine and Euclidean, and leaves Translation unchanged:

```python
try:
    moving_to_reference, geometry_diagnostics = _normalize_ecc_result(
        moving_to_reference,
        motion_model,
        cfg["AffineNormalize"],
        cfg["MaxAbsRotationDeg"])
except ValueError as ex:
    result["failure_reason"] = "NonFiniteTransform"
    result["message"] = str(ex)
    return result
```

Extract translation, rotation and scale from the resulting authoritative matrix. Merge the diagnostics into `result`.

- [ ] **Step 4: Preserve validation except for obsolete affine rotation rejection**

Keep correlation, translation and scale checks in their current order. Remove the rotation-rejection branch because Affine and Euclidean rotation is now clamped before validation, while Translation has zero rotation.

- [ ] **Step 5: Static review gate**

Inspect the resulting dictionary braces, function indentation, matrix direction and validation order. Do not run Python or the GUI under the repository's manual-test policy.

- [ ] **Step 6: Commit Task 1 only**

```bash
git add tools/ecc_sandbox/config.py tools/ecc_sandbox/pyramid_ecc.py
git commit -m "Normalize final affine ECC transforms"
```

Do not stage `tools/ecc_sandbox/preprocess.py`.

---

### Task 2: Expose normalization in the UI and diagnostics

**Files:**
- Modify: `tools/ecc_sandbox/app.py:177-261`
- Modify: `tools/ecc_sandbox/app.py:363-385`

**Interfaces:**
- Consumes: `config.DEFAULTS["AffineNormalize"]`
- Consumes result diagnostics from Task 1
- Produces: `App.affine_normalize` as a `tk.StringVar`

- [ ] **Step 1: Add the scale-normalization combobox**

Under the motion-model row in `_build_params()`, add a read-only combobox:

```python
frame = ttk.Frame(ep)
frame.pack(fill="x", pady=1)
ttk.Label(frame, text="Affine scale", width=22).pack(side="left")
self.affine_normalize = tk.StringVar(value=self.cfg["AffineNormalize"])
ttk.Combobox(frame, textvariable=self.affine_normalize, width=12, state="readonly",
             values=["median", "min"]).pack(side="left")
```

- [ ] **Step 2: Pass the selected mode to ECC**

Add this entry to the mapping in `current_cfg()`:

```python
"AffineNormalize": self.affine_normalize.get(),
```

- [ ] **Step 3: Report raw and normalized geometry**

For Affine results, print `raw_scale_x`, `raw_scale_y`, the selected policy, raw rotation, final rotation, and a clear clamp marker. Keep the existing final `scale` and `rotation` lines so previous output remains recognizable.

- [ ] **Step 4: Static review gate**

Confirm the combobox has exactly `median` and `min`, defaults from config, and `current_cfg()` passes its value. Confirm `_report()` accesses affine-only diagnostics with `get()` or behind an Affine diagnostic check so Euclidean/Translation do not raise `KeyError`.

- [ ] **Step 5: Commit Task 2 only**

```bash
git add tools/ecc_sandbox/app.py
git commit -m "Expose affine scale normalization in ECC sandbox"
```

---

### Task 3: Document behavior and prepare manual verification

**Files:**
- Modify: `tools/ecc_sandbox/README.md`

**Interfaces:**
- Consumes: final UI and result behavior from Tasks 1-2
- Produces: user-facing explanation and verification checklist

- [ ] **Step 1: Update sandbox behavior documentation**

Document these exact points:

- Sandbox default motion model is Affine even though the current C# default remains Euclidean.
- `Affine scale` provides `median` and `min`, defaulting to `min`.
- `median` means `(scaleX + scaleY) / 2` for the two extracted scales.
- `min` chooses the smaller scale.
- Normalization removes shear, preserves translation, and clamps signed rotation to `MaxAbsRotationDeg`.
- Euclidean retains its previous transform when within the limit and is rebuilt only when rotation is clamped; Translation is unchanged.

- [ ] **Step 2: Add the manual verification cases**

Add checks for default selections, distinct scale X/Y under both policies, positive and negative rotation clamp, preview/log matrix consistency, and unchanged Euclidean behavior.

- [ ] **Step 3: Final static diff review**

Run read-only inspection commands only:

```bash
git diff --check
git diff -- tools/ecc_sandbox/config.py tools/ecc_sandbox/pyramid_ecc.py tools/ecc_sandbox/app.py tools/ecc_sandbox/README.md
git status --short
```

Confirm `tools/ecc_sandbox/preprocess.py` remains unstaged and its diff is unchanged from the initial user work.

- [ ] **Step 4: Commit documentation only**

```bash
git add tools/ecc_sandbox/README.md
git commit -m "Document affine ECC normalization controls"
```

- [ ] **Step 5: User verification gate**

Ask the user to run `python tools/ecc_sandbox/app.py` and perform the six cases in the design spec. Report implementation as statically reviewed, not runtime-tested.
