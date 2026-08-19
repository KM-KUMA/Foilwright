"""白の上に色が乗るか(定着するか)を確かめる検査図。

DOMAIN §4.11 は「白のパスの直後に色のパスを置いてはならない。間に
コーティング(Finish)を挟む」を不変条件としている。根拠は §10.7 の実測。
一方 §10.9.2 の外部報告は「特色ホワイトなら色が乗りやすい」としており、
**どちらが正しいかは使った白の種類に依存する可能性がある**(§10.10 に
「使用したホワイトの種類が §10.7 の記録に残っていない」と明記)。

手元にあるのは紙用特色ホワイト (MDC-SCWH)。コーティング MDC-FLCG は未所有。
**この検査図 1 枚で、コーティングを買う必要があるかが決まる。**

    上段: 白ベタの上に C / M / Y / K を重ねる
    下段: 同じ色を紙へ直接(対照群)
    右端: 白のみ(白がどれだけ乗るかの確認)

上段が下段よりかすれる・剥がれる・色が薄いなら、コーティングが要る。
差が無ければ §4.11 は特色ホワイトには当てはまらない。

使い方:
    .venv\\Scripts\\python.exe tools\\make-white-adhesion-test.py
    dotnet run --project src/Foilwright.Cli -- print dumps/white_adhesion.bin --machine md-5000
"""

import pathlib
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config, emitter, raster

DPI = 600
MM = DPI / 25.4
WIDTH, HEIGHT = 2100, 6372

PATCH_MM = 12
PITCH_MM = 16
LEFT_MM = 6
ROW_ON_WHITE_MM = 20  # 上段: 白の上
ROW_ON_PAPER_MM = 40  # 下段: 紙に直接
WHITE_ONLY_MM = 70  # 右端: 白のみ

COLOURS = [
    ("シアン", (0, 255, 255)),
    ("マゼンタ", (255, 0, 255)),
    ("イエロー", (255, 255, 0)),
    ("ブラック", (0, 0, 0)),
]

K_SCRATCH = "__k_process"


def rect(x_mm, y_mm):
    return (int(x_mm * MM), int(y_mm * MM), int(PATCH_MM * MM))


def fill(pixels, x0, y0, size, rgb):
    for y in range(y0, y0 + size):
        if not (0 <= y < HEIGHT):
            continue
        row = y * WIDTH * 3
        for x in range(x0, x0 + size):
            if 0 <= x < WIDTH:
                pixels[row + x * 3 : row + x * 3 + 3] = bytes(rgb)


def set_plane(buf, x0, y0, size, row_bytes):
    for y in range(y0, y0 + size):
        base = y * row_bytes
        for x in range(x0, x0 + size):
            buf[base + (x >> 3)] |= 0x80 >> (x & 7)


def main():
    pixels = bytearray(b"\xff" * (WIDTH * HEIGHT * 3))
    for i, (_, rgb) in enumerate(COLOURS):
        for y_mm in (ROW_ON_WHITE_MM, ROW_ON_PAPER_MM):
            x0, y0, size = rect(LEFT_MM + i * PITCH_MM, y_mm)
            fill(pixels, x0, y0, size, rgb)

    with tempfile.TemporaryDirectory() as tmp:
        ppm = pathlib.Path(tmp) / "wa.ppm"
        ppm.write_bytes(b"P6\n%d %d\n255\n" % (WIDTH, HEIGHT) + bytes(pixels))
        image = raster.read_ppm(str(ppm))

    palette = config.load_palette(str(REPO / "palette" / "default.yaml"))
    white = next(i for i in palette if i["name"] == "white")
    inks = [i for i in palette if i["name"] != "white"]

    cmyk_map = {i["channel"]: i["name"] for i in inks if i.get("channel")}
    cmyk_map["K"] = K_SCRATCH
    planes = raster.to_planes_auto(
        image,
        inks,
        cmyk_map,
        halftone="none",
        colour_correction="plain",
    )

    # 二役インクの合成(ref/ には JobAssembly 相当の層が無い)。
    k_buf = planes.pop(K_SCRATCH)
    black = bytearray(planes["black"])
    for i, byte in enumerate(k_buf):
        black[i] |= byte
    planes["black"] = bytes(black)

    # 白版は自分で置く。上段の 4 パッチと、右端の「白のみ」だけを白にする。
    # 下段は白を敷かない — これが対照群になる。
    row_bytes = (WIDTH + 7) // 8
    wbuf = bytearray(row_bytes * HEIGHT)
    for i in range(len(COLOURS)):
        x0, y0, size = rect(LEFT_MM + i * PITCH_MM, ROW_ON_WHITE_MM)
        set_plane(wbuf, x0, y0, size, row_bytes)
    x0, y0, size = rect(WHITE_ONLY_MM, ROW_ON_WHITE_MM)
    set_plane(wbuf, x0, y0, size, row_bytes)
    planes["white"] = bytes(wbuf)

    order = [white] + [i for i in inks if any(planes.get(i["name"], b""))]
    used = [(ink, planes[ink["name"]]) for ink in order]

    print(f"パッチ {PATCH_MM}mm 角 / ピッチ {PITCH_MM}mm")
    print(
        f"  上段(y={ROW_ON_WHITE_MM}mm): 白の上に " + " / ".join(n for n, _ in COLOURS)
    )
    print(
        f"  下段(y={ROW_ON_PAPER_MM}mm): 紙に直接 " + " / ".join(n for n, _ in COLOURS)
    )
    print(f"  右端(x={WHITE_ONLY_MM}mm): 白のみ")
    print()
    for ink, plane in used:
        dots = sum(bin(b).count("1") for b in plane)
        # 注意: パレットの passes は emitter が参照していない(未実装)。
        # 実際に刷られるのは常に 1 回なので、期待値として表示しない。
        print(f"  {ink['label']}: {dots:,} ドット(実際に刷られるのは 1 回)")

    profile = config.load_profile(str(REPO / "profiles" / "md-5000.yaml"))
    job = {
        "resolution": DPI,
        "paper": config.resolve_paper_table(profile, str(REPO / "papers"))["a4"],
        "media": config.load_media_table(str(REPO / "media.yaml"))["plain_paper"],
        "inks": [
            {
                "name": ink["name"],
                "printer_code": ink["printer_code"],
                "passes": ink.get("passes", 1),
            }
            for ink, _ in used
        ],
        "width": WIDTH,
        "height": HEIGHT,
        "no_curl_correction": True,
    }
    out = emitter.emit_job(planes, job)

    dest = REPO / "dumps" / "white_adhesion.bin"
    dest.parent.mkdir(exist_ok=True)
    dest.write_bytes(out)

    sel = [
        i
        for i in range(len(out) - 4)
        if out[i] == 0x1B and out[i + 1] == 0x1A and out[i + 4] == 0x72
    ]
    back = out.count(bytes([0x1B, 0x1A, 0, 0, 0x0C]))
    print(f"\njob: {dest.name} ({len(out):,} バイト)")
    print(f"  色選択 {[hex(out[i + 2]) for i in sel]}")
    print(f"  バックフィード {back} / 排出 {out.count(bytes([0x0C])) - back}")


if __name__ == "__main__":
    main()
