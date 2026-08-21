"""Cross-language comparison of PPM input classification between
ref/'s raster.read_ppm and src/'s Foilwright.Core.PpmImage.Read.

Golden only covers single-page input, so the two implementations had
drifted apart: src/ demanded an exact length match (and reported
"truncated" even when twice the data was present), while ref/ silently
sliced off the extra and printed page 1 only. Both now classify input
identically; this test is what keeps them that way (D-006).

Mirrors the structure of test_cross_language_match.py (D-033): the C#
side is driven through the `build-rgl` development subcommand, which
reads the PPM directly without invoking Ghostscript. On failure
Foilwright.Cli prints `エラー: <message>` to stderr (cp932) and exits 1,
so the message is the observable classification. The IsMultiPage /
is_multi_page flag is not observable through the CLI; each side asserts
it in its own unit tests (PpmMultiPageTests.cs / test_ppm_multipage.py).

Requires `dotnet build` to have produced
`src/Foilwright.Cli/bin/Debug/net10.0/Foilwright.Cli.exe` (or the
extension-less equivalent) -- if not, every test in this module is
skipped, keeping `pytest ref/tests/` usable without a .NET SDK.
"""

from __future__ import annotations

import pathlib
import subprocess
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref.raster import PPMError, read_ppm

CASES_DIR = REPO_ROOT / "tests" / "cases"
BASE_PPM = CASES_DIR / "c1_black_120x120.ppm"

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
    "(D-006: this test compares ref/'s read_ppm against the C# build-rgl subcommand)"
)

# Foilwright.Cli prefixes every caught error with this on stderr.
_ERROR_PREFIX = "エラー: "


def _decode_cli_stderr(raw: bytes) -> str:
    """Decode Foilwright.Cli's stderr without assuming the console code page.

    .NET picks the encoding for a redirected stderr from the console code
    page, so the same executable emits cp932 on a stock Japanese Windows
    and UTF-8 when the code page has been switched to 65001. Decoding with
    a fixed cp932 mangles the Japanese `エラー: ` prefix under 65001, and
    the caller then reports "no recognisable error line" -- a test failure
    that says nothing about the code under test. Try both and keep the
    first decoding that actually carries the prefix.
    """
    candidates = []
    for encoding in ("cp932", "utf-8"):
        try:
            candidates.append(raw.decode(encoding))
        except UnicodeDecodeError:
            continue
    for text in candidates:
        if any(line.startswith(_ERROR_PREFIX) for line in text.splitlines()):
            return text
    # Neither decoding found the prefix: fall back to a lossy read so the
    # caller can still show something useful in its failure message.
    return raw.decode("cp932", errors="replace")


def _classify_python(ppm_path: pathlib.Path) -> str:
    try:
        read_ppm(str(ppm_path))
    except PPMError as error:
        return str(error)
    return "ok"


def _classify_csharp(ppm_path: pathlib.Path, out_path: pathlib.Path) -> str:
    assert _CLI_PATH is not None
    result = subprocess.run(
        [str(_CLI_PATH), "build-rgl", str(ppm_path), str(out_path)],
        capture_output=True,
        timeout=120,
        check=False,
    )
    if result.returncode == 0:
        return "ok"
    stderr = _decode_cli_stderr(result.stderr)
    for line in stderr.splitlines():
        if line.startswith(_ERROR_PREFIX):
            return line[len(_ERROR_PREFIX) :]
    pytest.fail(
        f"Foilwright.Cli build-rgl failed without a recognisable error line "
        f"(exit {result.returncode})\nstderr: {stderr}"
    )


def _make_case(name: str, tmp_path: pathlib.Path) -> pathlib.Path:
    page = BASE_PPM.read_bytes()
    if name == "single_page":
        data = page
    elif name == "truncated":
        data = page[:-5]
    elif name == "two_pages":
        data = page + page
    elif name == "three_pages":
        data = page + page + page
    elif name == "trailing_junk":
        data = page + b"\x00\x01\x02\x03"
    else:  # pragma: no cover - guards against typos in the parametrisation
        raise AssertionError(f"unknown case {name!r}")
    path = tmp_path / f"{name}.ppm"
    path.write_bytes(data)
    return path


@pytest.mark.skipif(_CLI_PATH is None, reason=_SKIP_REASON)
@pytest.mark.parametrize(
    "name",
    ["single_page", "truncated", "two_pages", "three_pages", "trailing_junk"],
)
def test_ppm_classification_matches(name: str, tmp_path: pathlib.Path) -> None:
    ppm_path = _make_case(name, tmp_path)
    python_result = _classify_python(ppm_path)
    csharp_result = _classify_csharp(ppm_path, tmp_path / "out.bin")

    assert python_result == csharp_result, (
        f"{name}: ref/ and src/ classified the same input differently.\n"
        f"python: {python_result}\ncsharp: {csharp_result}"
    )


@pytest.mark.skipif(_CLI_PATH is None, reason=_SKIP_REASON)
def test_multi_page_case_really_is_the_multi_page_error(tmp_path: pathlib.Path) -> None:
    # Guards the test above against agreeing on the wrong thing: if both
    # sides silently dropped the extra page they would still "match".
    ppm_path = _make_case("two_pages", tmp_path)
    assert _classify_python(ppm_path).startswith("multi-page PPM:")
    assert _classify_csharp(ppm_path, tmp_path / "out.bin").startswith(
        "multi-page PPM:"
    )
