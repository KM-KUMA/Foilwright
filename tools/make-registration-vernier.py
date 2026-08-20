"""版ずれ(色ごとのパスの位置ずれ)を定量する検査図を作る(DOMAIN §15.9)。

既存の `tools/make-registration-test.py` は「白の縁が四方均等か」を目視で見る図で、
**ずれていないことは分かるが、何 mm ずれているかは測れない**(分解能は縁の幅 1.02mm)。

この図は**わざとずらした階段**を刷る。段ごとに -12 〜 +12 ドットずらした組を並べ、
**実際のずれと打ち消し合う段が、段差なく揃って見える**。その段の番号がずれの量になる。

  分解能 1 ドット = 42µm / 測れる範囲 ±12 ドット = ±0.51mm

**長さを測るのではなく「直線の食い違いを見る」方式**なので、定規より 10 倍以上精度が出る
(人間は直線の段差を 20µm 程度から検出できる。印刷業界の見当合わせと同じ原理)。

なぜこの精度が要るか: 実物のアイデカールは 1 個 6.2x4.1mm で、**いちばん細い部品は 0.72mm**
(まつ毛の線)。白版がずれるとその分だけ色の裏に白が無くなり、クリアフィルムでは透ける。
**細い部品の 10%(0.07mm ≒ 1.7 ドット)以下に収めたい**ため、1 ドット刻みで測る。

置き方: **上・中・下の 3 箇所**に同じ階段を置く。バックフィードの誤差は送り量に比例して
効くとみられるため、位置による差を同時に見る。

インク: 黒(0,0,0)とマゼンタ(255,0,255)。**版ずれは紙送りの機構が作るものでインクには
依らないはず**なので、普通紙で見える 2 色で代用する【推測】。白のカセット特有の癖が
疑われる場合は、フィルムに白と黒で刷り直す。

使い方:
    .venv/Scripts/python.exe tools/make-registration-vernier.py --out v.ppm --emit-rgl v.bin
"""

from __future__ import annotations

import argparse
import pathlib
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config

DPI = 600
BLACK = (0, 0, 0)
MAGENTA = (255, 0, 255)

STEPS = list(range(-12, 13))  # 1 ドット刻み
PITCH = 72  # 段の間隔(3.05mm)
BAR_H = 30  # 帯の高さ(1.27mm)
GAP = 4  # 上下の帯のすきま
Y_BAR_W = 300  # 縦ずれ用の帯の幅(12.7mm)
X_BAR_W = 150  # 横ずれ用の帯の幅
TICK_W = 60  # 段の目印


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--paper", default="a4")
    ap.add_argument("--out", required=True)
    ap.add_argument("--emit-rgl", default=None)
    args = ap.parse_args()

    profile = config.load_profile(str(REPO / "profiles" / "md-5000.yaml"))
    table = config.resolve_paper_table(profile, str(REPO / "papers"))
    paper = table[args.paper]
    W, H = paper["width"], paper["length"]
    buf = bytearray(b"\xff" * (W * H * 3))

    def box(x0, y0, x1, y1, rgb):
        assert 0 <= x0 < x1 <= W and 0 <= y0 < y1 <= H, (x0, y0, x1, y1)
        for y in range(y0, y1):
            base = y * W * 3
            for x in range(x0, x1):
                i = base + x * 3
                buf[i : i + 3] = bytes(rgb)

    ladder_h = len(STEPS) * PITCH
    group_ys = [
        H // 6 - ladder_h // 2,
        H // 2 - ladder_h // 2,
        H * 5 // 6 - ladder_h // 2,
    ]

    for gi, gy in enumerate(group_ys):
        for si, d in enumerate(STEPS):
            y = gy + si * PITCH

            # 段の目印: d=0 は長く、4 段ごとに中くらい
            tick = TICK_W if d == 0 else (TICK_W // 2 if d % 4 == 0 else TICK_W // 4)
            box(200 - tick, y, 200, y + BAR_H, BLACK)

            # --- 縦ずれ(送り方向)。左右に並べ、マゼンタを d だけ縦にずらす ---
            x0 = 260
            box(x0, y, x0 + Y_BAR_W, y + BAR_H, BLACK)
            box(
                x0 + Y_BAR_W + GAP,
                y + d,
                x0 + 2 * Y_BAR_W + GAP,
                y + d + BAR_H,
                MAGENTA,
            )

            # --- 横ずれ。上下に積み、マゼンタを d だけ横にずらす ---
            x1 = x0 + 2 * Y_BAR_W + GAP + 200
            box(x1, y, x1 + X_BAR_W, y + BAR_H, BLACK)
            box(x1 + d, y + BAR_H + GAP, x1 + d + X_BAR_W, y + 2 * BAR_H + GAP, MAGENTA)

        print(
            f"段組 {gi + 1}: y = {gy} .. {gy + ladder_h} ドット "
            f"({gy / DPI * 25.4:.0f} .. {(gy + ladder_h) / DPI * 25.4:.0f} mm)"
        )

    out = pathlib.Path(args.out)
    out.write_bytes(f"P6\n{W} {H}\n255\n".encode("ascii") + bytes(buf))
    nb = sum(1 for i in range(0, len(buf), 3) if buf[i] == 0 and buf[i + 1] == 0)
    nm = sum(1 for i in range(0, len(buf), 3) if buf[i] == 255 and buf[i + 1] == 0)
    print(f"\n段数 {len(STEPS)}(-12 〜 +12 ドット、1 ドット刻み)")
    print(f"黒 {nb:,} 画素 / マゼンタ {nm:,} 画素")
    print(f"出力: {out}")

    if args.emit_rgl:
        emit(paper, out, pathlib.Path(args.emit_rgl))


def emit(paper, ppm_path: pathlib.Path, out_path: pathlib.Path) -> None:
    from foilwright_ref import emitter, job, raster

    image = raster.read_ppm(str(ppm_path))
    palette = config.load_palette(str(REPO / "palette" / "default.yaml"))
    inks, planes = job.build_job_planes(
        image,
        palette,
        "auto",
        halftone="none",
        white_mode="none",
        colour_correction="plain",
    )
    media = config.load_media_table(str(REPO / "media.yaml"))["plain_paper"]
    j = {
        "resolution": 600,
        "paper": paper,
        "media": media,
        "inks": [
            {"name": i["name"], "printer_code": i["printer_code"], "passes": 1}
            for i in inks
        ],
        "width": image[0],
        "height": image[1],
    }
    out_path.write_bytes(emitter.emit_job(planes, j))
    print(f"\nインク: {[i['name'] for i in inks]}")
    for i in inks:
        p = planes[i["name"]]
        print(f"  {i['name']:>16}: {sum(b.bit_count() for b in p):>10,} ドット")
    print(f"RGL: {out_path} ({out_path.stat().st_size:,} バイト)")


if __name__ == "__main__":
    main()
