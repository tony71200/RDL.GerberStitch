# ECC Affine Normalization — Design

## Goal

Update the Python-only ECC sandbox so that:

- ECC defaults to the `Affine` motion model.
- The UI provides an affine scale-normalization combobox with `median` and `min`; `min` is the default.
- A final affine result is converted to one uniform scale after ECC finishes.
- Rotation outside `MaxAbsRotationDeg` keeps its sign and is clamped to the configured limit instead of being rejected.

This remains an experimental sandbox change. It does not modify the C# pipeline or the behavior of
`GerberStitching.Core`.

## Scope

Modify only:

- `tools/ecc_sandbox/config.py`
- `tools/ecc_sandbox/app.py`
- `tools/ecc_sandbox/pyramid_ecc.py`
- `tools/ecc_sandbox/README.md`

Preserve the user's unrelated, uncommitted preprocessing changes in
`tools/ecc_sandbox/preprocess.py`.

## Configuration and UI

`config.DEFAULTS` uses:

```python
"EccMotionModel": "Affine"
"AffineNormalize": "min"
```

The PyramidECC UI group contains a read-only combobox for `AffineNormalize`. Its allowed values are
exactly `median` and `min`, with `min` initially selected. `App.current_cfg()` copies the selected value
into the configuration passed to `pyramid_ecc.match()`.

The existing motion-model combobox remains available so the sandbox can still compare Affine with
Euclidean or Translation. Affine normalization applies only when `EccMotionModel == "Affine"`.

## Transform Post-processing

OpenCV ECC is allowed to optimize the full affine transform at every pyramid level. Normalization runs
once, after the final `ReferenceImage -> MovingImage` result has been inverted into the sandbox's public
`MovingImage -> ReferenceImage` direction.

For the final matrix `M`:

```text
scaleX = hypot(M[0,0], M[1,0])
scaleY = hypot(M[0,1], M[1,1])
rawRotation = degrees(atan2(M[1,0], M[0,0]))
```

The uniform scale is selected as follows:

- `median`: median of the two scale values. For exactly two values this is their arithmetic mean,
  `(scaleX + scaleY) / 2`.
- `min`: `min(scaleX, scaleY)`.

Rotation is clamped with its sign preserved:

```text
clampedRotation = max(-rotationMax, min(rawRotation, rotationMax))
```

The normalized linear part is rebuilt as a similarity transform:

```text
[ s*cos(a)  -s*sin(a) ]
[ s*sin(a)   s*cos(a) ]
```

Translation `M[0,2]` and `M[1,2]` is preserved exactly. Rebuilding the linear part intentionally removes
anisotropic scale and shear from the affine result.

## Result and Validation Semantics

The normalized matrix is the authoritative result used by:

- `result["matrix"]`
- reported translation, rotation and scale
- geometry validation
- preview warping

Validation order otherwise stays unchanged. Correlation, translation and scale constraints continue to
reject invalid results. Rotation no longer fails merely for exceeding `MaxAbsRotationDeg`, because it is
clamped before validation.

Diagnostics expose enough data to compare raw ECC output with the normalized transform:

- raw `scaleX` and `scaleY`
- selected normalization mode
- raw rotation and final rotation
- whether rotation was clamped

For Euclidean and Translation modes, the transform is not rebuilt and existing behavior is preserved.

## Error Handling

- Reject unknown `AffineNormalize` values with a clear `ValueError`; the UI prevents them during normal use.
- Reject non-finite or degenerate extracted affine scales before attempting to rebuild the matrix.
- Treat `MaxAbsRotationDeg` as an absolute non-negative limit when clamping.
- Preserve the current ECC runtime-error handling and failure result structure.

## Verification

Project instructions reserve execution testing for the user. Implementation verification therefore consists
of static review plus user-run sandbox cases:

1. Start the UI and confirm `Affine` and `min` are selected by default.
2. Run an affine case where `scaleX != scaleY`; confirm `min` produces the smaller uniform scale.
3. Switch to `median`; confirm the uniform scale equals `(scaleX + scaleY) / 2`.
4. Run positive and negative rotations beyond the configured limit; confirm the final angles are respectively
   `+MaxAbsRotationDeg` and `-MaxAbsRotationDeg` and the result is not rejected for rotation.
5. Confirm the preview uses the same normalized matrix printed in the log.
6. Select Euclidean and confirm its existing transform behavior is unchanged.

