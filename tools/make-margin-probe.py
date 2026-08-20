"""下と右の余白がどこまで刷れるかを実機で測る(DOMAIN §5.5.0.1)。

2026-08-20 の実測で、**プリンタが自分で余白を取っている**ことが分かった —
emitter は位置ずらしの指令を送っていないのに、刷り始めが紙の上端から
12.03mm・左端から 3.39mm の所に来る(用紙表の値と一致)。上と左は動かせない。

下と右は「宣言した寸法ぶん刷って止まる」だけなので、伸ばせる可能性がある。
端に向かって階段状に印を並べ、**どこまで出るか**を見る。段ごとに幅(高さ)を
変えてあるので、**いちばん長いものが出た所が限界**になる。

2 つのモードがある:

  既定(--declare 省略)
      `custom`(0x00)で紙の端ぎりぎりまで宣言し、**表の値より先を攻める**。
  --declare <用紙名>
      用紙表のその用紙のコードと寸法を**そのまま宣言**し、階段をラスタの
      下端・右端から数える。**表に書いてある値が実機で本当に出るか**を確かめる。

安全のための設計(重要):

  **刷り始めが紙の端ではないため、「紙のサイズをそのまま宣言する」と
  はみ出してプラテンにインクが乗る。** 印の位置は**紙の端からの絶対位置**で
  決め、いちばん端の印でも **紙の内側に SAFETY_MM を残す**。

使い方:
    # 表の値が出るか
    .venv/Scripts/python.exe tools/make-margin-probe.py --sheet a4 --declare a4 \
        --out probe.ppm --emit-rgl probe.bin
    # 表より先を攻める
    .venv/Scripts/python.exe tools/make-margin-probe.py --sheet a4 \
        --out probe.ppm --emit-rgl probe.bin
"""

from __future__ import annotations

import argparse
import pathlib
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config

DPI = 600
SAFETY_MM = 3.0  # いちばん端の印でも、紙の内側にこれだけ残す

# 実測で確かめたプリンタ側の固定余白(2026-08-20)
ORIGIN_TOP_MM = 284 / DPI * 25.4
ORIGIN_LEFT_MM = 80 / DPI * 25.4

SHEETS = {
    "a4": (210.0, 297.0),
    "b5": (182.0, 257.0),
    "postcard": (100.0, 148.0),
}


def dots(mm: float) -> int:
    return round(mm / 25.4 * DPI)


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sheet", default="a4", choices=sorted(SHEETS))
    ap.add_argument("--declare", default=None)
    ap.add_argument("--out", required=True)
    ap.add_argument("--emit-rgl", default=None)
    args = ap.parse_args()

    sheet_w, sheet_h = SHEETS[args.sheet]

    if args.declare:
        profile = config.load_profile(str(REPO / "profiles" / "md-5000.yaml"))
        table = config.resolve_paper_table(profile, str(REPO / "papers"))
        entry = table[args.declare]
        paper_code = entry["code"]
        decl_w, decl_h = entry["width"], entry["length"]
        # ラスタの端から 0 / 1.7 / 3.4 ... mm 内側に置く
        bottom_gaps = [
            sheet_h - (ORIGIN_TOP_MM + (decl_h - k) / DPI * 25.4)
            for k in (0, 40, 80, 120, 160, 200, 240)
        ]
        right_gaps = [
            sheet_w - (ORIGIN_LEFT_MM + (decl_w - k) / DPI * 25.4)
            for k in (0, 40, 80, 120, 160)
        ]
    else:
        paper_code = 0x00
        decl_w = dots(sheet_w - ORIGIN_LEFT_MM - SAFETY_MM)
        decl_h = dots(sheet_h - ORIGIN_TOP_MM - SAFETY_MM)
        bottom_gaps = [25.0, 20.0, 15.0, 12.0, 9.0, 6.0, SAFETY_MM]
        right_gaps = [12.0, 9.0, 6.0, 4.5, SAFETY_MM]

    bottom_gaps = sorted(bottom_gaps, reverse=True)
    right_gaps = sorted(right_gaps, reverse=True)

    buf = bytearray(b"\xff" * (decl_w * decl_h * 3))

    def box(x0: int, y0: int, x1: int, y1: int) -> None:
        assert 0 <= x0 < x1 <= decl_w and 0 <= y0 < y1 <= decl_h, (x0, y0, x1, y1)
        for y in range(y0, y1):
            base = y * decl_w * 3
            for x in range(x0, x1):
                i = base + x * 3
                buf[i : i + 3] = b"\x00\x00\x00"

    reach_b = ORIGIN_TOP_MM + decl_h / DPI * 25.4
    reach_r = ORIGIN_LEFT_MM + decl_w / DPI * 25.4
    print(f"紙 {args.sheet}: {sheet_w} x {sheet_h} mm")
    print(f"刷り始め: 上 {ORIGIN_TOP_MM:.2f}mm / 左 {ORIGIN_LEFT_MM:.2f}mm(実測)")
    print(
        f"宣言: code=0x{paper_code:02x} {decl_w} x {decl_h} ドット "
        f"({decl_w / DPI * 25.4:.1f} x {decl_h / DPI * 25.4:.1f} mm)"
    )
    print(
        f"刷り終わり: 紙の下端から {sheet_h - reach_b:.2f}mm / "
        f"右端から {sheet_w - reach_r:.2f}mm"
    )

    print("\n下方向の階段(紙の下端からの距離 / 印の下辺):")
    for i, gap in enumerate(bottom_gaps):
        y1 = dots(sheet_h - gap - ORIGIN_TOP_MM)
        y0 = y1 - 40
        x0, x1 = 200, 200 + 200 + i * 120
        box(x0, y0, x1, y1)
        print(f"  下端から {gap:>5.2f}mm  幅 {(x1 - x0) / DPI * 25.4:>5.1f}mm")

    print("\n右方向の階段(紙の右端からの距離 / 印の右辺):")
    for i, gap in enumerate(right_gaps):
        x1 = dots(sheet_w - gap - ORIGIN_LEFT_MM)
        x0 = x1 - 40
        y0 = 400 + i * 300
        y1 = y0 + 200 + i * 120
        box(x0, y0, x1, y1)
        print(f"  右端から {gap:>5.2f}mm  高さ {(y1 - y0) / DPI * 25.4:>5.1f}mm")

    out = pathlib.Path(args.out)
    out.write_bytes(f"P6\n{decl_w} {decl_h}\n255\n".encode("ascii") + bytes(buf))
    black = sum(1 for i in range(0, len(buf), 3) if buf[i] == 0)
    print(f"\n黒画素: {black:,}")
    print(f"出力  : {out}")

    if args.emit_rgl:
        emit(decl_w, decl_h, paper_code, out, pathlib.Path(args.emit_rgl))


def emit(
    decl_w: int,
    decl_h: int,
    paper_code: int,
    ppm_path: pathlib.Path,
    out_path: pathlib.Path,
) -> None:
    from foilwright_ref import emitter, raster

    image = raster.read_ppm(str(ppm_path))
    palette = config.load_palette(str(REPO / "palette" / "default.yaml"))
    black = next(i for i in palette if i["name"] == "black")
    planes = raster.to_planes_magic(image, [black])
    media = config.load_media_table(str(REPO / "media.yaml"))["plain_paper"]
    job = {
        "resolution": 600,
        "paper": {
            "code": paper_code,
            "width": decl_w,
            "length": decl_h,
            "left_margin": 80,
            "top_margin": 284,
        },
        "media": media,
        "inks": [
            {"name": black["name"], "printer_code": black["printer_code"], "passes": 1}
        ],
        "width": image[0],
        "height": image[1],
    }
    out_path.write_bytes(emitter.emit_job(planes, job))
    print(f"RGL   : {out_path} ({out_path.stat().st_size:,} バイト)")


if __name__ == "__main__":
    main()
