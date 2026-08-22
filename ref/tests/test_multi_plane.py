"""Structural tests for the multiPlane transfer mode (DOMAIN.md §14.8).

Byte-exact golden coverage lives in `test_golden.py`
(`test_g25_five_inks_md5000_600` and friends). What is checked here is the
decision itself -- when the emitter swaps colourPlane for multiPlane, what
the cassette list looks like, and the two boundaries ppmtomd enforces
(barcode required, at most seven printing colours) -- because none of
those are visible in a single golden fixture.
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import emitter

ESC = 0x1B

# Minimal 1x8 all-black plane, as in test_passes.py: the raster body stays
# non-empty and deterministic without pulling raster.py in.
_WIDTH = 8
_HEIGHT = 1
_PLANE = bytes([0xFF])


def _ink(index: int, **extra: object) -> dict:
    return {
        "name": f"ink{index}",
        "printer_code": index,
        "barcode": 10 + index,
        **extra,
    }


def _job(inks: list[dict], **extra: object) -> dict:
    return {
        "resolution": 600,
        "width": _WIDTH,
        "height": _HEIGHT,
        "paper": {"code": 4, "width": 100, "length": 100},
        "media": {"byte1": 0, "byte2": 0},
        "inks": inks,
        **extra,
    }


def _emit(inks: list[dict], **extra: object) -> bytes:
    planes = {ink["name"]: _PLANE for ink in inks}
    return emitter.emit_job(planes, _job(inks, **extra))


def _transfer_mode_byte(stream: bytes) -> int:
    """Return the byte of the one `ESC * r {mode} U` command in the stream."""
    marker = bytes([ESC, 0x2A, 0x72])
    positions = [
        i
        for i in range(len(stream) - 4)
        if stream[i : i + 3] == marker and stream[i + 4] == 0x55
    ]
    assert len(positions) == 1, f"expected one transfer-mode command, got {positions}"
    return stream[positions[0] + 3]


def _cassette_lists(stream: bytes) -> list[bytes]:
    """Return the payload of every `ESC & l {count} 00 C` command.

    The stream is walked from the front rather than scanned for the byte
    pattern, so raster data can never be mistaken for a command.
    """
    found = []
    i = 0
    while i < len(stream):
        if stream[i] != ESC:
            i += 1
            continue
        kind = stream[i + 1]
        if kind == 0x26 and stream[i + 5] == 0x43:
            count = stream[i + 3]
            assert stream[i + 4] == 0, "cassette list count is a single byte"
            found.append(stream[i + 6 : i + 6 + count])
            i += 6 + count
            continue
        i += 1
    return found


def test_four_inks_stay_colour_plane():
    stream = _emit([_ink(i) for i in range(4)])
    assert _transfer_mode_byte(stream) == 0x04
    assert _cassette_lists(stream) == []


def test_five_inks_switch_to_multi_plane_and_list_cassettes():
    stream = _emit([_ink(i) for i in range(5)])
    assert _transfer_mode_byte(stream) == 0x08
    # One entry per ink, in print order, taken from each ink's `barcode`.
    assert _cassette_lists(stream) == [bytes([10, 11, 12, 13, 14])]


def test_cassette_list_sits_between_page_width_and_the_curl_command():
    """Position matters: ppmtomd emits it inside rgl_init_page, after the
    page-width command and before the x/y shifts and the curl byte
    (ppmtomd.c:2526-2544)."""
    stream = _emit([_ink(i) for i in range(5)])
    page_width = stream.index(bytes([ESC, 0x26, 0x61]))
    cassette = stream.index(bytes([ESC, 0x26, 0x6C, 5, 0, 0x43]))
    curl = stream.index(bytes([ESC, 0x1A, 0, 0, 0x43]))
    assert page_width < cassette < curl


def test_seven_inks_are_allowed():
    stream = _emit([_ink(i) for i in range(7)])
    assert _transfer_mode_byte(stream) == 0x08


def test_eight_inks_are_rejected():
    """ppmtomd's own boundary (ppmtomd.c:1778): the print head holds seven
    cartridges, so an eighth must fail rather than print something wrong."""
    with pytest.raises(ValueError, match="too many printing colours"):
        _emit([_ink(i) for i in range(8)])


def test_missing_barcode_is_rejected():
    inks = [_ink(i) for i in range(5)]
    del inks[3]["barcode"]
    with pytest.raises(ValueError, match="barcode"):
        _emit(inks)


@pytest.mark.parametrize("barcode", [-1, 256, "16", None])
def test_bad_barcode_is_rejected(barcode):
    inks = [_ink(i) for i in range(5)]
    inks[2]["barcode"] = barcode
    with pytest.raises(ValueError, match="barcode"):
        _emit(inks)


def test_barcode_not_required_below_the_threshold():
    """Four inks never send the cassette list, so they must not be forced
    to carry a barcode -- that would break every existing caller."""
    inks = [{"name": f"ink{i}", "printer_code": i} for i in range(4)]
    stream = _emit(inks)
    assert _transfer_mode_byte(stream) == 0x04


def test_passes_do_not_count_as_printing_colours():
    """`passes` (DOMAIN §6.2) reprints the same cassette, so it must not
    push a four-ink job over the multiPlane threshold: the printer is still
    asked to load four cartridges."""
    inks = [_ink(i, passes=3) for i in range(4)]
    stream = _emit(inks)
    assert _transfer_mode_byte(stream) == 0x04
    assert _cassette_lists(stream) == []


def test_explicit_black_raster_is_not_upgraded():
    """Only colourPlane is upgraded (ppmtomd.c:1780-1783); an explicitly
    requested single-plane mode stays as asked."""
    inks = [_ink(0)]
    stream = _emit(inks, transfer_mode="black_raster")
    assert _transfer_mode_byte(stream) == 0x00
    assert _cassette_lists(stream) == []
