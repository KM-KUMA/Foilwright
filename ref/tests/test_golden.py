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
        # golden はすべて ppmtomd の既定(普通紙)で採取されている
        "media": config.load_media_table(str(REPO_ROOT / "media.yaml"))["plain_paper"],
        "inks": inks,
    }


def _render(
    ppm_path: pathlib.Path, job: dict, palette: dict, halftone: str = "none"
) -> bytes:
    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image
    planes = raster.to_planes(image, palette, halftone=halftone)
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


_WHITE_MULTILAYER_PALETTE = {
    "white": "C",
    "metallic_gold": "M",
    "metallic_silver": "Y",
    "black": "K",
}
_WHITE_MULTILAYER_INKS = [
    {"name": "white", "printer_code": 0x0B},
    {"name": "metallic_gold", "printer_code": 0x04},
    {"name": "metallic_silver", "printer_code": 0x07},
    {"name": "black", "printer_code": 0x00},
]


def test_g10_white_multilayer_md5000_600():
    """White alongside other inks in a single job (DOMAIN §11.5). The point
    of this fixture is the eject count: one form feed at the very end, with
    backfeeds between passes. See test_single_eject_across_all_golden."""
    job = _job(600, "md-5000", _WHITE_MULTILAYER_INKS)
    actual = _render(
        CASES_DIR / "c5_metallic4_240x120.ppm",
        job,
        _WHITE_MULTILAYER_PALETTE,
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g10_c5_white_multilayer_md5000_600.bin")


def _count_ejects(data: bytes) -> int:
    """Parse the command stream and count bare form feeds (ejects).

    Counting raw 0x0C bytes does not work: raster data can contain 0x0C as
    an ordinary pixel byte. Dithered fixtures make this obvious -- a naive
    count reports 129 ejects for g13 where there is exactly one. The only
    reliable way is to walk the stream command by command and skip over the
    data payloads.

    Raises ValueError on any byte that is not part of a known command, so
    this doubles as a check that our understanding of the command set is
    complete.
    """
    i = 0
    ejects = 0
    while i < len(data):
        byte = data[i]
        if byte == 0x0C:  # bare form feed = eject
            ejects += 1
            i += 1
            continue
        if byte != 0x1B:  # ESC. Written out rather than imported from the
            # emitter, so the check stays independent of the implementation
            # it is guarding.
            raise ValueError(f"unexpected byte {byte:#04x} at offset {i}")
        kind = data[i + 1]
        if kind == 0x25:  # ESC % {n} A|X -- enter/leave RGL mode
            i += 4
        elif kind == 0x65:  # ESC e -- printer reset
            i += 2
        elif kind == 0x1A:  # ESC SUB {a} {b} {cmd} -- colour select, backfeed…
            i += 5
        elif kind == 0x26:  # ESC & {x} {lo} {hi} {cmd} -- page geometry
            i += 6
        elif kind == 0x2A:
            sub = data[i + 2]
            if sub == 0x74:  # ESC * t {res} R + stray NUL (ppmtomd quirk)
                i += 6
            elif sub == 0x72:  # ESC * r … -- raster start/end/transfer mode
                i += 4 if data[i + 3] == 0x43 else 5
            elif sub == 0x62:  # ESC * b {lo} {hi} {cmd} [+ payload]
                length = data[i + 3] + data[i + 4] * 256
                cmd = data[i + 5]
                # V and W carry a payload; M (compression) and Y (row skip)
                # use the length field as a value, not a byte count.
                i += 6 + (length if cmd in (0x56, 0x57) else 0)
            else:
                raise ValueError(f"unknown ESC * {sub:#04x} at offset {i}")
        else:
            raise ValueError(f"unknown ESC {kind:#04x} at offset {i}")
    return ejects


def test_single_eject_across_all_golden():
    """DOMAIN §4.10: the paper must be ejected once, after the final pass.
    Ejecting between passes loses registration irrecoverably (§10.6), so this
    guards every golden at once -- including g10/g11, where white shares a
    job with other inks, and the dithered fixtures whose raster data happens
    to contain 0x0C bytes."""
    for path in sorted(GOLDEN_DIR.glob("*.bin")):
        ejects = _count_ejects(path.read_bytes())
        assert ejects == 1, f"{path.name}: expected 1 eject, found {ejects}"


def test_g15_cardboard_media_md5000_600():
    """Thick stock (media 0x05 0x00) instead of the default plain paper.
    Selecting the right media is a safety setting, not a quality one: it
    is what stops the ink ribbon tearing under an undercoat pass
    (DOMAIN §5.5.2 / §10.8.2)."""
    media = config.load_media_table(str(REPO_ROOT / "media.yaml"))["cardboard"]
    job = dict(_job(600, "md-5000", _DEFAULT_INKS), media=media)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g15_c1_cardboard_md5000_600.bin")


def test_g12_fullcolour_md5000_600():
    """Red, green and blue each force a two-ink mix (M+Y, C+Y, C+M), so this
    is the first fixture where all four CMYK planes carry data at once.
    Earlier fixtures only ever exercised K alone (g1) or K+C (g2). Also
    covers pure white (nothing printed) and a mid grey (threshold
    behaviour, DOMAIN §4.2)."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(CASES_DIR / "c6_fullcolour_240x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g12_c6_fullcolour_md5000_600.bin")


def test_g8_positive_shift_md5000_600():
    """Explicit -xshift 100 -yshift 200. Positive shifts are the only ones
    ppmtomd expresses as commands (ESC & a {x} L / ESC & l {y} E)."""
    job = dict(_job(600, "md-5000", _DEFAULT_INKS), x_shift=100, y_shift=200)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g8_c1_shift_md5000_600.bin")


def test_g9_autoshift_md5000_600():
    """ppmtomd's -autoshift subtracts the paper's unprintable margins from
    the requested shift. Here -xshift 200 -yshift 400 against A4's left=80
    top=284 leaves 120 and 116. The subtraction happens in the caller (the
    paper table owns the margins), so this also checks that papers/*.yaml
    carries the values the golden was generated with."""
    paper = _job(600, "md-5000", _DEFAULT_INKS)["paper"]
    job = dict(
        _job(600, "md-5000", _DEFAULT_INKS),
        x_shift=200 - paper["left_margin"],
        y_shift=400 - paper["top_margin"],
    )
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g9_c1_autoshift_md5000_600.bin")


def test_negative_shift_is_rejected():
    """A negative shift trims the raster in ppmtomd rather than emitting a
    command. That path is not implemented, so it must fail loudly instead of
    printing in the wrong place."""
    job = dict(_job(600, "md-5000", _DEFAULT_INKS), x_shift=-10)
    with pytest.raises(NotImplementedError):
        _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)


def test_g13_halftone_md5000_600():
    """-dither Halftone: ppmtomd's fine-line ordered dither (DOMAIN §4.2.1).
    Same fixture as g12, so this exercises the halftone matrices against
    solid colours, pure white and a mid grey side by side."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="halftone",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g13_c6_halftone_md5000_600.bin")


def test_g14_coarsehalftone_md5000_600():
    """-dither CoarseHalftone: ppmtomd's coarser-dot ordered dither
    (DOMAIN §4.2.1), sharing the same 10x10 matrix across all four CMYK
    components (only the per-component screen angle differs)."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="coarse_halftone",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g14_c6_coarsehalftone_md5000_600.bin")


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
