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

from foilwright_ref import config, emitter, raster

CASES_DIR = REPO_ROOT / "tests" / "cases"
GOLDEN_DIR = REPO_ROOT / "tests" / "golden"
PROFILES_DIR = REPO_ROOT / "profiles"
PAPERS_DIR = REPO_ROOT / "papers"

# Default (no -colours) job: ppmtomd always drives all four CMYK
# components in this order, even when some end up entirely blank
# (ppmtomd.c:1469-1490 default comp_colours; :84-86 comp_print_order).
_DEFAULT_PALETTE = {"cyan": "C", "magenta": "M", "yellow": "Y", "black": "K"}
_DEFAULT_INKS = [
    {"name": "cyan", "printer_code": 0x01},
    {"name": "magenta", "printer_code": 0x02},
    {"name": "yellow", "printer_code": 0x03},
    {"name": "black", "printer_code": 0x00},
]

# `-colours K=White`: only K is active, and it is driven by the White
# ink's colour-selection byte (mddata.h colWhite = 0x0B) instead of
# Black's (ppmtomd.c:1517-1525 for the K=... parse, mddata.c colour
# enum for the byte value).
_WHITE_PALETTE = {"white": "K"}
_WHITE_INKS = [{"name": "white", "printer_code": 0x0B}]

# `-colours C=MetallicCyan,M=MetallicMagenta,Y=MetallicGold,K=MetallicSilver`.
# Colour-selection bytes follow mddata.c's colour enum order
# (mddata.c:12-15): Gold=0x04, Magenta=0x05, Cyan=0x06, Silver=0x07.
# Pass order is the CMYK component order, which ppmtomd only permutes for
# dyesub (ppmtomd.c:1456-1464), so C(=Cyan) prints first and K(=Silver) last.
# These four inks all share order 50 in palette/default.yaml, so this is the
# only fixture where DOMAIN §4.9's stable-sort requirement has any effect --
# a tie-break that reorders them changes the byte stream.
_METALLIC4_PALETTE = {
    "metallic_cyan": "C",
    "metallic_magenta": "M",
    "metallic_gold": "Y",
    "metallic_silver": "K",
}
_METALLIC4_INKS = [
    {"name": "metallic_cyan", "printer_code": 0x06},
    {"name": "metallic_magenta", "printer_code": 0x05},
    {"name": "metallic_gold", "printer_code": 0x04},
    {"name": "metallic_silver", "printer_code": 0x07},
]


def _job(resolution: int, model: str, inks: list) -> dict:
    profile = config.load_profile(str(PROFILES_DIR / f"{model}.yaml"))
    paper_table = config.resolve_paper_table(profile, str(PAPERS_DIR))
    paper = paper_table["a4"]
    return {
        "resolution": resolution,
        "paper": paper,
        "media_byte1": 0x00,
        "media_byte2": 0x00,
        "inks": inks,
    }


def _render(ppm_path: pathlib.Path, job: dict, palette: dict) -> bytes:
    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image
    planes = raster.to_planes(image, palette)
    full_job = dict(job, width=width, height=height)
    return emitter.emit_job(planes, full_job)


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
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g1_c1_black_md5000_600.bin")


def test_g5_black_md5500_600_profile_swap_only():
    """MD-5000 and MD-5500 must produce byte-identical output from the
    same profile values with only the (informational) model name
    changed -- there is no model-specific branch in the emitter
    (DOMAIN.md §4.4)."""
    job = _job(600, "md-5500", _DEFAULT_INKS)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g5_c1_black_md5500_600.bin")


def test_g4_black_md5000_1200():
    job = _job(1200, "md-5000", _DEFAULT_INKS)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g4_c1_black_md5000_1200.bin")


def test_g2_blackcyan_md5000_600():
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c2_blackcyan_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g2_c2_blackcyan_md5000_600.bin")


def test_g3_white_md5000_600():
    job = _job(600, "md-5000", _WHITE_INKS)
    actual = _render(
        CASES_DIR / "c3_black_for_white_120x120.ppm",
        job,
        _WHITE_PALETTE,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g3_c3_white_md5000_600.bin")


def test_g6_square_on_white_md5000_600():
    """White background, so this is the only case that exercises blank-row
    skipping (ESC * b {n} Y), trailing-zero trimming within a row, and a
    trailing run of blank rows at the bottom of the page. Solid-fill cases
    never reach those paths."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c4_square_on_white_120x120.ppm",
        job,
        _DEFAULT_PALETTE,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g6_c4_square_md5000_600.bin")


def test_g7_metallic4_md5000_600():
    """Four metallic inks, all at the same `order` value. Pass order here is
    fixed by the ink list's own sequence (DOMAIN §4.3 tie-break = palette file
    order, §4.9 stable sort). Any implementation that reorders same-order inks
    -- e.g. C# List<T>.Sort(), which is unstable -- produces a different byte
    stream and fails against this golden."""
    job = _job(600, "md-5000", _METALLIC4_INKS)
    actual = _render(
        CASES_DIR / "c5_metallic4_240x120.ppm",
        job,
        _METALLIC4_PALETTE,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g7_c5_metallic4_md5000_600.bin")
