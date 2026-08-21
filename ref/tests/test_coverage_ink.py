"""Unit tests for "coverage" inks -- the third ink kind, whose printed
area is chosen per job rather than by pixel colour (D-048, DOMAIN.md
§14.7 / §6.1).

Two layers are covered here:

  - config.load_palette's relaxed rule: an ink now qualifies with
    `magic_rgb`, `channel`, *or* `coverage`. `coverage` combined with
    either of the other two is rejected outright, because D-048 does not
    define what the combination would mean.
  - job.build_job_planes's `coverage_modes` argument: "none" (default),
    "artwork" (every pixel that is not pure white), "full" (every pixel).

The most important test in this file is
test_no_coverage_modes_builds_no_coverage_plane: D-048's premise is that
adding this ink kind changes nothing for anyone who does not ask for it.

The last section locks ref/ and src/ to the same bytes (D-006). The
`build-rgl` CLI entry point used by test_cross_language_match.py has no
way to pass `coverage_modes`, so instead both suites hash the planes
built from the same repository fixture and assert the same constants --
JobAssemblyTests.cs's CoverageInk_* tests carry the identical hex
strings, so a change in either implementation alone turns one side red.
"""

from __future__ import annotations

import hashlib
import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config, raster
from foilwright_ref import job as job_module

PALETTE_DIR = REPO_ROOT / "palette"
CASES_DIR = REPO_ROOT / "tests" / "cases"

# tests/cases/c8_cmyk4_598x1208.ppm. Chosen because 598 is not a multiple
# of 8: the padding bits past the last pixel of each row must stay zero,
# and a fixture whose width divides evenly cannot detect that.
CROSS_LANGUAGE_FIXTURE = "c8_cmyk4_598x1208.ppm"
CROSS_LANGUAGE_ARTWORK_SHA256 = (
    "dcb5f662930681ae74fcdac25e4bccf7c22d91f4f54412589817ecb9e0c444c5"
)
CROSS_LANGUAGE_FULL_SHA256 = (
    "f8fcbec82d4fd3e5c676ad0500e1c5c38c6e5e5f505c0b1f1c529ea4fdea2e16"
)


def _make_image(pixels_rgb: list[tuple[int, int, int]], width: int, height: int):
    assert len(pixels_rgb) == width * height
    buf = bytearray()
    for r, g, b in pixels_rgb:
        buf.extend((r, g, b))
    return width, height, bytes(buf)


def _fill_image(rgb: tuple[int, int, int], width: int, height: int):
    return _make_image([rgb] * (width * height), width, height)


def _bit(plane: bytes, row_bytes: int, x: int, y: int) -> int:
    byte = plane[y * row_bytes + (x >> 3)]
    return 1 if byte & (0x80 >> (x & 7)) else 0


def _popcount(plane: bytes) -> int:
    return sum(byte.bit_count() for byte in plane)


def _write_palette(tmp_path: pathlib.Path, body: str) -> str:
    path = tmp_path / "palette.yaml"
    path.write_text(body, encoding="utf-8")
    return str(path)


# ---------------------------------------------------------------------------
# config.load_palette: the relaxed ink-kind rule (D-048 decision 1)
# ---------------------------------------------------------------------------


def test_coverage_only_ink_is_accepted(tmp_path):
    """A coverage ink has neither magic_rgb nor channel and must still
    load -- this is the whole point of relaxing D-019's rule."""
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: glossy\n"
        "    label: gloss\n"
        "    printer_code: 0x0E\n"
        "    order: 95\n"
        "    coverage: true\n",
    )
    inks = config.load_palette(path)
    assert len(inks) == 1
    assert inks[0]["coverage"] is True
    assert inks[0]["magic_rgb"] is None
    assert inks[0]["channel"] is None


def test_coverage_defaults_to_false(tmp_path):
    """Every existing ink keeps working unchanged: `coverage` is optional
    and defaults to False in both implementations."""
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: black\n"
        "    label: k\n"
        "    printer_code: 0x00\n"
        "    order: 90\n"
        "    magic_rgb: [0, 0, 0]\n"
        "    tolerance: 8\n",
    )
    inks = config.load_palette(path)
    assert inks[0]["coverage"] is False


def test_coverage_with_magic_rgb_is_rejected(tmp_path):
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: glossy\n"
        "    label: gloss\n"
        "    printer_code: 0x0E\n"
        "    order: 95\n"
        "    coverage: true\n"
        "    magic_rgb: [1, 2, 3]\n"
        "    tolerance: 8\n",
    )
    with pytest.raises(config.ConfigError, match="cannot be combined"):
        config.load_palette(path)


def test_coverage_with_channel_is_rejected(tmp_path):
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: glossy\n"
        "    label: gloss\n"
        "    printer_code: 0x0E\n"
        "    order: 95\n"
        "    coverage: true\n"
        "    channel: K\n",
    )
    with pytest.raises(config.ConfigError, match="cannot be combined"):
        config.load_palette(path)


def test_ink_with_none_of_the_three_is_still_rejected(tmp_path):
    """D-019's rule is relaxed, not removed: an ink with no selection
    route at all is still an error."""
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: nothing\n"
        "    label: n\n"
        "    printer_code: 0x00\n"
        "    order: 10\n",
    )
    with pytest.raises(config.ConfigError, match="must have 'magic_rgb'"):
        config.load_palette(path)


def test_coverage_false_ink_with_none_of_the_three_is_rejected(tmp_path):
    """`coverage: false` is not a selection route either."""
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: nothing\n"
        "    label: n\n"
        "    printer_code: 0x00\n"
        "    order: 10\n"
        "    coverage: false\n",
    )
    with pytest.raises(config.ConfigError, match="must have 'magic_rgb'"):
        config.load_palette(path)


def test_coverage_must_be_boolean(tmp_path):
    path = _write_palette(
        tmp_path,
        "inks:\n"
        "  - name: glossy\n"
        "    label: gloss\n"
        "    printer_code: 0x0E\n"
        "    order: 95\n"
        "    coverage: yes please\n",
    )
    with pytest.raises(config.ConfigError, match="'coverage' must be true or false"):
        config.load_palette(path)


def test_default_palette_coverage_inks(tmp_path):
    """palette/default.yaml carries the two inks whose values §14.7
    confirmed: MF ink (0x10 / barcode 18) and glossy finish II
    (0x0E / barcode 19)."""
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    by_name = {ink["name"]: ink for ink in inks}

    mf = by_name["mf_ink"]
    assert mf["coverage"] is True
    assert mf["printer_code"] == 0x10
    assert mf["barcode"] == 18
    assert mf["order"] == 5
    assert mf["passes"] == 1
    assert mf["magic_rgb"] is None
    assert mf["channel"] is None

    glossy = by_name["glossy_finish"]
    assert glossy["coverage"] is True
    assert glossy["printer_code"] == 0x0E
    assert glossy["barcode"] == 19
    assert glossy["order"] == 95

    # Every other ink keeps coverage False -- the nine original entries
    # were not touched.
    for name, ink in by_name.items():
        if name not in ("mf_ink", "glossy_finish"):
            assert ink["coverage"] is False, name


# ---------------------------------------------------------------------------
# build_job_planes: coverage_modes (D-048 decisions 2/3)
# ---------------------------------------------------------------------------


def _default_palette() -> list[dict]:
    return config.load_palette(str(PALETTE_DIR / "default.yaml"))


def _artwork_image(width=4, height=2):
    """Half black, half pure white -- black is a magic_rgb match for the
    default palette's `black`, so the job is never empty."""
    pixels = []
    for y in range(height):
        for x in range(width):
            pixels.append((0, 0, 0) if x < width // 2 else (255, 255, 255))
    return _make_image(pixels, width, height)


def test_no_coverage_modes_builds_no_coverage_plane():
    """D-048 decision 3, and the detector for "nothing changes if you do
    not use it": with coverage_modes omitted entirely, neither coverage
    ink appears in the job or in the plane dict."""
    image = _artwork_image()
    inks, planes = job_module.build_job_planes(
        image, _default_palette(), "spot_only", white_mode="none"
    )
    names = [ink["name"] for ink in inks]
    assert names == ["black"]
    assert set(planes) == {"black"}


def test_explicit_none_builds_no_coverage_plane():
    image = _artwork_image()
    inks, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"mf_ink": "none", "glossy_finish": "none"},
    )
    assert [ink["name"] for ink in inks] == ["black"]
    assert "mf_ink" not in planes
    assert "glossy_finish" not in planes


def test_artwork_sets_every_non_pure_white_pixel():
    image = _artwork_image(width=4, height=2)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"glossy_finish": "artwork"},
    )
    plane = planes["glossy_finish"]
    row_bytes = 1
    for y in range(2):
        assert _bit(plane, row_bytes, 0, y) == 1
        assert _bit(plane, row_bytes, 1, y) == 1
        assert _bit(plane, row_bytes, 2, y) == 0
        assert _bit(plane, row_bytes, 3, y) == 0
    # Same result as the shared building block, which is exactly what the
    # implementation reuses (rather than repeating the pure-white test).
    assert plane == job_module.compute_non_white_pixel_plane(image)


def test_artwork_on_pure_white_image_leaves_the_ink_out_of_the_job():
    """An empty plane costs a pass and a cassette for nothing, so it is
    dropped -- same rule as every other ink (plane_has_content)."""
    image = _fill_image((255, 255, 255), width=8, height=2)
    inks, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"glossy_finish": "artwork"},
    )
    assert inks == []
    assert planes == {}


def test_full_sets_every_pixel():
    image = _fill_image((255, 255, 255), width=8, height=3)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"mf_ink": "full"},
    )
    plane = planes["mf_ink"]
    # "full" always has content, even on a blank sheet -- unlike "artwork".
    assert _popcount(plane) == 8 * 3
    assert plane == b"\xff\xff\xff"


def test_full_leaves_row_padding_bits_clear():
    """Row length is ceil(width/8) bytes; the bits past `width` must stay
    zero or the emitted RGL differs from src/."""
    image = _fill_image((255, 255, 255), width=5, height=2)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"mf_ink": "full"},
    )
    assert planes["mf_ink"] == b"\xf8\xf8"


def test_coverage_modes_do_not_touch_non_coverage_inks():
    """D-048: an entry naming an ink without `coverage` is ignored. Here
    `black` is asked for "full" and must still come out as its ordinary
    magic_rgb match (half the sheet), not a full-sheet plane."""
    image = _artwork_image(width=4, height=2)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"black": "full"},
    )
    assert _popcount(planes["black"]) == 4  # 2x2 black half only


def test_unknown_coverage_mode_raises():
    image = _artwork_image()
    with pytest.raises(ValueError, match="unknown coverage mode"):
        job_module.build_job_planes(
            image,
            _default_palette(),
            "spot_only",
            white_mode="none",
            coverage_modes={"glossy_finish": "everywhere"},
        )


def test_unknown_coverage_mode_is_not_silently_downgraded_to_none():
    """The failure mode this guards against is a typo silently meaning
    "do nothing", which would be invisible until a ribbon was wasted."""
    image = _artwork_image()
    with pytest.raises(ValueError):
        job_module.build_job_planes(
            image,
            _default_palette(),
            "spot_only",
            white_mode="none",
            coverage_modes={"glossy_finish": "Artwork"},
        )


def test_coverage_inks_land_at_their_palette_order():
    """D-048 decision 5, and the detector for the printing-layer order:
    MF ink (order 5) is laid down before white (10), glossy finish
    (order 95) after black (90)."""
    # White's magic_rgb (230,230,230) and black (0,0,0) both present, so
    # both colour inks are in the job.
    image = _make_image(
        [(230, 230, 230), (0, 0, 0), (255, 255, 255), (255, 255, 255)],
        width=4,
        height=1,
    )
    inks, _ = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="magic",
        coverage_modes={"mf_ink": "full", "glossy_finish": "full"},
    )
    assert [ink["name"] for ink in inks] == [
        "mf_ink",
        "white",
        "black",
        "glossy_finish",
    ]


def test_artwork_is_not_halftoned():
    """D-048 decision 4 / ppmtomd man:564-565: a coverage ink is on or
    off, never screened. A flat midtone would come out as a dot pattern
    if the halftone were applied, so requiring every pixel's bit proves
    it is not."""
    width, height = 16, 4
    image = _fill_image((128, 128, 128), width, height)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "auto",
        halftone="coarse_halftone",
        colour_correction="none",
        white_mode="none",
        coverage_modes={"glossy_finish": "artwork"},
    )
    assert _popcount(planes["glossy_finish"]) == width * height

    # Sanity: the colour side of the very same job *is* screened, so the
    # assertion above is not passing because the halftone silently did
    # nothing on this image.
    screened = [
        name
        for name, plane in planes.items()
        if name != "glossy_finish" and 0 < _popcount(plane) < width * height
    ]
    assert screened, "expected at least one halftoned colour plane"


def test_full_is_not_colour_corrected():
    """Colour correction never reaches a coverage ink either: a mid grey
    under "photo" correction still yields a solid full-sheet plane."""
    width, height = 16, 4
    image = _fill_image((128, 128, 128), width, height)
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "auto",
        colour_correction="photo",
        photo_lut_path=str(REPO_ROOT / "colour" / "photo_colcor.bin"),
        white_mode="none",
        coverage_modes={"mf_ink": "full"},
    )
    assert _popcount(planes["mf_ink"]) == width * height


# ---------------------------------------------------------------------------
# ref/ vs src/ (D-006): the same constants appear in JobAssemblyTests.cs
# ---------------------------------------------------------------------------


def test_cross_language_artwork_plane_hash():
    # Driven through build_job_planes, not the building block directly,
    # so this catches a wrong mode-to-plane wiring too -- same entry point
    # as JobAssemblyTests.cs's CrossLanguage_ArtworkPlaneHash.
    image = raster.read_ppm(str(CASES_DIR / CROSS_LANGUAGE_FIXTURE))
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"glossy_finish": "artwork"},
    )
    digest = hashlib.sha256(planes["glossy_finish"]).hexdigest()
    assert digest == CROSS_LANGUAGE_ARTWORK_SHA256


def test_cross_language_full_plane_hash():
    image = raster.read_ppm(str(CASES_DIR / CROSS_LANGUAGE_FIXTURE))
    _, planes = job_module.build_job_planes(
        image,
        _default_palette(),
        "spot_only",
        white_mode="none",
        coverage_modes={"mf_ink": "full"},
    )
    digest = hashlib.sha256(planes["mf_ink"]).hexdigest()
    assert digest == CROSS_LANGUAGE_FULL_SHA256
