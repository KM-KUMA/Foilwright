"""Byte-comparison test between foilwright_ref.png (ref/, D-036) and
Foilwright.Core.PngImage (src/, D-036), run through the `decode-png`
development subcommand.

Mirrors the structure of test_cross_language_match.py (D-033): the C# side
is driven through a decode-png development subcommand rather than any
production entry point, so this test compares the two PNG decoders in
isolation. Requires `dotnet build` to have produced
`src/Foilwright.Cli/bin/Debug/net10.0/Foilwright.Cli.exe` (or the
extension-less equivalent) -- if not, every test in this module is skipped.
"""

from __future__ import annotations

import pathlib
import subprocess
import sys
import tempfile

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import png

CASES_DIR = REPO_ROOT / "tests" / "cases" / "png"

_CLI_CANDIDATES = [
    REPO_ROOT
    / "src"
    / "Foilwright.Cli"
    / "bin"
    / "Debug"
    / "net10.0"
    / "Foilwright.Cli.exe",
    REPO_ROOT
    / "src"
    / "Foilwright.Cli"
    / "bin"
    / "Debug"
    / "net10.0"
    / "Foilwright.Cli",
]


def _find_cli() -> pathlib.Path | None:
    for candidate in _CLI_CANDIDATES:
        if candidate.is_file():
            return candidate
    return None


_CLI_PATH = _find_cli()
_SKIP_REASON = (
    "Foilwright.Cli is not built; run 'dotnet build' under src/ first "
    "(D-036: this test compares ref/'s PNG decoder against the C# decode-png subcommand)"
)


def _decode_csharp(
    png_path: pathlib.Path, out_path: pathlib.Path
) -> tuple[int, int, bytes]:
    assert _CLI_PATH is not None
    result = subprocess.run(
        [str(_CLI_PATH), "decode-png", str(png_path), str(out_path)],
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    if result.returncode != 0:
        pytest.fail(
            f"Foilwright.Cli decode-png failed (exit {result.returncode})\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}"
        )
    width_str, height_str = result.stdout.strip().split()
    return int(width_str), int(height_str), out_path.read_bytes()


def _assert_byte_match(python_pixels: bytes, csharp_pixels: bytes, label: str) -> None:
    if python_pixels == csharp_pixels:
        return
    limit = min(len(python_pixels), len(csharp_pixels))
    first_diff = next(
        (i for i in range(limit) if python_pixels[i] != csharp_pixels[i]), limit
    )
    pytest.fail(
        f"{label}: byte mismatch between ref/ (Python) and src/ (C#) output.\n"
        f"python length={len(python_pixels)}, csharp length={len(csharp_pixels)}\n"
        f"first differing byte at offset {first_diff}: "
        f"python={python_pixels[first_diff : first_diff + 8]!r}, "
        f"csharp={csharp_pixels[first_diff : first_diff + 8]!r}"
    )


_FIXTURE_NAMES = [
    "filter0_none.png",
    "filter1_sub.png",
    "filter2_up.png",
    "filter3_average.png",
    "filter4_paeth.png",
    "idat_split.png",
    "ancillary.png",
]


@pytest.mark.skipif(_CLI_PATH is None, reason=_SKIP_REASON)
@pytest.mark.parametrize("name", _FIXTURE_NAMES)
def test_png_decoders_match(name: str) -> None:
    png_path = CASES_DIR / name
    py_width, py_height, py_pixels = png.read_png_rgba(str(png_path))

    with tempfile.TemporaryDirectory() as tmp_dir:
        out_path = pathlib.Path(tmp_dir) / "out.raw"
        cs_width, cs_height, cs_pixels = _decode_csharp(png_path, out_path)

    assert (py_width, py_height) == (cs_width, cs_height), (
        f"{name}: dimension mismatch python=({py_width},{py_height}) csharp=({cs_width},{cs_height})"
    )
    _assert_byte_match(py_pixels, cs_pixels, name)


@pytest.mark.skipif(_CLI_PATH is None, reason=_SKIP_REASON)
def test_png_decoders_match_gs_alpha() -> None:
    png_path = CASES_DIR / "gs_alpha.png"
    if not png_path.is_file():
        pytest.skip(
            "gs_alpha.png not present (Ghostscript was unavailable when fixtures were generated)"
        )

    py_width, py_height, py_pixels = png.read_png_rgba(str(png_path))

    with tempfile.TemporaryDirectory() as tmp_dir:
        out_path = pathlib.Path(tmp_dir) / "out.raw"
        cs_width, cs_height, cs_pixels = _decode_csharp(png_path, out_path)

    assert (py_width, py_height) == (cs_width, cs_height)
    _assert_byte_match(py_pixels, cs_pixels, "gs_alpha.png")
