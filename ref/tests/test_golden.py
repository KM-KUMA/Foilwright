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

# `-colours C=White,M=Finish,Y=MetallicGold,K=Black`.
# 作者の実作業手順そのもの — 白の下地を敷き、コーティングを挟み、その上に
# 色を乗せる(DOMAIN §4.11 / §10.7)。
#
# Finish の色選択バイトは golden から読み取った実測値 0x0E で、mddata.h の
# colGlossyFinish に対応する。バーコード側には Finish が 2 種類あるが
# (17 と 19。DOMAIN §6.5)、ホスト側が送るのはこの 1 バイトだけである。
# palette/default.yaml に finish のエントリは無いため、ここで定義する。
_WHITE_FINISH_PALETTE = {
    "white": "C",
    "finish": "M",
    "metallic_gold": "Y",
    "black": "K",
}
_WHITE_FINISH_INKS = [
    {"name": "white", "printer_code": 0x0B},
    {"name": "finish", "printer_code": 0x0E},
    {"name": "metallic_gold", "printer_code": 0x04},
    {"name": "black", "printer_code": 0x00},
]


# `-spotcolours 1=Finish=k` (and `,2=Finish=c` for the six-ink fixture):
# five and six printing colours, which is what makes ppmtomd swap
# colourPlane for multiPlane and send the cassette list (DOMAIN §14.8).
#
# Finish is the spot colour that leaves the CMYK planes alone -- ppmtomd's
# `isspot` (ppmtomd.c:3028-3044) blanks whatever sits under an ordinary
# spot colour, but Finish is exempt. So these fixtures isolate the emitter
# change instead of dragging the raster layer in with them.
#
# The spot planes themselves are exact copies of existing CMYK channels:
# ppmtomd computes a spot's `k`/`c` from the raw pixel with plain UCR and
# no colour correction (ppmtomd.c:2977-2991), which is the same expression
# colcorPlain uses (ppmtomd.c:2933-2937), and spot colours skip dithering
# entirely. Hence "finish_k" -> "K" and "finish_c" -> "C" below.
#
# Barcodes come from the cassette numbering (mddata.h `barCode`, DOMAIN
# §6.5), not from printer_code: Cyan=3, Magenta=2, Yellow=1, Black=0,
# Finish II=19.
_FIVE_INK_PALETTE = {
    "cyan": "C",
    "magenta": "M",
    "yellow": "Y",
    "black": "K",
    "finish_k": "K",
}
_FIVE_INKS = [
    {"name": "cyan", "printer_code": 0x01, "barcode": 3},
    {"name": "magenta", "printer_code": 0x02, "barcode": 2},
    {"name": "yellow", "printer_code": 0x03, "barcode": 1},
    {"name": "black", "printer_code": 0x00, "barcode": 0},
    {"name": "finish_k", "printer_code": 0x0E, "barcode": 19},
]

_SIX_INK_PALETTE = dict(_FIVE_INK_PALETTE, finish_c="C")
_SIX_INKS = [
    *_FIVE_INKS,
    {"name": "finish_c", "printer_code": 0x0E, "barcode": 19},
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
    ppm_path: pathlib.Path,
    job: dict,
    palette: dict,
    halftone: str = "none",
    colour_correction: str = "plain",
) -> bytes:
    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image
    planes = raster.to_planes(
        image,
        palette,
        halftone=halftone,
        colour_correction=colour_correction,
        resolution=job["resolution"],
    )
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


def test_g11_white_finish_colour_md5000_600():
    """White -> coating -> colour, the author's actual working order
    (DOMAIN §4.11 / §10.7). This fixture had a golden file but no test:
    the byte stream nobody was checking was the one the real workflow
    produces. Finish carries colour-selection byte 0x0E."""
    job = _job(600, "md-5000", _WHITE_FINISH_INKS)
    actual = _render(
        CASES_DIR / "c5_metallic4_240x120.ppm",
        job,
        _WHITE_FINISH_PALETTE,
    )
    _assert_golden_match(
        actual, GOLDEN_DIR / "g11_c5_white_finish_colour_md5000_600.bin"
    )


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
            # ESC & l {count} 00 C is the multiPlane cassette list, and is
            # the one ESC & command with a payload: {count} barcode bytes
            # follow the header (ppmtomd.c:2526-2544).
            i += 6 + (data[i + 3] if data[i + 5] == 0x43 else 0)
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


def test_g17_nocurl_md5000_600():
    """-nocurlcorrection: decal stock must stay flat, so the curl-correction
    byte is suppressed (DOMAIN §10.10.4). This is the main use case of the
    whole project -- water slide decals -- so the one byte that separates it
    from g1 is worth its own golden.

    g17 differs from g1 in exactly one byte, at offset 0x24:
    `1b 1a 00 00 43` becomes `1b 1a 01 00 43`."""
    job = dict(_job(600, "md-5000", _DEFAULT_INKS), no_curl_correction=True)
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, _DEFAULT_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g17_c1_nocurl_md5000_600.bin")


def test_g18_photo_coarse_md5000_600():
    """-colourcorrection Photo, -dither CoarseHalftone, 600dpi (D-029).
    Same fixture as g12/g14, so the only difference from g14 is the
    colour-correction path: colcorPhoto's gamma + lookup-table formula
    instead of colcorPlain's undercolour removal."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="coarse_halftone",
        colour_correction="photo",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g18_c6_photo_coarse_md5000_600.bin")


def test_g19_photo_halftone_md5000_600():
    """-colourcorrection Photo, -dither Halftone, 600dpi (D-029)."""
    job = _job(600, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="halftone",
        colour_correction="photo",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g19_c6_photo_halftone_md5000_600.bin")


def test_g20_photo_coarse_md5000_1200():
    """-colourcorrection Photo, -dither CoarseHalftone, 1200dpi (D-029).
    The default initgam flips sign and magnitude at 1200dpi (0.8 at
    600dpi vs -0.9 at 1200dpi), so this is the fixture that actually
    exercises colour.default_gamma's resolution branch and
    build_gamma_table's negative-gamma formula.

    Root cause (2026-08-19, docs/DOMAIN.md §11.6.1): ppmtomd's ht_init is
    a C macro, always invoked as
    ``ht_init(&kht, compM, row*row_factor+subrow)``. Macro arguments are
    substituted textually, so for yneg components the macro body's
    ``10000 - row`` becomes ``10000 - row*row_factor+subrow``, which -- by
    C's left-to-right +/- precedence -- evaluates as
    ``(10000 - row*row_factor) + subrow``, not the "intended"
    ``10000 - (row*row_factor + subrow)``. This only diverges when
    subrow != 0, i.e. only at 1200dpi (row_factor == 2), which is exactly
    why every 600dpi golden (subrow always 0) and g23/g24 at 1200dpi
    (Plain colour-correction drops enough ink that the AND-combine hides
    the resulting one-cell dither-phase shift) stayed green while g20
    alone was failing. _ht_row_positions now reproduces this bug
    deliberately (see its docstring); confirmed byte-exact via measured
    ppmtomd instrumentation (values reproduced in DOMAIN.md §11.6.1)."""
    job = _job(1200, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="coarse_halftone",
        colour_correction="photo",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g20_c6_photo_coarse_md5000_1200.bin")


def test_g23_plain_coarsehalftone_md5000_1200():
    """-colourcorrection Plain, -dither CoarseHalftone, 1200dpi.
    Companion to g14 (same options at 600dpi) but at 1200dpi, where
    ppmtomd dithers two subrows per source row and combines them with
    AND (ppmtomd.c:3174-3187; see test_g20_photo_coarse_md5000_1200's
    docstring for the derivation).
    Byte-exact against golden, confirming the subrow implementation is
    correct at 1200dpi for the Plain colour-correction path."""
    job = _job(1200, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="coarse_halftone",
        colour_correction="plain",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g23_c6_plain_coarse_md5000_1200.bin")


def test_g24_plain_halftone_md5000_1200():
    """-colourcorrection Plain, -dither Halftone, 1200dpi.
    Companion to g13 (same options at 600dpi) but at 1200dpi."""
    job = _job(1200, "md-5000", _DEFAULT_INKS)
    actual = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="halftone",
        colour_correction="plain",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g24_c6_plain_halftone_md5000_1200.bin")


def test_g23_and_g24_are_byte_identical():
    """At 1200dpi the two subrows are combined with AND (see
    test_g20_photo_coarse_md5000_1200's docstring), which collapses the
    CoarseHalftone/Halftone dither-pattern
    difference on this fixture: g23 and g24 render to the exact same
    bytes even though their dither options differ. This test pins that
    fact down directly so a change that breaks only one dither path
    while leaving the golden-file comparison "coincidentally" passing
    would still be caught."""
    job = _job(1200, "md-5000", _DEFAULT_INKS)
    actual_coarse = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="coarse_halftone",
        colour_correction="plain",
    )
    actual_halftone = _render(
        CASES_DIR / "c6_fullcolour_240x120.ppm",
        job,
        _DEFAULT_PALETTE,
        halftone="halftone",
        colour_correction="plain",
    )
    assert actual_coarse == actual_halftone


def test_g25_five_inks_md5000_600():
    """Five printing colours: transfer mode 0x08 (multiPlane) instead of
    0x04, plus the cassette list `ESC & l 05 00 C 03 02 01 00 13` right
    after the page-width command. Everything else -- selections, rasters,
    backfeeds -- is byte for byte what colourPlane emits (DOMAIN §14.8)."""
    job = _job(600, "md-5000", _FIVE_INKS)
    actual = _render(CASES_DIR / "c6_fullcolour_240x120.ppm", job, _FIVE_INK_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g25_c6_five_inks_md5000_600.bin")


def test_g26_six_inks_md5000_600():
    """Six printing colours. The cassette list grows by one entry and its
    count byte follows; the repeated barcode (19 twice) is deliberate --
    ppmtomd lists one entry per printing component, not per distinct
    cassette."""
    job = _job(600, "md-5000", _SIX_INKS)
    actual = _render(CASES_DIR / "c6_fullcolour_240x120.ppm", job, _SIX_INK_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g26_c6_six_inks_md5000_600.bin")


def test_g27_five_inks_md5000_1200():
    """multiPlane at 1200dpi. Spot colours skip both dithering and the
    1200dpi subrow pass (ppmtomd.c:2970-2972), so the fifth plane is the
    plain per-source-row threshold while the CMYK four go through the
    subrow AND-combine -- and the cassette list is unaffected by
    resolution."""
    job = _job(1200, "md-5000", _FIVE_INKS)
    actual = _render(CASES_DIR / "c6_fullcolour_240x120.ppm", job, _FIVE_INK_PALETTE)
    _assert_golden_match(actual, GOLDEN_DIR / "g27_c6_five_inks_md5000_1200.bin")


def test_g16_blackraster_md5000_600():
    """-black: the single-plane transfer mode. The mode byte itself says which
    ribbon to use, so the stream carries no colour-selection command and no
    backfeed between passes -- 35 bytes shorter than the colourPlane g1
    (1026 vs 1061). DOMAIN §11.1.1."""
    job = dict(
        _job(600, "md-5000", [{"name": "black", "printer_code": 0x00}]),
        transfer_mode="black_raster",
    )
    actual = _render(CASES_DIR / "c1_black_120x120.ppm", job, {"black": "K"})
    _assert_golden_match(actual, GOLDEN_DIR / "g16_c1_blackraster_md5000_600.bin")


def test_g21_white_twice_md5000_600():
    """`passes` (DOMAIN §6.2) byte-exact against a real ppmtomd capture.

    ppmtomd itself has no `passes` concept, so this fixture borrows a
    different route to the same byte stream: `-colourcorrection None`
    skips undercolour removal, so a solid black source makes C, M and Y
    all saturate to 255. Assigning the *same* ink (White) to two of those
    components (`-colours C=White,M=White`) makes ppmtomd emit two
    identical (colour-selection + raster) occurrences of White, separated
    by a backfeed -- which is exactly the structure `passes=2` builds
    for a single ink. Captured 2026-08-19 once WSL was available again
    (see the now-corrected NOTE in emitter.py); confirmed byte-identical
    to a second independent run (determinism) and to this ref/
    implementation's `passes=2` output."""
    job = _job(600, "md-5000", [{"name": "white", "printer_code": 0x0B, "passes": 2}])
    actual = _render(
        CASES_DIR / "c1_black_120x120.ppm",
        job,
        {"white": "C"},
        colour_correction="none",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g21_c1_white_twice_md5000_600.bin")


def test_g22_white_thrice_md5000_600():
    """Same construction as g21, with a third White component
    (`-colours C=White,M=White,Y=White`) standing in for `passes=3`."""
    job = _job(600, "md-5000", [{"name": "white", "printer_code": 0x0B, "passes": 3}])
    actual = _render(
        CASES_DIR / "c1_black_120x120.ppm",
        job,
        {"white": "C"},
        colour_correction="none",
    )
    _assert_golden_match(actual, GOLDEN_DIR / "g22_c1_white_thrice_md5000_600.bin")
