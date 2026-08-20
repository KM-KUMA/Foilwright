"""D-036 の PNG デコーダ用テストフィクスチャを作る。

生成するもの(tests/cases/png/ 以下):
  - filter0_none.png .. filter4_paeth.png -- フィルタ 5 種を 1 つずつ
    確実に踏む 16x16 の RGBA PNG。自前の PNG エンコーダで、行ごとの
    フィルタ種別を明示的に指定して作る(Ghostscript 任せではフィルタ
    種別を制御できないため)。
  - idat_split.png -- 同じ画像を IDAT 3 個以上に分割したもの。
  - ancillary.png -- tEXt チャンクを IHDR と IDAT の間、および IDAT の
    後に挟んだもの。
  - gs_alpha.png -- Ghostscript の pngalpha デバイスが実際に出したもの
    (白い四角を塗り、残りは描かない PostScript を変換する)。
    Ghostscript が見つからない環境では生成をスキップし、既存のファイル
    をそのまま使う。

フィクスチャは合成画像のみ。利用者の原稿は入れない(D-036 / リポジトリは
public)。

使い方:
    .venv\\Scripts\\python.exe tools\\make-png-fixtures.py
"""

from __future__ import annotations

import pathlib
import shutil
import struct
import subprocess
import sys
import zlib

REPO = pathlib.Path(__file__).resolve().parent.parent
OUT_DIR = REPO / "tests" / "cases" / "png"

_SIGNATURE = b"\x89PNG\r\n\x1a\n"
_BPP = 4  # RGBA, 8-bit


def _chunk(chunk_type: bytes, data: bytes) -> bytes:
    return (
        struct.pack(">I", len(data))
        + chunk_type
        + data
        + struct.pack(">I", zlib.crc32(chunk_type + data) & 0xFFFFFFFF)
    )


def _paeth(a: int, b: int, c: int) -> int:
    p = a + b - c
    pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
    if pa <= pb and pa <= pc:
        return a
    if pb <= pc:
        return b
    return c


def _filter_row(cur: bytes, prev: bytes, filter_type: int) -> bytes:
    """PNG のエンコード側フィルタ(decode の逆演算)。cur/prev は
    フィルタ前(素の RGBA)の行バイト列。"""
    row_bytes = len(cur)
    out = bytearray(row_bytes)
    if filter_type == 0:  # None
        out[:] = cur
    elif filter_type == 1:  # Sub
        for i in range(row_bytes):
            left = cur[i - _BPP] if i >= _BPP else 0
            out[i] = (cur[i] - left) & 0xFF
    elif filter_type == 2:  # Up
        for i in range(row_bytes):
            out[i] = (cur[i] - prev[i]) & 0xFF
    elif filter_type == 3:  # Average
        for i in range(row_bytes):
            left = cur[i - _BPP] if i >= _BPP else 0
            up = prev[i]
            out[i] = (cur[i] - ((left + up) >> 1)) & 0xFF
    elif filter_type == 4:  # Paeth
        for i in range(row_bytes):
            left = cur[i - _BPP] if i >= _BPP else 0
            up = prev[i]
            upper_left = prev[i - _BPP] if i >= _BPP else 0
            out[i] = (cur[i] - _paeth(left, up, upper_left)) & 0xFF
    else:
        raise ValueError(f"unknown filter type {filter_type}")
    return bytes(out)


def _encode_png(
    width: int,
    height: int,
    pixels: bytes,
    *,
    row_filters: list[int] | None = None,
    idat_chunk_count: int = 1,
    extra_chunks_before_idat: list[bytes] | None = None,
    extra_chunks_after_idat: list[bytes] | None = None,
) -> bytes:
    """RGBA 8bit / インタレース無しの PNG バイト列を組み立てる。

    row_filters -- 行ごとのフィルタ種別(0-4)。None なら全行 0(None)。
    idat_chunk_count -- 圧縮後のバイト列をいくつの IDAT チャンクに割る
    か(D-036: Ghostscript は 47 個に分割していた実績がある)。
    """
    row_bytes = width * _BPP
    if row_filters is None:
        row_filters = [0] * height
    assert len(row_filters) == height

    raw = bytearray()
    prev = bytes(row_bytes)
    for y in range(height):
        cur = pixels[y * row_bytes : (y + 1) * row_bytes]
        ftype = row_filters[y]
        raw.append(ftype)
        raw += _filter_row(cur, prev, ftype)
        prev = cur

    compressed = zlib.compress(bytes(raw), level=9)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)

    parts = [_SIGNATURE, _chunk(b"IHDR", ihdr)]
    parts.extend(extra_chunks_before_idat or [])

    n = max(1, idat_chunk_count)
    chunk_size = max(1, (len(compressed) + n - 1) // n)
    for i in range(0, len(compressed), chunk_size):
        parts.append(_chunk(b"IDAT", compressed[i : i + chunk_size]))

    parts.extend(extra_chunks_after_idat or [])

    parts.append(_chunk(b"IEND", b""))
    return b"".join(parts)


def _patterned_pixels(width: int, height: int) -> bytes:
    """フィルタが効く(単色でない)模様を作る。行・列・対角で値を変化させ、
    Sub/Up/Average/Paeth のどれが効いても巻き戻しの誤りが出るようにする。"""
    buf = bytearray(width * height * _BPP)
    for y in range(height):
        for x in range(width):
            r = (x * 17 + y * 3) & 0xFF
            g = (x * 5 + y * 23) & 0xFF
            b = (255 - x * 11 - y * 7) & 0xFF
            a = (x * 13 ^ y * 29) & 0xFF
            idx = (y * width + x) * _BPP
            buf[idx : idx + 4] = bytes((r, g, b, a))
    return bytes(buf)


def _make_filter_fixtures() -> None:
    width, height = 16, 16
    pixels = _patterned_pixels(width, height)
    names = {
        0: "filter0_none.png",
        1: "filter1_sub.png",
        2: "filter2_up.png",
        3: "filter3_average.png",
        4: "filter4_paeth.png",
    }
    for ftype, name in names.items():
        data = _encode_png(width, height, pixels, row_filters=[ftype] * height)
        out_path = OUT_DIR / name
        out_path.write_bytes(data)
        print(f"wrote {out_path} ({width}x{height}, filter {ftype})")


def _make_idat_split_fixture() -> None:
    width, height = 24, 24
    pixels = _patterned_pixels(width, height)
    # 行ごとにフィルタを変えて、分割 + 複数フィルタが同時に効くようにする。
    row_filters = [y % 5 for y in range(height)]
    data = _encode_png(
        width, height, pixels, row_filters=row_filters, idat_chunk_count=5
    )
    out_path = OUT_DIR / "idat_split.png"
    out_path.write_bytes(data)
    idat_count = data.count(b"IDAT")
    print(f"wrote {out_path} ({width}x{height}, {idat_count} IDAT chunks)")
    assert idat_count >= 3, f"expected >=3 IDAT chunks, got {idat_count}"


def _make_ancillary_fixture() -> None:
    width, height = 12, 12
    pixels = _patterned_pixels(width, height)
    text_before = _chunk(b"tEXt", b"Comment\x00before IDAT")
    text_after = _chunk(b"tEXt", b"Comment\x00after IDAT")
    data = _encode_png(
        width,
        height,
        pixels,
        row_filters=[2] * height,  # Up, just to not be all-None
        extra_chunks_before_idat=[text_before],
        extra_chunks_after_idat=[text_after],
    )
    out_path = OUT_DIR / "ancillary.png"
    out_path.write_bytes(data)
    print(f"wrote {out_path} ({width}x{height}, with tEXt chunks)")


_GS_CANDIDATES = [
    r"C:\Program Files\gs\gs9.53.3\bin\gswin64c.exe",
]


def _find_ghostscript() -> pathlib.Path | None:
    on_path = shutil.which("gswin64c")
    if on_path:
        return pathlib.Path(on_path)
    gs_root = pathlib.Path(r"C:\Program Files\gs")
    if gs_root.is_dir():
        for candidate in sorted(gs_root.iterdir(), reverse=True):
            exe = candidate / "bin" / "gswin64c.exe"
            if exe.is_file():
                return exe
    return None


_POSTSCRIPT = """\
%!PS
% D-036 gs_alpha.png fixture: paint a white square (alpha=255) and leave
% the rest of the page untouched (alpha=0, "nothing drawn"). pngalpha's
% whole point is to distinguish these two cases.
1 1 1 setrgbcolor
50 50 100 100 rectfill
showpage
"""


def _make_gs_alpha_fixture() -> None:
    out_path = OUT_DIR / "gs_alpha.png"
    gs = _find_ghostscript()
    if gs is None:
        print(
            f"Ghostscript (gswin64c.exe) not found; skipping generation of {out_path}. "
            "Using the existing file as-is if present."
        )
        return

    ps_path = OUT_DIR / "_gs_alpha_source.ps"
    ps_path.write_text(_POSTSCRIPT, encoding="ascii")
    try:
        subprocess.run(
            [
                str(gs),
                "-q",
                "-dNOPAUSE",
                "-dBATCH",
                "-dSAFER",
                "-sDEVICE=pngalpha",
                "-r72",
                "-g200x200",
                f"-sOutputFile={out_path}",
                str(ps_path),
            ],
            check=True,
        )
    finally:
        ps_path.unlink(missing_ok=True)
    print(f"wrote {out_path} (via Ghostscript pngalpha)")


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    _make_filter_fixtures()
    _make_idat_split_fixture()
    _make_ancillary_fixture()
    _make_gs_alpha_fixture()


if __name__ == "__main__":
    sys.exit(main())
