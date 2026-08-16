# Pitch Diagnostics and Chamfer-Assisted Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Python ECC sandbox (`tools/ecc_sandbox/`) an independent pitch-measurement tool, a chamfer-based seeding matcher for thin/broken traces, an opt-in pitch-corrected seeding path, a controlled expanded-search fallback, a full-grid regression mode, a scale/translation consistency report, and live per-stage logging in the GUI.

**Architecture:** Two new standalone modules (`pitch_diagnostics.py`, `chamfer_alignment.py`) that reuse existing building blocks (`preprocess.flatten_and_enhance`, `coarse_alignment.build_distance_similarity`, `alignment_quality.measure_alignment`) rather than duplicating them. `pyramid_ecc.match()` gains a chamfer seed source, a controlled second search round, and an optional progress callback — its existing `attempts`/`classify_candidates` pipeline is otherwise untouched. `pairs.direct_pair` gains an opt-in additive pitch-correction offset. `experiment_runner.py` gains an `--all-tiles` mode and a consistency-summary step. `app.py` wires the new callback into its log.

**Tech Stack:** Python 3.12, OpenCV (`opencv-python`, already a dependency — no new packages), NumPy, `unittest` + `unittest.mock`.

## Global Constraints

- Modify or add files only under `tools/ecc_sandbox/` and `docs/superpowers/` (spec Scope section).
- Do not create a git worktree; implement directly on branch `Ver2_8` (spec Scope section).
- No new third-party dependency — everything must be buildable from `opencv-python`, `numpy`,
  `matplotlib`, `Pillow` (the existing `requirements.txt`). No `scipy`, no `opencv-contrib-python`.
- Every new config key defaults to a value that reproduces today's behavior exactly (spec Part 1/2/3):
  `PitchCorrectionPxPerStepX = 0.0`, `PitchCorrectionPxPerStepY = 0.0`, `ExpandedSearchMaxRounds`
  only triggers on total failure, `on_stage` defaults to `None`.
- Reuse existing metric/field implementations instead of duplicating them: chamfer scoring reuses
  `alignment_quality.measure_alignment`'s distance-transform logic; phase-correlation preprocessing
  reuses `preprocess.flatten_and_enhance`; chamfer's coarse translation filter reuses
  `coarse_alignment.build_distance_similarity`.
- Follow the file's existing convention: Vietnamese inline comments only where the *why* is
  non-obvious (see `pyramid_ecc.py`, `coarse_alignment.py` for the house style), PascalCase for
  config dict keys, snake_case for functions/locals.
- `AGENTS.md` §4: do not add a Python test *runner framework* beyond the existing `unittest`
  convention, and do not claim results from the real 3-FOV datasets without actually running the
  commands and reading their output.

---

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `tools/ecc_sandbox/pitch_diagnostics.py` | new | ROI cropping, phase-correlation residual, full-grid pitch measurement, CLI + A/B discrimination print |
| `tools/ecc_sandbox/tests/test_pitch_diagnostics.py` | new | Unit tests for the above |
| `tools/ecc_sandbox/chamfer_alignment.py` | new | Rotation+translation candidate search scored by true chamfer distance |
| `tools/ecc_sandbox/tests/test_chamfer_alignment.py` | new | Unit tests for the above |
| `tools/ecc_sandbox/pyramid_ecc.py` | modify | Add chamfer bootstrap call, controlled expanded-search round, `on_stage` callback |
| `tools/ecc_sandbox/tests/test_pyramid_ecc.py` | modify | Add tests for the three behaviors above |
| `tools/ecc_sandbox/config.py` | modify | Add `ChamferAngleStepDeg`, `ChamferCandidateCount`, `ChamferSeparationPixels`, `PitchCorrectionPxPerStepX/Y`, `ExpandedSearchFactor`, `ExpandedSearchMaxRounds`, `MaxTranslationPixelsHardCap` to `DEFAULTS` |
| `tools/ecc_sandbox/pairs.py` | modify | `direct_pair` applies pitch-correction offset |
| `tools/ecc_sandbox/tests/test_pairs.py` | new | Unit tests for `direct_pair`'s offset behavior (no test file exists for `pairs.py` today) |
| `tools/ecc_sandbox/alignment_quality.py` | modify | Add `summarize_consistency` |
| `tools/ecc_sandbox/tests/test_alignment_quality.py` | modify | Add tests for `summarize_consistency` |
| `tools/ecc_sandbox/experiment_runner.py` | modify | Add `--all-tiles` flag, wire `summarize_consistency` into the JSON output |
| `tools/ecc_sandbox/tests/test_experiment_runner.py` | modify | Add a test for `--all-tiles` |
| `tools/ecc_sandbox/app.py` | modify | Pass `on_stage` callback into `ecc.match()`, add `_on_stage` method |

No file above needs splitting further — each new module stays under ~150 lines by construction
(mirrors `coarse_alignment.py`'s size), and each modified file gets a small, additive change.

---

## Task 1: Overlap ROI cropping (`pitch_diagnostics.py` part 1)

**Files:**
- Create: `tools/ecc_sandbox/pitch_diagnostics.py`
- Test: `tools/ecc_sandbox/tests/test_pitch_diagnostics.py`

**Interfaces:**
- Consumes: nothing from other new modules yet (pure geometry).
- Produces: `crop_overlap_roi(image, anchor_tile, target_tile, direction, is_anchor_image) ->
  np.ndarray`, used by Task 2's `measure_pitch`. `anchor_tile`/`target_tile` are the same tile
  dicts `pairs.load_payload` already produces (keys: `OrderIndex`, `Row`, `Column`, `ExpectedX`,
  `ExpectedY`, `Width`, `Height`). `direction` is one of `"right"`, `"bottom"` (the function also
  accepts `"left"`/`"top"` for symmetry, but only `"right"`/`"bottom"` are ever called with real
  data). `is_anchor_image` (`bool`) is required, not inferred: the same `direction` value produces
  a *different* crop depending on whether `image` is the anchor's capture or the target's capture
  (for `"right"`, the anchor crops its own rightmost columns while the target crops its own
  leftmost columns) — there is no way to tell those two cases apart from pixel content, so the
  caller must say which one `image` is.

- [ ] **Step 1: Write the failing test**

Create `tools/ecc_sandbox/tests/test_pitch_diagnostics.py`:

```python
import os
import sys
import unittest

import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import pitch_diagnostics


def _tile(order, row, column, expected_x, expected_y, width=100, height=80):
    return {"OrderIndex": order, "Row": row, "Column": column,
            "ExpectedX": expected_x, "ExpectedY": expected_y,
            "Width": width, "Height": height}


class OverlapRoiTests(unittest.TestCase):
    def test_right_direction_takes_near_edge_strips_matching_overlap_width(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(1, 0, 1, 90, 0)
        anchor_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100)
        target_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100) + 1

        anchor_roi = pitch_diagnostics.crop_overlap_roi(
            anchor_image, anchor, target, "right", True)
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "right", False)

        # overlap width = min(0+100, 90+100) - max(0, 90) = 100 - 90 = 10
        self.assertEqual(anchor_roi.shape, (80, 10))
        self.assertEqual(target_roi.shape, (80, 10))
        # anchor crop is the rightmost 10 columns, target crop is the leftmost 10 columns
        np.testing.assert_array_equal(anchor_roi, anchor_image[:, 90:100])
        np.testing.assert_array_equal(target_roi, target_image[:, 0:10])

    def test_bottom_direction_takes_near_edge_strips_matching_overlap_height(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(2, 1, 0, 0, 65)
        anchor_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100)
        target_image = np.arange(80 * 100, dtype=np.uint8).reshape(80, 100) + 1

        anchor_roi = pitch_diagnostics.crop_overlap_roi(
            anchor_image, anchor, target, "bottom", True)
        target_roi = pitch_diagnostics.crop_overlap_roi(
            target_image, anchor, target, "bottom", False)

        # overlap height = min(0+80, 65+80) - max(0, 65) = 80 - 65 = 15
        self.assertEqual(anchor_roi.shape, (15, 100))
        self.assertEqual(target_roi.shape, (15, 100))
        np.testing.assert_array_equal(anchor_roi, anchor_image[65:80, :])
        np.testing.assert_array_equal(target_roi, target_image[0:15, :])

    def test_degenerate_overlap_raises(self):
        anchor = _tile(0, 0, 0, 0, 0)
        target = _tile(1, 0, 5, 500, 0)  # far apart, no real overlap
        image = np.zeros((80, 100), dtype=np.uint8)
        with self.assertRaises(ValueError):
            pitch_diagnostics.crop_overlap_roi(image, anchor, target, "right", True)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: FAIL with `ModuleNotFoundError: No module named 'pitch_diagnostics'`

- [ ] **Step 3: Write minimal implementation**

Create `tools/ecc_sandbox/pitch_diagnostics.py`:

```python
"""Do pitch luoi thuc te bang Phase Correlation giua cac anh chup ke nhau.

Port cong thuc ROI overlap tu AlignStitchWorkflowService.cs:1295-1329 (AnchorRoi/TargetRoi):
overlap = giao cua ExpectedX/Y + Width/Height giua hai tile ke, cat o mep gan nhau cua moi anh.
Neu ROI da cat dung theo overlap khai bao, shift do duoc tu phase correlation CHINH LA residual --
khong can tru gi them (khac voi recoveryEdges trong Findings, phai tru expectedTargetToAnchorTransform
vi lam viec tren full-frame chua cat).
"""
import numpy as np


def _overlap_extent(anchor_tile, target_tile, axis):
    """axis: 'x' hoac 'y'. Tra ve (w_hoac_h) phan giao giua hai tile theo truc do."""
    expected_key = "ExpectedX" if axis == "x" else "ExpectedY"
    size_key = "Width" if axis == "x" else "Height"
    anchor_start = anchor_tile[expected_key]
    target_start = target_tile[expected_key]
    anchor_end = anchor_start + anchor_tile[size_key]
    target_end = target_start + target_tile[size_key]
    extent = min(anchor_end, target_end) - max(anchor_start, target_start)
    return extent


def crop_overlap_roi(image, anchor_tile, target_tile, direction, is_anchor_image):
    """Cat dai overlap o mep gan nhau cua `image`, theo dung cong thuc AnchorRoi/TargetRoi.

    Cung mot `direction` cho ra crop KHAC NHAU tuy `image` thuoc anchor hay target -- vi du
    "right": anchor (ben trai) crop canh PHAI cua chinh no, target (ben phai) crop canh TRAI
    cua chinh no. Khong the tu doan duoc dieu nay tu noi dung pixel, nen `is_anchor_image` la
    tham so BAT BUOC, khong suy luan. Goi ham nay 2 lan (mot voi is_anchor_image=True cho anh
    anchor, mot voi is_anchor_image=False cho anh target) de co ca hai ROI.
    """
    if direction not in ("right", "left", "bottom", "top"):
        raise ValueError("direction phai la right/left/bottom/top.")

    height, width = image.shape[:2]
    if direction in ("right", "left"):
        w = int(round(_overlap_extent(anchor_tile, target_tile, "x")))
        if w <= 0 or w > width:
            raise ValueError(
                "Overlap ngang khong hop le (w=%d) giua tile %s va %s." %
                (w, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))
        anchor_crops_right_edge = (direction == "right")
        use_right_edge = (anchor_crops_right_edge if is_anchor_image
                          else not anchor_crops_right_edge)
        return image[:, width - w:width] if use_right_edge else image[:, 0:w]

    h = int(round(_overlap_extent(anchor_tile, target_tile, "y")))
    if h <= 0 or h > height:
        raise ValueError(
            "Overlap doc khong hop le (h=%d) giua tile %s va %s." %
            (h, anchor_tile.get("OrderIndex"), target_tile.get("OrderIndex")))
    anchor_crops_bottom_edge = (direction == "bottom")
    use_bottom_edge = (anchor_crops_bottom_edge if is_anchor_image
                       else not anchor_crops_bottom_edge)
    return image[height - h:height, :] if use_bottom_edge else image[0:h, :]
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/pitch_diagnostics.py tools/ecc_sandbox/tests/test_pitch_diagnostics.py
git commit -m "Add overlap ROI cropping for pitch diagnostics"
```

---

## Task 2: Phase-correlation residual + full-grid pitch measurement + CLI

**Files:**
- Modify: `tools/ecc_sandbox/pitch_diagnostics.py`
- Test: `tools/ecc_sandbox/tests/test_pitch_diagnostics.py`

**Interfaces:**
- Consumes: `crop_overlap_roi` (Task 1); `preprocess.flatten_and_enhance(mono8, bg_sigma, clip_limit,
  clahe_tile)` (existing, `tools/ecc_sandbox/preprocess.py:20`); `pairs.load_payload`,
  `pairs.load_captured` (existing, `tools/ecc_sandbox/pairs.py`); `config.DEFAULTS` (existing).
- Produces: `phase_correlate_shift(anchor_roi, moving_roi, cfg) -> (dx, dy)`;
  `neighbor_edges_for_grid(payload) -> list[(anchor_tile, target_tile, direction)]`;
  `measure_pitch(payload, images_dir, extension, cfg) -> dict[str, dict]` with shape
  `{"right": {"n": int, "mean_dx": float, "mean_dy": float, "std_dx": float, "std_dy": float},
  "bottom": {...}}`; CLI entry point `main()`.

- [ ] **Step 1: Write the failing test**

Append to `tools/ecc_sandbox/tests/test_pitch_diagnostics.py` (inside the same file, add new test
classes before the `if __name__ == "__main__":` line):

```python
import json
import tempfile

import cv2


def _grid_payload(rows, cols, width=120, height=100):
    tiles = []
    order = 0
    for row in range(rows):
        for column in range(cols):
            tiles.append({
                "OrderIndex": order, "Row": row, "Column": column,
                "ExpectedX": column * (width - 20), "ExpectedY": row * (height - 15),
                "Width": width, "Height": height,
            })
            order += 1
    return {"tiles": tiles, "image_width": width, "image_height": height,
            "sample_path": None, "overlap_x": None, "overlap_y": None}


def _textured_image(width, height, seed):
    rng = np.random.RandomState(seed)
    image = (rng.rand(height, width) * 40 + 30).astype(np.uint8)
    cv2.circle(image, (width // 3, height // 2), 8, 220, -1)
    cv2.rectangle(image, (width // 2, height // 4), (width // 2 + 15, height // 4 + 12), 200, -1)
    return image


class PhaseCorrelateShiftTests(unittest.TestCase):
    def _cfg(self):
        values = dict()
        values.update({"BackgroundSigma": 15.0, "ClaheClipLimit": 2.0, "ClaheTile": 8})
        return values

    def test_recovers_known_injected_shift(self):
        base = _textured_image(160, 140, seed=1)
        shift = (3.0, -2.0)
        matrix = np.array([[1.0, 0.0, shift[0]], [0.0, 1.0, shift[1]]], dtype=np.float32)
        shifted = cv2.warpAffine(base, matrix, (160, 140), flags=cv2.INTER_LINEAR,
                                 borderMode=cv2.BORDER_REPLICATE)

        dx, dy = pitch_diagnostics.phase_correlate_shift(base, shifted, self._cfg())

        self.assertAlmostEqual(dx, shift[0], delta=0.6)
        self.assertAlmostEqual(dy, shift[1], delta=0.6)


class NeighborEdgesForGridTests(unittest.TestCase):
    def test_enumerates_right_and_bottom_edges_exactly_once_each(self):
        payload = _grid_payload(rows=3, cols=4)

        edges = pitch_diagnostics.neighbor_edges_for_grid(payload)

        # 3 rows x 3 right-edges per row + 2 bottom-edges per column x 4 columns
        right_edges = [e for e in edges if e[2] == "right"]
        bottom_edges = [e for e in edges if e[2] == "bottom"]
        self.assertEqual(len(right_edges), 3 * 3)
        self.assertEqual(len(bottom_edges), 2 * 4)
        for anchor, target, direction in edges:
            if direction == "right":
                self.assertEqual(anchor["Row"], target["Row"])
                self.assertEqual(anchor["Column"] + 1, target["Column"])
            else:
                self.assertEqual(anchor["Column"], target["Column"])
                self.assertEqual(anchor["Row"] + 1, target["Row"])


class MeasurePitchTests(unittest.TestCase):
    def test_groups_by_direction_and_reports_constant_injected_residual(self):
        payload = _grid_payload(rows=2, cols=2, width=120, height=100)
        residual = (4.0, 0.0)

        with tempfile.TemporaryDirectory() as images_dir:
            base = _textured_image(120, 100, seed=7)
            matrix = np.array([[1.0, 0.0, residual[0]], [0.0, 1.0, residual[1]]], dtype=np.float32)
            shifted = cv2.warpAffine(base, matrix, (120, 100), flags=cv2.INTER_LINEAR,
                                     borderMode=cv2.BORDER_REPLICATE)
            # order 0=(0,0), 1=(0,1), 2=(1,0), 3=(1,1); every tile after the first is `shifted`
            # by the same constant residual relative to its left/top neighbor.
            for order, image in enumerate([base, shifted, shifted, base]):
                cv2.imwrite(os.path.join(images_dir, "%d.bmp" % order), image)

            cfg = dict()
            cfg.update({"BackgroundSigma": 15.0, "ClaheClipLimit": 2.0, "ClaheTile": 8})
            result = pitch_diagnostics.measure_pitch(payload, images_dir, ".bmp", cfg)

        self.assertIn("right", result)
        self.assertIn("bottom", result)
        self.assertEqual(result["right"]["n"], 2)
        self.assertAlmostEqual(result["right"]["mean_dx"], residual[0], delta=0.6)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: FAIL with `AttributeError: module 'pitch_diagnostics' has no attribute 'phase_correlate_shift'`

- [ ] **Step 3: Write minimal implementation**

Append to `tools/ecc_sandbox/pitch_diagnostics.py` (after `crop_overlap_roi`, before end of file):

```python
import argparse
import json
import os
import statistics as st

import cv2

import config
import pairs
import preprocess


def phase_correlate_shift(anchor_roi, moving_roi, cfg):
    """Tra ve (dx, dy) -- shift can ap cho moving_roi de khop anchor_roi.

    Tien xu ly theo Findings Sec8.2: san phang illumination (flatten_and_enhance), KHONG
    threshold, nhan Hann window truoc khi dua vao phase correlation.
    """
    anchor = preprocess.flatten_and_enhance(
        anchor_roi, cfg["BackgroundSigma"], cfg["ClaheClipLimit"], cfg["ClaheTile"])
    moving = preprocess.flatten_and_enhance(
        moving_roi, cfg["BackgroundSigma"], cfg["ClaheClipLimit"], cfg["ClaheTile"])
    if anchor.shape != moving.shape:
        raise ValueError("phase_correlate_shift can hai ROI cung kich thuoc.")

    window = cv2.createHanningWindow((anchor.shape[1], anchor.shape[0]), cv2.CV_32F)
    anchor32 = anchor.astype(np.float32)
    moving32 = moving.astype(np.float32)
    (dx, dy), _response = cv2.phaseCorrelate(anchor32, moving32, window)
    return float(dx), float(dy)


def neighbor_edges_for_grid(payload):
    """Liet ke moi canh ke nhau (right, bottom) dung mot lan tren toan luoi."""
    by_position = {(t["Row"], t["Column"]): t for t in payload["tiles"]}
    edges = []
    for (row, column), tile in by_position.items():
        right = by_position.get((row, column + 1))
        if right is not None:
            edges.append((tile, right, "right"))
        bottom = by_position.get((row + 1, column))
        if bottom is not None:
            edges.append((tile, bottom, "bottom"))
    return edges


def measure_pitch(payload, images_dir, extension, cfg):
    """Chay phase_correlate_shift tren moi canh ke nhau, gom theo huong.

    Mot canh loi (thieu file anh, overlap suy bien) bi bo qua va khong lam dung cac canh con lai.
    """
    extension = extension or ".bmp"
    by_direction = {"right": {"dx": [], "dy": []}, "bottom": {"dx": [], "dy": []}}
    for anchor_tile, target_tile, direction in neighbor_edges_for_grid(payload):
        try:
            anchor_image = pairs.load_captured(
                images_dir, anchor_tile["OrderIndex"], extension)
            target_image = pairs.load_captured(
                images_dir, target_tile["OrderIndex"], extension)
            anchor_roi = crop_overlap_roi(anchor_image, anchor_tile, target_tile, direction, True)
            target_roi = crop_overlap_roi(target_image, anchor_tile, target_tile, direction, False)
            dx, dy = phase_correlate_shift(anchor_roi, target_roi, cfg)
        except (ValueError, IOError, cv2.error):
            continue
        by_direction[direction]["dx"].append(dx)
        by_direction[direction]["dy"].append(dy)

    result = {}
    for direction, values in by_direction.items():
        n = len(values["dx"])
        result[direction] = {
            "n": n,
            "mean_dx": st.mean(values["dx"]) if n else None,
            "mean_dy": st.mean(values["dy"]) if n else None,
            "std_dx": st.pstdev(values["dx"]) if n > 1 else 0.0,
            "std_dy": st.pstdev(values["dy"]) if n > 1 else 0.0,
        }
    return result


def _parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True)
    parser.add_argument("--images", required=True)
    parser.add_argument("--ext", default="")
    parser.add_argument("--output", required=True)
    return parser.parse_args()


def main():
    args = _parse_args()
    payload = pairs.load_payload(args.payload)
    cfg = dict(config.DEFAULTS)
    result = measure_pitch(payload, args.images, args.ext or ".bmp", cfg)

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8") as stream:
        json.dump(result, stream, indent=2)

    for direction, stats in result.items():
        if stats["n"] == 0:
            print("%-8s n=0 (khong do duoc canh nao)" % direction)
            continue
        print("%-8s n=%3d mean_dx=%+8.3f std_dx=%.3f mean_dy=%+8.3f std_dy=%.3f"
              % (direction, stats["n"], stats["mean_dx"], stats["std_dx"],
                 stats["mean_dy"], stats["std_dy"]))
    print("Ket qua: %s" % args.output)


if __name__ == "__main__":
    main()
```

Note: the module already has `import numpy as np` at the top from Task 1; the appended code adds
`argparse`, `json`, `os`, `statistics as st`, `cv2`, `config`, `pairs`, `preprocess` — put these new
imports at the top of the file alongside the existing `import numpy as np`, not inline, and remove
the duplicate `if __name__ == "__main__":` guard from Task 1's version of the file (there is only
one `main()` in the finished file, added by this task). `main()` builds its config from
`config.DEFAULTS` (matching `experiment_runner.py`'s `base_cfg = dict(config.DEFAULTS)` pattern),
not a hand-written literal dict — this keeps the CLI's defaults from silently drifting out of sync
with `config.DEFAULTS` as the sandbox evolves.

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: PASS (6 tests total: 3 from Task 1 + 3 new)

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/pitch_diagnostics.py tools/ecc_sandbox/tests/test_pitch_diagnostics.py
git commit -m "Add phase-correlation pitch measurement and CLI"
```

---

## Task 3: Chamfer candidate search (`chamfer_alignment.py`)

**Files:**
- Create: `tools/ecc_sandbox/chamfer_alignment.py`
- Test: `tools/ecc_sandbox/tests/test_chamfer_alignment.py`

**Interfaces:**
- Consumes: `coarse_alignment.build_distance_similarity(mono8, cfg)` (existing,
  `tools/ecc_sandbox/coarse_alignment.py:17`); `alignment_quality.measure_alignment(reference_mono8,
  moving_mono8, matrix, cfg)` (existing, `tools/ecc_sandbox/alignment_quality.py:37`, returns dict
  with `eligible`, `symmetric_edge_coverage`, `symmetric_chamfer_p95`); config keys
  `ChamferAngleStepDeg`, `ChamferCandidateCount`, `ChamferSeparationPixels` (added in Task 5, but
  this task's tests pass them explicitly in a local `_cfg()` helper so it does not block on Task 5).
- Produces: `find_chamfer_candidates(reference_mono8, moving_mono8, cfg) -> list[dict]`, each dict
  shaped like `coarse_alignment.find_translation_seeds`'s seed dicts —
  `{"matrix": np.ndarray(3,3), "source": "chamfer_bootstrap", "coarse_score": float}` — so
  Task 4 can append it into `pyramid_ecc.match()`'s existing seed-consumption loop unmodified.

- [ ] **Step 1: Write the failing test**

Create `tools/ecc_sandbox/tests/test_chamfer_alignment.py`:

```python
import os
import sys
import unittest

import cv2
import numpy as np


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import chamfer_alignment
import config


def _cfg(**overrides):
    values = dict(config.DEFAULTS)
    values.update({
        "ChamferAngleStepDeg": 0.05,
        "ChamferCandidateCount": 3,
        "ChamferSeparationPixels": 10.0,
        "MaxAbsRotationDeg": 1.0,
        "MaxTranslationPixels": 40.0,
        "CoarseSearchDownsample": 2,
    })
    values.update(overrides)
    return values


def _traces_image(width=160, height=140):
    image = np.zeros((height, width), dtype=np.uint8)
    cv2.line(image, (20, 30), (140, 34), 255, 3)
    cv2.line(image, (25, 70), (135, 66), 255, 3)
    cv2.rectangle(image, (60, 90), (100, 118), 255, 3)
    cv2.circle(image, (30, 110), 9, 255, 3)
    return image


class FindChamferCandidatesTests(unittest.TestCase):
    def test_recovers_known_translation_with_small_rotation(self):
        reference = _traces_image()
        angle, tx, ty = 0.3, 7.0, -5.0
        h, w = reference.shape
        center = (w / 2.0, h / 2.0)
        rot = cv2.getRotationMatrix2D(center, -angle, 1.0)
        rot[0, 2] += tx
        rot[1, 2] += ty
        moving = cv2.warpAffine(reference, rot, (w, h), flags=cv2.INTER_LINEAR)

        # find_chamfer_candidates returns a MovingImage -> ReferenceImage matrix -- the
        # inverse of the transform used above to build `moving` from `reference`.
        expected_matrix = np.linalg.inv(np.vstack([rot, [0.0, 0.0, 1.0]]))

        candidates = chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())

        self.assertGreater(len(candidates), 0)
        best = candidates[0]
        self.assertEqual(best["source"], "chamfer_bootstrap")
        self.assertEqual(best["matrix"].shape, (3, 3))
        # Chamfer only has to produce a coarse SEED (refined later by ECC in
        # pyramid_ecc.match()), not sub-pixel precision -- a few px off is expected given
        # CoarseSearchDownsample=2 quantizing the coarse translation search.
        self.assertAlmostEqual(best["matrix"][0, 2], expected_matrix[0, 2], delta=5.0)
        self.assertAlmostEqual(best["matrix"][1, 2], expected_matrix[1, 2], delta=5.0)

    def test_candidates_respect_separation_and_count_bound(self):
        reference = _traces_image()
        moving = _traces_image()

        candidates = chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())

        self.assertLessEqual(len(candidates), 3)
        translations = [(c["matrix"][0, 2], c["matrix"][1, 2]) for c in candidates]
        for i in range(len(translations)):
            for j in range(i + 1, len(translations)):
                dist = np.hypot(translations[i][0] - translations[j][0],
                                translations[i][1] - translations[j][1])
                self.assertGreaterEqual(dist, 10.0 - 1e-6)

    def test_blank_images_return_no_candidates(self):
        blank = np.zeros((80, 80), dtype=np.uint8)

        candidates = chamfer_alignment.find_chamfer_candidates(blank, blank, _cfg())

        self.assertEqual(candidates, [])

    def test_mismatched_shapes_raise(self):
        reference = _traces_image()
        moving = _traces_image()[:, :100]

        with self.assertRaises(ValueError):
            chamfer_alignment.find_chamfer_candidates(reference, moving, _cfg())


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_chamfer_alignment -v` (from `tools/ecc_sandbox/`)
Expected: FAIL with `ModuleNotFoundError: No module named 'chamfer_alignment'`

- [ ] **Step 3: Write minimal implementation**

Create `tools/ecc_sandbox/chamfer_alignment.py`:

```python
"""Directional chamfer matching: tim (goc, tx, ty) khong phu thuoc gradient cuong do anh.

Khac ECC (Gauss-Newton tren gradient) va Phase Correlation (noi dung tan so cao on dinh) -- chamfer
chi can "diem canh gan mot diem canh nao do", nen chiu duoc trace mong/dut gay va seed lech xa.
Tai dung ha tang da co thay vi viet lai: build_distance_similarity (coarse_alignment) cho buoc loc
tho theo goc, measure_alignment (alignment_quality) cho diem so chamfer that o buoc xep hang cuoi --
chi mot dinh nghia "gan la gan bao nhieu" duy nhat trong codebase.
"""
import math

import cv2
import numpy as np

import alignment_quality
import coarse_alignment


def _mono8(image):
    arr = np.asarray(image)
    if arr.ndim != 2 or arr.size == 0:
        raise ValueError("Chamfer alignment can anh mono8 2D khong rong.")
    if arr.dtype == np.uint8:
        return arr
    return np.clip(arr, 0, 255).astype(np.uint8)


def _rotation_matrix_3x3(angle_deg, center):
    m2x3 = cv2.getRotationMatrix2D(center, angle_deg, 1.0)
    return np.vstack([m2x3, [0.0, 0.0, 1.0]]).astype(float)


def _best_translation(ref_field, mov_field, cfg):
    """Mot dinh duy nhat (khong multi-peak nhu find_translation_seeds -- chamfer da loc theo goc)."""
    downsample = max(1, int(cfg["CoarseSearchDownsample"]))
    max_translation = abs(float(cfg["MaxTranslationPixels"]))
    coarse_bound = max(0, int(math.floor(max_translation / downsample)))
    padded = cv2.copyMakeBorder(
        ref_field, coarse_bound, coarse_bound, coarse_bound, coarse_bound,
        cv2.BORDER_CONSTANT, value=0.0)
    scores = cv2.matchTemplate(padded, mov_field, cv2.TM_CCOEFF_NORMED)
    scores = np.asarray(scores, dtype=np.float32)
    scores[~np.isfinite(scores)] = -1.0
    _min_value, max_value, _min_loc, max_location = cv2.minMaxLoc(scores)
    if not math.isfinite(max_value) or max_value <= -1.0:
        return None
    peak_x, peak_y = max_location
    tx = float((peak_x - coarse_bound) * downsample)
    ty = float((peak_y - coarse_bound) * downsample)
    if abs(tx) > max_translation or abs(ty) > max_translation:
        return None
    return tx, ty, float(max_value)


def _angle_grid(cfg):
    limit = abs(float(cfg["MaxAbsRotationDeg"]))
    step = abs(float(cfg["ChamferAngleStepDeg"]))
    if step <= 0.0:
        return [0.0]
    count = int(math.floor(limit / step))
    return [i * step for i in range(-count, count + 1)]


def find_chamfer_candidates(reference_mono8, moving_mono8, cfg):
    """Tim toi da ChamferCandidateCount ung vien MovingImage -> ReferenceImage.

    Buoc 1 (loc tho, theo goc): xoay moving quanh tam, dung NCC tren distance-similarity field
      de tim tinh tien tot nhat cho tung goc trong luoi [-MaxAbsRotationDeg, +MaxAbsRotationDeg].
    Buoc 2 (xep hang chinh xac): cham diem lai moi ung vien song bang chamfer distance THAT
      (alignment_quality.measure_alignment), khong tin NCC lam diem cuoi -- tranh false convergence
      tren pattern lap lai giong nhu structural bootstrap da lam voi ECC.
    """
    reference = _mono8(reference_mono8)
    moving = _mono8(moving_mono8)
    if reference.shape != moving.shape:
        raise ValueError("Chamfer alignment can hai anh cung kich thuoc.")

    ref_field = coarse_alignment.build_distance_similarity(reference, cfg)
    if np.count_nonzero(ref_field) < 8:
        return []

    height, width = moving.shape
    center = (width / 2.0, height / 2.0)

    raw_candidates = []
    for angle in _angle_grid(cfg):
        try:
            rot = _rotation_matrix_3x3(angle, center)
            rotated_moving = cv2.warpAffine(
                moving, rot[:2, :].astype(np.float32), (width, height),
                flags=cv2.INTER_LINEAR)
            mov_field = coarse_alignment.build_distance_similarity(rotated_moving, cfg)
            if np.count_nonzero(mov_field) < 8:
                continue
            found = _best_translation(ref_field, mov_field, cfg)
        except (ValueError, cv2.error):
            continue
        if found is None:
            continue
        tx, ty, coarse_score = found
        translate = np.array([[1.0, 0.0, tx], [0.0, 1.0, ty], [0.0, 0.0, 1.0]])
        matrix = translate @ rot
        raw_candidates.append({"matrix": matrix, "coarse_score": coarse_score})

    scored = []
    for candidate in raw_candidates:
        metrics = alignment_quality.measure_alignment(reference, moving, candidate["matrix"], cfg)
        if not metrics["eligible"]:
            continue
        scored.append((
            -metrics["symmetric_edge_coverage"],
            metrics["symmetric_chamfer_p95"],
            candidate,
        ))
    scored.sort(key=lambda item: (item[0], item[1]))

    separation = max(0.0, float(cfg["ChamferSeparationPixels"]))
    count = max(0, int(cfg["ChamferCandidateCount"]))
    kept = []
    for _neg_coverage, _chamfer_p95, candidate in scored:
        if len(kept) >= count:
            break
        translation = candidate["matrix"][:2, 2]
        if any(np.linalg.norm(translation - kept_item["matrix"][:2, 2]) < separation
               for kept_item in kept):
            continue
        kept.append({
            "matrix": candidate["matrix"],
            "source": "chamfer_bootstrap",
            "coarse_score": candidate["coarse_score"],
        })

    return kept
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_chamfer_alignment -v` (from `tools/ecc_sandbox/`)
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/chamfer_alignment.py tools/ecc_sandbox/tests/test_chamfer_alignment.py
git commit -m "Add directional chamfer candidate search"
```

---

## Task 4: New config defaults

**Files:**
- Modify: `tools/ecc_sandbox/config.py:6-51` (the `DEFAULTS` dict)
- Test: `tools/ecc_sandbox/tests/test_pitch_diagnostics.py` (add one assertion; no new file)

**Interfaces:**
- Consumes: nothing.
- Produces: seven new keys in `config.DEFAULTS`, all backward compatible (existing keys/values
  untouched):
  `ChamferAngleStepDeg=0.02`, `ChamferCandidateCount=5`, `ChamferSeparationPixels=48.0`,
  `PitchCorrectionPxPerStepX=0.0`, `PitchCorrectionPxPerStepY=0.0`, `ExpandedSearchFactor=2.0`,
  `ExpandedSearchMaxRounds=1`, `MaxTranslationPixelsHardCap=800.0`.

- [ ] **Step 1: Write the failing test**

Append a new test class to `tools/ecc_sandbox/tests/test_pitch_diagnostics.py` (it already imports
nothing from `config`, so add the import at the top alongside the existing ones, and the class
before `if __name__ == "__main__":`):

```python
import config


class NewConfigDefaultsTests(unittest.TestCase):
    def test_chamfer_pitch_and_expanded_search_defaults_exist(self):
        expected = {
            "ChamferAngleStepDeg": 0.02,
            "ChamferCandidateCount": 5,
            "ChamferSeparationPixels": 48.0,
            "PitchCorrectionPxPerStepX": 0.0,
            "PitchCorrectionPxPerStepY": 0.0,
            "ExpandedSearchFactor": 2.0,
            "ExpandedSearchMaxRounds": 1,
            "MaxTranslationPixelsHardCap": 800.0,
        }
        for key, value in expected.items():
            self.assertIn(key, config.DEFAULTS)
            self.assertEqual(config.DEFAULTS[key], value)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `AssertionError` on the first missing key (`ChamferAngleStepDeg`).

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/config.py`. The `DEFAULTS` dict currently ends with:

```python
    "VerificationSameTransformPixels": 4.0,
    "VerificationMinCoverageMargin": 0.03,
}
```

Change to:

```python
    "VerificationSameTransformPixels": 4.0,
    "VerificationMinCoverageMargin": 0.03,
    # Chamfer bootstrap (docs/superpowers/specs/2026-08-16-ecc-pitch-diagnostics-and-chamfer-
    # recovery-design.md Part 2) -- tim (goc, tx, ty) khong phu thuoc gradient cuong do.
    "ChamferAngleStepDeg": 0.02,
    "ChamferCandidateCount": 5,
    "ChamferSeparationPixels": 48.0,
    # Pitch-corrected seeding (Part 3) -- THU NGHIEM sandbox, mac dinh 0.0 = khong doi hanh vi.
    # Dat theo so do duoc tu pitch_diagnostics.measure_pitch(), KHONG phai fix o Master.
    "PitchCorrectionPxPerStepX": 0.0,
    "PitchCorrectionPxPerStepY": 0.0,
    # Mo rong search co kiem soat (Part 3) -- chi chay vong 2 khi vong 1 that bai toan bo.
    "ExpandedSearchFactor": 2.0,
    "ExpandedSearchMaxRounds": 1,
    "MaxTranslationPixelsHardCap": 800.0,
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pitch_diagnostics -v` (from `tools/ecc_sandbox/`)
Expected: PASS. Also run the full suite to confirm nothing else broke:
`python -m unittest discover -s tests -v` (from `tools/ecc_sandbox/`) — expect all prior tests
(`test_preprocess`, `test_coarse_alignment`, `test_alignment_quality`, `test_pyramid_ecc`,
`test_experiment_runner`) still pass, since `DEFAULTS` only gained keys, none changed.

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/config.py tools/ecc_sandbox/tests/test_pitch_diagnostics.py
git commit -m "Add chamfer, pitch-correction, and expanded-search config defaults"
```

---

## Task 5: Integrate chamfer bootstrap into `pyramid_ecc.match()`

**Files:**
- Modify: `tools/ecc_sandbox/pyramid_ecc.py`
- Test: `tools/ecc_sandbox/tests/test_pyramid_ecc.py`

**Interfaces:**
- Consumes: `chamfer_alignment.find_chamfer_candidates(reference_mono8, moving_mono8, cfg)` (Task 3);
  existing `_run_single_attempt`, `_seed_is_duplicate` (both already in `pyramid_ecc.py`).
- Produces: `match()`'s `attempts` list now may contain entries with `source="chamfer_bootstrap"`
  alongside the existing `"primary"`/`"structural_bootstrap"` sources — no change to `attempts`'
  dict shape, so `classify_candidates`, `app.py._report`, and `experiment_runner.py` need no
  changes to consume it.

- [ ] **Step 1: Write the failing test**

Add to `tools/ecc_sandbox/tests/test_pyramid_ecc.py`, inside `MultiCandidateMatchTests` (add these
as new test methods; the file already imports `mock`, `np`, `cv2`, `coarse_alignment`,
`alignment_quality`, `pyramid_ecc`, `config` and defines `_cfg`, `_structure`, `_success`,
`_failure`, `_seed`, `_metrics` — reuse them, do not redefine):

```python
    def test_chamfer_bootstrap_runs_after_structural_bootstrap_and_can_win(self):
        image = _structure()
        chamfer_matrix = np.array([[1.0, 0.0, 55.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]])
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(
                    chamfer_alignment, "find_chamfer_candidates",
                    return_value=[{"matrix": chamfer_matrix, "source": "chamfer_bootstrap",
                                   "coarse_score": 0.5}]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        side_effect=[_failure(2, "primary"),
                                     _success(chamfer_matrix, "chamfer_bootstrap")]):
                    result = pyramid_ecc.match(image, image, _cfg())

        self.assertTrue(result["success"])
        self.assertEqual(result["verification_status"], "Verified")
        sources = [a["source"] for a in result["attempts"]]
        self.assertIn("chamfer_bootstrap", sources)

    def test_chamfer_bootstrap_failure_does_not_abort_match(self):
        image = _structure()
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(
                    chamfer_alignment, "find_chamfer_candidates",
                    side_effect=ValueError("chamfer khong hop le")):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        return_value=_failure(2, "primary")):
                    result = pyramid_ecc.match(image, image, _cfg())

        self.assertFalse(result["success"])
        self.assertEqual(result["verification_status"], "Rejected")
        sources = [a["source"] for a in result["attempts"]]
        self.assertIn("chamfer_bootstrap", sources)
        chamfer_attempt = next(a for a in result["attempts"] if a["source"] == "chamfer_bootstrap")
        self.assertEqual(chamfer_attempt["failure_reason"], "ChamferBootstrapFailure")

    def test_chamfer_seed_duplicate_of_earlier_seed_is_skipped(self):
        image = _structure()
        duplicate_matrix = np.eye(3)  # same as the default primary seed -> duplicate
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(
                    chamfer_alignment, "find_chamfer_candidates",
                    return_value=[{"matrix": duplicate_matrix, "source": "chamfer_bootstrap",
                                   "coarse_score": 0.9}]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        return_value=_failure(2, "primary")) as run_mock:
                    pyramid_ecc.match(image, image, _cfg())

        # Only the primary attempt should have called _run_single_attempt -- the chamfer seed
        # duplicates the primary identity seed and must be skipped, not run a second time.
        self.assertEqual(run_mock.call_count, 1)
```

Also add the import at the top of the file, alongside the existing `import pyramid_ecc`:

```python
import chamfer_alignment
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `AttributeError: <module 'chamfer_alignment'> does not have the attribute
'find_chamfer_candidates'` is not the failure (that exists from Task 3); the actual failure is the
three new tests not finding `"chamfer_bootstrap"` in `result["attempts"]`'s sources, since
`match()` does not call `chamfer_alignment.find_chamfer_candidates` yet.

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/pyramid_ecc.py`. Add the import near the top, alongside the existing
`import coarse_alignment`:

```python
import alignment_quality
import chamfer_alignment
import coarse_alignment
```

Then, in `match()`, the existing structural-bootstrap block is:

```python
        try:
            seeds = coarse_alignment.find_translation_seeds(
                reference_mono8, moving_mono8, cfg)
        except (ValueError, cv2.error) as ex:
            seeds = []
            attempts.append({
                "success": False,
                "matcher": "PyramidEccMatcher",
                "source": "structural_bootstrap",
                "seed_matrix": None,
                "levels": [],
                "matrix": None,
                "geometry_valid": False,
                "failure_reason": "CoarseBootstrapFailure",
                "message": str(ex),
            })
        for seed in seeds:
            seed_matrix = np.asarray(seed["matrix"], dtype=float)
            if _seed_is_duplicate(seed_matrix, used_matrices):
                continue
            used_matrices.append(seed_matrix.copy())
            attempt = _run_single_attempt(
                reference_mono8, moving_mono8, cfg, seed_matrix,
                seed.get("source", "structural_bootstrap"))
            attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
            attempts.append(attempt)
```

Immediately after that block (still inside the `if reference_mono8.shape == moving_mono8.shape:`
body), add the same pattern for chamfer seeds:

```python
        try:
            chamfer_seeds = chamfer_alignment.find_chamfer_candidates(
                reference_mono8, moving_mono8, cfg)
        except (ValueError, cv2.error) as ex:
            chamfer_seeds = []
            attempts.append({
                "success": False,
                "matcher": "PyramidEccMatcher",
                "source": "chamfer_bootstrap",
                "seed_matrix": None,
                "levels": [],
                "matrix": None,
                "geometry_valid": False,
                "failure_reason": "ChamferBootstrapFailure",
                "message": str(ex),
            })
        for seed in chamfer_seeds:
            seed_matrix = np.asarray(seed["matrix"], dtype=float)
            if _seed_is_duplicate(seed_matrix, used_matrices):
                continue
            used_matrices.append(seed_matrix.copy())
            attempt = _run_single_attempt(
                reference_mono8, moving_mono8, cfg, seed_matrix,
                seed.get("source", "chamfer_bootstrap"))
            attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
            attempts.append(attempt)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: PASS (all prior `test_pyramid_ecc` tests plus the 3 new ones). Also run
`python -m unittest discover -s tests -v` to confirm the full suite (now including
`test_pitch_diagnostics`, `test_chamfer_alignment`) still passes.

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/pyramid_ecc.py tools/ecc_sandbox/tests/test_pyramid_ecc.py
git commit -m "Run chamfer bootstrap alongside structural bootstrap in match()"
```

---

## Task 6: Pitch-corrected seeding in `pairs.direct_pair`

**Files:**
- Modify: `tools/ecc_sandbox/pairs.py:62-78` (`direct_pair`)
- Modify: `tools/ecc_sandbox/experiment_runner.py:109-110` (the `pairs.direct_pair` call site)
- Modify: `tools/ecc_sandbox/app.py` (the `pairs_mod.direct_pair` call site and a new config row)
- Test: `tools/ecc_sandbox/tests/test_pairs.py` (new — no test file exists for `pairs.py` today)

**Interfaces:**
- Consumes: nothing new.
- Produces: `direct_pair(payload, raster, images_folder, order_index, step_override=None,
  extension=".bmp", pitch_correction_px_per_step_x=0.0, pitch_correction_px_per_step_y=0.0)` — two
  new trailing keyword parameters, defaults `0.0` reproduce today's behavior exactly (positional
  call sites with 6 args are unaffected).

- [ ] **Step 1: Write the failing test**

Create `tools/ecc_sandbox/tests/test_pairs.py`:

```python
import os
import sys
import tempfile
import unittest

import numpy as np
from PIL import Image


SANDBOX_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if SANDBOX_DIR not in sys.path:
    sys.path.insert(0, SANDBOX_DIR)

import pairs


def _payload_and_files(temp_dir, row, column, expected_x, expected_y, width=64, height=64):
    tile = {"OrderIndex": 0, "Row": row, "Column": column,
            "ExpectedX": expected_x, "ExpectedY": expected_y,
            "Width": width, "Height": height}
    payload = {"tiles": [tile], "image_width": width, "image_height": height,
              "sample_path": None, "overlap_x": None, "overlap_y": None}

    raster_path = os.path.join(temp_dir, "raster.png")
    raster_image = np.arange(400 * 400, dtype=np.uint8).reshape(400, 400) % 256
    Image.fromarray(raster_image).save(raster_path)
    raster = pairs.RasterSource(raster_path)

    captured = np.zeros((height, width), dtype=np.uint8)
    Image.fromarray(captured).save(os.path.join(temp_dir, "0.bmp"))

    return payload, raster


class DirectPairPitchCorrectionTests(unittest.TestCase):
    def test_default_offset_reproduces_current_origin(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=2, column=3,
                                                  expected_x=192, expected_y=128)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0)
            self.assertEqual(meta["reference_origin"], (192, 128))

    def test_pitch_correction_shifts_origin_by_column_and_row(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=2, column=3,
                                                  expected_x=192, expected_y=128)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0,
                pitch_correction_px_per_step_x=5.0, pitch_correction_px_per_step_y=-2.0)
            # column=3 * 5.0 = +15, row=2 * -2.0 = -4
            self.assertEqual(meta["reference_origin"], (192 + 15, 128 - 4))

    def test_pitch_correction_also_applies_with_step_override(self):
        with tempfile.TemporaryDirectory() as temp_dir:
            payload, raster = _payload_and_files(temp_dir, row=1, column=2,
                                                  expected_x=999, expected_y=999)
            _reference, _moving, meta = pairs.direct_pair(
                payload, raster, temp_dir, order_index=0, step_override=64.0,
                pitch_correction_px_per_step_x=3.0, pitch_correction_px_per_step_y=1.0)
            # step_override branch: x = column * step = 2*64 = 128, then +column*3.0 = +6
            # y = row * step = 1*64 = 64, then +row*1.0 = +1
            self.assertEqual(meta["reference_origin"], (128 + 6, 64 + 1))


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pairs -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `TypeError: direct_pair() got an unexpected keyword argument
'pitch_correction_px_per_step_x'`

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/pairs.py`. Current `direct_pair`:

```python
def direct_pair(payload, raster, images_folder, order_index, step_override=None, extension=".bmp"):
    """Reference = crop raster tai vet chan danh nghia cua anh chup, CUNG kich thuoc anh chup.

    step_override cho phep so sanh buoc luoi khac nhau (vd 4096 vs 4031.5) ma khong phai
    sinh lai payload -- dung de nhin tan mat anh huong cua sai buoc len ECC.
    """
    tile = next(t for t in payload["tiles"] if t["OrderIndex"] == order_index)
    w, h = payload["image_width"], payload["image_height"]
    if step_override is None:
        x = tile["ExpectedX"] + (tile["Width"] - w) // 2
        y = tile["ExpectedY"] + (tile["Height"] - h) // 2
    else:
        x = int(tile["Column"] * step_override)
        y = int(tile["Row"] * step_override)
    reference = raster.crop_mono8(x, y, w, h)
    moving = load_captured(images_folder, order_index, extension)
    return reference, moving, {"reference_origin": (x, y), "tile": tile}
```

Replace with:

```python
def direct_pair(payload, raster, images_folder, order_index, step_override=None, extension=".bmp",
                pitch_correction_px_per_step_x=0.0, pitch_correction_px_per_step_y=0.0):
    """Reference = crop raster tai vet chan danh nghia cua anh chup, CUNG kich thuoc anh chup.

    step_override cho phep so sanh buoc luoi khac nhau (vd 4096 vs 4031.5) ma khong phai
    sinh lai payload -- dung de nhin tan mat anh huong cua sai buoc len ECC.

    pitch_correction_px_per_step_x/y (mac dinh 0.0 = khong doi hanh vi) la THU NGHIEM sandbox:
    cong them column*x + row*y vao goc crop, dung so do duoc tu pitch_diagnostics.measure_pitch()
    de kiem chung gia thuyet sai luoi bang ket qua match that, KHONG phai sua Master.
    """
    tile = next(t for t in payload["tiles"] if t["OrderIndex"] == order_index)
    w, h = payload["image_width"], payload["image_height"]
    if step_override is None:
        x = tile["ExpectedX"] + (tile["Width"] - w) // 2
        y = tile["ExpectedY"] + (tile["Height"] - h) // 2
    else:
        x = int(tile["Column"] * step_override)
        y = int(tile["Row"] * step_override)
    x += tile["Column"] * pitch_correction_px_per_step_x
    y += tile["Row"] * pitch_correction_px_per_step_y
    x, y = int(round(x)), int(round(y))
    reference = raster.crop_mono8(x, y, w, h)
    moving = load_captured(images_folder, order_index, extension)
    return reference, moving, {"reference_origin": (x, y), "tile": tile}
```

Then wire the two callers. In `tools/ecc_sandbox/experiment_runner.py`, the call site is:

```python
        reference_raw, moving_raw, _meta = pairs.direct_pair(
            payload, raster, images_dir, order, None, extension)
```

(`raster` here is the `RasterSource` built earlier in `run_experiment`, not a directory path — keep
it identical, only add the two new keyword arguments):

```python
        reference_raw, moving_raw, _meta = pairs.direct_pair(
            payload, raster, images_dir, order, None, extension,
            pitch_correction_px_per_step_x=base_cfg["PitchCorrectionPxPerStepX"],
            pitch_correction_px_per_step_y=base_cfg["PitchCorrectionPxPerStepY"])
```

In `tools/ecc_sandbox/app.py`, the call site inside `_run` is:

```python
            reference, moving, meta = pairs_mod.direct_pair(
                payload, raster, images, order, step, ext)
```

Change to:

```python
            reference, moving, meta = pairs_mod.direct_pair(
                payload, raster, images, order, step, ext,
                pitch_correction_px_per_step_x=c["PitchCorrectionPxPerStepX"],
                pitch_correction_px_per_step_y=c["PitchCorrectionPxPerStepY"])
```

(`c` is already the local variable holding `self.current_cfg()`'s result in `_run` — this line sits
right after `c = self.current_cfg()`.) Add the two new fields to `current_cfg()`'s `c.update({...})`
dict (next to the existing `"MaxTranslationPixels": self._num("maxtrans"),` line):

```python
            "PitchCorrectionPxPerStepX": self._num("pitchx"),
            "PitchCorrectionPxPerStepY": self._num("pitchy"),
```

And add the two corresponding UI rows in `_build_params`, right after the existing
`self._row(ep, "MaxTranslationPixels", "maxtrans", self.cfg["MaxTranslationPixels"])` line:

```python
        self._row(ep, "Pitch corr. X (px/step)", "pitchx", self.cfg["PitchCorrectionPxPerStepX"])
        self._row(ep, "Pitch corr. Y (px/step)", "pitchy", self.cfg["PitchCorrectionPxPerStepY"])
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pairs -v` (from `tools/ecc_sandbox/`)
Expected: PASS (3 tests). Then run `python -m unittest discover -s tests -v` (from
`tools/ecc_sandbox/`) to confirm `test_experiment_runner` still passes unchanged (its mocked
`experiment_runner.ecc.match` call is unaffected by `direct_pair`'s new optional arguments, and its
payload's `PitchCorrectionPxPerStepX/Y` will be `0.0` from `config.DEFAULTS`, reproducing the
existing 14-row assertions exactly).

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/pairs.py tools/ecc_sandbox/experiment_runner.py \
        tools/ecc_sandbox/app.py tools/ecc_sandbox/tests/test_pairs.py
git commit -m "Add opt-in pitch-corrected seeding to direct_pair"
```

---

## Task 7: Controlled expanded search (round 2) in `pyramid_ecc.match()`

By this task, `match()` has two near-identical bootstrap blocks (structural, chamfer). Adding a
second round for each would make four copies of the same seed-consume-dedup pattern, so this task
refactors both round-1 blocks into a shared helper first (behavior-preserving — Task 5's tests must
stay green), then reuses that helper for round 2.

**Files:**
- Modify: `tools/ecc_sandbox/pyramid_ecc.py`
- Test: `tools/ecc_sandbox/tests/test_pyramid_ecc.py`

**Interfaces:**
- Consumes: `coarse_alignment.find_translation_seeds`, `chamfer_alignment.find_chamfer_candidates`
  (existing), config keys `ExpandedSearchFactor`, `ExpandedSearchMaxRounds`,
  `MaxTranslationPixelsHardCap` (Task 4).
- Produces: every entry in `match()`'s `attempts` list now carries an additional `"round"` key
  (`1` or `2`); no existing key is removed or renamed, so `classify_candidates`, `app.py`, and
  `experiment_runner.py` need no changes to keep working.

- [ ] **Step 1: Write the failing test**

Add to `tools/ecc_sandbox/tests/test_pyramid_ecc.py`, inside `MultiCandidateMatchTests`:

```python
    def test_expanded_round_runs_when_round_one_totally_fails(self):
        image = _structure()
        round2_matrix = np.array([[1.0, 0.0, 90.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]])

        def seeds_side_effect(reference, moving, cfg):
            if cfg["MaxTranslationPixels"] > 40.0:
                return [_seed(90.0)]
            return []

        with mock.patch.object(coarse_alignment, "find_translation_seeds",
                               side_effect=seeds_side_effect):
            with mock.patch.object(chamfer_alignment, "find_chamfer_candidates",
                                   return_value=[]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        side_effect=[_failure(2, "primary"),
                                     _success(round2_matrix, "structural_bootstrap")]):
                    result = pyramid_ecc.match(image, image, _cfg())

        self.assertTrue(result["success"])
        rounds = [a.get("round") for a in result["attempts"]]
        self.assertIn(2, rounds)
        expanded_call_cfg = coarse_alignment.find_translation_seeds.call_args_list[1][0][2]
        self.assertAlmostEqual(expanded_call_cfg["MaxTranslationPixels"], 80.0)

    def test_expanded_round_skipped_when_round_one_has_valid_candidate(self):
        image = _structure()
        with mock.patch.object(coarse_alignment, "find_translation_seeds",
                               return_value=[_seed(12.0)]) as seeds_mock:
            with mock.patch.object(chamfer_alignment, "find_chamfer_candidates",
                                   return_value=[]) as chamfer_mock:
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        side_effect=[_failure(2, "primary"),
                                     _success(_seed(12.0)["matrix"], "structural_bootstrap")]):
                    result = pyramid_ecc.match(image, image, _cfg())

        self.assertTrue(result["success"])
        # exactly one call each -- round 2 must not run when round 1 already found a valid candidate
        self.assertEqual(seeds_mock.call_count, 1)
        self.assertEqual(chamfer_mock.call_count, 1)
        rounds = [a.get("round") for a in result["attempts"]]
        self.assertNotIn(2, rounds)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `find_translation_seeds` is only called once (no round 2 exists yet), so
`call_args_list[1]` raises `IndexError` in the first new test, and `result["attempts"]` never
contains a `"round"` key.

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/pyramid_ecc.py`. Replace the two bootstrap blocks added in Tasks 5 (the
structural-bootstrap block and the chamfer-bootstrap block, both inside
`if reference_mono8.shape == moving_mono8.shape:`) with a shared helper plus two calls, then a
conditional round 2.

First, add the helper function near `_seed_is_duplicate` (top-level, alongside the other private
helpers):

```python
def _run_bootstrap_round(seed_finder, reference_mono8, moving_mono8, cfg, used_matrices,
                         attempts, source_label, failure_reason, round_number):
    """Chay mot nguon seed (structural hoac chamfer), mot round. Loi khong chan cac seed con lai."""
    try:
        seeds = seed_finder(reference_mono8, moving_mono8, cfg)
    except (ValueError, cv2.error) as ex:
        attempts.append({
            "success": False,
            "matcher": "PyramidEccMatcher",
            "source": source_label,
            "seed_matrix": None,
            "levels": [],
            "matrix": None,
            "geometry_valid": False,
            "failure_reason": failure_reason,
            "message": str(ex),
            "round": round_number,
        })
        return
    for seed in seeds:
        seed_matrix = np.asarray(seed["matrix"], dtype=float)
        if _seed_is_duplicate(seed_matrix, used_matrices):
            continue
        used_matrices.append(seed_matrix.copy())
        attempt = _run_single_attempt(
            reference_mono8, moving_mono8, cfg, seed_matrix,
            seed.get("source", source_label))
        attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
        attempt["round"] = round_number
        attempts.append(attempt)
```

Then, in `match()`, the primary attempt line:

```python
    attempts = [_run_single_attempt(
        reference_mono8, moving_mono8, cfg, primary_seed, "primary")]
```

Change to also tag it `"round": 1`:

```python
    attempts = [_run_single_attempt(
        reference_mono8, moving_mono8, cfg, primary_seed, "primary")]
    attempts[0]["round"] = 1
```

Replace both Task-5 bootstrap blocks (the structural-bootstrap `try/except` + `for seed in seeds:`
loop, and the chamfer-bootstrap `try/except` + `for seed in chamfer_seeds:` loop) with:

```python
        _run_bootstrap_round(
            coarse_alignment.find_translation_seeds, reference_mono8, moving_mono8, cfg,
            used_matrices, attempts, "structural_bootstrap", "CoarseBootstrapFailure", 1)
        _run_bootstrap_round(
            chamfer_alignment.find_chamfer_candidates, reference_mono8, moving_mono8, cfg,
            used_matrices, attempts, "chamfer_bootstrap", "ChamferBootstrapFailure", 1)

        if not any(attempt.get("geometry_valid") for attempt in attempts):
            max_rounds = max(0, int(cfg.get("ExpandedSearchMaxRounds", 0)))
            if max_rounds >= 1:
                expanded_cfg = dict(cfg)
                factor = float(cfg.get("ExpandedSearchFactor", 1.0))
                hard_cap = float(cfg.get(
                    "MaxTranslationPixelsHardCap", cfg["MaxTranslationPixels"]))
                expanded_cfg["MaxTranslationPixels"] = min(
                    hard_cap, cfg["MaxTranslationPixels"] * factor)
                expanded_cfg["CoarseCandidateSeparationPixels"] = (
                    cfg["CoarseCandidateSeparationPixels"] * factor)
                expanded_cfg["ChamferSeparationPixels"] = (
                    cfg["ChamferSeparationPixels"] * factor)

                _run_bootstrap_round(
                    coarse_alignment.find_translation_seeds, reference_mono8, moving_mono8,
                    expanded_cfg, used_matrices, attempts, "structural_bootstrap",
                    "CoarseBootstrapFailure", 2)
                _run_bootstrap_round(
                    chamfer_alignment.find_chamfer_candidates, reference_mono8, moving_mono8,
                    expanded_cfg, used_matrices, attempts, "chamfer_bootstrap",
                    "ChamferBootstrapFailure", 2)
```

This sits in the same place the two Task-5 blocks occupied, still inside the
`if reference_mono8.shape == moving_mono8.shape:` body, still before the
`quality_reference = (...)` line that starts the verification step.

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: PASS — all tests from Tasks 5's `MultiCandidateMatchTests` (unchanged behavior, just an
extra `"round"` key now present) plus the 2 new tests in this task. Then run
`python -m unittest discover -s tests -v` for the full suite.

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/pyramid_ecc.py tools/ecc_sandbox/tests/test_pyramid_ecc.py
git commit -m "Add controlled expanded-search round when round 1 fully fails"
```

---

## Task 8: `on_stage` progress callback + live logging in `app.py`

**Files:**
- Modify: `tools/ecc_sandbox/pyramid_ecc.py`
- Modify: `tools/ecc_sandbox/app.py`
- Test: `tools/ecc_sandbox/tests/test_pyramid_ecc.py`

**Interfaces:**
- Consumes: nothing new.
- Produces: `match(..., on_stage=None)` — when given, `on_stage(stage: str, detail: dict)` is
  called at: `"primary_start"`/`"primary_done"`, `"structural_bootstrap_start"` (detail has
  `seed_count`) or `"structural_bootstrap_failed"`, one `"structural_bootstrap_seed_done"` per
  surviving seed, the same three for `"chamfer_bootstrap_*"`, `"expanded_search_start"` (only if
  round 2 actually runs), and `"classification_done"`. Default `None` — no call, no behavior
  change, no overhead (this is what `experiment_runner.py` and every existing test keep using).

- [ ] **Step 1: Write the failing test**

Add to `tools/ecc_sandbox/tests/test_pyramid_ecc.py`, inside `MultiCandidateMatchTests`:

```python
    def test_on_stage_fires_primary_and_bootstrap_events_in_order(self):
        image = _structure()
        events = []
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(chamfer_alignment, "find_chamfer_candidates",
                                   return_value=[]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        return_value=_success(np.eye(3), "primary")):
                    pyramid_ecc.match(image, image, _cfg(),
                                      on_stage=lambda stage, detail: events.append(stage))

        self.assertEqual(events[0], "primary_start")
        self.assertIn("primary_done", events)
        self.assertLess(events.index("primary_start"), events.index("primary_done"))
        self.assertIn("structural_bootstrap_start", events)
        self.assertIn("chamfer_bootstrap_start", events)
        self.assertEqual(events[-1], "classification_done")
        # round 1 already produced a valid (primary) candidate -- no expanded search
        self.assertNotIn("expanded_search_start", events)

    def test_on_stage_fires_expanded_search_event_only_when_round_two_triggers(self):
        image = _structure()
        events = []
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(chamfer_alignment, "find_chamfer_candidates",
                                   return_value=[]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        return_value=_failure(2, "primary")):
                    pyramid_ecc.match(image, image, _cfg(),
                                      on_stage=lambda stage, detail: events.append(stage))

        # round 1 fully failed (no valid candidate anywhere) -> round 2 must trigger
        self.assertIn("expanded_search_start", events)

    def test_on_stage_default_none_does_not_raise(self):
        image = _structure()
        with mock.patch.object(coarse_alignment, "find_translation_seeds", return_value=[]):
            with mock.patch.object(chamfer_alignment, "find_chamfer_candidates",
                                   return_value=[]):
                with mock.patch.object(
                        pyramid_ecc, "_run_single_attempt",
                        return_value=_success(np.eye(3), "primary")):
                    pyramid_ecc.match(image, image, _cfg())  # no on_stage -- must not raise
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `TypeError: match() got an unexpected keyword argument 'on_stage'`

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/pyramid_ecc.py`. Change `_run_bootstrap_round`'s signature and body (from
Task 7) to accept and fire the callback:

```python
def _run_bootstrap_round(seed_finder, reference_mono8, moving_mono8, cfg, used_matrices,
                         attempts, source_label, failure_reason, round_number,
                         stage_prefix, on_stage):
    """Chay mot nguon seed (structural hoac chamfer), mot round. Loi khong chan cac seed con lai."""
    try:
        seeds = seed_finder(reference_mono8, moving_mono8, cfg)
    except (ValueError, cv2.error) as ex:
        attempts.append({
            "success": False,
            "matcher": "PyramidEccMatcher",
            "source": source_label,
            "seed_matrix": None,
            "levels": [],
            "matrix": None,
            "geometry_valid": False,
            "failure_reason": failure_reason,
            "message": str(ex),
            "round": round_number,
        })
        if on_stage is not None:
            on_stage(stage_prefix + "_failed", {"round": round_number, "message": str(ex)})
        return
    if on_stage is not None:
        on_stage(stage_prefix + "_start", {"round": round_number, "seed_count": len(seeds)})
    for index, seed in enumerate(seeds):
        seed_matrix = np.asarray(seed["matrix"], dtype=float)
        if _seed_is_duplicate(seed_matrix, used_matrices):
            continue
        used_matrices.append(seed_matrix.copy())
        attempt = _run_single_attempt(
            reference_mono8, moving_mono8, cfg, seed_matrix,
            seed.get("source", source_label))
        attempt["coarse_score"] = float(seed.get("coarse_score", float("nan")))
        attempt["round"] = round_number
        attempts.append(attempt)
        if on_stage is not None:
            on_stage(stage_prefix + "_seed_done", {
                "round": round_number, "index": index, "source": attempt.get("source"),
                "geometry_valid": attempt.get("geometry_valid"),
                "failure_reason": attempt.get("failure_reason")})
```

Then update `match()`'s signature and body. Signature:

```python
def match(reference_mono8, moving_mono8, cfg, initial_moving_to_reference=None,
          verification_reference=None, verification_moving=None, on_stage=None):
```

The primary-attempt lines:

```python
    attempts = [_run_single_attempt(
        reference_mono8, moving_mono8, cfg, primary_seed, "primary")]
    attempts[0]["round"] = 1
```

Change to:

```python
    if on_stage is not None:
        on_stage("primary_start", {})
    attempts = [_run_single_attempt(
        reference_mono8, moving_mono8, cfg, primary_seed, "primary")]
    attempts[0]["round"] = 1
    if on_stage is not None:
        on_stage("primary_done", {"geometry_valid": attempts[0].get("geometry_valid"),
                                  "failure_reason": attempts[0].get("failure_reason")})
```

The two round-1 bootstrap calls (from Task 7) get `stage_prefix`/`on_stage` arguments:

```python
        _run_bootstrap_round(
            coarse_alignment.find_translation_seeds, reference_mono8, moving_mono8, cfg,
            used_matrices, attempts, "structural_bootstrap", "CoarseBootstrapFailure", 1,
            "structural_bootstrap", on_stage)
        _run_bootstrap_round(
            chamfer_alignment.find_chamfer_candidates, reference_mono8, moving_mono8, cfg,
            used_matrices, attempts, "chamfer_bootstrap", "ChamferBootstrapFailure", 1,
            "chamfer_bootstrap", on_stage)
```

The round-2 block (from Task 7) gets a callback right before the two round-2 calls, and those two
calls get the two new trailing arguments too:

```python
        if not any(attempt.get("geometry_valid") for attempt in attempts):
            max_rounds = max(0, int(cfg.get("ExpandedSearchMaxRounds", 0)))
            if max_rounds >= 1:
                expanded_cfg = dict(cfg)
                factor = float(cfg.get("ExpandedSearchFactor", 1.0))
                hard_cap = float(cfg.get(
                    "MaxTranslationPixelsHardCap", cfg["MaxTranslationPixels"]))
                expanded_cfg["MaxTranslationPixels"] = min(
                    hard_cap, cfg["MaxTranslationPixels"] * factor)
                expanded_cfg["CoarseCandidateSeparationPixels"] = (
                    cfg["CoarseCandidateSeparationPixels"] * factor)
                expanded_cfg["ChamferSeparationPixels"] = (
                    cfg["ChamferSeparationPixels"] * factor)

                if on_stage is not None:
                    on_stage("expanded_search_start", {
                        "factor": factor,
                        "max_translation_pixels": expanded_cfg["MaxTranslationPixels"]})

                _run_bootstrap_round(
                    coarse_alignment.find_translation_seeds, reference_mono8, moving_mono8,
                    expanded_cfg, used_matrices, attempts, "structural_bootstrap",
                    "CoarseBootstrapFailure", 2, "structural_bootstrap", on_stage)
                _run_bootstrap_round(
                    chamfer_alignment.find_chamfer_candidates, reference_mono8, moving_mono8,
                    expanded_cfg, used_matrices, attempts, "chamfer_bootstrap",
                    "ChamferBootstrapFailure", 2, "chamfer_bootstrap", on_stage)
```

Finally, right before `return final` at the end of `match()`:

```python
    final["attempts"] = attempts
    final["runner_up"] = classification["runner_up"]
    final["coverage_margin"] = classification["coverage_margin"]
    if on_stage is not None:
        on_stage("classification_done", {
            "verification_status": final.get("verification_status")})
    return final
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_pyramid_ecc -v` (from `tools/ecc_sandbox/`)
Expected: PASS. Then `python -m unittest discover -s tests -v` for the full suite.

- [ ] **Step 5: Commit the `pyramid_ecc.py` change**

```bash
git add tools/ecc_sandbox/pyramid_ecc.py tools/ecc_sandbox/tests/test_pyramid_ecc.py
git commit -m "Add on_stage progress callback to match()"
```

- [ ] **Step 6: Wire the callback into `app.py`**

`app.py` has no automated test file (it is a Tk GUI; per `AGENTS.md` §4 this repo does not add a
test project, and GUI verification is manual). Verify this step with `python -m py_compile
tools/ecc_sandbox/app.py` instead of a unit test, then a manual run is the user's job.

Edit `tools/ecc_sandbox/app.py`. Add a new method right after `say`:

```python
    def _on_stage(self, stage, detail):
        self.say("  [stage] %s %s" % (stage, detail))
        self.root.update_idletasks()
```

Change the `ecc.match(...)` call inside `_run` from:

```python
        result = ecc.match(ref_v["final"], mov_v["final"], c,
                           verification_reference=ref_v["contrast"],
                           verification_moving=mov_v["contrast"])
```

to:

```python
        result = ecc.match(ref_v["final"], mov_v["final"], c,
                           verification_reference=ref_v["contrast"],
                           verification_moving=mov_v["contrast"],
                           on_stage=self._on_stage)
```

- [ ] **Step 7: Verify and commit**

Run: `python -m py_compile tools/ecc_sandbox/app.py` (from `tools/ecc_sandbox/`)
Expected: no output, exit code 0.

```bash
git add tools/ecc_sandbox/app.py
git commit -m "Show live per-stage progress in the sandbox GUI log"
```

---

## Task 9: `experiment_runner.py --all-tiles`

**Files:**
- Modify: `tools/ecc_sandbox/experiment_runner.py`
- Test: `tools/ecc_sandbox/tests/test_experiment_runner.py`

**Interfaces:**
- Consumes: nothing new.
- Produces: `run_experiment(payload_path, images_dir, raster_path, extension, output_dir,
  all_tiles=False)` — new trailing keyword, default `False` reproduces today's 7-coordinate
  behavior exactly. CLI gains `--all-tiles` (a flag, no value).

- [ ] **Step 1: Write the failing test**

Add to `tools/ecc_sandbox/tests/test_experiment_runner.py`, inside `ExperimentRunnerTests` (the
file already imports `csv`, `json`, `os`, `sys`, `tempfile`, `unittest`, `mock`, `cv2`, `np`,
`Image`, and `experiment_runner` — reuse them):

```python
    def test_all_tiles_iterates_every_tile_instead_of_requested_coordinates(self):
        rows, cols = 3, 3
        coordinates = [(r, c) for r in range(rows) for c in range(cols)]

        with tempfile.TemporaryDirectory() as temp_dir:
            images_dir = os.path.join(temp_dir, "images")
            output_dir = os.path.join(temp_dir, "result_test")
            os.makedirs(images_dir)
            raster_path = os.path.join(temp_dir, "gerber.tiff")
            payload_path = os.path.join(temp_dir, "sample.json")

            base = np.zeros((32, 32), dtype=np.uint8)
            cv2.circle(base, (10, 9), 5, 255, 2)
            Image.fromarray(base).save(raster_path)

            tiles = []
            for order, (row, column) in enumerate(coordinates):
                tiles.append({
                    "OrderIndex": order, "Row": row, "Column": column,
                    "ExpectedX": 0, "ExpectedY": 0, "Width": 32, "Height": 32,
                })
                self.assertTrue(cv2.imwrite(
                    os.path.join(images_dir, "%d.bmp" % order), base))
            with open(payload_path, "w", encoding="utf-8") as stream:
                json.dump({"Width_CaptureImages": 32, "Height_CaptureImages": 32,
                          "GerberTiles": tiles}, stream)

            match_result = {
                "success": True, "verification_status": "Verified", "failure_reason": None,
                "message": "verified", "matrix": np.eye(3), "translation_x": 0.0,
                "translation_y": 0.0, "rotation_deg": 0.0, "scale": 1.0, "raw_score": 0.9,
                "reference_edge_coverage": 1.0, "moving_edge_coverage": 1.0,
                "symmetric_edge_coverage": 1.0, "symmetric_chamfer_p95": 0.0,
                "coverage_margin": None, "attempts": [],
            }
            with mock.patch.object(experiment_runner.ecc, "match",
                                   return_value=match_result) as match_call:
                rows_out = experiment_runner.run_experiment(
                    payload_path, images_dir, raster_path, "", output_dir, all_tiles=True)

            self.assertEqual(len(rows_out), rows * cols * 2)
            self.assertEqual(match_call.call_count, rows * cols * 2)
            self.assertEqual(
                {(row["row"], row["column"]) for row in rows_out},
                set(coordinates))
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_experiment_runner -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `TypeError: run_experiment() got an unexpected keyword argument 'all_tiles'`

- [ ] **Step 3: Write minimal implementation**

Edit `tools/ecc_sandbox/experiment_runner.py`. Current `run_experiment` signature and coordinate
loop:

```python
def run_experiment(payload_path, images_dir, raster_path, extension, output_dir):
    """Run all 14 (coordinate x preprocessing mode) cases and save evidence.

    Returns the list of per-case summary dicts (also written to the CSV).
    """
    extension = extension or ".bmp"
    os.makedirs(output_dir, exist_ok=True)

    payload = pairs.load_payload(payload_path)
    raster = pairs.RasterSource(raster_path)

    base_cfg = dict(config.DEFAULTS)
    base_cfg["Contrast"] = _EXPERIMENT_CONTRAST

    rows = []
    json_results = []
    for row, column in REQUESTED_COORDINATES:
```

Change to:

```python
def run_experiment(payload_path, images_dir, raster_path, extension, output_dir,
                   all_tiles=False):
    """Run each (coordinate x preprocessing mode) case and save evidence.

    Without all_tiles: the fixed 7 REQUESTED_COORDINATES (14 rows total) -- unchanged behavior.
    With all_tiles=True: every tile in the payload x both modes -- for judging whether chamfer
    bootstrap / pitch-corrected seeding actually help across the whole grid, not just 7 samples.
    Returns the list of per-case summary dicts (also written to the CSV).
    """
    extension = extension or ".bmp"
    os.makedirs(output_dir, exist_ok=True)

    payload = pairs.load_payload(payload_path)
    raster = pairs.RasterSource(raster_path)

    base_cfg = dict(config.DEFAULTS)
    base_cfg["Contrast"] = _EXPERIMENT_CONTRAST

    if all_tiles:
        coordinates = [(tile["Row"], tile["Column"]) for tile in payload["tiles"]]
    else:
        coordinates = REQUESTED_COORDINATES

    rows = []
    json_results = []
    for row, column in coordinates:
```

The rest of the loop body is unchanged (it already reads `row`/`column` generically, not
`REQUESTED_COORDINATES` directly).

Then edit `_parse_args` and `main`:

```python
def _parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", required=True, help="Duong dan payload JSON.")
    parser.add_argument("--images", required=True, help="Thu muc anh chup.")
    parser.add_argument("--raster", required=True, help="Duong dan raster Gerber.")
    parser.add_argument("--ext", default="", help="Duoi file anh chup (mac dinh .bmp).")
    parser.add_argument("--output", required=True, help="Thu muc result_test de ghi ket qua.")
    parser.add_argument("--all-tiles", action="store_true",
                        help="Chay toan bo tile trong payload thay vi 7 toa do mac dinh.")
    return parser.parse_args()


def main():
    args = _parse_args()
    rows = run_experiment(args.payload, args.images, args.raster, args.ext, args.output,
                          all_tiles=args.all_tiles)
```

(the rest of `main` — the `verified`/`uncertain`/`rejected` summary print — is unchanged.)

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_experiment_runner -v` (from `tools/ecc_sandbox/`)
Expected: PASS (both the existing 7-coordinate test and the new `--all-tiles` test).

- [ ] **Step 5: Commit**

```bash
git add tools/ecc_sandbox/experiment_runner.py tools/ecc_sandbox/tests/test_experiment_runner.py
git commit -m "Add --all-tiles mode to experiment_runner"
```

---

## Task 10: `summarize_consistency` and wiring into `--all-tiles`

**Files:**
- Modify: `tools/ecc_sandbox/alignment_quality.py` (append after `compute_tre`)
- Modify: `tools/ecc_sandbox/experiment_runner.py`
- Test: `tools/ecc_sandbox/tests/test_alignment_quality.py`
- Test: `tools/ecc_sandbox/tests/test_experiment_runner.py`

**Interfaces:**
- Consumes: nothing new from other modules (pure NumPy on already-computed result dicts).
- Produces: `alignment_quality.summarize_consistency(results) -> dict | None`. `results` is any
  iterable of dicts with `row`, `column`, `translation_x`, `translation_y`, `scale`, `matrix` keys
  (exactly the shape of `experiment_runner.py`'s `json_results` entries). Returns `None` when fewer
  than 2 entries have a non-`None` `matrix`. Otherwise returns
  `{"n": int, "scale_spread": float, "scale_mean": float, "scale_std": float,
  "translation_x_per_column": {"slope": float, "intercept": float, "residual_std": float},
  "translation_y_per_row": {"slope": float, "intercept": float, "residual_std": float}}`.
  `experiment_runner.run_experiment(..., all_tiles=True)` calls this and adds a `"consistency"` key
  to `experiment_results.json`.

- [ ] **Step 1: Write the failing test**

Add to `tools/ecc_sandbox/tests/test_alignment_quality.py` as a new test class (the file already
has `import numpy as np`, `import alignment_quality` — reuse them):

```python
class SummarizeConsistencyTests(unittest.TestCase):
    def _case(self, row, column, scale, tx, ty, has_matrix=True):
        return {
            "row": row, "column": column, "scale": scale,
            "translation_x": tx, "translation_y": ty,
            "matrix": (np.eye(3).tolist() if has_matrix else None),
        }

    def test_computes_scale_spread_and_translation_slope_on_known_data(self):
        results = []
        for row in range(3):
            for column in range(4):
                tx = 5.0 * column + 100.0
                ty = -3.0 * row + 50.0
                scale = 0.98
                results.append(self._case(row, column, scale, tx, ty))
        # inject one outlier scale, matching the Findings-style signature this is meant to catch
        results[0]["scale"] = 0.94

        summary = alignment_quality.summarize_consistency(results)

        self.assertIsNotNone(summary)
        self.assertEqual(summary["n"], 12)
        self.assertAlmostEqual(summary["scale_spread"], 0.04, places=6)
        self.assertAlmostEqual(
            summary["translation_x_per_column"]["slope"], 5.0, places=4)
        self.assertAlmostEqual(
            summary["translation_y_per_row"]["slope"], -3.0, places=4)
        self.assertLess(summary["translation_x_per_column"]["residual_std"], 1e-6)

    def test_fewer_than_two_valid_cases_returns_none(self):
        results = [self._case(0, 0, 1.0, 0.0, 0.0),
                  self._case(0, 1, 1.0, 5.0, 0.0, has_matrix=False)]

        summary = alignment_quality.summarize_consistency(results)

        self.assertIsNone(summary)

    def test_empty_results_returns_none(self):
        self.assertIsNone(alignment_quality.summarize_consistency([]))
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m unittest tests.test_alignment_quality -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `AttributeError: module 'alignment_quality' has no attribute
'summarize_consistency'`

- [ ] **Step 3: Write minimal implementation**

Append to `tools/ecc_sandbox/alignment_quality.py`, after `compute_tre` (the file's last function):

```python
def _fit_line(x, y):
    """Hoi quy tuyen tinh dong-form (khong can scipy). Tra ve (slope, intercept, residual_std)."""
    mean_x = float(np.mean(x))
    mean_y = float(np.mean(y))
    denominator = float(np.sum((x - mean_x) ** 2))
    if denominator <= 1e-12:
        return 0.0, mean_y, float(np.std(y))
    slope = float(np.sum((x - mean_x) * (y - mean_y)) / denominator)
    intercept = mean_y - slope * mean_x
    residuals = y - (slope * x + intercept)
    return slope, intercept, float(np.std(residuals))


def summarize_consistency(results):
    """Findings.md Phu luc A3 (scale spread / rotation spread) tren ket qua match cua sandbox.

    `results`: iterable cac dict co 'row', 'column', 'translation_x', 'translation_y', 'scale',
    'matrix'. Chi xet case co 'matrix' khac None -- hinh hoc cua case Uncertain van co thong tin,
    khong chi Verified. Tra ve None neu it hon 2 case hop le (spread/hoi quy vo nghia voi 0-1 diem).
    Day la bao cao THUAN TUY -- khong gate Verified/Uncertain/Rejected.
    """
    valid = [r for r in results if r.get("matrix") is not None
             and r.get("scale") is not None
             and r.get("translation_x") is not None
             and r.get("translation_y") is not None
             and r.get("row") is not None
             and r.get("column") is not None]
    if len(valid) < 2:
        return None

    scales = np.array([float(r["scale"]) for r in valid])
    columns = np.array([float(r["column"]) for r in valid])
    rows_arr = np.array([float(r["row"]) for r in valid])
    tx = np.array([float(r["translation_x"]) for r in valid])
    ty = np.array([float(r["translation_y"]) for r in valid])

    slope_x, intercept_x, residual_std_x = _fit_line(columns, tx)
    slope_y, intercept_y, residual_std_y = _fit_line(rows_arr, ty)

    return {
        "n": len(valid),
        "scale_spread": float(np.max(scales) - np.min(scales)),
        "scale_mean": float(np.mean(scales)),
        "scale_std": float(np.std(scales)),
        "translation_x_per_column": {
            "slope": slope_x, "intercept": intercept_x, "residual_std": residual_std_x},
        "translation_y_per_row": {
            "slope": slope_y, "intercept": intercept_y, "residual_std": residual_std_y},
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m unittest tests.test_alignment_quality -v` (from `tools/ecc_sandbox/`)
Expected: PASS.

- [ ] **Step 5: Commit `alignment_quality.py`**

```bash
git add tools/ecc_sandbox/alignment_quality.py tools/ecc_sandbox/tests/test_alignment_quality.py
git commit -m "Add scale/translation consistency summary"
```

- [ ] **Step 6: Write the failing test for wiring into `experiment_runner.py`**

Add to `tools/ecc_sandbox/tests/test_experiment_runner.py`, inside `ExperimentRunnerTests` (reuses
the same synthetic-payload pattern as Task 9's test; this one only needs `all_tiles=True` and to
inspect the saved JSON's `"consistency"` key):

```python
    def test_all_tiles_run_adds_consistency_summary_to_json(self):
        rows, cols = 2, 3
        coordinates = [(r, c) for r in range(rows) for c in range(cols)]

        with tempfile.TemporaryDirectory() as temp_dir:
            images_dir = os.path.join(temp_dir, "images")
            output_dir = os.path.join(temp_dir, "result_test")
            os.makedirs(images_dir)
            raster_path = os.path.join(temp_dir, "gerber.tiff")
            payload_path = os.path.join(temp_dir, "sample.json")

            base = np.zeros((32, 32), dtype=np.uint8)
            cv2.circle(base, (10, 9), 5, 255, 2)
            Image.fromarray(base).save(raster_path)

            tiles = []
            for order, (row, column) in enumerate(coordinates):
                tiles.append({
                    "OrderIndex": order, "Row": row, "Column": column,
                    "ExpectedX": 0, "ExpectedY": 0, "Width": 32, "Height": 32,
                })
                self.assertTrue(cv2.imwrite(
                    os.path.join(images_dir, "%d.bmp" % order), base))
            with open(payload_path, "w", encoding="utf-8") as stream:
                json.dump({"Width_CaptureImages": 32, "Height_CaptureImages": 32,
                          "GerberTiles": tiles}, stream)

            match_result = {
                "success": True, "verification_status": "Verified", "failure_reason": None,
                "message": "verified", "matrix": np.eye(3), "translation_x": 2.0,
                "translation_y": 1.0, "rotation_deg": 0.0, "scale": 0.98, "raw_score": 0.9,
                "reference_edge_coverage": 1.0, "moving_edge_coverage": 1.0,
                "symmetric_edge_coverage": 1.0, "symmetric_chamfer_p95": 0.0,
                "coverage_margin": None, "attempts": [],
            }
            with mock.patch.object(experiment_runner.ecc, "match", return_value=match_result):
                experiment_runner.run_experiment(
                    payload_path, images_dir, raster_path, "", output_dir, all_tiles=True)

            with open(os.path.join(output_dir, "experiment_results.json"),
                     "r", encoding="utf-8") as stream:
                saved_json = json.load(stream)

        self.assertIn("consistency", saved_json)
        self.assertIsNotNone(saved_json["consistency"])
        self.assertEqual(saved_json["consistency"]["n"], rows * cols * 2)
```

- [ ] **Step 7: Run test to verify it fails**

Run: `python -m unittest tests.test_experiment_runner -v` (from `tools/ecc_sandbox/`)
Expected: FAIL — `KeyError: 'consistency'` (the key does not exist in the saved JSON yet).

- [ ] **Step 8: Write minimal implementation**

Edit `tools/ecc_sandbox/experiment_runner.py`. Add the import at the top, alongside the existing
`import pyramid_ecc as ecc`:

```python
import alignment_quality
```

Then change the JSON-writing block from:

```python
    with open(os.path.join(output_dir, _JSON_NAME), "w", encoding="utf-8") as stream:
        json.dump({
            "config": _json_safe(base_cfg),
            "requested_coordinates": REQUESTED_COORDINATES,
            "preprocess_modes": list(PREPROCESS_MODES),
            "results": json_results,
        }, stream, indent=2)
```

to:

```python
    consistency = alignment_quality.summarize_consistency(json_results) if all_tiles else None

    with open(os.path.join(output_dir, _JSON_NAME), "w", encoding="utf-8") as stream:
        json.dump({
            "config": _json_safe(base_cfg),
            "requested_coordinates": coordinates,
            "preprocess_modes": list(PREPROCESS_MODES),
            "results": json_results,
            "consistency": consistency,
        }, stream, indent=2)
```

- [ ] **Step 9: Run test to verify it passes**

Run: `python -m unittest tests.test_experiment_runner -v` (from `tools/ecc_sandbox/`)
Expected: PASS. Then run the full suite: `python -m unittest discover -s tests -v` (from
`tools/ecc_sandbox/`) — expect every test across all files (`test_preprocess`,
`test_coarse_alignment`, `test_alignment_quality`, `test_pyramid_ecc`, `test_experiment_runner`,
`test_pitch_diagnostics`, `test_chamfer_alignment`, `test_pairs`) to pass.

- [ ] **Step 10: Commit**

```bash
git add tools/ecc_sandbox/experiment_runner.py tools/ecc_sandbox/tests/test_experiment_runner.py
git commit -m "Wire consistency summary into --all-tiles experiment output"
```

---

## Real Dataset Validation (manual, after Task 10 — `[USER]`)

All ten tasks above are verified by synthetic-array unit tests only. Per `AGENTS.md` §4, running
against the real 4192/4240/4320 datasets under `H:\005_Project\AOI_2026_07_imp\20260813\` and
judging the outcome is the user's step, not something to claim as "done" from unit tests alone.
This mirrors the spec's own Test Strategy closing paragraph — the actual success signal is this
before/after comparison, not test-pass counts.

Suggested sequence once all ten tasks are merged:

1. Run `pitch_diagnostics.py` on all three FOVs to get a measured `PitchCorrectionPxPerStepX/Y` per
   dataset (right-direction `mean_dx`, bottom-direction `mean_dy`).
2. Run `experiment_runner.py --all-tiles` once per FOV with `PitchCorrectionPxPerStepX/Y` left at
   `0.0` (baseline, same as the 0/42-Verified run already on record).
3. Run it again with `PitchCorrectionPxPerStepX/Y` set to step 1's measured values (this requires
   passing those two values through to `config.DEFAULTS` or overriding them before calling
   `run_experiment` — no CLI flag for this was added in Task 6, since the spec scopes
   `PitchCorrectionPxPerStepX/Y` as a `config` dict entry, consumed via `app.py`'s GUI fields or a
   direct Python call; if a CLI flag turns out to be more convenient at this point, that is a small
   follow-up, not part of this plan).
4. Compare `Verified`/`Uncertain`/`Rejected` counts between steps 2 and 3, and specifically whether
   `(4,0)`'s translation error (currently ≈ −280px, right at the `MaxTranslationPixels=300` bound)
   collapses to a small residual.
5. Read `experiment_results.json["consistency"]` from both runs and check whether `scale_spread`
   shrinks and `translation_x_per_column`/`translation_y_per_row` slopes move toward zero once
   pitch-corrected seeding is applied — this is the same signature documented in the spec's
   Evidence section, now measurable directly instead of only inferred from Findings.md.

---

## Execution Handoff

Once every task's tests pass and the real-dataset validation above has been run by the user, the
plan is complete.
