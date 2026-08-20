"""白版モード「アルファ」(D-037)の相互言語テスト用フィクスチャを作る。

Ghostscript を同じ PostScript ソースに対して 2 回走らせ、寸法の揃った
PPM(ppmraw、色用)/ PNG(pngalpha、アルファ用)の組を作る:

  - alpha_pair_NNNxNNN.ppm -- 色(ppmraw)。既存の色パイプラインの入力と
    同じ体裁(build_job_planes/JobAssembly.BuildJobPlanes の `image` 引数)。
  - alpha_pair_NNNxNNN.png -- アルファ(pngalpha)。RGB は使わず、アルファ
    チャンネルだけを読む(D-037: pngalpha の RGB を白へ合成すると ppmraw
    と一致しないことが実測済み -- D-036 補足)。

PostScript の中身は 3 領域 + 1 つのアンチエイリアス縁を持つ:
  1. 明示的に白で塗った四角(alpha=255、RGB も白) -- opaque モードでは
     「純白は無条件で対象外」のため見えないが、alpha モードでは見える。
     これが alpha と opaque が実際に違う結果を出すことの核心(D-037)。
  2. 色付きの四角(alpha=255、RGB は白でない)。
  3. 何も描いていない領域(alpha=0)。
  4. アンチエイリアスを効かせた円(-dGraphicsAlphaBits)。縁に 0 と 255
     の中間のアルファ値(半透明)が出る -- D-034 で学んだ教訓と同じ理屈:
     「alpha > 0」判定を誤って「alpha == 255」に変えても検知できない
     フィクスチャは弱い。中間値の画素があって初めて、相互言語テストの
     意図的破壊(Verification #6)が意味を持つ。

Ghostscript が見つからない環境では生成をスキップし、既存のファイルを
そのまま使う(既存ファイルには触れない)。

使い方:
    .venv\\Scripts\\python.exe tools\\make-alpha-fixture.py
"""

from __future__ import annotations

import pathlib
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO / "ref"))

from foilwright_ref import png as png_module
from foilwright_ref import raster

OUT_DIR = REPO / "tests" / "cases"
WIDTH = 200
HEIGHT = 200
DPI = 72
PPM_PATH = OUT_DIR / f"alpha_pair_{WIDTH}x{HEIGHT}.ppm"
PNG_PATH = OUT_DIR / f"alpha_pair_{WIDTH}x{HEIGHT}.png"

_GS_CANDIDATES = [
    r"C:\Program Files\gs\gs9.53.3\bin\gswin64c.exe",
]

_POSTSCRIPT = """\
%!PS
% D-037 alpha_pair fixture. 4 regions:
%   1. explicitly white-painted square (alpha=255, RGB white) -- invisible
%      to "opaque" (pure white is unconditionally excluded), visible to
%      "alpha" (alpha>0 is the only rule).
%   2. coloured square (alpha=255, RGB red).
%   3. undrawn area (alpha=0) -- most of the page.
%   4. anti-aliased circle (alpha varies 0..255 at the edge, via
%      -dGraphicsAlphaBits) -- gives at least one pixel with
%      0 < alpha < 255, needed to make "alpha > 0" vs "alpha == 255"
%      actually distinguishable by a cross-language test.
1 1 1 setrgbcolor
20 20 60 60 rectfill
1 0 0 setrgbcolor
120 20 60 60 rectfill
0 0 1 setrgbcolor
100 140 30 0 360 arc fill
showpage
"""


def _find_ghostscript() -> pathlib.Path | None:
    on_path = shutil.which("gswin64c")
    if on_path:
        return pathlib.Path(on_path)
    for candidate in _GS_CANDIDATES:
        path = pathlib.Path(candidate)
        if path.is_file():
            return path
    gs_root = pathlib.Path(r"C:\Program Files\gs")
    if gs_root.is_dir():
        for candidate in sorted(gs_root.iterdir(), reverse=True):
            exe = candidate / "bin" / "gswin64c.exe"
            if exe.is_file():
                return exe
    return None


def _run_ghostscript(
    gs: pathlib.Path,
    ps_path: pathlib.Path,
    device: str,
    out_path: pathlib.Path,
    *,
    antialias: bool,
) -> None:
    args = [
        str(gs),
        "-q",
        "-dNOPAUSE",
        "-dBATCH",
        "-dSAFER",
        f"-sDEVICE={device}",
        f"-r{DPI}",
        f"-g{WIDTH}x{HEIGHT}",
    ]
    if antialias:
        # pngalpha 専用: 円の縁に半透明(0 < alpha < 255)を作る
        # (D-034 の教訓: 中間値が無いと "alpha>0" vs "alpha==255" の
        # 意図的破壊を検知できない弱いフィクスチャになる)。
        args += ["-dGraphicsAlphaBits=4"]
    args += [f"-sOutputFile={out_path}", str(ps_path)]
    subprocess.run(args, check=True)


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    gs = _find_ghostscript()
    if gs is None:
        print(
            f"Ghostscript (gswin64c.exe) not found; skipping generation of "
            f"{PPM_PATH} / {PNG_PATH}. Using the existing files as-is if present."
        )
        return

    ps_path = OUT_DIR / "_alpha_pair_source.ps"
    ps_path.write_text(_POSTSCRIPT, encoding="ascii")
    try:
        _run_ghostscript(gs, ps_path, "ppmraw", PPM_PATH, antialias=False)
        _run_ghostscript(gs, ps_path, "pngalpha", PNG_PATH, antialias=True)
    finally:
        ps_path.unlink(missing_ok=True)

    ppm_width, ppm_height, _ = raster.read_ppm(str(PPM_PATH))
    png_width, png_height, rgba = png_module.read_png_rgba(str(PNG_PATH))
    if ppm_width != png_width or ppm_height != png_height:
        raise SystemExit(
            f"dimension mismatch: ppm={ppm_width}x{ppm_height} "
            f"png={png_width}x{png_height}"
        )

    alphas = {rgba[i * 4 + 3] for i in range(png_width * png_height)}
    print(f"wrote {PPM_PATH} and {PNG_PATH} ({ppm_width}x{ppm_height})")
    print(
        f"distinct alpha values present: {sorted(alphas)[:5]}...{sorted(alphas)[-5:]} ({len(alphas)} distinct)"
    )
    has_mid = any(0 < a < 255 for a in alphas)
    if not has_mid:
        raise SystemExit(
            "no semi-transparent (0 < alpha < 255) pixels found -- the "
            "anti-aliased circle should have produced some; the fixture "
            "would be too weak to detect an 'alpha > 0' vs 'alpha == 255' "
            "regression (D-034 lesson)."
        )


if __name__ == "__main__":
    sys.exit(main())
