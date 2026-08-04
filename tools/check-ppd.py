#!/usr/bin/env python3
"""Foilwright — PPD の最低限の健全性検査

Copyright (C) 2026 JunkQuality (github.com/KM-KUMA/Foilwright)
SPDX-License-Identifier: GPL-3.0-or-later

Windows の印刷スタックに読ませる前に、機械的に潰せる誤りを落とす。
PPD の完全な検証器ではない(それは Adobe の仕様書の領分)。

  python tools/check-ppd.py ppd/foilwright.ppd
"""

import re
import sys

# PPD 仕様が要求し、Windows の PostScript ドライバが実際に参照するもの。
REQUIRED = [
    "*PPD-Adobe",
    "*FormatVersion",
    "*FileVersion",
    "*LanguageEncoding",
    "*LanguageVersion",
    "*PCFileName",
    "*Product",
    "*PSVersion",
    "*ModelName",
    "*ShortNickName",
    "*NickName",
    "*DefaultPageSize",
    "*DefaultPageRegion",
    "*DefaultImageableArea",
    "*DefaultPaperDimension",
    "*DefaultResolution",
]

UI_RE = re.compile(r"^\*(OpenUI|CloseUI)\s*:?\s*\*?(\w+)")


def check(path):
    problems = []
    with open(path, "rb") as handle:
        raw = handle.read()

    if not raw.startswith(b'*PPD-Adobe: "4.'):
        problems.append("1 行目が *PPD-Adobe のバージョン宣言でない")

    for offset, byte in enumerate(raw):
        if byte > 0x7E or (byte < 0x20 and byte not in (0x09, 0x0A, 0x0D)):
            line = raw[:offset].count(b"\n") + 1
            problems.append(f"{line} 行目に非 ASCII バイト 0x{byte:02x}")
            break

    text = raw.decode("ascii", "replace")
    lines = text.split("\n")

    for keyword in REQUIRED:
        if not any(ln.startswith(keyword) for ln in lines):
            problems.append(f"必須キーワードがない: {keyword}")

    # *OpenUI と *CloseUI の対応。入れ子は許さない(PPD 4.3)。
    open_stack = []
    for number, ln in enumerate(lines, 1):
        m = UI_RE.match(ln)
        if not m:
            continue
        kind, name = m.groups()
        if kind == "OpenUI":
            if open_stack:
                problems.append(f"{number} 行目: *OpenUI が入れ子 ({name})")
            open_stack.append((name, number))
        else:
            if not open_stack:
                problems.append(f"{number} 行目: 対応しない *CloseUI ({name})")
            elif open_stack[-1][0] != name:
                problems.append(
                    f"{number} 行目: *CloseUI {name} が "
                    f"*OpenUI {open_stack[-1][0]} と対応しない"
                )
                open_stack.pop()
            else:
                open_stack.pop()
    for name, number in open_stack:
        problems.append(f"{number} 行目: *OpenUI {name} が閉じていない")

    # 各 UI 群の既定値が、実在する選択肢を指しているか。
    options = {}
    for ln in lines:
        m = re.match(r"^\*(\w+)\s+(\S+?)(/|:)", ln)
        if m and not ln.startswith("*Default"):
            options.setdefault(m.group(1), set()).add(m.group(2))
    for ln in lines:
        m = re.match(r"^\*Default(\w+)\s*:\s*(\S+)", ln)
        if not m:
            continue
        group, value = m.groups()
        if group in options and value not in options[group]:
            problems.append(
                f"*Default{group}: {value} に対応する選択肢がない "
                f"(候補: {sorted(options[group])})"
            )

    return problems


def main():
    if len(sys.argv) != 2:
        raise SystemExit("使い方: check-ppd.py <file.ppd>")
    path = sys.argv[1]
    problems = check(path)
    if problems:
        print(f"NG {path}")
        for p in problems:
            print(f"  - {p}")
        raise SystemExit(1)
    print(f"OK {path}")


if __name__ == "__main__":
    main()
