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

## 検証済みの事実(2026-07-28)

- **g1 と g5 はバイト完全一致**(`cmp` 差分ゼロ)。MD-5000 と MD-5500 は ppmtomd レベルでコマンド差分なし(DOMAIN §11 #3 の裏付け)
- g1 と g4 の差分はバイト 8 の解像度コード(`\033*t{res}R` の 0x03→0x04)とページ幅(4800→9600 ドット)のみ。docs/research/ppmtomd-survey.md の Q1/Q2 と整合
- 全 golden の先頭は `1b 25 80 41`(ESC % 0x80 A = RGL モード選択)

## 注意

- golden は基準であり必ずリポジトリに含める(.gitignore で除外しない。DOMAIN §12.7)
- 実機テストの生ダンプ(dumps/)と混同しないこと。外見が同じバイナリなので置き場所で分離する
- 追加時はこの表に 1 行追記し、`make_golden.sh` にも生成コマンドを追加する
