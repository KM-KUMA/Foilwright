# CASES — 入力画像と生成手順

golden(tests/golden/)の入力となる PPM と、その決定的な生成手順。

## 入力ファイル

| ファイル | 内容 | 生成コマンド(netpbm) |
| --- | --- | --- |
| c1_black_120x120.ppm | 全面ベタ黒 120x120(600dpi 時 5mm≒118 ドットを意識) | `ppmmake black 120 120` |
| c2_blackcyan_240x120.ppm | 左半分黒・右半分シアン 240x120 | `ppmmake black 120 120 > a.ppm; ppmmake cyan 120 120 > b.ppm; pnmcat -lr a.ppm b.ppm` |
| c3_black_for_white_120x120.ppm | c1 と同一の黒ベタ。White 差し替え(`-colours K=White`)で刷る想定 | c1 のコピー |

3 ファイルとも上記コマンドの出力とバイト一致することを確認済み(2026-07-28)。

## オプション選定の理由

- ベタ画像のため FP 依存のディザ・ガンマ経路の影響は実質なし。各 golden は 2 回生成のバイト一致で決定性を確認している
- `-colours K=White` は黒コンポーネントを White インクへ差し替える指定(docs/research/ppmtomd-survey.md Q5)
- モデル名の受理文字列は `MD-5000` / `MD-5500`(vendor/ppmtomd-1.6/mddata.c の model_table)

## 再生成

```
wsl -e bash tests/cases/make_golden.sh
```
