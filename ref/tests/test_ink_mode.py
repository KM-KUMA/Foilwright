"""Unit tests for the ``auto`` ink specification method
(foilwright_ref.raster.to_planes_auto, DOMAIN.md §6.6 / D-016).

See docs/DOMAIN.md §6.6 (the 3 ink-specification methods and the `auto`
dispatch rule), §6.3.2 (spot matching rule), §6.2 (field meanings), §4.3
(one pass = one cartridge), §4.2 (1bit planes).
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config, raster

PALETTE_DIR = REPO_ROOT / "palette"

CMYK_MAP = {"C": "cyan", "M": "magenta", "Y": "yellow", "K": "black_process"}


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


def _union(*planes: bytes) -> bytes:
    length = len(planes[0])
    result = bytearray(length)
    for plane in planes:
        assert len(plane) == length
        for i, byte in enumerate(plane):
            result[i] |= byte
    return bytes(result)


# ---------------------------------------------------------------------------
# realistic scenario: default.yaml palette, white/red/blue/pure-white image
# ---------------------------------------------------------------------------


def test_realistic_mixed_spot_and_cmyk_image():
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    pixels = [
        (230, 230, 230),  # spot white (magic colour, exact)
        (255, 0, 0),  # red -- no spot match, goes to CMYK
        (0, 0, 255),  # blue -- no spot match, goes to CMYK
        (255, 255, 255),  # pure white -- matches nothing at all
    ]
    image = _make_image(pixels, width=4, height=1)
    planes = raster.to_planes_auto(image, inks, CMYK_MAP)

    row_bytes = 1

    # pixel 0: spot white only, not on any CMYK plane.
    assert _bit(planes["white"], row_bytes, 0, 0) == 1
    for name in CMYK_MAP.values():
        assert _bit(planes[name], row_bytes, 0, 0) == 0

    # pixel 1 (red = 255,0,0): CMYK separation gives C=0,M=255,Y=255,K=0.
    assert _bit(planes["magenta"], row_bytes, 1, 0) == 1
    assert _bit(planes["yellow"], row_bytes, 1, 0) == 1
    assert _bit(planes["cyan"], row_bytes, 1, 0) == 0
    assert _bit(planes["black_process"], row_bytes, 1, 0) == 0
    # not on any spot plane (other than white's auto_undercoat union,
    # checked separately below). D-019 でパレットにプロセスインクが
    # 入ったため、特色(magic_rgb を持つもの)だけを見る。
    for ink in inks:
        if ink["name"] == "white" or ink["magic_rgb"] is None:
            continue
        assert _bit(planes[ink["name"]], row_bytes, 1, 0) == 0

    # pixel 2 (blue = 0,0,255): CMYK separation gives C=255,M=255,Y=0,K=0.
    assert _bit(planes["cyan"], row_bytes, 2, 0) == 1
    assert _bit(planes["magenta"], row_bytes, 2, 0) == 1
    assert _bit(planes["yellow"], row_bytes, 2, 0) == 0
    assert _bit(planes["black_process"], row_bytes, 2, 0) == 0
    for ink in inks:
        if ink["name"] == "white" or ink["magic_rgb"] is None:
            continue
        assert _bit(planes[ink["name"]], row_bytes, 2, 0) == 0

    # pixel 3 (pure white 255,255,255): matches no spot ink, and CMYK
    # separation of pure white is C=M=Y=K=0 -- lands on nothing.
    for name in CMYK_MAP.values():
        assert _bit(planes[name], row_bytes, 3, 0) == 0

    # white is auto_undercoat: its plane must be the union of every
    # other plane (spot and CMYK alike) plus its own direct match --
    # i.e. pixels 0, 1, 2 but not pixel 3.
    assert _bit(planes["white"], row_bytes, 0, 0) == 1
    assert _bit(planes["white"], row_bytes, 1, 0) == 1
    assert _bit(planes["white"], row_bytes, 2, 0) == 1
    assert _bit(planes["white"], row_bytes, 3, 0) == 0


def test_spot_match_excludes_pixel_from_cmyk_planes():
    # A pixel matching a spot ink must never also appear on a CMYK plane
    # (DOMAIN.md §4.3: one pass = one cartridge -- no double printing).
    inks = [_ink("gold", (225, 160, 0), tolerance=10, order=50)]
    image = _make_image([(225, 160, 0)], width=1, height=1)
    planes = raster.to_planes_auto(image, inks, CMYK_MAP)
    assert _bit(planes["gold"], 1, 0, 0) == 1
    for name in CMYK_MAP.values():
        assert _bit(planes[name], 1, 0, 0) == 0


def test_auto_undercoat_union_includes_both_spot_and_cmyk():
    inks = [
        _ink("white", (230, 230, 230), tolerance=8, order=10, auto_undercoat=True),
        _ink("gold", (225, 160, 0), tolerance=10, order=50),
    ]
    pixels = [
        (225, 160, 0),  # spot: gold
        (255, 0, 0),  # cmyk: red -> magenta + yellow
        (255, 255, 255),  # matches nothing
    ]
    image = _make_image(pixels, width=3, height=1)
    planes = raster.to_planes_auto(image, inks, CMYK_MAP)

    row_bytes = 1
    assert _bit(planes["gold"], row_bytes, 0, 0) == 1
    assert _bit(planes["magenta"], row_bytes, 1, 0) == 1
    assert _bit(planes["yellow"], row_bytes, 1, 0) == 1

    # white (auto_undercoat) covers pixel 0 (gold) and pixel 1 (cmyk red),
    # but not pixel 2 (unmatched).
    assert _bit(planes["white"], row_bytes, 0, 0) == 1
    assert _bit(planes["white"], row_bytes, 1, 0) == 1
    assert _bit(planes["white"], row_bytes, 2, 0) == 0


def test_multiple_auto_undercoat_rejected():
    inks = [
        _ink("under1", (0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink("under2", (10, 10, 10), tolerance=0, order=20, auto_undercoat=True),
    ]
    image = _make_image([(0, 0, 0)], width=1, height=1)
    with pytest.raises(ValueError):
        raster.to_planes_auto(image, inks, CMYK_MAP)


# ---------------------------------------------------------------------------
# no-regression: with no spot matches at all, result must equal to_planes
# ---------------------------------------------------------------------------


def test_no_spot_match_matches_to_planes_exactly():
    # An image with colours nowhere near any spot ink's magic_rgb must
    # decompose identically to plain to_planes (no spot ink ever fires).
    inks = [
        _ink("gold", (225, 160, 0), tolerance=5, order=50),
        _ink("silver", (189, 193, 197), tolerance=5, order=50),
    ]
    width, height = 6, 3
    pixels = [
        (255, 0, 0),
        (0, 255, 0),
        (0, 0, 255),
        (10, 20, 30),
        (128, 64, 200),
        (0, 0, 0),
    ] * height
    image = _make_image(pixels, width=width, height=height)

    palette = {"cyan": "C", "magenta": "M", "yellow": "Y", "black_process": "K"}
    expected = raster.to_planes(image, palette)

    planes = raster.to_planes_auto(image, inks, CMYK_MAP)
    for channel_name, expected_bytes in expected.items():
        # palette maps ink name -> channel; CMYK_MAP maps channel -> ink
        # name, so translate via the shared channel letter.
        channel = palette[channel_name]
        auto_name = CMYK_MAP[channel]
        assert planes[auto_name] == expected_bytes

    # and no spot ink ever fired.
    for ink in inks:
        assert planes[ink["name"]] == bytes(len(planes[ink["name"]]))


# ---------------------------------------------------------------------------
# halftone forwarding to the CMYK side
# ---------------------------------------------------------------------------


def test_halftone_forwarded_to_cmyk_side():
    inks = [_ink("gold", (225, 160, 0), tolerance=5, order=50)]
    width, height = 12, 12
    pixels = [(128, 64, 200)] * (width * height)
    image = _make_image(pixels, width=width, height=height)

    palette = {"cyan": "C", "magenta": "M", "yellow": "Y", "black_process": "K"}
    expected = raster.to_planes(image, palette, halftone="halftone")

    planes = raster.to_planes_auto(image, inks, CMYK_MAP, halftone="halftone")
    for channel_name, expected_bytes in expected.items():
        channel = palette[channel_name]
        auto_name = CMYK_MAP[channel]
        assert planes[auto_name] == expected_bytes

    # sanity: halftoned output must differ from the "none" output for
    # this non-trivial image (otherwise the test would not actually be
    # exercising the halftone path).
    planes_flat = raster.to_planes_auto(image, inks, CMYK_MAP, halftone="none")
    assert planes["black_process"] != planes_flat["black_process"]


def test_unknown_halftone_mode_rejected():
    inks = [_ink("gold", (225, 160, 0), tolerance=5, order=50)]
    image = _make_image([(0, 0, 0)], width=1, height=1)
    with pytest.raises(ValueError):
        raster.to_planes_auto(image, inks, CMYK_MAP, halftone="bogus")
