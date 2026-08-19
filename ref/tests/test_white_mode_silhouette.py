"""Unit tests for the "silhouette" white mode building blocks
(foilwright_ref.job.compute_silhouette_plane / apply_silhouette_white_mode,
DOMAIN.md §6.1 / §7.1 / D-034).

Mirrors test_white_mode_opaque.py's structure: these functions live in
job.py (D-033's JobAssembly-equivalent layer), not raster.py, and are
called directly here (bypassing job.build_job_planes/apply_white_mode) so
that these tests exercise the building blocks in isolation.
"""

from __future__ import annotations

import pathlib
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import job as job_module
from foilwright_ref import raster


def _make_image(pixels_rgb: list[tuple[int, int, int]], width: int, height: int):
    assert len(pixels_rgb) == width * height
    buf = bytearray()
    for r, g, b in pixels_rgb:
        buf.extend((r, g, b))
    return width, height, bytes(buf)


def _make_ring_image(width: int, height: int, ring: set[tuple[int, int]]):
    """A width x height image, pure white everywhere except `ring`
    (black). Used to build an enclosed pure-white hole."""
    pixels = []
    for y in range(height):
        for x in range(width):
            pixels.append((0, 0, 0) if (x, y) in ring else (255, 255, 255))
    return _make_image(pixels, width, height)


def _ink(name, magic_rgb=None, tolerance=None, order=10, auto_undercoat=False):
    ink = {"name": name, "order": order, "auto_undercoat": auto_undercoat}
    if magic_rgb is not None:
        ink["magic_rgb"] = list(magic_rgb)
        ink["tolerance"] = tolerance
    else:
        ink["magic_rgb"] = None
    return ink


def _bit(plane: bytes, row_bytes: int, x: int, y: int) -> int:
    byte = plane[y * row_bytes + (x >> 3)]
    return 1 if byte & (0x80 >> (x & 7)) else 0


# 5x5 image: outer ring is a closed black square frame (x=1..3, y=1..3
# border), the frame's centre (x=2, y=2) is pure white -- an enclosed
# hole. Everything outside the frame is pure white too (the sheet's
# background).
_RING_5X5 = {
    (1, 1),
    (2, 1),
    (3, 1),
    (1, 2),
    (3, 2),
    (1, 3),
    (2, 3),
    (3, 3),
}


# ---------------------------------------------------------------------------
# compute_silhouette_plane
# ---------------------------------------------------------------------------


def test_all_pure_white_image_plane_is_empty():
    image = _make_image([(255, 255, 255), (255, 255, 255)], width=2, height=1)
    plane = job_module.compute_silhouette_plane(image)
    assert plane == bytes(1)


def test_all_non_white_image_plane_is_full():
    image = _make_image([(0, 0, 0), (10, 10, 10)], width=2, height=1)
    plane = job_module.compute_silhouette_plane(image)
    assert _bit(plane, 1, 0, 0) == 1
    assert _bit(plane, 1, 1, 0) == 1


def test_enclosed_white_hole_is_included_unlike_opaque():
    # This is the core difference from "opaque" (D-034): the pure-white
    # pixel in the middle of the closed ring is not reachable from the
    # sheet's edges, so it gets a bit -- even though it is pure white and
    # "opaque" would never set a bit for a pure-white pixel.
    image = _make_ring_image(5, 5, _RING_5X5)
    row_bytes = 1  # (5+7)//8 == 1
    silhouette = job_module.compute_silhouette_plane(image)
    opaque = job_module.compute_non_white_pixel_plane(image)

    # Ring pixels: set in both.
    for x, y in _RING_5X5:
        assert _bit(silhouette, row_bytes, x, y) == 1
        assert _bit(opaque, row_bytes, x, y) == 1

    # Enclosed hole (2,2): set in silhouette, not in opaque.
    assert _bit(silhouette, row_bytes, 2, 2) == 1
    assert _bit(opaque, row_bytes, 2, 2) == 0

    # Background corner (0,0), reachable from the edge: not set in either.
    assert _bit(silhouette, row_bytes, 0, 0) == 0
    assert _bit(opaque, row_bytes, 0, 0) == 0

    # silhouette has strictly more bits set than opaque on this fixture --
    # direct evidence the two modes produce different results.
    def count_bits(plane: bytes) -> int:
        return sum(b.bit_count() for b in plane)

    assert count_bits(silhouette) > count_bits(opaque)


def test_open_ring_hole_leaks_and_is_not_included():
    # Same ring but with a gap in one side (x=2, y=1 removed): the hole
    # is now connected to the background through the gap, so it is
    # reachable from the edge and must NOT be set -- confirms the
    # "closed outline only" caveat (D-034 supplementary note).
    ring = _RING_5X5 - {(2, 1)}
    image = _make_ring_image(5, 5, ring)
    row_bytes = 1
    silhouette = job_module.compute_silhouette_plane(image)

    assert _bit(silhouette, row_bytes, 2, 2) == 0


def test_single_row_image_every_pixel_touches_an_edge():
    # A 1-row image has every pixel on the top/bottom edge simultaneously
    # (height == 1), so a pure-white run has no way to be "enclosed" --
    # sanity check that the algorithm does not spuriously mark interior
    # pixels when there is no interior.
    image = _make_image(
        [(255, 255, 255), (0, 0, 0), (255, 255, 255)], width=3, height=1
    )
    plane = job_module.compute_silhouette_plane(image)
    assert _bit(plane, 1, 0, 0) == 0
    assert _bit(plane, 1, 1, 0) == 1
    assert _bit(plane, 1, 2, 0) == 0


# ---------------------------------------------------------------------------
# apply_silhouette_white_mode
# ---------------------------------------------------------------------------


def test_apply_silhouette_white_mode_merges_into_existing_white_plane():
    # Mirrors test_apply_opaque_white_mode_merges_into_existing_white_plane.
    original_inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    adjusted_inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=False,
        ),
        _ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=20),
    ]
    pixels = [
        (255, 0, 0),  # red: matched by spot, not pure white
        (0, 255, 255),  # unmatched by any spot ink, not pure white
        (230, 230, 230),  # white's own magic_rgb, direct match
        (255, 255, 255),  # pure white, reachable from the edge (1-row image)
    ]
    image = _make_image(pixels, width=4, height=1)
    baseline = raster.to_planes_magic(image, adjusted_inks)

    result = job_module.apply_silhouette_white_mode(image, original_inks, baseline)

    assert _bit(result["white"], 1, 0, 0) == 1  # not pure white
    assert _bit(result["white"], 1, 1, 0) == 1  # not pure white
    assert _bit(result["white"], 1, 2, 0) == 1  # direct magic match + silhouette
    assert _bit(result["white"], 1, 3, 0) == 0  # reachable pure white: excluded

    # red's own plane is untouched by the merge.
    assert _bit(result["red"], 1, 0, 0) == 1
    assert _bit(result["red"], 1, 1, 0) == 0


def test_apply_silhouette_white_mode_does_not_mutate_input_dict():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    image = _make_image([(255, 0, 0)], width=1, height=1)
    baseline = {"white": bytes(1)}
    baseline_copy = dict(baseline)

    job_module.apply_silhouette_white_mode(image, inks, baseline)

    assert baseline == baseline_copy


def test_apply_silhouette_white_mode_zero_undercoat_inks_returns_planes_unchanged():
    inks = [_ink("red", magic_rgb=(255, 0, 0), tolerance=0, order=10)]
    image = _make_image([(255, 0, 0)], width=1, height=1)
    planes = {"red": bytes(1)}

    result = job_module.apply_silhouette_white_mode(image, inks, planes)

    assert result is planes


def test_apply_silhouette_white_mode_multiple_undercoat_inks_returns_planes_unchanged():
    inks = [
        _ink("under1", magic_rgb=(0, 0, 0), tolerance=0, order=10, auto_undercoat=True),
        _ink(
            "under2", magic_rgb=(10, 10, 10), tolerance=0, order=20, auto_undercoat=True
        ),
    ]
    image = _make_image([(0, 0, 0)], width=1, height=1)
    planes = {"under1": bytes(1), "under2": bytes(1)}

    result = job_module.apply_silhouette_white_mode(image, inks, planes)

    assert result is planes


def test_apply_silhouette_white_mode_creates_plane_if_missing():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=0,
            order=10,
            auto_undercoat=True,
        )
    ]
    image = _make_image([(255, 0, 0)], width=1, height=1)

    result = job_module.apply_silhouette_white_mode(image, inks, {})

    assert _bit(result["white"], 1, 0, 0) == 1


# ---------------------------------------------------------------------------
# build_job_planes end-to-end (mirrors DOMAIN §7.1 usage)
# ---------------------------------------------------------------------------


def test_build_job_planes_silhouette_includes_enclosed_hole():
    palette = [
        {
            "name": "white",
            "order": 10,
            "auto_undercoat": True,
            "magic_rgb": [230, 230, 230],
            "tolerance": 0,
        },
        {
            "name": "black",
            "order": 90,
            "auto_undercoat": False,
            "magic_rgb": [0, 0, 0],
            "tolerance": 0,
            "channel": "K",
        },
    ]
    image = _make_ring_image(5, 5, _RING_5X5)

    inks, planes = job_module.build_job_planes(
        image, palette, "spot_only", white_mode="silhouette"
    )
    row_bytes = 1

    names = [ink["name"] for ink in inks]
    assert "white" in names
    assert _bit(planes["white"], row_bytes, 2, 2) == 1  # enclosed hole
    assert _bit(planes["white"], row_bytes, 0, 0) == 0  # background

    _inks_opaque, planes_opaque = job_module.build_job_planes(
        image, palette, "spot_only", white_mode="opaque"
    )
    assert _bit(planes_opaque["white"], row_bytes, 2, 2) == 0  # opaque excludes it


# ---------------------------------------------------------------------------
# no-regression: to_planes_magic/to_planes_auto (behind "none"/"auto"/"magic")
# are untouched by the new functions existing in the same module.
# ---------------------------------------------------------------------------


def test_to_planes_magic_auto_undercoat_union_still_works_unchanged():
    inks = [
        _ink(
            "white",
            magic_rgb=(230, 230, 230),
            tolerance=8,
            order=10,
            auto_undercoat=True,
        ),
        _ink("gold", magic_rgb=(225, 160, 0), tolerance=10, order=50),
    ]
    pixels = [(225, 160, 0), (255, 255, 255)]
    image = _make_image(pixels, width=2, height=1)
    planes = raster.to_planes_magic(image, inks)

    assert _bit(planes["gold"], 1, 0, 0) == 1
    assert _bit(planes["white"], 1, 0, 0) == 1
    assert _bit(planes["white"], 1, 1, 0) == 0


# ---------------------------------------------------------------------------
# The maze fixture: a detector for flood-fill bugs the ring fixture misses.
#
# silhouette_ring_64x48.ppm shows that silhouette and opaque differ, but it
# barely constrains *how* the fill works -- its background is one wide open
# rectangle, so a fill that loses some propagation still reaches every
# background pixel by another route. silhouette_maze_64x48.ppm is built the
# other way round: nearly all black, with a one-pixel-wide serpentine white
# corridor that touches the paper edge at exactly one place and has no
# alternative route. See tools/make-silhouette-maze-fixture.py.
# ---------------------------------------------------------------------------

MAZE_PATH = REPO_ROOT / "tests" / "cases" / "silhouette_maze_64x48.ppm"

# Counted directly from the fixture: 64*48 = 3072 pixels, of which 493 are
# pure white (445 in the corridor, reachable from the edge; 48 in the sealed
# chamber, not reachable).
MAZE_CORRIDOR = 445
MAZE_CHAMBER = 48
MAZE_NON_WHITE = 64 * 48 - MAZE_CORRIDOR - MAZE_CHAMBER


def _dots(plane: bytes) -> int:
    return sum(byte.bit_count() for byte in plane)


def test_maze_fixture_silhouette_covers_corridor_walls_and_sealed_chamber():
    image = raster.read_ppm(str(MAZE_PATH))
    plane = job_module.compute_silhouette_plane(image)
    assert _dots(plane) == MAZE_NON_WHITE + MAZE_CHAMBER


def test_maze_fixture_opaque_excludes_the_sealed_chamber():
    image = raster.read_ppm(str(MAZE_PATH))
    plane = job_module.compute_non_white_pixel_plane(image)
    assert _dots(plane) == MAZE_NON_WHITE


def test_maze_fixture_silhouette_and_opaque_differ_by_the_chamber_only():
    image = raster.read_ppm(str(MAZE_PATH))
    silhouette = job_module.compute_silhouette_plane(image)
    opaque = job_module.compute_non_white_pixel_plane(image)
    # silhouette is a strict superset of opaque, larger by exactly the
    # sealed chamber. If a flood-fill bug stranded part of the corridor,
    # this difference would grow.
    assert all(o & ~s == 0 for s, o in zip(silhouette, opaque, strict=True))
    assert _dots(silhouette) - _dots(opaque) == MAZE_CHAMBER
