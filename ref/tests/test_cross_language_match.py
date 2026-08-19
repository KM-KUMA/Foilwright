"""Byte-comparison test between ref/'s job-assembly layer
(foilwright_ref.job, D-033) and src/'s JobAssembly.cs, run through the
same emitter logic on both sides.

D-033's whole point is that JobAssembly.cs has no golden coverage: golden
compares against ppmtomd's own output, and JobAssembly is Foilwright-only
logic with no ppmtomd equivalent. This test is the substitute -- it feeds
the same PPM fixture and settings into both implementations and requires
byte-identical RGL output.

The C# side is driven through the `build-rgl` development subcommand
(Foilwright.Cli, D-033), which takes a PPM directly and writes the RGL
bytes to a file -- unlike `--debug-rgl` (Foilwright.Tray), which goes
through Ghostscript and would mix rasteriser differences into the
comparison.

Requires `dotnet build` to have produced
`src/Foilwright.Cli/bin/Debug/net10.0/Foilwright.Cli.exe` (or macOS/Linux's
extension-less equivalent). If it has not, every test in this module is
skipped -- this keeps `pytest ref/tests/` working standalone without a
.NET SDK (D-033).
"""

from __future__ import annotations

import pathlib
import subprocess
import sys
import tempfile

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config, emitter, job, raster

CASES_DIR = REPO_ROOT / "tests" / "cases"
PROFILES_DIR = REPO_ROOT / "profiles"
PAPERS_DIR = REPO_ROOT / "papers"
PALETTE_PATH = REPO_ROOT / "palette" / "default.yaml"
MEDIA_PATH = REPO_ROOT / "media.yaml"
PHOTO_LUT_PATH = REPO_ROOT / "colour" / "photo_colcor.bin"

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
    "(D-033: this test compares ref/ against the C# build-rgl subcommand)"
)


def _render_python(
    ppm_path: pathlib.Path,
    *,
    machine: str,
    paper_name: str,
    media_name: str,
    resolution: int,
    ink_mode: str,
    halftone: str,
    white_mode: str,
    colour_correction: str,
) -> bytes:
    profile = config.load_profile(str(PROFILES_DIR / f"{machine}.yaml"))
    paper_table = config.resolve_paper_table(profile, str(PAPERS_DIR))
    paper = paper_table[paper_name]
    media = config.load_media_table(str(MEDIA_PATH))[media_name]
    palette = config.load_palette(str(PALETTE_PATH))

    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image

    inks, planes = job.build_job_planes(
        image,
        palette,
        ink_mode,
        halftone=halftone,
        white_mode=white_mode,
        colour_correction=colour_correction,
        resolution=resolution,
        photo_lut_path=str(PHOTO_LUT_PATH),
    )

    job_dict = {
        "resolution": resolution,
        "paper": paper,
        "media": media,
        "inks": [
            {
                "name": ink["name"],
                "printer_code": ink["printer_code"],
                "passes": ink["passes"],
            }
            for ink in inks
        ],
        "width": width,
        "height": height,
    }
    return emitter.emit_job(planes, job_dict)


def _render_csharp(
    ppm_path: pathlib.Path,
    out_path: pathlib.Path,
    *,
    machine: str,
    paper_name: str,
    media_name: str,
    resolution: int,
    ink_mode: str,
    halftone: str,
    white_mode: str,
    colour_correction: str,
) -> bytes:
    assert _CLI_PATH is not None
    result = subprocess.run(
        [
            str(_CLI_PATH),
            "build-rgl",
            str(ppm_path),
            str(out_path),
            "--machine",
            machine,
            "--paper",
            paper_name,
            "--media",
            media_name,
            "--resolution",
            str(resolution),
            "--ink-mode",
            ink_mode,
            "--halftone",
            halftone,
            "--white-mode",
            white_mode,
            "--colour-correction",
            colour_correction,
        ],
        capture_output=True,
        text=True,
        timeout=60,
        check=False,
    )
    if result.returncode != 0:
        pytest.fail(
            f"Foilwright.Cli build-rgl failed (exit {result.returncode})\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}"
        )
    return out_path.read_bytes()


def _assert_byte_match(python_bytes: bytes, csharp_bytes: bytes, label: str) -> None:
    if python_bytes == csharp_bytes:
        return
    limit = min(len(python_bytes), len(csharp_bytes))
    first_diff = next(
        (i for i in range(limit) if python_bytes[i] != csharp_bytes[i]), limit
    )
    ctx_start = max(0, first_diff - 16)
    py_ctx = python_bytes[ctx_start : first_diff + 16].hex(" ")
    cs_ctx = csharp_bytes[ctx_start : first_diff + 16].hex(" ")
    pytest.fail(
        f"{label}: ref/ vs src/ byte mismatch at offset {first_diff} "
        f"(ref/ len={len(python_bytes)}, src/ len={len(csharp_bytes)})\n"
        f"ref/  [{ctx_start}:]: {py_ctx}\n"
        f"src/  [{ctx_start}:]: {cs_ctx}"
    )


# (case id, ppm fixture, kwargs forwarded to both renderers, minus machine/paper/media)
_CASES = [
    (
        "white_none",
        "c5_metallic4_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "none",
            "colour_correction": "plain",
        },
    ),
    (
        "white_auto",
        "c5_metallic4_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "auto",
            "colour_correction": "plain",
        },
    ),
    (
        "white_magic",
        "c5_metallic4_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "magic",
            "colour_correction": "plain",
        },
    ),
    (
        "white_opaque",
        "c5_metallic4_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "opaque",
            "colour_correction": "plain",
        },
    ),
    (
        "colour_correction_plain",
        "c6_fullcolour_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "auto",
            "colour_correction": "plain",
        },
    ),
    (
        "colour_correction_photo",
        "c6_fullcolour_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "auto",
            "colour_correction": "photo",
        },
    ),
    (
        "halftone_none",
        "c6_fullcolour_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "none",
            "white_mode": "auto",
            "colour_correction": "plain",
        },
    ),
    (
        "halftone_coarse",
        "c6_fullcolour_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "coarse_halftone",
            "white_mode": "auto",
            "colour_correction": "plain",
        },
    ),
    (
        "opaque_with_coarse_halftone_and_photo",
        "c6_fullcolour_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "auto",
            "halftone": "coarse_halftone",
            "white_mode": "opaque",
            "colour_correction": "photo",
        },
    ),
    (
        "spot_only_white_opaque",
        "c5_metallic4_240x120.ppm",
        {
            "resolution": 600,
            "ink_mode": "spot_only",
            "halftone": "none",
            "white_mode": "opaque",
            "colour_correction": "plain",
        },
    ),
    (
        # D-034: uses a fixture with a pure-white pixel enclosed by a
        # closed black ring, generated by tools/make-silhouette-fixture.py.
        # "silhouette" and "opaque" (below) must differ on this fixture --
        # confirmed separately by
        # test_white_mode_silhouette.test_build_job_planes_silhouette_includes_enclosed_hole
        # and the JobAssemblyTests.cs equivalent -- this case only proves
        # ref/ and src/ agree byte-for-byte on the silhouette result.
        "white_silhouette",
        "silhouette_ring_64x48.ppm",
        {
            "resolution": 600,
            "ink_mode": "spot_only",
            "halftone": "none",
            "white_mode": "silhouette",
            "colour_correction": "plain",
        },
    ),
    (
        # Same fixture, "opaque" mode -- pairs with "white_silhouette" above
        # to make the ref/-vs-src/ match cover both branches on the same
        # enclosed-hole geometry.
        "opaque_on_silhouette_fixture",
        "silhouette_ring_64x48.ppm",
        {
            "resolution": 600,
            "ink_mode": "spot_only",
            "halftone": "none",
            "white_mode": "opaque",
            "colour_correction": "plain",
        },
    ),
    (
        # D-034: the ring fixture proves silhouette and opaque differ, but it
        # is a weak detector of flood-fill bugs -- its background is one wide
        # open rectangle, so a fill that loses some propagation still reaches
        # every background pixel by another route and the comparison passes
        # unchanged. Measured: shortening the right-hand span extension by one
        # pixel, and dropping the leftmost column of the neighbour-row scan,
        # both left every case green.
        #
        # This fixture is built the other way round (see
        # tools/make-silhouette-maze-fixture.py): nearly the whole sheet is
        # black, with a one-pixel-wide serpentine white corridor carved into
        # it. The corridor reaches the paper edge at exactly one place and has
        # no alternative route, so losing propagation anywhere along it turns
        # the whole remainder of the corridor into "unreached pure white" and
        # the silhouette plane changes substantially.
        "white_silhouette_maze",
        "silhouette_maze_64x48.ppm",
        {
            "resolution": 600,
            "ink_mode": "spot_only",
            "halftone": "none",
            "white_mode": "silhouette",
            "colour_correction": "plain",
        },
    ),
    (
        # Same maze fixture under "opaque", pairing with the case above the
        # same way the ring fixture's two cases pair.
        "opaque_on_maze_fixture",
        "silhouette_maze_64x48.ppm",
        {
            "resolution": 600,
            "ink_mode": "spot_only",
            "halftone": "none",
            "white_mode": "opaque",
            "colour_correction": "plain",
        },
    ),
]


@pytest.mark.parametrize("case_id,ppm_name,kwargs", _CASES, ids=[c[0] for c in _CASES])
def test_ref_and_csharp_rgl_match(case_id, ppm_name, kwargs, tmp_path):
    if _CLI_PATH is None:
        pytest.skip(_SKIP_REASON)

    ppm_path = CASES_DIR / ppm_name
    machine = "md-5000"
    paper_name = "a4"
    media_name = "plain_paper"

    python_bytes = _render_python(
        ppm_path,
        machine=machine,
        paper_name=paper_name,
        media_name=media_name,
        **kwargs,
    )

    out_path = tmp_path / f"{case_id}.bin"
    csharp_bytes = _render_csharp(
        ppm_path,
        out_path,
        machine=machine,
        paper_name=paper_name,
        media_name=media_name,
        **kwargs,
    )

    _assert_byte_match(python_bytes, csharp_bytes, case_id)


def test_at_least_one_case_actually_produced_non_trivial_output():
    """Guard against every case degenerating to an empty job (which would
    make the byte-match assertion trivially true for the wrong reason --
    e.g. both sides silently building zero ink planes)."""
    if _CLI_PATH is None:
        pytest.skip(_SKIP_REASON)

    with tempfile.TemporaryDirectory() as tmp:
        tmp_path = pathlib.Path(tmp)
        python_bytes = _render_python(
            CASES_DIR / "c6_fullcolour_240x120.ppm",
            machine="md-5000",
            paper_name="a4",
            media_name="plain_paper",
            resolution=600,
            ink_mode="auto",
            halftone="none",
            white_mode="auto",
            colour_correction="plain",
        )
        out_path = tmp_path / "sanity.bin"
        csharp_bytes = _render_csharp(
            CASES_DIR / "c6_fullcolour_240x120.ppm",
            out_path,
            machine="md-5000",
            paper_name="a4",
            media_name="plain_paper",
            resolution=600,
            ink_mode="auto",
            halftone="none",
            white_mode="auto",
            colour_correction="plain",
        )
    assert len(python_bytes) > 100
    assert len(csharp_bytes) > 100
