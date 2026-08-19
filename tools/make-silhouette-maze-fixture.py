"""白版モード「シルエット」(D-034)の突き合わせを厳しくするための PPM を作る。

既存の silhouette_ring_64x48.ppm(tools/make-silhouette-fixture.py)は
opaque と silhouette の差を示すには十分だが、**塗りつぶしの誤りを検出する
力が弱い**。枠の外側がだだっ広い純白の長方形なので、塗りつぶしに多少の
抜けがあっても別の経路から回り込んで全部埋まってしまい、結果が変わらない
(実測: 右端の伸長を 1 画素短くする / 隣接行の走査から左端を落とす、の
どちらの誤りを入れても突き合わせテストが通ってしまった)。

そこでこのフィクスチャは逆に作る -- 紙のほぼ全面を黒で埋め、そこに
**幅 1 画素の蛇行した純白の通路**を彫る。通路は左辺の 1 箇所だけで外に
つながっており、迂回路が無い。塗りつぶしがどこか 1 箇所でも伝播に失敗
すると、そこから先の通路が丸ごと「到達できなかった純白」に化けるため、
シルエットの結果が大きく変わる。

さらに、通路とつながっていない純白の小部屋を 1 つ置く。これが
opaque(純白は無条件で対象外)と silhouette(囲まれた純白は対象)を
分ける部分になる。

出力は netpbm 非依存(P6 の生バイト列を直接書く)。

使い方:
    .venv/Scripts/python.exe tools/make-silhouette-maze-fixture.py
"""

import pathlib

REPO = pathlib.Path(__file__).resolve().parent.parent
OUT_PATH = REPO / "tests" / "cases" / "silhouette_maze_64x48.ppm"

WIDTH, HEIGHT = 64, 48

WHITE = (255, 255, 255)
BLACK = (0, 0, 0)

# 蛇行する通路の横棒を通す行。左端(x=0)に口があるのは最初の行だけ。
CORRIDOR_ROWS = [4, 10, 16, 22, 28, 34, 40]
CORRIDOR_X0, CORRIDOR_X1 = 2, 60  # inclusive

# 通路とつながっていない純白の小部屋(黒に完全に囲まれている)。
CHAMBER_X0, CHAMBER_Y0, CHAMBER_X1, CHAMBER_Y1 = 40, 44, 55, 46  # inclusive


def make_pixels() -> bytearray:
    # 既定は黒。通路と小部屋だけを白で彫る。
    buf = bytearray(WIDTH * HEIGHT * 3)

    def put_white(x: int, y: int) -> None:
        idx = (y * WIDTH + x) * 3
        buf[idx : idx + 3] = bytes(WHITE)

    # 横棒
    for y in CORRIDOR_ROWS:
        for x in range(CORRIDOR_X0, CORRIDOR_X1 + 1):
            put_white(x, y)

    # 縦の継ぎ目。右端と左端を交互に使って蛇行させる(迂回路を作らない)。
    for i in range(len(CORRIDOR_ROWS) - 1):
        y0, y1 = CORRIDOR_ROWS[i], CORRIDOR_ROWS[i + 1]
        x = CORRIDOR_X1 if i % 2 == 0 else CORRIDOR_X0
        for y in range(y0, y1 + 1):
            put_white(x, y)

    # 左辺の口。ここだけが紙の外周に接する純白であり、唯一の種になる。
    for x in range(CORRIDOR_X0):
        put_white(x, CORRIDOR_ROWS[0])

    # 囲まれた小部屋
    for y in range(CHAMBER_Y0, CHAMBER_Y1 + 1):
        for x in range(CHAMBER_X0, CHAMBER_X1 + 1):
            put_white(x, y)

    return buf


def main() -> None:
    pixels = make_pixels()
    header = f"P6\n{WIDTH} {HEIGHT}\n255\n".encode("ascii")
    OUT_PATH.write_bytes(header + bytes(pixels))
    print(f"wrote {OUT_PATH} ({WIDTH}x{HEIGHT})")


if __name__ == "__main__":
    main()
