"""Invariant tests for D-052: at 1200dpi, non-process planes halve in width.

The printer picks its scan resolution from the cassette barcode, so spot
and coverage cassettes run at 600dpi even when the job asked for 1200
(DOMAIN §14.7.1). The emitter compensates by halving those planes
horizontally, OR-ing each pair of dots.

**ppmtomd does not do this**, so no golden fixture can cover it (D-052) --
these structural tests are the substitute. They parse the emitted command
stream directly, the same way test_passes.py does, rather than diffing
against a capture.

The mirror of this file is src/Foilwright.Core.Tests/Shrink1200Tests.cs;
keep the two in step (D-006).
"""

from __future__ import annotations

import pathlib
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import emitter

ESC = 0x1B


def _unpack_packbits(data: bytes) -> bytes:
    """Inverse of emitter._packbits' compressed branch (ppmtomd's format).

    A count byte c (signed): c >= 0 means the next c+1 bytes are literal,
    c < 0 means the next single byte repeats -c+1 times.
    """
    out = bytearray()
    i = 0
    while i < len(data):
        count = data[i]
        if count >= 128:
            count -= 256
        i += 1
        if count >= 0:
            out += data[i : i + count + 1]
            i += count + 1
        else:
            out += bytes([data[i]]) * (-count + 1)
            i += 1
    return bytes(out)


def _rows_by_ink(
    stream: bytes, row_bytes_by_code: dict[int, int]
) -> dict[int, list[bytes]]:
    """Parse the raster section and return {printer_code: [row, ...]}.

    Rows are padded back out to `row_bytes_by_code[code]` (the emitter
    trims trailing zero bytes) and rows skipped by the blank-row command
    come back as all-zero rows. Parsing is strictly sequential -- every
    command's length is consumed -- so raster payload bytes can never be
    mistaken for commands.
    """
    start = stream.find(bytes([ESC, 0x2A, 0x72, 0x00, 0x41]))
    assert start != -1, "start-raster-graphics command not found"
    i = start + 5

    rows: dict[int, list[bytes]] = {}
    current: int | None = None
    compressed = False
    end = bytes([ESC, 0x2A, 0x72, 0x43])

    while i < len(stream):
        if stream[i : i + 4] == end:
            break
        assert stream[i] == ESC, f"unexpected byte 0x{stream[i]:02x} at {i}"
        if stream[i + 1] == 0x1A:
            # colour selection (ends 'r') or backfeed (ends 0x0C)
            if stream[i + 4] == 0x72:
                current = stream[i + 2]
                rows.setdefault(current, [])
                compressed = False
            i += 5
            continue
        assert stream[i + 1 : i + 3] == bytes([0x2A, 0x62]), f"unknown command at {i}"
        value = stream[i + 3] + stream[i + 4] * 256
        opcode = stream[i + 5]
        i += 6
        if opcode == 0x4D:  # compression mode
            compressed = value == 2
        elif opcode == 0x59:  # skip N blank rows
            assert current is not None
            rows[current] += [bytes(row_bytes_by_code[current])] * value
        elif opcode in (0x56, 0x57):  # row data (last row / not last row)
            assert current is not None
            data = stream[i : i + value]
            i += value
            raw = _unpack_packbits(data) if compressed else data
            width = row_bytes_by_code[current]
            assert len(raw) <= width, (
                f"row longer than the plane's row: {len(raw)} > {width}"
            )
            rows[current] += [raw + bytes(width - len(raw))]
        else:
            raise AssertionError(f"unknown raster opcode 0x{opcode:02x} at {i - 1}")
    return rows


_SPOT = 0x0B
_PROCESS = 0x00


def _job(
    width: int, height: int, resolution: int, *, spot_is_process: bool | None = False
) -> dict:
    spot: dict = {"name": "spot", "printer_code": _SPOT}
    if spot_is_process is not None:
        spot["is_process"] = spot_is_process
    return {
        "resolution": resolution,
        "width": width,
        "height": height,
        "paper": {"code": 4, "width": 100, "length": 100},
        "media": {"byte1": 0, "byte2": 0},
        "inks": [
            spot,
            {"name": "process", "printer_code": _PROCESS, "is_process": True},
        ],
    }


def _emit(plane: bytes, width: int, height: int, resolution: int, **kwargs) -> bytes:
    job = _job(width, height, resolution, **kwargs)
    return emitter.emit_job({"spot": plane, "process": plane}, job)


def _rows(plane: bytes, width: int, height: int, resolution: int, **kwargs):
    stream = _emit(plane, width, height, resolution, **kwargs)
    src_row_bytes = (width + 7) // 8
    shrunk = resolution == 1200 and kwargs.get("spot_is_process", False) is False
    dst_row_bytes = ((width + 1) // 2 + 7) // 8 if shrunk else src_row_bytes
    parsed = _rows_by_ink(stream, {_SPOT: dst_row_bytes, _PROCESS: src_row_bytes})
    return parsed[_SPOT], parsed[_PROCESS]


def test_1200_halves_the_width_of_a_spot_plane():
    spot, _ = _rows(bytes([0xFF, 0xFF]), 16, 1, 1200)
    assert spot == [bytes([0xFF])]


def test_1200_leaves_a_process_plane_alone():
    _, process = _rows(bytes([0xFF, 0xFF]), 16, 1, 1200)
    assert process == [bytes([0xFF, 0xFF])]


def test_600_leaves_a_spot_plane_alone():
    spot, process = _rows(bytes([0xFF, 0xFF]), 16, 1, 600)
    assert spot == [bytes([0xFF, 0xFF])]
    assert process == [bytes([0xFF, 0xFF])]


def test_300_leaves_a_spot_plane_alone():
    spot, _ = _rows(bytes([0xFF, 0xFF]), 16, 1, 300)
    assert spot == [bytes([0xFF, 0xFF])]


def test_600_output_is_byte_identical_whatever_the_ink_kind():
    """The strongest form of "1200 以外では 1 バイトも変わらない" (D-052)."""
    for resolution in (300, 600):
        as_spot = _emit(bytes([0x55, 0xAA]), 16, 1, resolution, spot_is_process=False)
        as_process = _emit(bytes([0x55, 0xAA]), 16, 1, resolution, spot_is_process=True)
        assert as_spot == as_process, f"{resolution}dpi output depends on the ink kind"


def test_missing_is_process_defaults_to_process_and_does_not_shrink():
    """A caller that knows nothing about ink kinds keeps ppmtomd's bytes."""
    omitted = _emit(bytes([0xFF, 0xFF]), 16, 1, 1200, spot_is_process=None)
    as_process = _emit(bytes([0xFF, 0xFF]), 16, 1, 1200, spot_is_process=True)
    assert omitted == as_process


def test_shrink_is_or_not_decimation():
    """The detector for *how* the plane is halved.

    0x55 sets only odd source columns (1, 3, 5, 7). OR keeps all four
    pairs; taking every second column instead drops all four dots and the
    row disappears from the stream entirely.
    """
    spot, _ = _rows(bytes([0x55, 0x00]), 16, 1, 1200)
    assert spot == [bytes([0xF0])]


def test_shrink_is_or_the_other_way_round_too():
    """0xAA sets only even source columns; OR must keep those as well."""
    spot, _ = _rows(bytes([0xAA, 0x00]), 16, 1, 1200)
    assert spot == [bytes([0xF0])]


def test_row_padding_bits_stay_zero():
    # width 10 -> 5 output dots in a 1-byte row; bits 5..7 are padding.
    spot, _ = _rows(bytes([0xFF, 0xC0]), 10, 1, 1200)
    assert spot == [bytes([0xF8])]
    assert spot[0][0] & 0x07 == 0


def test_odd_width_keeps_the_last_lone_dot():
    """width 9: source dot 8 has no partner and is kept on its own
    (output width ceil(9/2) = 5), so no column of ink goes missing."""
    spot, _ = _rows(bytes([0x00, 0x80]), 9, 1, 1200)
    assert spot == [bytes([0x08])]


def test_odd_width_output_is_ceil_half():
    spot, _ = _rows(bytes([0xFF, 0x80]), 9, 1, 1200)
    assert spot == [bytes([0xF8])]


def test_page_width_command_is_unaffected_by_shrinking():
    """The page-width command is per job and stays at the 1200 value:
    only the shrunk ink's rows get shorter (D-052)."""
    as_spot = _emit(bytes([0xFF, 0xFF]), 16, 1, 1200, spot_is_process=False)
    as_process = _emit(bytes([0xFF, 0xFF]), 16, 1, 1200, spot_is_process=True)
    marker = bytes([ESC, 0x26, 0x61])
    start_spot = as_spot.find(marker)
    start_process = as_process.find(marker)
    assert start_spot != -1
    assert (
        as_spot[start_spot : start_spot + 6]
        == as_process[start_process : start_process + 6]
    )
    assert as_spot[start_spot + 5] == 0x4D
    # ... while the streams as a whole do differ, i.e. the test above is
    # not passing because nothing happened.
    assert as_spot != as_process


def test_multi_row_plane_shrinks_every_row():
    plane = bytes([0x55, 0x00, 0xAA, 0x00, 0xFF, 0xFF])
    spot, _ = _rows(plane, 16, 3, 1200)
    assert spot == [bytes([0xF0]), bytes([0xF0]), bytes([0xFF])]


def test_black_raster_mode_shrinks_too():
    job = {
        "resolution": 1200,
        "width": 16,
        "height": 1,
        "paper": {"code": 4, "width": 100, "length": 100},
        "media": {"byte1": 0, "byte2": 0},
        "transfer_mode": "black_raster",
        "inks": [{"name": "spot", "printer_code": _SPOT, "is_process": False}],
    }
    stream = emitter.emit_job({"spot": bytes([0x55, 0x00])}, job)
    # black_raster emits no colour-selection command, so the rows are
    # read out directly rather than through _rows_by_ink.
    start = stream.find(bytes([ESC, 0x2A, 0x72, 0x00, 0x41])) + 5
    end = stream.find(bytes([ESC, 0x2A, 0x72, 0x43]))
    body = stream[start:end]
    assert body.endswith(bytes([0xF0])), body.hex(" ")
