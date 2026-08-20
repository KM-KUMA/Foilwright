"""下と右の余白がどこまで削れるかを実機で測る(DOMAIN §5.5)。

2026-08-20 の実測で、**プリンタが自分で余白を取っている**ことが分かった —
emitter は位置ずらしの指令を送っていないのに、刷り始めが紙の上端から
12.03mm・左端から 3.39mm の所に来る(用紙表の top_margin / left_margin と一致)。

つまり **上と左は動かせない**。一方 **下と右は「宣言した寸法ぶん刷って止まる」**
だけなので、長く・広く宣言すれば伸びる可能性がある。それを測る。

やり方: 端に向かって階段状に印を並べ、**どこまで出るか**を見る。

安全のための設計(重要):

  **刷り始めが紙の端ではないため、「紙のサイズをそのまま宣言する」と
  はみ出してプラテンにインクが乗る。** このスクリプトは印の位置を
  **紙の端からの絶対位置**で決め、いちばん端の印でも **紙の内側に
  SAFETY_MM を残す**。宣言する寸法も同じ考えで決める。

使い方:
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
    ap.add_argument("--out", required=True)
    ap.add_argument("--emit-rgl", default=None)
    args = ap.parse_args()

    sheet_w, sheet_h = SHEETS[args.sheet]

    # 宣言する寸法: 刷り始めの位置を引き、安全のぶんを残す
    decl_w = dots(sheet_w - ORIGIN_LEFT_MM - SAFETY_MM)
    decl_h = dots(sheet_h - ORIGIN_TOP_MM - SAFETY_MM)

    buf = bytearray(b"\xff" * (decl_w * decl_h * 3))

    def box(x0: int, y0: int, x1: int, y1: int) -> None:
        assert 0 <= x0 < x1 <= decl_w and 0 <= y0 < y1 <= decl_h, (x0, y0, x1, y1)
        for y in range(y0, y1):
            base = y * decl_w * 3
            for x in range(x0, x1):
                i = base + x * 3
                buf[i : i + 3] = b"\x00\x00\x00"

    # --- 下方向の階段。紙の下端からの距離で置く ---
    print(f"紙 {args.sheet}: {sheet_w} x {sheet_h} mm")
    print(f"刷り始め: 上 {ORIGIN_TOP_MM:.2f}mm / 左 {ORIGIN_LEFT_MM:.2f}mm(実測)")
    print(
        f"宣言する寸法: {decl_w} x {decl_h} ドット "
        f"({decl_w / DPI * 25.4:.1f} x {decl_h / DPI * 25.4:.1f} mm)"
    )
    print()
    print("下方向の階段(紙の下端からの距離 / 印の下辺):")
    bottom_steps = [25.0, 20.0, 15.0, 12.0, 9.0, 6.0, SAFETY_MM]
    for i, gap in enumerate(bottom_steps):
        y_abs = sheet_h - gap  # 紙の上端からの絶対位置
        y_ras = y_abs - ORIGIN_TOP_MM  # ラスタ座標
        y1 = dots(y_ras)
        y0 = y1 - 40
        # 段ごとに幅を変えて、どれが出たか見分けられるようにする
        x0 = 200
        x1 = x0 + 200 + i * 120
        box(x0, y0, x1, y1)
        print(f"  下端から {gap:>5.1f}mm  幅 {(x1 - x0) / DPI * 25.4:>5.1f}mm")

    # --- 右方向の階段。紙の右端からの距離で置く ---
    print("\n右方向の階段(紙の右端からの距離 / 印の右辺):")
    right_steps = [12.0, 9.0, 6.0, 4.5, SAFETY_MM]
    for i, gap in enumerate(right_steps):
        x_abs = sheet_w - gap
        x_ras = x_abs - ORIGIN_LEFT_MM
        x1 = dots(x_ras)
        x0 = x1 - 40
        y0 = 400 + i * 300
        y1 = y0 + 200 + i * 120
        box(x0, y0, x1, y1)
        print(f"  右端から {gap:>5.1f}mm  高さ {(y1 - y0) / DPI * 25.4:>5.1f}mm")

    out = pathlib.Path(args.out)
    out.write_bytes(f"P6\n{decl_w} {decl_h}\n255\n".encode("ascii") + bytes(buf))
    black = sum(1 for i in range(0, len(buf), 3) if buf[i] == 0)
    print(f"\n黒画素: {black:,}")
    print(f"出力  : {out}")

    if args.emit_rgl:
        emit(decl_w, decl_h, out, pathlib.Path(args.emit_rgl))


def emit(
    decl_w: int, decl_h: int, ppm_path: pathlib.Path, out_path: pathlib.Path
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
            "code": 0x00,  # custom(2026-08-20 に実機で通ることを確認)
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
