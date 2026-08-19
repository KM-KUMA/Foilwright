"""インクごとのベタ塗り検査図を作る。

「イエローが出ない」「シアンが出ない」が、
  (a) 色分解の結果そのプレーンが空だったのか
  (b) プレーンはあるのにリボンが物理的に刷れていないのか
のどちらなのかを切り分ける。原稿を通さず、各インクを 100% で直接置く。

パッチは 4 色 x 2 段:
  上段 = 100%(ベタ)         … リボンが刷れるかどうか
  下段 = 50% 相当のディザ    … ハーフトーンが機能するかどうか

注意: `ref/` の `to_planes_auto` は、二役インク(黒のように magic_rgb と
channel の両方を持つもの)の特色側プレーンを CMYK 側で上書きする。
製品側では `JobAssembly` がこの 2 つを OR 合成しているが、`ref/` に同等の
層は無い。そのためここでは CMYK の K を別名で受け取り、自分で合成する。

使い方:
    .venv\\Scripts\\python.exe tools\\make-ink-test.py
    dotnet run --project src/Foilwright.Cli -- print dumps/inktest.bin --machine md-5000
"""

import pathlib
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config, emitter, raster  # noqa: E402

DPI = 600
MM = DPI / 25.4
WIDTH, HEIGHT = 1600, 6372

PATCH_MM = 12
GAP_MM = 4
ORIGIN_MM = (6, 20)

# 100% で 1 色だけが立つ RGB。Plain の式(k = min(c,m,y) を引く)を通しても
# 引かれる k が 0 になるため、狙ったインクだけが 255 で出る。
FULL = [
    ("シアン", (0, 255, 255)),
    ("マゼンタ", (255, 0, 255)),
    ("イエロー", (255, 255, 0)),
    ("ブラック", (0, 0, 0)),
]
# 50% 相当。中間調がディザで網点になるかを見る。
HALF = [
    ("シアン 50%", (128, 255, 255)),
    ("マゼンタ 50%", (255, 128, 255)),
    ("イエロー 50%", (255, 255, 128)),
    ("グレー 50%", (128, 128, 128)),
]

# K の受け皿を黒とは別名にして、あとで OR 合成する(上の注意を参照)。
K_SCRATCH = "__k_process"


def fill(pixels, x0, y0, w, h, rgb):
    for y in range(y0, y0 + h):
        if not (0 <= y < HEIGHT):
            continue
        row = y * WIDTH * 3
        for x in range(x0, x0 + w):
            if 0 <= x < WIDTH:
                pixels[row + x * 3 : row + x * 3 + 3] = bytes(rgb)


def main():
    pixels = bytearray(b"\xff" * (WIDTH * HEIGHT * 3))
    size = int(PATCH_MM * MM)
    step = int((PATCH_MM + GAP_MM) * MM)
    x0 = int(ORIGIN_MM[0] * MM)
    y0 = int(ORIGIN_MM[1] * MM)

    for i, (_, rgb) in enumerate(FULL):
        fill(pixels, x0 + i * step, y0, size, size, rgb)
    for i, (_, rgb) in enumerate(HALF):
        fill(pixels, x0 + i * step, y0 + step, size, size, rgb)

    print(f"パッチ {PATCH_MM}mm 角({size}px) / 間隔 {GAP_MM}mm")
    print("上段(左から): " + " / ".join(n for n, _ in FULL))
    print("下段(左から): " + " / ".join(n for n, _ in HALF))

    with tempfile.TemporaryDirectory() as tmp:
        ppm = pathlib.Path(tmp) / "inktest.ppm"
        ppm.write_bytes(b"P6\n%d %d\n255\n" % (WIDTH, HEIGHT) + bytes(pixels))
        image = raster.read_ppm(str(ppm))

    palette = config.load_palette(str(REPO / "palette" / "default.yaml"))
    # 白の下地は今回の切り分けに不要(白のパスが増えるだけ)。パレットから外す
    # のは D-028 のインク除外と同じ意味になる。
    inks = [ink for ink in palette if ink["name"] != "white"]

    cmyk_map = {i["channel"]: i["name"] for i in inks if i.get("channel")}
    cmyk_map["K"] = K_SCRATCH
    planes = raster.to_planes_auto(image, inks, cmyk_map, halftone="coarse_halftone")

    # 二役インクの合成: CMYK 側の K を、特色側の黒へ OR で足し込む。
    k_buf = planes.pop(K_SCRATCH)
    black = bytearray(planes["black"])
    for i, byte in enumerate(k_buf):
        black[i] |= byte
    planes["black"] = bytes(black)

    used = [
        (ink, planes[ink["name"]])
        for ink in inks
        if ink["name"] in planes and any(planes[ink["name"]])
    ]
    print()
    for ink, plane in used:
        dots = sum(bin(b).count("1") for b in plane)
        print(f"  {ink['label']}: {dots:,} ドット")

    profile = config.load_profile(str(REPO / "profiles" / "md-5000.yaml"))
    job = {
        "resolution": DPI,
        "paper": config.resolve_paper_table(profile, str(REPO / "papers"))["a4"],
        "media": config.load_media_table(str(REPO / "media.yaml"))["plain_paper"],
        "inks": [
            {"name": ink["name"], "printer_code": ink["printer_code"]}
            for ink, _ in used
        ],
        "width": WIDTH,
        "height": HEIGHT,
        "no_curl_correction": True,
    }
    out = emitter.emit_job(planes, job)

    dest = REPO / "dumps" / "inktest.bin"
    dest.parent.mkdir(exist_ok=True)
    dest.write_bytes(out)

    sel = [
        i
        for i in range(len(out) - 4)
        if out[i] == 0x1B and out[i + 1] == 0x1A and out[i + 4] == 0x72
    ]
    print(f"\njob: {dest.name} ({len(out):,} バイト)")
    print(f"  色選択 {[hex(out[i + 2]) for i in sel]}")


if __name__ == "__main__":
    main()
