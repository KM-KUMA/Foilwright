#!/bin/bash
# golden 再生成スクリプト(WSL Ubuntu 上で実行)
# 使い方: wsl -e bash tests/cases/make_golden.sh
# 各 golden は 2 回生成してバイト一致(決定性)を確認する
cd /mnt/e/build/Foilwright || exit 1
P=vendor/ppmtomd-1.6/ppmtomd
mkdir -p tests/golden

gen() {
  out=$1; shift
  if ! "$P" "$@" > "tests/golden/$out" 2>"/tmp/err_$out.txt"; then
    echo "FAIL_RUN $out: $(head -2 "/tmp/err_$out.txt")"
    return 1
  fi
  "$P" "$@" > "/tmp/rerun_$out" 2>/dev/null
  if cmp -s "tests/golden/$out" "/tmp/rerun_$out"; then
    echo "OK_DETERMINISTIC $out size=$(stat -c%s "tests/golden/$out")"
  else
    echo "NON_DETERMINISTIC $out"
  fi
}

gen g1_c1_black_md5000_600.bin    -model MD-5000 -resolution 600  tests/cases/c1_black_120x120.ppm
gen g2_c2_blackcyan_md5000_600.bin -model MD-5000 -resolution 600  tests/cases/c2_blackcyan_240x120.ppm
gen g3_c3_white_md5000_600.bin    -model MD-5000 -resolution 600 -colours K=White tests/cases/c3_black_for_white_120x120.ppm
gen g4_c1_black_md5000_1200.bin   -model MD-5000 -resolution 1200 tests/cases/c1_black_120x120.ppm
gen g5_c1_black_md5500_600.bin    -model MD-5500 -resolution 600  tests/cases/c1_black_120x120.ppm
gen g6_c4_square_md5000_600.bin   -model MD-5000 -resolution 600  tests/cases/c4_square_on_white_120x120.ppm
# 特色を使わない純粋なフルカラー。赤緑青が 2 インクの混色になり、
# 4 プレーン全部にデータが乗る唯一のケース(g1 は K のみ、g2 は K+C)
gen g12_c6_fullcolour_md5000_600.bin -model MD-5000 -resolution 600 \
    tests/cases/c6_fullcolour_240x120.ppm
# 白 → コーティング → 色。作者の実作業手順(DOMAIN §4.11 / §10.7)
gen g11_c5_white_finish_colour_md5000_600.bin -model MD-5000 -resolution 600 \
    -colours C=White,M=Finish,Y=MetallicGold,K=Black \
    tests/cases/c5_metallic4_240x120.ppm
# 位置合わせ。正のシフトだけがコマンドになる(ESC & a {x} L / ESC & l {y} E)
gen g8_c1_shift_md5000_600.bin -model MD-5000 -resolution 600 \
    -xshift 100 -yshift 200 tests/cases/c1_black_120x120.ppm
# -autoshift は用紙余白(A4: left=80 top=284)を引く。引いた結果が負になると
# ppmtomd は画像側を削る経路に入り、120 行の画像では足りず EOF エラーになる。
# 余白より大きいシフトを与えて正の値に収め、減算ロジックだけを検証する
gen g9_c1_autoshift_md5000_600.bin -model MD-5000 -resolution 600 \
    -autoshift -xshift 200 -yshift 400 tests/cases/c1_black_120x120.ppm
# 白を含む多層。DOMAIN §11.5 の判定用 — 排出(単独 0x0C)が 1 回だけであることを
# 数えるのが目的。白が他インクと同一ジョブに同居できることの実証も兼ねる
gen g10_c5_white_multilayer_md5000_600.bin -model MD-5000 -resolution 600 \
    -colours C=White,M=MetallicGold,Y=MetallicSilver,K=Black \
    tests/cases/c5_metallic4_240x120.ppm
# メタリック 4 色。order 同値のパスが 4 本生じる唯一のケース(DOMAIN §4.9 / §9.3)
gen g7_c5_metallic4_md5000_600.bin -model MD-5000 -resolution 600 \
    -colours C=MetallicCyan,M=MetallicMagenta,Y=MetallicGold,K=MetallicSilver \
    tests/cases/c5_metallic4_240x120.ppm

# ディザリング。-colourcorrection Plain の明示が必須(DOMAIN §4.2.1)
gen g13_c6_halftone_md5000_600.bin -model MD-5000 -resolution 600     -colourcorrection Plain -dither Halftone tests/cases/c6_fullcolour_240x120.ppm
gen g14_c6_coarsehalftone_md5000_600.bin -model MD-5000 -resolution 600     -colourcorrection Plain -dither CoarseHalftone tests/cases/c6_fullcolour_240x120.ppm

# メディア種別。厚紙はリボン切れ対策(DOMAIN §5.5.2 / §10.8.2)
gen g15_c1_cardboard_md5000_600.bin -model MD-5000 -resolution 600 -media Cardboard tests/cases/c1_black_120x120.ppm

if cmp -s tests/golden/g1_c1_black_md5000_600.bin tests/golden/g5_c1_black_md5500_600.bin; then
  echo "MODEL_DIFF_ZERO (MD-5000 == MD-5500)"
else
  echo "MODEL_DIFF_PRESENT"
  cmp tests/golden/g1_c1_black_md5000_600.bin tests/golden/g5_c1_black_md5500_600.bin | head -3
fi
