"""Unit tests for the ``per_page`` ink specification method.

See docs/DOMAIN.md §6.4.1 (spec), §6.6 (the three methods), §4.3 (pass
order comes from the palette, not from page order).
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import raster

BLACK = (0, 0, 0)
WHITE = (255, 255, 255)
MID_GREY = (128, 128, 128)


def _page(width: int, height: int, colours: list[tuple[int, int, int]]):
    """Build a page whose columns cycle through `colours`."""
    band = max(1, width // len(colours))
    pixels = bytearray()
    for _ in range(height):
        for x in range(width):
            pixels += bytes(colours[min(x // band, len(colours) - 1)])
    return (width, height, bytes(pixels))


def _bit(plane: bytes, width: int, x: int, y: int) -> int:
    row_bytes = (width + 7) // 8
    return 1 if plane[y * row_bytes + (x >> 3)] & (0x80 >> (x & 7)) else 0


def test_layers_map_to_their_own_inks():
    """The real workflow: one layer for the eye whites, another for the
    rest. Each page is drawn in black and printed in its assigned ink
    (§10.9.3), so nothing has to be colour-matched."""
    width, height = 16, 4
    eye_layer = _page(width, height, [BLACK, WHITE])  # left half inked
    body_layer = _page(width, height, [WHITE, BLACK])  # right half inked

    planes = raster.to_planes_per_page([eye_layer, body_layer], ["white", "black"])

    assert set(planes) == {"white", "black"}
    # left half belongs to the white ink only
    assert _bit(planes["white"], width, 2, 1) == 1
    assert _bit(planes["black"], width, 2, 1) == 0
    # right half belongs to the black ink only
    assert _bit(planes["white"], width, 13, 1) == 0
    assert _bit(planes["black"], width, 13, 1) == 1


def test_pure_white_is_not_printed():
    """White areas of the artwork are the unprinted background, matching
    §6.1's treatment of pure white."""
    width, height = 8, 2
    page = _page(width, height, [WHITE])
    planes = raster.to_planes_per_page([page], ["white"])
    assert set(planes["white"]) == {0}


def test_threshold_matches_to_planes():
    """Binarisation uses the same K formula as the CMYK path, so a mid
    grey lands on the same side of the threshold in both (DOMAIN §4.2)."""
    width, height = 8, 2
    page = _page(width, height, [MID_GREY])

    per_page = raster.to_planes_per_page([page], ["black"])
    cmyk = raster.to_planes(page, {"black": "K"})

    assert per_page["black"] == cmyk["black"]


def test_page_count_must_match_ink_count():
    page = _page(8, 2, [BLACK])
    with pytest.raises(ValueError):
        raster.to_planes_per_page([page, page], ["white"])


def test_pages_must_share_dimensions():
    """Pages print onto one sheet, so differing sizes cannot register."""
    with pytest.raises(ValueError) as exc:
        raster.to_planes_per_page(
            [_page(8, 2, [BLACK]), _page(16, 2, [BLACK])], ["white", "black"]
        )
    assert "register" in str(exc.value)


def test_an_ink_may_not_be_assigned_twice():
    """Two pages claiming the same ink would silently overwrite one
    another; reject instead."""
    page = _page(8, 2, [BLACK])
    with pytest.raises(ValueError):
        raster.to_planes_per_page([page, page], ["white", "white"])


def test_empty_document_is_rejected():
    with pytest.raises(ValueError):
        raster.to_planes_per_page([], [])
