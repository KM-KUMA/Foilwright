"""Unit tests for the "alpha" white mode building blocks
(foilwright_ref.job.compute_alpha_plane / apply_alpha_white_mode,
DOMAIN.md §7.1 / §7.1.1 / D-037).

Rule (D-037): alpha > 0 means white. Colour never enters the judgement --
the alpha image (from Ghostscript's pngalpha device) and the colour image
(from ppmraw) are two independent inputs, and only the alpha channel of
the former decides which pixels are white. These tests exercise
compute_alpha_plane/apply_alpha_white_mode directly (bypassing
job.build_job_planes/apply_white_mode) and confirm that
to_planes_magic/to_planes_auto -- the golden-verified functions behind
"none"/"auto"/"magic" -- are untouched by their presence in the same
package.
"""

from __future__ import annotations

import pathlib
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import job as job_module
from foilwright_ref import raster


def _make_image(pixels_rgb: list[tuple[int, int, int]], width: int, height: int):
    assert len(pixels_rgb) == width * height
    buf = bytearray()
    for r, g, b in pixels_rgb:
        buf.extend((r, g, b))
    return width, height, bytes(buf)


def _make_alpha_image(alphas: list[int], width: int, height: int):
    assert len(alphas) == width * height
    buf = bytearray()
    for a in alphas:
        buf.extend((0, 0, 0, a))
    return width, height, bytes(buf)


def _ink(name, magic_rgb=None, tolerance=None, order=10, auto_undercoat=False):
    ink = {"name": name, "order": order, "auto_undercoat": auto_undercoat}
    if magic_rgb is not None:
        ink["magic_rgb"] = list(magic_rgb)
        ink["tolerance"] = tolerance
    else:
        ink["magic_rgb"] = None
    return ink


def _bit(plane: bytes, row_bytes: int, x: int, y: int) -> int:
    byte = plane[y * row_bytes + (x >> 3)]
    return 1 if byte & (0x80 >> (x & 7)) else 0


# ---------------------------------------------------------------------------
# compute_alpha_plane
# ---------------------------------------------------------------------------


def test_all_zero_alpha_plane_is_empty():
    _, _, rgba = _make_alpha_image([0, 0], width=2, height=1)
    plane = job_module.compute_alpha_plane(2, 1, rgba)
    assert plane == bytes(1)


def test_alpha_greater_than_zero_sets_bit_regardless_of_value():
    # x=0: alpha=0 -> unset. x=1: alpha=1 (barely non-zero) -> set.
    # x=2: alpha=255 (fully opaque) -> set. D-037's rule is "alpha > 0",
    # not "alpha == 255".
    _, _, rgba = _make_alpha_image([0, 1, 255], width=3, height=1)
    plane = job_module.compute_alpha_plane(3, 1, rgba)
    assert _bit(plane, 1, 0, 0) == 0
    assert _bit(plane, 1, 1, 0) == 1
    assert _bit(plane, 1, 2, 0) == 1


def test_colour_channels_of_alpha_image_are_ignored():
    # Non-zero RGB with alpha=0 must still be unset -- only the alpha
    # channel is read (D-037: pngalpha's RGB is discarded entirely).
    width, height = 1, 1
    rgba = bytes((255, 128, 64, 0))
    plane = job_module.compute_alpha_plane(width, height, rgba)
    assert _bit(plane, 1, 0, 0) == 0


def test_multi_row_plane_uses_row_padding_like_other_planes():
    # 9 wide -> row_bytes = ceil(9/8) = 2. Row 0: only x=8 has alpha>0.
    # Row 1: only x=0 has alpha>0.
    row0 = [0] * 8 + [255]
    row1 = [255] + [0] * 8
    _, _, rgba = _make_alpha_image(row0 + row1, width=9, height=2)
    plane = job_module.compute_alpha_plane(9, 2, rgba)
    assert len(plane) == 2 * 2
    assert _bit(plane, 2, 8, 0) == 1
    assert _bit(plane, 2, 0, 0) == 0
    assert _bit(plane, 2, 0, 1) == 1
    assert _bit(plane, 2, 8, 1) == 0


# ---------------------------------------------------------------------------
# apply_alpha_white_mode
# ---------------------------------------------------------------------------


def test_apply_alpha_white_mode_merges_into_existing_white_plane():
    # Mirrors test_apply_opaque_white_mode_merges_into_existing_white_plane
    # (test_white_mode_opaque.py), but the alpha-derived pixels are chosen
    # to *disagree* with colour-derived matches, to prove colour plays no
    # role: x=0 is red's own magic_rgb match (colour-driven, from the
    # baseline plane) but alpha=0 there; x=2 is white's own magic_rgb
    # direct match (colour-driven, from the baseline plane) but alpha=0
    # there too -- it must still show up in the merged result because the
    # baseline (direct magic_rgb match) is kept, per apply_white_mode's
    # "alpha" handling being the same as "magic"/"opaque"/"silhouette".
    original_inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    adjusted_inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=False,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    pixels = [
        (255, 0, 0),  # red: matched by spot
        (0, 255, 255),  # unmatched by any spot ink
        (230, 230, 230),  # white's own magic_rgb, direct match
        (255, 255, 255),  # pure white
    ]
    image = _make_image(pixels, width=4, height=1)
    alpha_image = _make_alpha_image([0, 255, 0, 255], width=4, height=1)
    baseline = raster.to_planes_magic(image, adjusted_inks)

    result = job_module.apply_alpha_white_mode(alpha_image, original_inks, baseline)

    assert _bit(result["white"], 1, 0, 0) == 0  # alpha=0, no colour match either
    assert _bit(result["white"], 1, 1, 0) == 1  # alpha=255
    assert (
        _bit(result["white"], 1, 2, 0) == 1
    )  # baseline direct magic match kept even though alpha=0
    assert (
        _bit(result["white"], 1, 3, 0) == 1
    )  # alpha=255, even though pure white -- unlike opaque

    # red's own plane is untouched by the merge.
    assert _bit(result["red"], 1, 0, 0) == 1
    assert _bit(result["red"], 1, 1, 0) == 0


def test_apply_alpha_white_mode_does_not_mutate_input_dict():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    alpha_image = _make_alpha_image([255], width=1, height=1)
    baseline = {"white": bytes(1)}
    baseline_copy = dict(baseline)

    job_module.apply_alpha_white_mode(alpha_image, inks, baseline)

    assert baseline == baseline_copy


def test_apply_alpha_white_mode_zero_undercoat_inks_returns_planes_unchanged():
    inks = [_ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=10)]
    alpha_image = _make_alpha_image([255], width=1, height=1)
    planes = {"red": bytes(1)}

    result = job_module.apply_alpha_white_mode(alpha_image, inks, planes)

    assert result is planes


def test_apply_alpha_white_mode_multiple_undercoat_inks_returns_planes_unchanged():
    inks = [
        _ink("under1", magic_rgb=(0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink(
            "under2", magic_rgb=(10, 10, 10), tolerance=0, order=20, auto_undercoat=True
        ),
    ]
    alpha_image = _make_alpha_image([255], width=1, height=1)
    planes = {"under1": bytes(1), "under2": bytes(1)}

    result = job_module.apply_alpha_white_mode(alpha_image, inks, planes)

    assert result is planes


def test_apply_alpha_white_mode_creates_plane_if_missing():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    alpha_image = _make_alpha_image([255], width=1, height=1)

    result = job_module.apply_alpha_white_mode(alpha_image, inks, {})

    assert _bit(result["white"], 1, 0, 0) == 1


# ---------------------------------------------------------------------------
# build_job_planes: white_mode == "alpha" wiring/validation
# ---------------------------------------------------------------------------


def test_build_job_planes_alpha_mode_requires_alpha_image():
    palette = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    image = _make_image([(255, 0, 0)], width=1, height=1)

    try:
        job_module.build_job_planes(image, palette, "auto", white_mode="alpha")
    except ValueError:
        pass
    else:
        raise AssertionError("expected ValueError when alpha_image is missing")


def test_build_job_planes_alpha_mode_rejects_dimension_mismatch():
    palette = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    image = _make_image([(255, 0, 0), (255, 0, 0)], width=2, height=1)
    mismatched_alpha_image = _make_alpha_image([255], width=1, height=1)

    try:
        job_module.build_job_planes(
            image,
            palette,
            "auto",
            white_mode="alpha",
            alpha_image=mismatched_alpha_image,
        )
    except ValueError:
        pass
    else:
        raise AssertionError(
            "expected ValueError on alpha_image/image dimension mismatch"
        )


def test_build_job_planes_alpha_mode_uses_alpha_channel():
    palette = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        ),
        _ink("cyan", order=60),
    ]
    palette[1]["channel"] = "C"
    pixels = [(255, 0, 0), (255, 255, 255)]
    image = _make_image(pixels, width=2, height=1)
    alpha_image = _make_alpha_image([0, 255], width=2, height=1)

    _inks, by_name = job_module.build_job_planes(
        image, palette, "auto", white_mode="alpha", alpha_image=alpha_image
    )

    assert _bit(by_name["white"], 1, 0, 0) == 0  # alpha=0, no colour match
    assert _bit(by_name["white"], 1, 1, 0) == 1  # alpha=255, even though pure white


# ---------------------------------------------------------------------------
# no-regression: to_planes_magic/to_planes_auto (behind "none"/"auto"/"magic")
# are untouched by the new functions existing in the same module.
# ---------------------------------------------------------------------------


def test_to_planes_magic_auto_undercoat_union_still_works_unchanged():
    # Same scenario as the equivalent check in test_white_mode_opaque.py,
    # repeated here to pin that adding the alpha helpers to this module
    # did not shift to_planes_magic's behaviour by even one bit.
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=8,
            order=10,
            auto_undercoat=True,
        ),
        _ink("gold", magic_rgb=(225, 160, 0), tolerance=10, order=50),
    ]
    pixels = [(225, 160, 0), (255, 255, 255)]
    image = _make_image(pixels, width=2, height=1)
    planes = raster.to_planes_magic(image, inks)

    assert _bit(planes["gold"], 1, 0, 0) == 1
    assert _bit(planes["white"], 1, 0, 0) == 1
    assert _bit(planes["white"], 1, 1, 0) == 0
