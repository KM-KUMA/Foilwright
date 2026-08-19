"""Unit tests for the "opaque" white mode building blocks
(foilwright_ref.raster.compute_non_white_pixel_plane /
apply_opaque_white_mode, DOMAIN.md §6.1 / §7.1 / D-032).

There is no JobAssembly-equivalent layer in ref/ (see raster.py's
apply_opaque_white_mode docstring): D-027's white-mode selector
(none/auto/magic) was never built out here, only the underlying
`auto_undercoat` flag that to_planes_magic/to_planes_auto honour. These
tests therefore exercise the two new functions directly, and confirm
that to_planes_magic/to_planes_auto -- the golden-verified functions
behind "none"/"auto"/"magic" -- are untouched.
"""

from __future__ import annotations

import pathlib
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import raster


def _make_image(pixels_rgb: list[tuple[int, int, int]], width: int, height: int):
    assert len(pixels_rgb) == width * height
    buf = bytearray()
    for r, g, b in pixels_rgb:
        buf.extend((r, g, b))
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
# compute_non_white_pixel_plane
# ---------------------------------------------------------------------------


def test_all_pure_white_image_plane_is_empty():
    image = _make_image([(255, 255, 255), (255, 255, 255)], width=2, height=1)
    plane = raster.compute_non_white_pixel_plane(image)
    assert plane == bytes(1)


def test_every_non_pure_white_pixel_is_set():
    # x=0: red; x=1: near-white but not pure; x=2: pure white -> excluded.
    pixels = [(255, 0, 0), (254, 254, 254), (255, 255, 255)]
    image = _make_image(pixels, width=3, height=1)
    plane = raster.compute_non_white_pixel_plane(image)
    assert _bit(plane, 1, 0, 0) == 1
    assert _bit(plane, 1, 1, 0) == 1
    assert _bit(plane, 1, 2, 0) == 0


def test_near_white_but_not_pure_is_included():
    # This is the core difference from "auto" (D-032): a bright pixel
    # that is not exactly (255,255,255) still gets a bit, even though it
    # would not be assigned to any ink under CMYK separation or spot
    # matching (both treat it as effectively blank).
    image = _make_image([(254, 254, 254)], width=1, height=1)
    plane = raster.compute_non_white_pixel_plane(image)
    assert _bit(plane, 1, 0, 0) == 1


# ---------------------------------------------------------------------------
# apply_opaque_white_mode
# ---------------------------------------------------------------------------


def test_apply_opaque_white_mode_merges_into_existing_white_plane():
    # Two ink lists, mirroring how JobAssembly.BuildJobPlanes calls this:
    #   - `original_inks`: auto_undercoat=True on white, used only to
    #     *identify* which ink is white (same rule as
    #     to_planes_magic/to_planes_auto and JobAssembly.ApplyOpaqueWhite).
    #   - `adjusted_inks`: auto_undercoat forced False, used to build the
    #     baseline plane dict -- this reproduces what
    #     JobAssembly.ApplyWhiteMode builds for "opaque" (direct
    #     magic_rgb match only, no union step) before the opaque mask is
    #     layered on top.
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
        (255, 0, 0),  # red: matched by spot, not pure white
        (0, 255, 255),  # unmatched by any spot ink, not pure white
        (230, 230, 230),  # white's own magic_rgb, direct match
        (255, 255, 255),  # pure white -- excluded from opaque
    ]
    image = _make_image(pixels, width=4, height=1)
    baseline = raster.to_planes_magic(image, adjusted_inks)

    result = raster.apply_opaque_white_mode(image, original_inks, baseline)

    assert _bit(result["white"], 1, 0, 0) == 1  # opaque: not pure white
    assert _bit(result["white"], 1, 1, 0) == 1  # opaque: not pure white
    assert _bit(result["white"], 1, 2, 0) == 1  # direct magic match (baseline) + opaque
    assert _bit(result["white"], 1, 3, 0) == 0  # pure white: excluded

    # red's own plane is untouched by the merge.
    assert _bit(result["red"], 1, 0, 0) == 1
    assert _bit(result["red"], 1, 1, 0) == 0


def test_apply_opaque_white_mode_does_not_mutate_input_dict():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    image = _make_image([(255, 0, 0)], width=1, height=1)
    baseline = {"white": bytes(1)}
    baseline_copy = dict(baseline)

    raster.apply_opaque_white_mode(image, inks, baseline)

    assert baseline == baseline_copy


def test_apply_opaque_white_mode_zero_undercoat_inks_returns_planes_unchanged():
    inks = [_ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=10)]
    image = _make_image([(255, 0, 0)], width=1, height=1)
    planes = {"red": bytes(1)}

    result = raster.apply_opaque_white_mode(image, inks, planes)

    assert result is planes


def test_apply_opaque_white_mode_multiple_undercoat_inks_returns_planes_unchanged():
    inks = [
        _ink("under1", magic_rgb=(0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink(
            "under2", magic_rgb=(10, 10, 10), tolerance=0, order=20, auto_undercoat=True
        ),
    ]
    image = _make_image([(0, 0, 0)], width=1, height=1)
    planes = {"under1": bytes(1), "under2": bytes(1)}

    result = raster.apply_opaque_white_mode(image, inks, planes)

    assert result is planes


def test_apply_opaque_white_mode_creates_plane_if_missing():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    image = _make_image([(255, 0, 0)], width=1, height=1)

    result = raster.apply_opaque_white_mode(image, inks, {})

    assert _bit(result["white"], 1, 0, 0) == 1


# ---------------------------------------------------------------------------
# no-regression: to_planes_magic/to_planes_auto (behind "none"/"auto"/"magic")
# are untouched by the new functions existing in the same module.
# ---------------------------------------------------------------------------


def test_to_planes_magic_auto_undercoat_union_still_works_unchanged():
    # Same scenario as test_ink_mode.test_auto_undercoat_union_includes_both_spot_and_cmyk,
    # but through to_planes_magic (the function "magic"/"auto" both build
    # on) to pin that its union behaviour (D-027's "auto") did not shift
    # by even one bit when the opaque helpers were added to this module.
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
    # white (auto_undercoat) covers pixel 0 (gold's union) but not
    # pixel 1 (matches nothing).
    assert _bit(planes["white"], 1, 0, 0) == 1
    assert _bit(planes["white"], 1, 1, 0) == 0
