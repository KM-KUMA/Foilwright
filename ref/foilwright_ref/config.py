"""Config loading: machine profiles (profiles/*.yaml), ink palettes
(palette/*.yaml), and paper tables (papers/*.yaml).

Schemas are defined in docs/DOMAIN.md §5.1 (machine profile), §5.5 (paper
table), and §6.1/§6.2 (palette). This module knows only the *shape* of
those schemas; it never hardcodes which models, inks, or papers exist
(DOMAIN.md §4.4 / §4.5) -- that information always comes from the YAML
files passed in by the caller.

Pass ordering (DOMAIN.md §4.3 / §4.9): `load_palette` returns inks sorted
by ascending `order`, using a stable sort so that inks sharing the same
`order` keep the order they were written in the palette file. `name` is
never used as a tie-break (explicitly forbidden by DOMAIN.md §4.3).
"""

from __future__ import annotations

import os
import re

import yaml

_NAME_RE = re.compile(r"^[a-z_]+$")

_PALETTE_REQUIRED_FIELDS = ("name", "label", "magic_rgb", "printer_code", "order")

_PAPER_REQUIRED_FIELDS = (
    "name",
    "code",
    "width",
    "length",
    "left_margin",
    "top_margin",
)


class ConfigError(ValueError):
    """Raised when a profile or palette file fails validation, or when a
    caller requires a value that is present but held as null (unmeasured;
    DOMAIN.md §5.2)."""


def _load_yaml(path: str) -> object:
    with open(path, "rb") as f:
        return yaml.safe_load(f)


def load_profile(path: str) -> dict:
    """Load a machine profile (DOMAIN.md §5.1).

    Returns the parsed mapping unchanged, except that this is where any
    structural validation of the profile file happens. `lf_correction`
    and `max_width_dots` are preserved as `None` when null in the YAML
    (DOMAIN.md §5.2): they are never filled with guessed values here.

    Required fields: `model`, `resolutions` (a non-empty list of
    `{dpi_x, dpi_y, ...}` entries), and `paper_table` (the name resolved
    against a `papers/` directory by `resolve_paper_table`).
    """
    data = _load_yaml(path)
    if not isinstance(data, dict):
        raise ConfigError(f"{path}: profile must be a YAML mapping")
    if not data.get("model"):
        raise ConfigError(f"{path}: profile is missing required field 'model'")

    resolutions = data.get("resolutions")
    if not isinstance(resolutions, list) or not resolutions:
        raise ConfigError(f"{path}: 'resolutions' must be a non-empty list")
    for index, entry in enumerate(resolutions):
        if not isinstance(entry, dict) or "dpi_x" not in entry or "dpi_y" not in entry:
            raise ConfigError(
                f"{path}: resolutions[{index}] must be a mapping with "
                "'dpi_x' and 'dpi_y'"
            )

    if not data.get("paper_table"):
        raise ConfigError(f"{path}: profile is missing required field 'paper_table'")

    return data


def require_value(profile: dict, key: str):
    """Return `profile[key]`, raising ConfigError if it is absent or null.

    Use this for fields such as `lf_correction` / `max_width_dots` that are
    allowed to be `None` in a freshly-loaded profile (DOMAIN.md §5.2) but
    are required by some particular caller.
    """
    value = profile.get(key)
    if value is None:
        raise ConfigError(
            f"profile field '{key}' is required here but is unset (null); "
            "it must be measured on real hardware before this operation "
            "can proceed (see DOMAIN.md §5.2)"
        )
    return value


def _validate_ink(raw: dict, index: int) -> dict:
    if not isinstance(raw, dict):
        raise ConfigError(f"palette ink #{index}: entry must be a mapping")

    missing = [field for field in _PALETTE_REQUIRED_FIELDS if field not in raw]
    if missing:
        raise ConfigError(f"palette ink #{index}: missing required field(s) {missing}")

    name = raw["name"]
    if not isinstance(name, str) or not _NAME_RE.match(name):
        raise ConfigError(
            f"palette ink #{index} ({name!r}): 'name' must contain only "
            "ASCII lowercase letters and underscores"
        )

    magic_rgb = raw["magic_rgb"]
    if (
        not isinstance(magic_rgb, (list, tuple))
        or len(magic_rgb) != 3
        or not all(isinstance(v, int) and 0 <= v <= 255 for v in magic_rgb)
    ):
        raise ConfigError(
            f"palette ink '{name}': 'magic_rgb' must be 3 integers in 0..255"
        )

    # These are hand-written YAML files, so a quoted number (order: "50")
    # is a realistic mistake. Without this check it would surface much
    # later as a TypeError from sorted(), or as a wrong byte on the wire.
    # bool is a subclass of int, hence the explicit exclusion.
    def _require_int(field: str, value, low: int, high: int | None = None) -> None:
        if isinstance(value, bool) or not isinstance(value, int) or value < low:
            raise ConfigError(
                f"palette ink '{name}': '{field}' must be an integer "
                f">= {low}, got {value!r}"
            )
        if high is not None and value > high:
            raise ConfigError(
                f"palette ink '{name}': '{field}' must be an integer in "
                f"{low}..{high}, got {value!r}"
            )

    _require_int("order", raw["order"], 0)
    _require_int("printer_code", raw["printer_code"], 0, 255)

    ink = dict(raw)
    ink["magic_rgb"] = list(magic_rgb)
    ink.setdefault("passes", 1)
    ink.setdefault("auto_undercoat", False)
    _require_int("passes", ink["passes"], 1)

    if not isinstance(ink["auto_undercoat"], bool):
        raise ConfigError(
            f"palette ink '{name}': 'auto_undercoat' must be true or false, "
            f"got {ink['auto_undercoat']!r}"
        )
    return ink


def load_palette(path: str) -> list[dict]:
    """Load a palette (DOMAIN.md §6.1) and return its inks sorted into
    pass execution order.

    Sort key is `order` (ascending). Ties are broken by preserving the
    order the inks were written in the file (DOMAIN.md §4.3), which
    requires a stable sort (DOMAIN.md §4.9) -- Python's `sorted()`
    satisfies this by spec.
    """
    data = _load_yaml(path)
    if not isinstance(data, dict) or "inks" not in data:
        raise ConfigError(f"{path}: palette must be a YAML mapping with an 'inks' list")

    raw_inks = data["inks"]
    if not isinstance(raw_inks, list) or not raw_inks:
        raise ConfigError(f"{path}: 'inks' must be a non-empty list")

    inks = [_validate_ink(raw, index) for index, raw in enumerate(raw_inks)]

    seen_names: dict[str, int] = {}
    for index, ink in enumerate(inks):
        name = ink["name"]
        if name in seen_names:
            raise ConfigError(
                f"palette has duplicate ink name '{name}' "
                f"(entries #{seen_names[name]} and #{index})"
            )
        seen_names[name] = index

    return sorted(inks, key=lambda ink: ink["order"])


def _validate_paper(raw: dict, index: int) -> dict:
    if not isinstance(raw, dict):
        raise ConfigError(f"paper table entry #{index}: entry must be a mapping")

    missing = [field for field in _PAPER_REQUIRED_FIELDS if field not in raw]
    if missing:
        raise ConfigError(
            f"paper table entry #{index}: missing required field(s) {missing}"
        )

    name = raw["name"]
    if not isinstance(name, str) or not name:
        raise ConfigError(
            f"paper table entry #{index}: 'name' must be a non-empty string"
        )

    def _require_int(field: str, value, low: int, high: int | None = None) -> None:
        if isinstance(value, bool) or not isinstance(value, int) or value < low:
            raise ConfigError(
                f"paper '{name}': '{field}' must be an integer >= {low}, got {value!r}"
            )
        if high is not None and value > high:
            raise ConfigError(
                f"paper '{name}': '{field}' must be an integer in "
                f"{low}..{high}, got {value!r}"
            )

    _require_int("code", raw["code"], 0, 255)
    _require_int("width", raw["width"], 0)
    _require_int("length", raw["length"], 0)
    _require_int("left_margin", raw["left_margin"], 0)
    _require_int("top_margin", raw["top_margin"], 0)

    return {
        "code": raw["code"],
        "width": raw["width"],
        "length": raw["length"],
        "left_margin": raw["left_margin"],
        "top_margin": raw["top_margin"],
    }


def load_paper_table(path: str) -> dict:
    """Load a paper table (DOMAIN.md §5.5) and return it as a mapping from
    paper name to its geometry: `{name: {code, width, length, left_margin,
    top_margin}}`.
    """
    data = _load_yaml(path)
    if not isinstance(data, dict) or "papers" not in data:
        raise ConfigError(
            f"{path}: paper table must be a YAML mapping with a 'papers' list"
        )

    raw_papers = data["papers"]
    if not isinstance(raw_papers, list) or not raw_papers:
        raise ConfigError(f"{path}: 'papers' must be a non-empty list")

    table: dict[str, dict] = {}
    for index, raw in enumerate(raw_papers):
        paper = _validate_paper(raw, index)
        name = raw["name"]
        if name in table:
            raise ConfigError(f"{path}: duplicate paper name '{name}'")
        table[name] = paper

    return table


def resolve_paper_table(profile: dict, papers_dir: str) -> dict:
    """Resolve a machine profile's `paper_table` reference (DOMAIN.md §5.1
    / §5.5) against `papers_dir` and return the loaded table.

    Raises ConfigError if the profile has no `paper_table` value, or if
    `{papers_dir}/{value}.yaml` does not exist.
    """
    name = profile.get("paper_table")
    if not name:
        raise ConfigError("profile is missing required field 'paper_table'")

    path = os.path.join(papers_dir, f"{name}.yaml")
    if not os.path.isfile(path):
        raise ConfigError(f"paper table '{name}' not found: {path} does not exist")

    return load_paper_table(path)
