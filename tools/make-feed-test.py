"""用紙サイズコードを実機で確認するための最小ジョブを作る(DOMAIN §5.5)。

`papers/*.yaml` の用紙表は ppmtomd のソースから写した参考値であり、
実機で確かめられているのは一部だけである。このスクリプトは、残りを
**リボンをほとんど使わずに**潰していくためのもの。

作るジョブの性質:

  - 黒 1 色・1 パスのみ。バックフィード 0 回。排出 1 回。
  - 印字は小さな黒い印だけ(既定で 4 個、合計 3 万ドット程度)。
  - 紙の上・中央・下・右下に置くので、**どこまで送れたか**が目で分かる。

安全のための制約(重要):

  **宣言した用紙より実際に入れる紙が小さいと、印字がプラテンに直接乗って
  インクが付く。** それを避けるため `--loaded` で実際に入れる紙を指定させ、
  **両方に収まる位置にしか印を置かない**。収まらない印は置かずに、その旨を
  表示する(置けなかった印がある場合、「最後まで送れたか」は排出の有無で
  判断することになる)。

使い方:
    .venv/Scripts/python.exe tools/make-feed-test.py --paper b5 --loaded a4
    src/Foilwright.Cli/bin/Debug/net10.0/Foilwright.Cli.exe build-rgl \
        <出力.ppm> <出力.bin> --paper b5 --ink-mode spot_only \
        --halftone none --white-mode none --colour-correction none
"""

from __future__ import annotations

import argparse
import pathlib
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config

MARK_W, MARK_H = 200, 40


def build(paper_name, paper, loaded_name, loaded, out_path: pathlib.Path) -> None:
    w, h = paper["width"], paper["length"]
    lw, lh = loaded["width"], loaded["length"]
    buf = bytearray(b"\xff" * (w * h * 3))

    def box(x0: int, y0: int) -> bool:
        """印を 1 つ置く。実際の紙からはみ出す場合は置かずに False を返す。"""
        if x0 + MARK_W > min(w, lw) or y0 + MARK_H > min(h, lh):
            return False
        for y in range(y0, y0 + MARK_H):
            base = y * w * 3
            for x in range(x0, x0 + MARK_W):
                i = base + x * 3
                buf[i : i + 3] = b"\x00\x00\x00"
        return True

    marks = [
        ("上", 200, 100),
        ("中央", 200, h // 2),
        ("下", 200, h - 200),
        ("右下", w - 400, h - 200),
    ]
    placed, skipped = [], []
    for label, x, y in marks:
        (placed if box(x, y) else skipped).append(label)

    out_path.write_bytes(f"P6\n{w} {h}\n255\n".encode("ascii") + bytes(buf))
    dots = sum(1 for i in range(0, len(buf), 3) if buf[i] == 0)

    print(f"用紙   : {paper_name} code=0x{paper['code']:02x} {w}x{h} ドット")
    print(f"入れる紙: {loaded_name} {lw}x{lh} ドット")
    print(f"置いた印: {', '.join(placed) if placed else 'なし'}")
    if skipped:
        print(f"置かなかった印: {', '.join(skipped)}  <- 実際の紙からはみ出すため")
        print("  (最後まで送れたかは、排出されたかどうかで判断すること)")
    print(f"黒画素 : {dots:,}")
    print(f"出力   : {out_path}")


def emit_rgl(profile, paper, ppm_path: pathlib.Path, out_path: pathlib.Path) -> None:
    """黒 1 色・1 パスの RGL を組み立てる。用紙表に無い寸法(custom)を試すため、
    C# 側の build-rgl(用紙名でしか指定できない)ではなく ref/ の emitter を使う。"""
    from foilwright_ref import emitter, raster

    image = raster.read_ppm(str(ppm_path))
    width, height, _ = image
    palette = config.load_palette(str(REPO / "palette" / "default.yaml"))
    black = next(i for i in palette if i["name"] == "black")
    planes = raster.to_planes_magic(image, [black])
    media = config.load_media_table(str(REPO / "media.yaml"))["plain_paper"]

    job = {
        "resolution": 600,
        "paper": paper,
        "media": media,
        "inks": [
            {"name": black["name"], "printer_code": black["printer_code"], "passes": 1}
        ],
        "width": width,
        "height": height,
    }
    out_path.write_bytes(emitter.emit_job(planes, job))
    print(f"RGL    : {out_path} ({out_path.stat().st_size:,} バイト)")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--paper", required=True, help="ジョブで宣言する用紙名")
    ap.add_argument("--loaded", required=True, help="実際にプリンタへ入れる用紙名")
    ap.add_argument("--machine", default="md-5000")
    ap.add_argument("--out", default=None)
    ap.add_argument(
        "--width",
        type=int,
        default=None,
        help="用紙表の幅を上書きする(ドット)。custom(0x00)を試すときに使う",
    )
    ap.add_argument(
        "--length",
        type=int,
        default=None,
        help="用紙表の長さを上書きする(ドット)。custom(0x00)を試すときに使う",
    )
    ap.add_argument(
        "--emit-rgl",
        default=None,
        help="RGL バイト列も書き出す(ref/ の emitter を使う)",
    )
    args = ap.parse_args()

    profile = config.load_profile(str(REPO / "profiles" / f"{args.machine}.yaml"))
    table = config.resolve_paper_table(profile, str(REPO / "papers"))
    for name in (args.paper, args.loaded):
        if name not in table:
            ap.error(
                f"用紙 {name!r} は表にない。選べるのは: {', '.join(sorted(table))}"
            )

    out = pathlib.Path(args.out) if args.out else REPO / f"feedtest_{args.paper}.ppm"
    paper = dict(table[args.paper])
    if args.width is not None:
        paper["width"] = args.width
    if args.length is not None:
        paper["length"] = args.length
    if paper["width"] == 0 or paper["length"] == 0:
        ap.error(
            f"用紙 {args.paper!r} は寸法を持たない。--width と --length で指定すること"
        )

    build(args.paper, paper, args.loaded, table[args.loaded], out)

    if args.emit_rgl:
        emit_rgl(profile, paper, out, pathlib.Path(args.emit_rgl))


if __name__ == "__main__":
    main()
