# 色補正テーブル

ppmtomd 1.6 由来の色補正ルックアップテーブル。**Foilwright が生成した値ではない。**

## photo_colcor.bin

| | |
|---|---|
| 大きさ | 16,384 バイト(16 x 16 x 16 x 4) |
| sha256 | `e37a9444d6a10f5c4b6baff20387cecb103ad03a48be66185cf55a6770ca61b8` |
| 出典 | ppmtomd 1.6 の `photocolcor.c`、配列 `photo_colcor[16*16*16*4]` |
| 作者 | J. C. Bradfield |
| ライセンス | GNU General Public License version 2 以降 |

CMY(各成分 `255 - RGB`)の 16 段階格子を添字とし、CMYK の 4 値を返す。
並びは C が最も遅く変化する順(`[c][m][y][成分]`)で、格子点あたり 4 バイト。

**ppmtomd の man によれば、この表は ALPS の Windows ドライバから導出された**もの
であり、純正ドライバに近い色を得るために使う(DOMAIN §4.2.2)。

### 取り込みの根拠

ppmtomd は GPL-2.0-or-later で配布されており(`LICENCE` に明記)、Foilwright は
GPL-3.0-or-later である。**GPL-2 以降は GPL-3 のプロジェクトに取り込める。**
作者自身が LICENCE に「もっと緩いライセンスが必要なら相談してほしい」と
書いており、再利用を想定した配布である。

**ALPS の著作物(マニュアル・ドライババイナリ)とは扱いが異なる。** そちらは
権利者が再配布を許していないためリポジトリに置かない(DOMAIN §12.3 / §12.6)。
本ファイルは GPL で明示的に再利用が許諾されたものであり、この区別は D-029 に
記録している。

### 使い方

そのままでは使わない。**16³ から 64³ へ三重線形補間で展開してから**参照する
(ppmtomd の `expand_lut`)。展開後の表を CMY の上位 6 ビットで引く。

### 未取り込み

同じ形式のテーブルが ppmtomd にもう 2 つある。必要になった時点で追加する。

- `vphoto_colcor` — nybble(多値)モード用。多値モードは未実装
- `dyesub_colcor` — 昇華紙用。`DyeSub` 色補正を実装する場合に要る
