"""read_ppm's classification of PPM inputs (single page / truncated /
multi-page / trailing junk).

Ghostscript concatenates every page into one ppmraw file when
-sOutputFile has no %d, so a multi-page document arrives as several P6
images in a single file. Silently reading only the first page would let
the user print one page without noticing, so it is a hard error --
distinct from arbitrary trailing junk, which means a corrupt file.

The same classification and the same wording live in
src/Foilwright.Core/Ppm.cs (D-006); the two are compared against each
other in test_ppm_multipage_cross_language.py.
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref.raster import PPMError, read_ppm


def _make_ppm(width: int, height: int, seed: int = 0) -> bytes:
    header = f"P6\n{width} {height}\n255\n".encode("ascii")
    pixels = bytes((seed + i) % 256 for i in range(width * height * 3))
    return header + pixels


def _write(tmp_path: pathlib.Path, name: str, data: bytes) -> str:
    path = tmp_path / name
    path.write_bytes(data)
    return str(path)


def test_single_page_reads(tmp_path: pathlib.Path) -> None:
    # Regression detector: a well-formed single page still reads.
    width, height, pixels = read_ppm(
        _write(tmp_path, "single.ppm", _make_ppm(4, 3, seed=7))
    )

    assert (width, height) == (4, 3)
    assert len(pixels) == 4 * 3 * 3
    assert pixels[0] == 7
    assert pixels[-1] == (7 + len(pixels) - 1) % 256


def test_truncated_raises_truncated(tmp_path: pathlib.Path) -> None:
    data = _make_ppm(4, 3)[:-5]

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "short.ppm", data))

    assert str(excinfo.value) == "truncated PPM data: expected 36 bytes, got 31"
    assert excinfo.value.is_multi_page is False


def test_two_concatenated_images_raise_multi_page(tmp_path: pathlib.Path) -> None:
    data = _make_ppm(4, 3) + _make_ppm(4, 3, seed=100)

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "two.ppm", data))

    assert excinfo.value.is_multi_page is True
    assert str(excinfo.value) == (
        "multi-page PPM: the document has more than one page; "
        "Foilwright prints one page per job (found 2 pages)"
    )


def test_three_concatenated_images_count_pages_exactly(tmp_path: pathlib.Path) -> None:
    # Pages are counted by walking each header to find where the next
    # image starts, so a "P6 " sequence that happens to occur inside the
    # pixel data (planted here on purpose) does not inflate the count.
    second = bytearray(_make_ppm(4, 3))
    second[-6:-3] = b"P6 "
    data = _make_ppm(4, 3) + bytes(second) + _make_ppm(2, 2, seed=5)

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "three.ppm", data))

    assert excinfo.value.is_multi_page is True
    assert "(found 3 pages)" in str(excinfo.value)


def test_trailing_junk_is_not_multi_page(tmp_path: pathlib.Path) -> None:
    data = _make_ppm(4, 3) + b"\x00\x01\x02\x03"

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "junk.ppm", data))

    assert excinfo.value.is_multi_page is False
    assert str(excinfo.value) == (
        "unexpected trailing data after PPM image: expected 36 bytes, got 40"
    )


def test_trailing_magic_without_valid_header_is_not_multi_page(
    tmp_path: pathlib.Path,
) -> None:
    # "P6" not followed by whitespace is not the start of a next image.
    data = _make_ppm(4, 3) + b"P6x"

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "fakemagic.ppm", data))

    assert excinfo.value.is_multi_page is False
    assert str(excinfo.value).startswith("unexpected trailing data after PPM image:")


def test_second_page_truncated_reports_lower_bound(tmp_path: pathlib.Path) -> None:
    # The trailing page is cut short, so the exact page count is unknown.
    # Say "at least" rather than reporting a number that may be wrong.
    data = _make_ppm(4, 3) + _make_ppm(4, 3)[:-4]

    with pytest.raises(PPMError) as excinfo:
        read_ppm(_write(tmp_path, "cut.ppm", data))

    assert excinfo.value.is_multi_page is True
    assert "(found at least 2 pages)" in str(excinfo.value)


def test_ppm_error_defaults_to_not_multi_page() -> None:
    # The single-argument constructor must keep working.
    error = PPMError("boom")
    assert error.is_multi_page is False
    assert str(error) == "boom"
