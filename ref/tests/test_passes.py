"""Structural tests for ink `passes` (overprinting; DOMAIN.md §6.2).

Byte-exact golden coverage is not possible for this change: WSL was
unavailable to regenerate `tests/golden/*.bin` from a real ppmtomd run
(see docs/DOMAIN.md §6.1's 2026-08-19 note). These tests instead check
the command-stream *structure* that was confirmed by hand from a real
`ppmtomd -colours C=White,M=White` capture: repeating an ink's
(colour-selection + raster) `passes` times, separated by backfeeds,
with the 0x80 "final" flag on only the very last occurrence in the
job, and exactly one eject at the very end.

**passes >= 2 is unverified against a golden fixture.** Re-run
`tests/cases/make_golden.sh` under WSL and diff against a fresh
ppmtomd capture once WSL is available, per DOMAIN.md §6.1.
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import emitter

ESC = 0x1B

# Minimal 1x8 all-black plane (one row, one byte, fully set) so the
# raster body is non-empty and deterministic without needing raster.py.
_ROW_BYTES = 1
_WIDTH = 8
_HEIGHT = 1
_PLANE = bytes([0xFF]) * (_ROW_BYTES * _HEIGHT)


def _base_job(inks: list[dict]) -> dict:
    return {
        "resolution": 600,
        "width": _WIDTH,
        "height": _HEIGHT,
        "paper": {"code": 4, "width": 100, "length": 100},
        "media": {"byte1": 0, "byte2": 0},
        "inks": inks,
    }


def _planes_for(inks: list[dict]) -> dict[str, bytes]:
    return {ink["name"]: _PLANE for ink in inks}


def _find_selections(stream: bytes) -> list[tuple[int, int]]:
    """Return [(printer_code, flag), ...] for every colour-selection
    command (`\\x1b\\x1a{code}{flag}r`) in the stream, in order.

    Distinguishes selections from backfeeds (`\\x1b\\x1a\\x00\\x00\\x0c`)
    by the trailing opcode byte: selections end in 'r' (0x72), backfeeds
    end in 0x0C.
    """
    selections = []
    i = 0
    while i < len(stream) - 4:
        if stream[i] == ESC and stream[i + 1] == 0x1A and stream[i + 4] == 0x72:
            selections.append((stream[i + 2], stream[i + 3]))
            i += 5
        else:
            i += 1
    return selections


def _count_backfeeds(stream: bytes) -> int:
    target = bytes([ESC, 0x1A, 0x00, 0x00, 0x0C])
    count = 0
    i = 0
    while True:
        idx = stream.find(target, i)
        if idx == -1:
            break
        count += 1
        i = idx + 1
    return count


def _count_ejects(stream: bytes) -> int:
    """Count standalone form-feed (0x0C) bytes that are NOT part of a
    backfeed command (`\\x1b\\x1a\\x00\\x00\\x0c`)."""
    backfeed = bytes([ESC, 0x1A, 0x00, 0x00, 0x0C])
    backfeed_positions = set()
    i = 0
    while True:
        idx = stream.find(backfeed, i)
        if idx == -1:
            break
        backfeed_positions.add(idx + 4)  # index of the 0x0C byte itself
        i = idx + 1

    count = 0
    for i, byte in enumerate(stream):
        if byte == 0x0C and i not in backfeed_positions:
            count += 1
    return count


def test_single_ink_passes_2_selects_twice_with_one_backfeed_and_final_flag_once():
    inks = [{"name": "white", "printer_code": 0x0B, "passes": 2}]
    job = _base_job(inks)
    stream = emitter.emit_job(_planes_for(inks), job)

    selections = _find_selections(stream)
    assert selections == [(0x0B, 0x00), (0x0B, 0x80)]
    assert _count_backfeeds(stream) == 1
    assert _count_ejects(stream) == 1


def test_single_ink_passes_3_selects_thrice_with_two_backfeeds():
    inks = [{"name": "white", "printer_code": 0x0B, "passes": 3}]
    job = _base_job(inks)
    stream = emitter.emit_job(_planes_for(inks), job)

    selections = _find_selections(stream)
    assert selections == [(0x0B, 0x00), (0x0B, 0x00), (0x0B, 0x80)]
    assert _count_backfeeds(stream) == 2
    assert _count_ejects(stream) == 1


def test_passes_1_matches_no_passes_key_at_all():
    inks_explicit = [{"name": "white", "printer_code": 0x0B, "passes": 1}]
    inks_default = [{"name": "white", "printer_code": 0x0B}]

    stream_explicit = emitter.emit_job(
        _planes_for(inks_explicit), _base_job(inks_explicit)
    )
    stream_default = emitter.emit_job(
        _planes_for(inks_default), _base_job(inks_default)
    )

    assert stream_explicit == stream_default


def test_multi_ink_multi_passes_ejects_exactly_once():
    inks = [
        {"name": "white", "printer_code": 0x0B, "passes": 2},
        {"name": "cyan", "printer_code": 0x01, "passes": 3},
        {"name": "black", "printer_code": 0x00},  # default passes=1
    ]
    job = _base_job(inks)
    stream = emitter.emit_job(_planes_for(inks), job)

    selections = _find_selections(stream)
    # 2 white + 3 cyan + 1 black = 6 occurrences, final flag on the last only.
    assert selections == [
        (0x0B, 0x00),
        (0x0B, 0x00),
        (0x01, 0x00),
        (0x01, 0x00),
        (0x01, 0x00),
        (0x00, 0x80),
    ]
    assert _count_backfeeds(stream) == 5  # 6 occurrences -> 5 backfeeds between them
    assert _count_ejects(stream) == 1


@pytest.mark.parametrize("bad_passes", [0, -1, -5])
def test_passes_zero_or_negative_raises(bad_passes):
    inks = [{"name": "white", "printer_code": 0x0B, "passes": bad_passes}]
    job = _base_job(inks)
    with pytest.raises(ValueError):
        emitter.emit_job(_planes_for(inks), job)
