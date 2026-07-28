"""Unit tests for magic-colour ink separation (foilwright_ref.raster.to_planes_magic).

See docs/DOMAIN.md §6.3.2 (matching rule), §6.3 (tolerance background),
§6.1/§6.2 (palette schema, auto_undercoat), §4.2 (1bit planes), D-015
(integer-only, max-of-channels distance, order tie-break).
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config, raster

PALETTE_DIR = REPO_ROOT / "palette"


def _make_image(pixels_rgb: list[tuple[int, int, int]], width: int, height: int):
    """Build a (width, height, pixels) tuple as read_ppm would return."""
    assert len(pixels_rgb) == width * height
    buf = bytearray()
    for r, g, b in pixels_rgb:
        buf.extend((r, g, b))
    return width, height, bytes(buf)


def _ink(name, magic_rgb, tolerance, order, auto_undercoat=False):
    return {
        "name": name,
        "magic_rgb": list(magic_rgb),
        "tolerance": tolerance,
        "order": order,
        "auto_undercoat": auto_undercoat,
    }


def _bit(plane: bytes, row_bytes: int, x: int, y: int) -> int:
    byte = plane[y * row_bytes + (x >> 3)]
    return 1 if byte & (0x80 >> (x & 7)) else 0


# ---------------------------------------------------------------------------
# default.yaml palette, real inks
# ---------------------------------------------------------------------------


def test_default_palette_separates_white_and_silver():
    # White is not confused with silver at the exact-match level: each
    # pixel is assigned to exactly one non-undercoat ink. (`white` is
    # itself auto_undercoat, so it additionally picks up silver's pixel
    # as part of its union -- that is covered separately below.)
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    pixels = [
        (230, 230, 230),  # white magic colour, exact
        (189, 193, 197),  # metallic_silver magic colour, exact
    ]
    image = _make_image(pixels, width=2, height=1)
    planes = raster.to_planes_magic(image, inks)

    row_bytes = 1
    assert _bit(planes["metallic_silver"], row_bytes, 0, 0) == 0
    assert _bit(planes["metallic_silver"], row_bytes, 1, 0) == 1
    # white's own direct match is pixel 0 only; pixel 1 is silver's, and
    # only appears in white's plane via the auto_undercoat union.
    assert _bit(planes["white"], row_bytes, 0, 0) == 1
    assert _bit(planes["white"], row_bytes, 1, 0) == 1


def test_default_palette_pure_white_matches_nothing():
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    image = _make_image([(255, 255, 255)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    for name, plane in planes.items():
        if name == "white":
            # white is auto_undercoat, but nothing else matched either,
            # so its union plane must still be empty.
            assert plane == bytes(len(plane))
        else:
            assert plane == bytes(len(plane))


def test_default_palette_white_auto_undercoat_is_union_of_other_inks():
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    pixels = [
        (0, 0, 0),  # black
        (225, 160, 0),  # metallic_gold
        (255, 255, 255),  # nothing
    ]
    image = _make_image(pixels, width=3, height=1)
    planes = raster.to_planes_magic(image, inks)

    row_bytes = 1
    assert _bit(planes["black"], row_bytes, 0, 0) == 1
    assert _bit(planes["metallic_gold"], row_bytes, 1, 0) == 1
    # white auto-undercoat covers both matched pixels, but not the
    # unmatched (255,255,255) one.
    assert _bit(planes["white"], row_bytes, 0, 0) == 1
    assert _bit(planes["white"], row_bytes, 1, 0) == 1
    assert _bit(planes["white"], row_bytes, 2, 0) == 0


# ---------------------------------------------------------------------------
# matching rule (tolerance boundaries)
# ---------------------------------------------------------------------------


def test_tolerance_boundary_matches_exactly_at_tolerance():
    inks = [_ink("a", (100, 100, 100), tolerance=5, order=10)]
    image = _make_image([(105, 95, 105)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["a"], 1, 0, 0) == 1


def test_tolerance_boundary_does_not_match_one_over():
    inks = [_ink("a", (100, 100, 100), tolerance=5, order=10)]
    image = _make_image([(106, 100, 100)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["a"], 1, 0, 0) == 0


def test_unmatched_pixel_is_set_in_no_plane():
    inks = [
        _ink("a", (10, 10, 10), tolerance=2, order=10),
        _ink("b", (200, 200, 200), tolerance=2, order=20),
    ]
    image = _make_image([(128, 128, 128)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["a"], 1, 0, 0) == 0
    assert _bit(planes["b"], 1, 0, 0) == 0


# ---------------------------------------------------------------------------
# multiple-match resolution: closest wins, then order, then file position
# ---------------------------------------------------------------------------


def test_multiple_match_closest_wins():
    # pixel (100,100,100): distance to 'near' is max(2,2,2)=2,
    # distance to 'far' is max(6,6,6)=6. 'near' must win.
    inks = [
        _ink("far", (94, 94, 94), tolerance=10, order=10),
        _ink("near", (98, 98, 98), tolerance=10, order=20),
    ]
    image = _make_image([(100, 100, 100)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["near"], 1, 0, 0) == 1
    assert _bit(planes["far"], 1, 0, 0) == 0


def test_multiple_match_tie_broken_by_order():
    # Both inks are equidistant (max deviation 2); 'lower_order' has the
    # smaller `order` and must win regardless of list position.
    inks = [
        _ink("higher_order", (98, 100, 100), tolerance=10, order=20),
        _ink("lower_order", (98, 100, 100), tolerance=10, order=5),
    ]
    image = _make_image([(100, 100, 100)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["lower_order"], 1, 0, 0) == 1
    assert _bit(planes["higher_order"], 1, 0, 0) == 0


def test_multiple_match_tie_broken_by_file_position_when_order_equal():
    # Equidistant, equal order: the ink listed first in `inks` must win.
    inks = [
        _ink("first", (98, 100, 100), tolerance=10, order=10),
        _ink("second", (98, 100, 100), tolerance=10, order=10),
    ]
    image = _make_image([(100, 100, 100)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["first"], 1, 0, 0) == 1
    assert _bit(planes["second"], 1, 0, 0) == 0


def test_multiple_match_using_tmp_palette_file(tmp_path):
    palette_path = tmp_path / "close.yaml"
    palette_path.write_text(
        """
inks:
  - name: ink_a
    label: A
    magic_rgb: [100, 100, 100]
    printer_code: 0x01
    tolerance: 10
    order: 10
  - name: ink_b
    label: B
    magic_rgb: [104, 104, 104]
    printer_code: 0x02
    tolerance: 10
    order: 20
""",
        encoding="utf-8",
    )
    inks = config.load_palette(str(palette_path))
    # pixel is closer to ink_b (distance 2) than ink_a (distance 6).
    image = _make_image([(106, 106, 106)], width=1, height=1)
    planes = raster.to_planes_magic(image, inks)
    assert _bit(planes["ink_b"], 1, 0, 0) == 1
    assert _bit(planes["ink_a"], 1, 0, 0) == 0


# ---------------------------------------------------------------------------
# auto_undercoat
# ---------------------------------------------------------------------------


def test_auto_undercoat_is_union_of_other_inks_plus_own_match():
    inks = [
        _ink("under", (0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink("a", (10, 10, 10), tolerance=0, order=20),
        _ink("b", (20, 20, 20), tolerance=0, order=30),
    ]
    pixels = [
        (0, 0, 0),  # matches 'under' directly
        (10, 10, 10),  # matches 'a'
        (20, 20, 20),  # matches 'b'
        (255, 255, 255),  # matches nothing
    ]
    image = _make_image(pixels, width=4, height=1)
    planes = raster.to_planes_magic(image, inks)

    row_bytes = 1
    assert _bit(planes["a"], row_bytes, 1, 0) == 1
    assert _bit(planes["b"], row_bytes, 2, 0) == 1
    # under = union of its own direct match + a's + b's pixels.
    assert _bit(planes["under"], row_bytes, 0, 0) == 1
    assert _bit(planes["under"], row_bytes, 1, 0) == 1
    assert _bit(planes["under"], row_bytes, 2, 0) == 1
    assert _bit(planes["under"], row_bytes, 3, 0) == 0


def test_multiple_auto_undercoat_rejected():
    inks = [
        _ink("under1", (0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink("under2", (10, 10, 10), tolerance=0, order=20, auto_undercoat=True),
    ]
    image = _make_image([(0, 0, 0)], width=1, height=1)
    with pytest.raises(ValueError):
        raster.to_planes_magic(image, inks)


# ---------------------------------------------------------------------------
# byte-packing format matches to_planes
# ---------------------------------------------------------------------------


def test_byte_packing_matches_to_planes_format():
    # Construct a scenario where the CMYK-based to_planes and the
    # magic-colour to_planes_magic agree on which pixels are set, so the
    # packed byte layout (MSB-first, row padded to byte boundary) can be
    # compared directly between the two functions.
    width, height = 10, 2

    # Pure black pixels: CMYK separation puts these fully into K
    # (values = {"C": 0, "M": 0, "Y": 0, "K": 255}), and the magic
    # palette below maps the same pixels to ink 'k' via exact match.
    pixels = [(0, 0, 0)] * (width * height)
    image = _make_image(pixels, width=width, height=height)

    cmyk_palette = {"k": "K"}
    planes_cmyk = raster.to_planes(image, cmyk_palette)

    magic_inks = [_ink("k", (0, 0, 0), tolerance=0, order=10)]
    planes_magic = raster.to_planes_magic(image, magic_inks)

    row_bytes = (width + 7) // 8
    assert len(planes_cmyk["k"]) == row_bytes * height
    assert len(planes_magic["k"]) == row_bytes * height
    assert planes_cmyk["k"] == planes_magic["k"]
