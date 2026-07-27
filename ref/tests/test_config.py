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

    # white (order 10) first, black (order 90) last.
    assert names[0] == "white"
    assert names[-1] == "black"

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
    assert white["passes"] == 2

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
    order: 10
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))


def test_load_palette_duplicate_name(tmp_path):
    inks_yaml = """  - name: white
    label: White
    magic_rgb: [230, 230, 230]
    printer_code: 11
    order: 10
  - name: white
    label: White Again
    magic_rgb: [231, 231, 231]
    printer_code: 12
    order: 20
"""
    path = _write_palette(tmp_path, inks_yaml)
    with pytest.raises(config.ConfigError):
        config.load_palette(str(path))
