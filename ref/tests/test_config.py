"""Unit tests for the config loading layer (foilwright_ref.config).

See docs/DOMAIN.md §4.3 (pass order / tie-break), §4.9 (stable sort),
§5.1/§5.2 (machine profile schema, null preservation), §6.1/§6.2
(palette schema).
"""

from __future__ import annotations

import pathlib
import sys

import pytest

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config

PROFILES_DIR = REPO_ROOT / "profiles"
PALETTE_DIR = REPO_ROOT / "palette"
PAPERS_DIR = REPO_ROOT / "papers"


# ---------------------------------------------------------------------------
# load_profile
# ---------------------------------------------------------------------------


def test_load_profile_md5000_preserves_null_fields():
    profile = config.load_profile(str(PROFILES_DIR / "md-5000.yaml"))
    assert profile["model"] == "MD-5000"
    assert profile["lf_correction"] is None
    assert profile["max_width_dots"] is None
    assert profile["supports_opaque_white"] is True


def test_load_profile_md5500():
    profile = config.load_profile(str(PROFILES_DIR / "md-5500.yaml"))
    assert profile["model"] == "MD-5500"
    assert profile["lf_correction"] is None
    assert profile["max_width_dots"] is None


def test_require_value_raises_on_null(tmp_path):
    profile = config.load_profile(str(PROFILES_DIR / "md-5000.yaml"))
    with pytest.raises(config.ConfigError):
        config.require_value(profile, "lf_correction")
    with pytest.raises(config.ConfigError):
        config.require_value(profile, "max_width_dots")


def test_require_value_returns_present_value():
    profile = config.load_profile(str(PROFILES_DIR / "md-5000.yaml"))
    assert config.require_value(profile, "model") == "MD-5000"


def test_load_profile_missing_model(tmp_path):
    bad = tmp_path / "bad_profile.yaml"
    bad.write_text("resolutions: []\n", encoding="utf-8")
    with pytest.raises(config.ConfigError):
        config.load_profile(str(bad))


def test_load_profile_missing_resolutions(tmp_path):
    bad = tmp_path / "bad_profile.yaml"
    bad.write_text("model: MD-9999\npaper_table: 5000-series\n", encoding="utf-8")
    with pytest.raises(config.ConfigError):
        config.load_profile(str(bad))


def test_load_profile_empty_resolutions(tmp_path):
    bad = tmp_path / "bad_profile.yaml"
    bad.write_text(
        "model: MD-9999\npaper_table: 5000-series\nresolutions: []\n", encoding="utf-8"
    )
    with pytest.raises(config.ConfigError):
        config.load_profile(str(bad))


def test_load_profile_resolution_missing_dpi(tmp_path):
    bad = tmp_path / "bad_profile.yaml"
    bad.write_text(
        "model: MD-9999\npaper_table: 5000-series\nresolutions:\n  - { dpi_x: 600 }\n",
        encoding="utf-8",
    )
    with pytest.raises(config.ConfigError):
        config.load_profile(str(bad))


def test_load_profile_missing_paper_table(tmp_path):
    bad = tmp_path / "bad_profile.yaml"
    bad.write_text(
        "model: MD-9999\nresolutions:\n  - { dpi_x: 600, dpi_y: 600 }\n",
        encoding="utf-8",
    )
    with pytest.raises(config.ConfigError):
        config.load_profile(str(bad))


def test_load_profile_md5000_has_resolutions_and_paper_table():
    profile = config.load_profile(str(PROFILES_DIR / "md-5000.yaml"))
    assert profile["paper_table"] == "5000-series"
    assert len(profile["resolutions"]) == 2
    assert profile["resolutions"][0]["dpi_x"] == 600


# ---------------------------------------------------------------------------
# load_paper_table / resolve_paper_table
# ---------------------------------------------------------------------------


def test_load_paper_table_5000_series():
    table = config.load_paper_table(str(PAPERS_DIR / "5000-series.yaml"))
    assert set(table) == {
        "custom",
        "executive",
        "letter",
        "legal",
        "a4",
        "b5",
        "postcard",
        "dyesublabel",
    }
    a4 = table["a4"]
    assert a4["code"] == 0x04
    assert a4["width"] == 4800
    assert a4["length"] == 6372
    assert a4["left_margin"] == 80
    assert a4["top_margin"] == 284

    postcard = table["postcard"]
    assert postcard["top_margin"] == 71


def test_resolve_paper_table_md5000():
    profile = config.load_profile(str(PROFILES_DIR / "md-5000.yaml"))
    table = config.resolve_paper_table(profile, str(PAPERS_DIR))
    assert table["a4"]["width"] == 4800


def test_resolve_paper_table_missing_paper_table_field():
    with pytest.raises(config.ConfigError):
        config.resolve_paper_table({"model": "MD-9999"}, str(PAPERS_DIR))


def test_resolve_paper_table_file_not_found():
    with pytest.raises(config.ConfigError):
        config.resolve_paper_table(
            {"model": "MD-9999", "paper_table": "nonexistent-series"}, str(PAPERS_DIR)
        )


def _write_paper_table(tmp_path, papers_yaml: str) -> pathlib.Path:
    path = tmp_path / "papers.yaml"
    path.write_text(f"papers:\n{papers_yaml}", encoding="utf-8")
    return path


def test_load_paper_table_missing_required_field(tmp_path):
    papers_yaml = """  - name: a4
    code: 4
    width: 4800
    length: 6372
    left_margin: 80
    # top_margin omitted
"""
    path = _write_paper_table(tmp_path, papers_yaml)
    with pytest.raises(config.ConfigError):
        config.load_paper_table(str(path))


def test_load_paper_table_negative_width(tmp_path):
    papers_yaml = """  - name: a4
    code: 4
    width: -1
    length: 6372
    left_margin: 80
    top_margin: 284
"""
    path = _write_paper_table(tmp_path, papers_yaml)
    with pytest.raises(config.ConfigError):
        config.load_paper_table(str(path))


def test_load_paper_table_code_out_of_range(tmp_path):
    papers_yaml = """  - name: a4
    code: 256
    width: 4800
    length: 6372
    left_margin: 80
    top_margin: 284
"""
    path = _write_paper_table(tmp_path, papers_yaml)
    with pytest.raises(config.ConfigError):
        config.load_paper_table(str(path))


def test_load_paper_table_duplicate_name(tmp_path):
    papers_yaml = """  - name: a4
    code: 4
    width: 4800
    length: 6372
    left_margin: 80
    top_margin: 284
  - name: a4
    code: 5
    width: 100
    length: 100
    left_margin: 0
    top_margin: 0
"""
    path = _write_paper_table(tmp_path, papers_yaml)
    with pytest.raises(config.ConfigError):
        config.load_paper_table(str(path))


def test_load_paper_table_empty_list(tmp_path):
    path = _write_paper_table(tmp_path, "")
    with pytest.raises(config.ConfigError):
        config.load_paper_table(str(path))


# ---------------------------------------------------------------------------
# load_palette: default.yaml regression (DOMAIN §4.3 / §4.9)
# ---------------------------------------------------------------------------


def test_load_palette_default_orders_metallics_by_file_order():
    """The four metallic inks in palette/default.yaml all share order=50.
    DOMAIN §4.3 requires the tie-break to be file order (gold, silver,
    magenta, cyan as written), and §4.9 requires a stable sort to make
    that deterministic. This is the regression test for that guarantee."""
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    names = [ink["name"] for ink in inks]

    # mf_ink (order 5) first, glossy_finish (order 95) last -- the two
    # coverage inks added by D-048 bracket the colour inks, whose own
    # ends are white (order 10) and black (order 90).
    assert names[0] == "mf_ink"
    assert names[-1] == "glossy_finish"
    assert names[1] == "white"
    assert names[-2] == "black"

    metallic_names = [n for n in names if n.startswith("metallic_")]
    assert metallic_names == [
        "metallic_gold",
        "metallic_silver",
        "metallic_magenta",
        "metallic_cyan",
    ]


def test_load_palette_default_field_values():
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    by_name = {ink["name"]: ink for ink in inks}

    white = by_name["white"]
    assert white["magic_rgb"] == [230, 230, 230]
    assert white["printer_code"] == 0x0B
    assert white["auto_undercoat"] is True
    # D-038: 既定を 2 から 1 に下げた(薄いほうから試せる。ジョブごとに上書き可能)
    assert white["passes"] == 1

    gold = by_name["metallic_gold"]
    assert gold["magic_rgb"] == [225, 160, 0]
    assert gold["printer_code"] == 0x04
    # not specified in the file -> defaults apply
    assert gold["auto_undercoat"] is False
    assert gold["passes"] == 1


# ---------------------------------------------------------------------------
# load_palette: stable sort with an all-tied synthetic file
# ---------------------------------------------------------------------------


def _write_palette(tmp_path, inks_yaml: str) -> pathlib.Path:
    path = tmp_path / "palette.yaml"
    path.write_text(f"inks:\n{inks_yaml}", encoding="utf-8")
    return path


def test_load_palette_all_same_order_preserves_file_order(tmp_path):
    names = [f"ink_{chr(ord('a') + i)}" for i in range(6)]
    inks_yaml = "".join(
        f"""  - name: {name}
    label: "Ink {name}"
    magic_rgb: [{i}, {i}, {i}]
    printer_code: {i}
    tolerance: 8
    order: 50
"""
        for i, name in enumerate(names)
    )
    path = _write_palette(tmp_path, inks_yaml)
    inks = config.load_palette(str(path))
    assert [ink["name"] for ink in inks] == names


# ---------------------------------------------------------------------------
# load_palette: validation errors
# ---------------------------------------------------------------------------


def test_load_palette_missing_required_field(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    # order omitted
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_bad_magic_rgb_length(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230]
    printer_code: 11
    tolerance: 8
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_bad_magic_rgb_range(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 300]
    printer_code: 11
    tolerance: 8
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_missing_tolerance_is_rejected(tmp_path):
    """tolerance is required (DOMAIN §6.2). Without this check a palette
    missing it loads fine and then raises KeyError inside magic-colour
    matching, far from the actual cause."""
    inks_yaml = """  - name: black
    label: Black
    magic_rgb: [0, 0, 0]
    printer_code: 0
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_tolerance_out_of_range(tmp_path):
    inks_yaml = """  - name: black
    label: Black
    magic_rgb: [0, 0, 0]
    printer_code: 0
    tolerance: 256
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_quoted_order_is_rejected(tmp_path):
    """A quoted number in hand-written YAML is a realistic mistake. It must
    fail as a ConfigError at load time, not as a TypeError from sorted()."""
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    order: "10"
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_printer_code_out_of_range(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 256
    tolerance: 8
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_zero_passes_is_rejected(tmp_path):
    """passes is a print-repeat count; 0 would silently drop the pass."""
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    order: 10
    passes: 0
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_bad_name_uppercase(tmp_path):
    inks_yaml = """  - name: White
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_bad_name_non_ascii(tmp_path):
    inks_yaml = """  - name: "しろ"
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


# ---------------------------------------------------------------------------
# load_palette: process inks (channel field, D-019)
# ---------------------------------------------------------------------------


def test_load_palette_process_ink_without_magic_rgb(tmp_path):
    inks_yaml = """  - name: cyan
    label: Cyan
    printer_code: 1
    channel: C
    order: 60
"""
    path = _write_palette(tmp_path, inks_yaml)
    inks = config.load_palette(str(path))
    assert len(inks) == 1
    cyan = inks[0]
    assert cyan["channel"] == "C"
    assert cyan["magic_rgb"] is None
    assert "tolerance" not in cyan


def test_load_palette_duplicate_channel_is_rejected(tmp_path):
    inks_yaml = """  - name: cyan
    label: Cyan
    printer_code: 1
    channel: C
    order: 60
  - name: cyan2
    label: Cyan 2
    printer_code: 2
    channel: C
    order: 61
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_ink_without_magic_rgb_or_channel_is_rejected(tmp_path):
    inks_yaml = """  - name: mystery
    label: Mystery
    printer_code: 1
    order: 60
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_magic_rgb_without_tolerance_is_rejected(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_ink_with_both_magic_rgb_and_channel(tmp_path):
    """The black ink is allowed to be both a spot ink and a process ink
    (D-019)."""
    inks_yaml = """  - name: black
    label: Black
    magic_rgb: [0, 0, 0]
    tolerance: 8
    printer_code: 0
    channel: K
    order: 90
"""
    path = _write_palette(tmp_path, inks_yaml)
    inks = config.load_palette(str(path))
    black = inks[0]
    assert black["magic_rgb"] == [0, 0, 0]
    assert black["channel"] == "K"


def test_load_palette_invalid_channel_value_is_rejected(tmp_path):
    inks_yaml = """  - name: mystery
    label: Mystery
    printer_code: 1
    channel: X
    order: 60
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_default_process_inks():
    """palette/default.yaml has cyan/magenta/yellow process inks alongside
    the existing spot inks (D-019)."""
    inks = config.load_palette(str(PALETTE_DIR / "default.yaml"))
    by_name = {ink["name"]: ink for ink in inks}

    cyan = by_name["cyan"]
    assert cyan["channel"] == "C"
    assert cyan["printer_code"] == 0x01
    assert cyan["magic_rgb"] is None

    magenta = by_name["magenta"]
    assert magenta["channel"] == "M"
    assert magenta["printer_code"] == 0x02

    yellow = by_name["yellow"]
    assert yellow["channel"] == "Y"
    assert yellow["printer_code"] == 0x03

    black = by_name["black"]
    assert black["channel"] == "K"
    assert black["magic_rgb"] == [0, 0, 0]


def test_load_palette_duplicate_name(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    tolerance: 8
    order: 10
  - name: white
    label: White Again
    magic_rgb: [231, 231, 231]
    printer_code: 12
    tolerance: 8
    order: 20
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))
