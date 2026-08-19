"""白版モード「シルエット」(D-034)専用のテスト用 PPM を作る。

紙全体は純白の背景。その中に黒い枠(輪)を 1 つ描き、枠の内側は塗り
つぶさず純白のまま残す -- 「絵に囲まれた純白の穴」を意図的に作る図形。

  - 白版「不透明」(opaque, D-032)はこの枠だけを白版に入れる(純白は
    無条件で対象外という定義のため、内側の穴は入らない)。
  - 白版「シルエット」(silhouette, D-034)は枠 + 内側の穴の両方を
    白版に入れる(紙の外周から純白を辿って到達できないため)。

この違いが `opaque` と `silhouette` を区別する唯一の目的なので、
枠は必ず閉じた形にする(閉じていないと内側の穴が外周とつながってしまい、
シルエットも opaque と同じ結果になってしまう)。

出力は netpbm 非依存(P6 の生バイト列を直接書く)。他の tests/cases/*.ppm
は netpbm コマンドの記録(cases.md)から作られているが、この環境には
netpbm が無いため、このスクリプトはヘッダとピクセルを自分で組み立てる。

使い方:
    .venv\\Scripts\\python.exe tools\\make-silhouette-fixture.py
"""

import pathlib

REPO = pathlib.Path(__file__).resolve().parent.parent
OUT_PATH = REPO / "tests" / "cases" / "silhouette_ring_64x48.ppm"

WIDTH, HEIGHT = 64, 48

WHITE = (255, 255, 255)
BLACK = (0, 0, 0)

# 枠(輪)の外周と内周。内周の内側が「絵に囲まれた純白の穴」になる。
OUTER_X0, OUTER_Y0, OUTER_X1, OUTER_Y1 = 16, 12, 47, 35  # inclusive
INNER_X0, INNER_Y0, INNER_X1, INNER_Y1 = 20, 16, 43, 31  # inclusive


def make_pixels() -> bytes:
    buf = bytearray(WIDTH * HEIGHT * 3)
    for y in range(HEIGHT):
        for x in range(WIDTH):
            on_outer_ring = OUTER_X0 <= x <= OUTER_X1 and OUTER_Y0 <= y <= OUTER_Y1
            in_inner_hole = INNER_X0 <= x <= INNER_X1 and INNER_Y0 <= y <= INNER_Y1
            rgb = WHITE if (not on_outer_ring or in_inner_hole) else BLACK
            idx = (y * WIDTH + x) * 3
            buf[idx : idx + 3] = bytes(rgb)
    return bytes(buf)


def main() -> None:
    pixels = make_pixels()
    header = f"P6\n{WIDTH} {HEIGHT}\n255\n".encode("ascii")
    OUT_PATH.write_bytes(header + pixels)
    print(f"wrote {OUT_PATH} ({WIDTH}x{HEIGHT})")


if __name__ == "__main__":
    main()
