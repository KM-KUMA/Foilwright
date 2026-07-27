"""Golden byte-exact tests for the ref/ L1 emitter + L2 raster.

Compares foilwright_ref output against tests/golden/*.bin, produced by
ppmtomd 1.6 (see tests/golden/README.md). golden files are never
modified by this test; a mismatch means the reference implementation
is wrong.
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import emitter, raster

CASES_DIR = REPO_ROOT / "tests" / "cases"
GOLDEN_DIR = REPO_ROOT / "tests" / "golden"

# Default (no -colours) job: ppmtomd always drives all four CMYK
# components in this order, even when some end up entirely blank
# (ppmtomd.c:1469-1490 default comp_colours; :84-86 comp_print_order).
_DEFAULT_PALETTE = {"cyan": "C", "magenta": "M", "yellow": "Y", "black": "K"}
_DEFAULT_INKS = [
    {"name": "cyan", "colour_code": 0x01},
    {"name": "magenta", "colour_code": 0x02},
    {"name": "yellow", "colour_code": 0x03},
    {"name": "black", "colour_code": 0x00},
]

# `-colours K=White`: only K is active, and it is driven by the White
# ink's colour-selection byte (mddata.h colWhite = 0x0B) instead of
# Black's (ppmtomd.c:1517-1525 for the K=... parse, mddata.c colour
# enum for the byte value).
_WHITE_PALETTE = {"white": "K"}
_WHITE_INKS = [{"name": "white", "colour_code": 0x0B}]

# ppmtomd's A4, 5000-series page geometry, baseline at 600dpi
# (ppmtomd.c:70-81 papersize_info_5000[paperA4]).
_A4_WIDTH_600 = 4800
_A4_LENGTH_600 = 6372


def _profile(resolution: int, model: str) -> dict:
    return {
        "model": model,  # informational only; emitter never branches on it
        "resolution": resolution,
        "paper_size": 0x04,  # paperA4
        "media_byte1": 0x00,
        "media_byte2": 0x00,
        "page_width": _A4_WIDTH_600,
        "page_length": _A4_LENGTH_600,
    }


def _render(ppm_path: pathlib.Path, profile: dict, palette: dict, inks: list) -> bytes:
    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image
    planes = raster.to_planes(image, palette)
    job_profile = dict(profile, inks=inks)
    return emitter.emit_job(planes, job_profile, {"width": width, "height": height})


def _assert_golden_match(actual: bytes, golden_path: pathlib.Path) -> None:
    expected = golden_path.read_bytes()
    if actual == expected:
        return
    limit = min(len(actual), len(expected))
    first_diff = next((i for i in range(limit) if actual[i] != expected[i]), limit)
    ctx_start = max(0, first_diff - 16)
    exp_ctx = expected[ctx_start : first_diff + 16].hex(" ")
    act_ctx = actual[ctx_start : first_diff + 16].hex(" ")
    pytest.fail(
        f"byte mismatch vs {golden_path.name} at offset {first_diff} "
        f"(expected len={len(expected)}, actual len={len(actual)})\n"
        f"expected[{ctx_start}:]: {exp_ctx}\n"
        f"actual[{ctx_start}:]:   {act_ctx}"
    )


def test_g1_black_md5000_600():
    profile = _profile(600, "MD-5000")
    actual = _render(
        CASES_DIR / "c1_black_120x120.ppm", profile, _DEFAULT_PALETTE, _DEFAULT_INKS
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g1_c1_black_md5000_600.bin")


def test_g5_black_md5500_600_profile_swap_only():
    """MD-5000 and MD-5500 must produce byte-identical output from the
    same profile values with only the (informational) model name
    changed -- there is no model-specific branch in the emitter
    (DOMAIN.md §4.4)."""
    profile = _profile(600, "MD-5500")
    actual = _render(
        CASES_DIR / "c1_black_120x120.ppm", profile, _DEFAULT_PALETTE, _DEFAULT_INKS
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g5_c1_black_md5500_600.bin")


def test_g4_black_md5000_1200():
    profile = _profile(1200, "MD-5000")
    actual = _render(
        CASES_DIR / "c1_black_120x120.ppm", profile, _DEFAULT_PALETTE, _DEFAULT_INKS
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g4_c1_black_md5000_1200.bin")


def test_g2_blackcyan_md5000_600():
    profile = _profile(600, "MD-5000")
    actual = _render(
        CASES_DIR / "c2_blackcyan_240x120.ppm",
        profile,
        _DEFAULT_PALETTE,
        _DEFAULT_INKS,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g2_c2_blackcyan_md5000_600.bin")


def test_g3_white_md5000_600():
    profile = _profile(600, "MD-5000")
    actual = _render(
        CASES_DIR / "c3_black_for_white_120x120.ppm",
        profile,
        _WHITE_PALETTE,
        _WHITE_INKS,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g3_c3_white_md5000_600.bin")
