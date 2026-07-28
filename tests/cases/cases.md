# CASES — 入力画像と生成手順

golden(tests/golden/)の入力となる PPM と、その決定的な生成手順。

## 入力ファイル

| ファイル | 内容 | 生成コマンド(netpbm) |
| --- | --- | --- |
| c1_black_120x120.ppm | 全面ベタ黒 120x120(600dpi 時 5mm≒118 ドットを意識) | `ppmmake black 120 120` |
| c2_blackcyan_240x120.ppm | 左半分黒・右半分シアン 240x120 | `ppmmake black 120 120 > a.ppm; ppmmake cyan 120 120 > b.ppm; pnmcat -lr a.ppm b.ppm` |
| c3_black_for_white_120x120.ppm | c1 と同一の黒ベタ。White 差し替え(`-colours K=White`)で刷る想定 | c1 のコピー |
| c4_square_on_white_120x120.ppm | 白地の中央に 40x40 の黒四角 | `ppmmake white 120 40 > top.ppm; ppmmake white 40 40 > l.ppm; ppmmake black 40 40 > blk.ppm; pnmcat -lr l.ppm blk.ppm l.ppm > mid.ppm; pnmcat -tb top.ppm mid.ppm top.ppm` |

| c5_metallic4_240x120.ppm | シアン・マゼンタ・イエロー・黒を各 60x120 で横に並べた 240x120 | `ppmmake cyan 60 120 > c.ppm; ppmmake magenta 60 120 > m.ppm; ppmmake yellow 60 120 > y.ppm; ppmmake black 60 120 > k.ppm; pnmcat -lr c.ppm m.ppm y.ppm k.ppm` |

| c6_fullcolour_240x120.ppm | 赤・緑・青・黒・白・中間調(50% 灰)を各 40x120 で横に並べた 240x120 | `ppmmake rgb:ff/00/00 40 120` 等で 6 色を作り `pnmcat -lr` で連結 |

**c6 は 4 プレーン全部にデータが乗る唯一のケース。** 赤は M+Y、緑は C+Y、青は C+M の混色になる。特色を一切使わないフルカラー印刷の検証で、g1(K のみ)や g2(K+C)では通らない経路を通す。純白は何も刷らず、50% 灰も閾値二値化(DOMAIN §4.2)により何も刷られない。

**c5 は `order` 同値のパスが複数生じる唯一のケース。** メタリック 4 色はパレット定義で order が同値のため(DOMAIN §4.3)、並べ替えに安定ソートを使わない実装では順序が変わりバイト列が食い違う(§4.9)。実機のメタリックカートリッジは不要で、バイト列の検証は完全に実施できる(§9.5)。

**c4 は空白の経路を通すための必須ケース。** ベタ画像だけでは空行スキップ(`ESC * b {n} Y`)・行内の末尾ゼロトリム・ページ下端の連続空行のいずれも一度も実行されない。実際 g6 には 40 行スキップのコマンド(`1b 2a 62 28 00 59`)が現れる。デカール用途では白地が大半を占めるため、この経路のほうが本番では主役になる。

3 ファイルとも上記コマンドの出力とバイト一致することを確認済み(2026-07-28)。

## オプション選定の理由

- ベタ画像のため FP 依存のディザ・ガンマ経路の影響は実質なし。各 golden は 2 回生成のバイト一致で決定性を確認している
- `-colours K=White` は黒コンポーネントを White インクへ差し替える指定(docs/research/ppmtomd-survey.md Q5)
- モデル名の受理文字列は `MD-5000` / `MD-5500`(vendor/ppmtomd-1.6/mddata.c の model_table)

## 再生成

```powershell
wsl -e bash tests/cases/make_golden.sh
```
