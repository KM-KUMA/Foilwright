"""見当合わせ検査図を生成する(白 + 黒の 2 パス)。

白の四角の上に一回り小さい黒を重ねる。白の縁が四方に均等に出ていれば
見当が合っている。片側に寄っていればその方向にずれている(DOMAIN §11.5)。

紙の上部・中央・下部の 3 箇所に置く。バックフィードの誤差は送り量に
比例して効くため、位置による差が出るかを同時に見る。

使い方:
    .venv\\Scripts\\python.exe tools\\make-registration-test.py
    # -> dumps/phase3_registration.bin ができる
    dotnet run --project src/Foilwright.Cli -- print dumps/phase3_registration.bin --machine md-5000

中間生成物の PPM は 22MB あるためリポジトリには残さない(既定で一時
ディレクトリに書き、終了時に消す)。
"""

import pathlib
import sys
import tempfile

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import config, emitter, raster  # noqa: E402

DPI = 600
MM = DPI / 25.4  # 1mm あたりのドット
WIDTH, HEIGHT = 1200, 6372  # 幅は検査に足りる分だけ。高さは A4 の印字可能長

WHITE_MM = 10  # 白の四角
BLACK_MM = 8  # 黒の四角(白の内側に 1mm ずつ収まる)

# パレットのマジックカラー(palette/default.yaml と一致していること)
WHITE_RGB = (230, 230, 230)
BLACK_RGB = (0, 0, 0)

# 上部・中央・下部。紙送りの累積誤差が位置で変わるかを見る。
CENTERS_MM = [(15, 30), (15, 105), (15, 180)]


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
    wsize = int(WHITE_MM * MM)
    bsize = int(BLACK_MM * MM)
    offset = (wsize - bsize) // 2

    for cx_mm, cy_mm in CENTERS_MM:
        wx, wy = int(cx_mm * MM), int(cy_mm * MM)
        fill(pixels, wx, wy, wsize, wsize, WHITE_RGB)
        fill(pixels, wx + offset, wy + offset, bsize, bsize, BLACK_RGB)

    print(
        f"白 {WHITE_MM}mm({wsize}px) / 黒 {BLACK_MM}mm({bsize}px) / "
        f"縁 {offset}px = {offset / MM:.2f}mm"
    )

    with tempfile.TemporaryDirectory() as tmp:
        ppm = pathlib.Path(tmp) / "registration.ppm"
        ppm.write_bytes(b"P6\n%d %d\n255\n" % (WIDTH, HEIGHT) + bytes(pixels))
        image = raster.read_ppm(str(ppm))

    inks = config.load_palette(str(REPO / "palette" / "default.yaml"))
    planes = raster.to_planes_magic(image, inks)

    # to_planes_magic はマジックカラーを持つ特色だけを返す(D-019)。
    # プロセスインクの名前は planes に無いため in で確かめてから引く。
    used = [
        (ink, planes[ink["name"]])
        for ink in inks
        if ink["name"] in planes and any(planes[ink["name"]])
    ]
    for ink, plane in used:
        dots = sum(bin(b).count("1") for b in plane)
        print(f"  {ink['label']}: {dots} ドット")

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
    }
    out = emitter.emit_job(planes, job)

    dest = REPO / "dumps" / "phase3_registration.bin"
    dest.parent.mkdir(exist_ok=True)
    dest.write_bytes(out)

    sel = [
        i
        for i in range(len(out) - 4)
        if out[i] == 0x1B and out[i + 1] == 0x1A and out[i + 4] == 0x72
    ]
    back = out.count(bytes([0x1B, 0x1A, 0, 0, 0x0C]))
    print(f"job: {dest.name} ({len(out)} バイト)")
    print(
        f"  色選択 {[hex(out[i + 2]) for i in sel]} / "
        f"バックフィード {back} / 排出 {out.count(bytes([0x0C])) - back}"
    )


if __name__ == "__main__":
    main()
