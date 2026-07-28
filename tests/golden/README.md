# GOLDEN — 台帳

ppmtomd 1.6 が生成した既知正解のバイト列(DOMAIN §9)。ref/ と src/ の実装はこれとのバイト一致で検証する。

## 生成環境

- ppmtomd 1.6(2009-09-09)。ソースは `vendor/ppmtomd-1.6/`(リポジトリ非同梱、配布元: <http://www.stevens-bradfield.com/ppmtomd/>)
- ビルド: WSL Ubuntu 26.04 LTS / gcc 15.2.0(Ubuntu 15.2.0-16ubuntu1)/ libnetpbm-dev 2:11.10.02-1build1
- ビルドコマンド: `make CDEBUGFLAGS='-O2 -I/usr/include/netpbm'`(Makefile 既定の最適化を維持)
- 再生成: `wsl -e bash tests/cases/make_golden.sh`
- 採取日: 2026-07-28

## 台帳

| ファイル | 入力 | コマンドライン(ppmtomd 引数) | 決定性 |
| --- | --- | --- | --- |
| g1_c1_black_md5000_600.bin | c1_black_120x120.ppm | `-model MD-5000 -resolution 600` | 2回生成バイト一致 |
| g2_c2_blackcyan_md5000_600.bin | c2_blackcyan_240x120.ppm | `-model MD-5000 -resolution 600` | 同上 |
| g3_c3_white_md5000_600.bin | c3_black_for_white_120x120.ppm | `-model MD-5000 -resolution 600 -colours K=White` | 同上 |
| g4_c1_black_md5000_1200.bin | c1_black_120x120.ppm | `-model MD-5000 -resolution 1200` | 同上 |
| g5_c1_black_md5500_600.bin | c1_black_120x120.ppm | `-model MD-5500 -resolution 600` | 同上 |
| g6_c4_square_md5000_600.bin | c4_square_on_white_120x120.ppm | `-model MD-5000 -resolution 600` | 同上 |
| g7_c5_metallic4_md5000_600.bin | c5_metallic4_240x120.ppm | `-model MD-5000 -resolution 600 -colours C=MetallicCyan,M=MetallicMagenta,Y=MetallicGold,K=MetallicSilver` | 同上 |
| g8_c1_shift_md5000_600.bin | c1_black_120x120.ppm | `-model MD-5000 -resolution 600 -xshift 100 -yshift 200` | 同上 |
| g9_c1_autoshift_md5000_600.bin | c1_black_120x120.ppm | `-model MD-5000 -resolution 600 -autoshift -xshift 200 -yshift 400` | 同上 |
| g10_c5_white_multilayer_md5000_600.bin | c5_metallic4_240x120.ppm | `-model MD-5000 -resolution 600 -colours C=White,M=MetallicGold,Y=MetallicSilver,K=Black` | 同上 |
| g11_c5_white_finish_colour_md5000_600.bin | c5_metallic4_240x120.ppm | `-model MD-5000 -resolution 600 -colours C=White,M=Finish,Y=MetallicGold,K=Black` | 同上 |
| g12_c6_fullcolour_md5000_600.bin | c6_fullcolour_240x120.ppm | `-model MD-5000 -resolution 600`(特色なし) | 同上 |
| g13_c6_halftone_md5000_600.bin | c6_fullcolour_240x120.ppm | `-model MD-5000 -resolution 600 -colourcorrection Plain -dither Halftone` | 同上 |
| g14_c6_coarsehalftone_md5000_600.bin | c6_fullcolour_240x120.ppm | `-model MD-5000 -resolution 600 -colourcorrection Plain -dither CoarseHalftone` | 同上 |

## 検証済みの事実(2026-07-28)

- **g1 と g5 はバイト完全一致**(`cmp` 差分ゼロ)。MD-5000 と MD-5500 は ppmtomd レベルでコマンド差分なし(DOMAIN §11 #3 の裏付け)
- g1 と g4 の差分はバイト 8 の解像度コード(`\033*t{res}R` の 0x03→0x04)とページ幅(4800→9600 ドット)のみ。docs/research/ppmtomd-survey.md の Q1/Q2 と整合
- 全 golden の先頭は `1b 25 80 41`(ESC % 0x80 A = RGL モード選択)
- **パスの実行順は CMYK コンポーネント順で固定**(C → M → Y → K)。ppmtomd が順序を入れ替えるのは DyeSub のときだけで(ppmtomd.c:1456-1464)、本プロジェクトの対象外(DOMAIN §1.3)
- **色選択コマンドのコードバイト**(`1b 1a {code} {flag} 72` の code)は mddata.c の colour enum 順。golden から読み取った実測値: Black=0x00 / Cyan=0x01 / Magenta=0x02 / Yellow=0x03 / MetallicGold=0x04 / MetallicMagenta=0x05 / MetallicCyan=0x06 / MetallicSilver=0x07 / White=0x0B。{flag} は最終プレーンのみ 0x80
- **ディザを使う golden は `-colourcorrection Plain` を明示すること。** ppmtomd は `-dither` を指定すると色補正を勝手に `Photo` へ切り替える(ppmtomd.c:1437-1441)。明示しないとディザの検証に色補正が混入する(DOMAIN §4.2.1)
- **位置合わせのシフトは正の値のときだけコマンドになる**(`ESC & a {x} L` / `ESC & l {y} E`、ppmtomd.c:2546-2555)。負のシフトはコマンドを出さず、画像データ側を削る別経路に入る(ppmtomd.c:2659)。`ref/` は負のシフトを未実装とし、渡されたら `NotImplementedError` で止める(黙って誤った位置に刷らないため)
- **シフト量の単位は 1/600 インチで、解像度設定に依存しない**(ppmtomd.man)。内部では出力解像度に換算され、300dpi は半分、1200dpi は x 方向のみ 2 倍(ppmtomd.c:1920-1921)
- **排出(単独のフォームフィード `0x0C`)は全 golden で 1 回のみ。** パス間の用紙戻しはバックフィード(`ESC SUB 0 0 FF`)で行われ、これは排出ではない。**白を含む 4 層(g10)でも同じ**であり、白が他インクと同一ジョブに同居できる。DOMAIN §11.5 / §4.10 の根拠
- 全 golden を再生成しても既存ファイルは 1 バイトも変化しない(2026-07-28 実施)

## 注意

- golden は基準であり必ずリポジトリに含める(.gitignore で除外しない。DOMAIN §12.7)
- 実機テストの生ダンプ(dumps/)と混同しないこと。外見が同じバイナリなので置き場所で分離する
- 追加時はこの表に 1 行追記し、`make_golden.sh` にも生成コマンドを追加する
