"""media.yaml: all 24 ppmtomd media_table[] entries load correctly.

Values come from mddata.c media_byte1[] / media_byte2[] (DOMAIN §5.5.2).
This test only checks that config.load_media_table can read every entry
and that the byte pairs match the documented table; it does not touch
golden bytes or emitter logic.
"""

from __future__ import annotations

import pathlib
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "ref"))

from foilwright_ref import config

MEDIA_YAML = REPO_ROOT / "media.yaml"

# name -> (byte1, byte2), per the mddata.c media_table[] order.
EXPECTED = {
    "plain_paper": (0x00, 0x00),
    "fine_plain_paper": (0x00, 0x02),
    "fine_special_ohp": (0x01, 0x00),
    "special_ohp": (0x01, 0x01),
    "special_iron": (0x02, 0x01),
    "iron_sheet": (0x02, 0x02),
    "labeca_sheet": (0x03, 0x00),
    "thermal_paper": (0x04, 0x00),
    "cd_master": (0x04, 0x01),
    "cardboard": (0x05, 0x00),
    "post_card": (0x06, 0x00),
    "laser_paper": (0x07, 0x00),
    "fine_ohp": (0x08, 0x00),
    "ohp": (0x08, 0x01),
    "back_print": (0x09, 0x00),
    "fine_back_print": (0x09, 0x02),
    "dye_sub_paper": (0x0A, 0x00),
    "reserved_type_b": (0x0B, 0x00),
    "dye_sub_label": (0x0C, 0x00),
    "reserved_type_d": (0x0D, 0x00),
    "glossy_label": (0x0E, 0x00),
    "glossy_paper": (0x0F, 0x00),
    "vphoto_film": (0x10, 0x00),
    "vphoto_card": (0x11, 0x00),
}


def test_media_table_has_all_24_entries():
    table = config.load_media_table(str(MEDIA_YAML))
    assert len(table) == 24
    assert set(table.keys()) == set(EXPECTED.keys())


def test_media_table_byte_pairs_match_mddata_c():
    table = config.load_media_table(str(MEDIA_YAML))
    for name, (byte1, byte2) in EXPECTED.items():
        entry = table[name]
        assert entry["byte1"] == byte1, f"{name}: byte1 mismatch"
        assert entry["byte2"] == byte2, f"{name}: byte2 mismatch"


def test_media_table_labels_are_non_empty():
    table = config.load_media_table(str(MEDIA_YAML))
    for name, entry in table.items():
        assert entry["label"].strip(), f"{name}: label must not be blank"
