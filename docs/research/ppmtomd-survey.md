# ppmtomd-1.6 調査ノート

Foilwright(ALPS MD-5000/MD-5500 を 64bit Windows から駆動する仮想プリンタ)の golden 採取・参照実装検討のため、
GPL 実装 `ppmtomd` (v1.6) のソースを読解した記録。

## 1. 入手元

- 入手形態: `vendor/ppmtomd-1.6.tar.gz` として展開済み(`vendor/ppmtomd-1.6/` 配下)。tar 内に取得元 URL の記載あり。
- 配布元(README 記載): `http://www.stevens-bradfield.com/ppmtomd/`(README:6-7)
- バージョン: 1.6, リリース日 2009-09-09(`version.h:1`)
- 作者: J.C. Bradfield, 2000-2004(一部 Angus Duggan の ppmtopcl を参考)(`ppmtomd.c:1-6`)
- ライセンス: GNU General Public License version 2 or any later version(README:24-27、`ppmtomd.c:7-8`、`LICENCE` ファイル同梱)
- 対象機種(README 記載): Citizen Printiva シリーズ、Alps MD シリーズ、Oki DP シリーズ(DP-7000 は未対応)(README:11-13)
- サポート範囲(README 記載): standard mode はほぼ良好、dye sublimation mode は良好、VPhoto mode はまずまず。DP-5000 までの全色(foil 含む)に対応(README:15-20)

## 2. ソース構成の概観

| ファイル | 役割 |
| --- | --- |
| `ppmtomd.c` (3444行) | 本体。オプション解析・PPM 読込・色変換・ハーフトーン・ラスタ化・MD/RGL コマンド生成のすべてを含む単一の巨大ファイル |
| `mddata.c` (373行) | 色コード・バーコード(カートリッジ識別)・メディア種別・用紙サイズ・機種一覧・機種プロパティビットマスク・印字モードなどのテーブル定義(実体) |
| `mddata.h` (251行) | 上記テーブル群の型定義(enum)と extern 宣言 |
| `photocolcor.c` (84.3K) | 標準(Photo)モード用の色補正ルックアップテーブル(16x16x16x4 の生データ、`photo_colcor[]`) |
| `vphotocolcor.c` (84.3K) | VPhoto モード用の色補正ルックアップテーブル(`vphoto_colcor[]`) |
| `dyesubcolcor.c` (84.3K) | 染料昇華(DyeSub)モード用の色補正ルックアップテーブル(`dyesub_colcor[]`) |
| `version.h` (1行) | バージョン文字列定数 |
| `Makefile` (54行) | Unix 向けビルド定義(netpbm 依存) |
| `README` (115行) | 配布元・ライセンス・既知の非対応リボンに関するノート |
| `ppmtomd.man` (744行) | man ページ(オプション詳細説明) |
| `LICENCE` | GPL v2 全文 |

## 3. Questions への回答

### Q1: MD コマンド言語の全体構造

- **ジョブ開始(初期化)**: `rgl_init_page()` が担う(`ppmtomd.c:2484-2564`)。シーケンスは以下の順:
  1. `\033%\200A` — RGL モード選択(`ppmtomd.c:2488`)
  2. `\033*t{res}R` — 出力解像度指定(`ppmtomd.c:2489`)
  3. `\033&l{byte1}{byte2}M` — メディア種別/ファインネスコード(`ppmtomd.c:2492-2495`)
  4. `\033&l{papersize}{0}A` — 用紙サイズ選択(`ppmtomd.c:2496-2497`)
  5. (任意)`\033&l{len%256}{len/256}P` — ページ長指定(`ppmtomd.c:2500`)
  6. (任意)`\033&a{width%256}{width/256}M` — ページ幅指定(`ppmtomd.c:2505`)
  7. (任意)`\033\032{adj}{0}L` — 紙送り(LF)補正(`ppmtomd.c:2510`)
  8. (任意)`\033\032{adj}{0}V` — 印字ヘッド駆動信号補正(phadj)(`ppmtomd.c:2515`)
  9. (5000系・cassettePlane/cassetteRaster以外の非multiraster機で)`\033&l{0}{num}C` に続けてバーコード配列 — 使用カートリッジ列挙(`ppmtomd.c:2526-2544`)
  10. (任意)x/yシフト、glossy finish 要求など
- **プレーン(色)選択**: `\033\032{colour_to_command[色]}{lastplaneフラグ:0x80 or 0}{'r' or 'c'}` — 通常は `r`、cassettePlane モードでは `c`(`ppmtomd.c:2259-2264`)。ラスタグラフィックス開始は `\033*r{0}A`(`ppmtomd.c:2243`)、終了は `\033*rC`(`ppmtomd.c:2203, 2333`)。
- **転送モード/印字モード変更**: 転送モード再設定は `\033*r{mode}U`(`ppmtomd.c:2232-2234`)。curl 補正は `\033\032{on/off}{0}C`(`ppmtomd.c:2223`)。印字モード(5000系の Standard/VPhoto/DyeSub)変更は `\033\032{print_mode_bytes[mode]}{0}U`(`ppmtomd.c:2239`)。
- **プレーン送出コマンド(1行分のデータ)**: `\033*b{n%256}{n/256}{'V' or 'W'}` に続けて生データ n バイト。`V` は最終行/最終パスでラスタポインタを進める、`W` は進めない(`ppmtomd.c:3338-3354`)。行がすべて 0 の場合はデータを送らず `rowstoskip` をカウントし、次の非ゼロ行の直前に `\033*b{skip%256}{skip/256}Y` でまとめて送る(空白行スキップ、`ppmtomd.c:3298-3323`)。
- **圧縮方式**: あり。TIFF 4.0 PackBits 相当の RLE(`packbits()`、`ppmtomd.c:2362-2383` コメントに明記)。圧縮/非圧縮は行ごとに短い方を自動選択し、モード変更時のみ `\033*b{0 or 2}{0}M`(0=無圧縮, 2=圧縮)を発行(`ppmtomd.c:3304-3316`)。4bit(nybble)モードでは `packnybbles()` を使い圧縮なしのニブル詰めのみ(`ppmtomd.c:3266-3267`)。
- **紙送り・パス制御**: ページ間は `\014`(フォームフィード)。複数プレーンをまたぐ transfer では、最後でない限りバックフィード `\033\032{0}{0}\014` を発行して用紙を戻す(`ppmtomd.c:2283, 2325`)。ジョブ終了時: `\033*rC`(ラスタ終了・ffonly でなければ)→ `\014`(フォームフィード)→ `\033%{0}X`(RGL モード終了)→(`-noreset`/`-overlay` でなければ)`\033e`(プリンタ完全リセット)(`ppmtomd.c:2332-2345`)。

### Q2: 解像度指定コマンドの組み立て・サポート解像度・2400dpi

- 組み立て: `\033*t{output_resolution}R` で、値は enum `res300=0x02, res600=0x03, res1200=0x04` のいずれか1バイト(`ppmtomd.c:288-289, 2489`)。
- サポート解像度: 300x300 / 600x600(既定)/ 1200x600 の3種のみ。`-resolution` オプションが 300/600/1200 以外だとエラー(`ppmtomd.c:1096, 1868-1872`、man:497-499)。
- **2400dpi への言及**: ソース中に数値としての "2400" は存在しない。man ページに一箇所だけ「VPhoto (variable dot, misleadingly called 2400dpi) printing」という記載があり(`ppmtomd.man:481-483`)、これは実解像度ではなく VPhoto の可変ドット印字を指すマーケティング上の俗称である旨が明記されている。実装上の解像度コードは 300/600/1200 の3値のみ。

### Q3: 用紙幅・ヘッド幅に相当する定数(最大幅ドット数)

- 600dpi 基準の論理ページ幅/長さ/左右上余白は静的テーブルで機種系列ごとに2種定義(`ppmtomd.c:51-81`)。
  - `papersize_info_pre5000[]`(5000シリーズ以前、`ppmtomd.c:53-67`): 例 A4 幅 4800, Letter 幅 4800, Legal 幅 4800, custom 左マージン 80
  - `papersize_info_5000[]`(MD-5000/MD-5500/DP-5000、`ppmtomd.c:70-81`): 例 A4 幅 4800, Letter 幅 4940, Legal 幅 4940, postcard 系は top マージンが 71(pre5000 は 284)
  - どちらのテーブルを使うかは `mtest(modelHas5000PageSizes, model)` で分岐(`ppmtomd.c:1899, 1907, 1917, 1926`)。`modelHas5000PageSizes = mbit(modelMD5000) | mbit(modelMD5500) | mbit(modelDP5000)`(`mddata.c:333-334`)。
  - 解像度に応じて調整: 300dpi は半分、1200dpi は倍(`ppmtomd.c:1903, 1911-1912`)。
  - 実装上の上限マクロ `PCL_MAXWIDTH = 6000`, `PCL_MAXHEIGHT = 10000` は安全マージンであり、コメントに「実際の最大は 4800 だが、超過分はプリンタ側が捨てるのでエラーにする意味がない」と明記(`ppmtomd.c:1083-1086`)。
- **MD-5000 と MD-5500 個別の値の差分**: ソース中に発見できず。両機種は一貫して `model5k`(`mbit(modelMD5000) | mbit(modelMD5500) | mbit(modelDP5000)`, `mddata.h:204`)という単一ビットマスクで束ねられ、用紙サイズテーブル・全機能フラグとも MD-5000/MD-5500/DP-5000 の間で分岐箇所なし。**MD-5000 と MD-5500 の相違はこのソースからは特定不可(不明)**。README・コメントにも「MD-5500 は日本国内専用モデルで、MD-5000 との違いは不明」という趣旨の記述はない(mddata.h 側コメントには `modelMD5500` について「current japanese model. differences from 5000 unknown」との作者コメントあり、`mddata.h:189-190`)。

### Q4: 機種差分の扱い

- 機種列挙は `model` enum(`mddata.h:157-195`)。Printiva600/600U/700/1700、MD-2000/2010/4000/2300/1000/1300/1500/5000/5500、DP-5000 の14機種。
- 差分はすべて「機能ビットマスク」(`modelHasWhite` 等、`mddata.c:301-336`)経由で `mtest(bitmask, model)` により判定する構造で、個々のモデル名で直接分岐する箇所は用紙サイズテーブル選択(`modelHas5000PageSizes`、Q3参照)を除き見当たらない。
- 分岐している機能ビットマスク一覧(`mddata.c:301-346`):
  - `modelHasWhite` / `modelHasFinish`: MD-1000, MD-1300, MD-1500, model5k(MD-5000/5500/DP-5000)
  - `modelHasDyeSub`: MD-2300, MD-1300, MD-1500, model5k
  - `modelHasHighResColour`(1200dpiカラー): MD-1000, MD-1300, MD-2300, MD-1500, model5k
  - `modelHasVPhoto` / `modelHasVPhotoPrimer` / `modelHasFoil` / `modelHasEconoBlack` / `modelHasPrintModes`: model5k のみ(= MD-5000, MD-5500, DP-5000 のみが対応)
  - `modelHas5000PageSizes`: MD-5000, MD-5500, DP-5000 のみ
  - `modelHasMultiRaster`(旧世代の複数プレーン一括転送/マルチカラーリボン対応。5000系で廃止): Printiva600/600U/700/1700, MD-2000/2010/4000/2300/1000/1300/1500(= model5k 以外全部)
- 個々の判定使用箇所は `ppmtomd.c:1361,1364,1540,1543,1547,1596,1599,1602,1609,1666,1669,1675,1701,1716,1752,1899,1907,1917,1926,2016,2023,2033,2526` 等。
- 結論: **MD-5000 と MD-5500 はこのドライバ内では常に同一のビット(model5k)で扱われ、両者を区別するコードパスは存在しない**。DOMAIN §5.2 の「機種プロファイルの null を推測値で埋めない」という方針の妥当性を裏付ける(実機ごとの実測が必要という結論に対し、参照実装側にも差分情報がない)。

### Q5: インク指定の方法・特色サポート状況

- 色コード `colCode` enum(`mddata.h:14-49`): Black, Cyan, Magenta, Yellow, MetallicGold, MetallicMagenta, MetallicCyan, MetallicSilver, LabecaBlack/Red/Blue, White, DyeSubOvercoat, DyeSubGlossyFinish, GlossyFinish, VPhotoPrimer, GoldFoil, SilverFoil, EconoBlack、および疑似値 RawGoldFoil/RawSilverFoil(下地なし直接foil)、NullSpot/NullFoil(不使用マーカー)。
- ユーザー指定方法は3系統のコマンドラインオプション(`ppmtomd.c:1494-1660`付近):
  - `-colours`: CMYK 4コンポーネントに対する色の再割当て(例 `K=White` で K プレーンを White インクに差し替え)。カンマ区切り `component[=colour]`(`ppmtomd.c:1494-1558`)。
  - `-spotcolours`: 未使用の under/spot コンポーネントスロットにインクを割り当てる、`n=colour=input` 形式(`ppmtomd.c:1561-` 以降)。
  - `-undercolours`: 同様に under colour コンポーネント向け(`ppmtomd.c:1629-`)。
- 印字コマンドへの反映: `colour_to_command[]`(`mddata.c:59-85`)でプログラム内色コード→プリンタへ送る色選択バイトへ変換(Raw系Foilは通常Foilコードへ丸め込み)。カートリッジ識別が必要な場面(cassette 系転送モード)では `colour_to_barcode[]` / `colour_to_barcode_dyesub[]`(`mddata.c:89-145`)でバーコード値に変換。
- 特色サポート状況:
  - White: `modelHasWhite` を満たす機種のみ許可、それ以外は `-colours` 指定時にエラー(`ppmtomd.c:1539-1541`)。
  - GoldFoil/SilverFoil: `modelHasFoil`(= model5k)機種のみ許可(`ppmtomd.c:1545-1548`)。
  - MetallicSilver: Printiva600(無印)では不可(`ppmtomd.c:1542-1544`)。
  - VPhotoPrimer: `modelHasVPhotoPrimer`(model5k)のみ(`ppmtomd.c:1601-1602, 1669`)。
  - DyeSub モードでは色の差し替え(colour swapping)自体が非対応(`ppmtomd.c:1533-1536`、"Colour swapping is not supported in dyesub mode")。
  - README にも Gold/Silver Foil・White・VPhoto Primer 等の実機での定着不良に関する経験的注記あり(README:93-108、非対応リボン節)。
- **インク一覧はソースにハードコードされている**(`mddata.c` の静的配列群)。Foilwright の禁止事項「インク一覧をコードにハードコードしない」との対比で言えば、ppmtomd は反面教師であり、Foilwright は外部ファイル化する設計判断が妥当と確認できる(示唆として §4 で後述)。

### Q6: 入力形式・コマンドラインオプション一覧・ビルド方法・Windows ビルド可否

- **入力形式**: PPM(Netpbm 形式)。`pam.h`(netpbm ライブラリ)経由で読み込み(`ppmtomd.c:31, 1230`)。`-informat` オプションで入力タイプ指定可(値の詳細はソース内 `informat_table` 相当だが未追跡、リスト外)。
- **出力形式**: `-outformat` で PPM/Diag/ColourDiag 等を選択可能(`ppmtomd.c:280-283`)。既定はプリンタへの直接ストリーム(標準出力への MD コマンド列)。
- **コマンドラインオプション一覧**(`usage` 文字列および `options[]` テーブル、`ppmtomd.c:1027-1208`):
  `-autoshift`, `-black`, `-forcecurlcorrection`/`-nocurlcorrection`, `-colourcorrection`, `-colours`, `-dither`, `-draft`, `-econoblack`, `-firstpass`/`-midpass`/`-lastpass`, `-gamma`, `-glossy`, `-informat`, `-inresolution`, `-keepblack`, `-lfadjust`, `-model`, `-media`, `-monochrome`, `-noglossy`, `-nopack`, `-noreset`, `-outformat`, `-overlay`, `-pagelength`, `-pagewidth`, `-papersize`, `-phadjust`, `-primer`, `-printmode`, `-resolution`, `-satgamma`, `-spotcolours`, `-spotfile`, `-transfermode`(= `-datamode` の別名), `-undercolours`, `-usemulticolourribbon`, `-version`, `-xshift`, `-yshift`。加えて互換用 `-datamode`、ppmtocpva 互換用 `-dummy`/`-halftone`/`-pageC/K/M/Y`/`-ppmout`/`-solidblack`、非公開の `-htscreen`/`-testint`。
  オプション解析は自作の前方一致マッチング(`parse_option()`、`ppmtomd.c:3379-3392`)。
- **ビルド方法**(`Makefile:1-55`):
  - `make` で `ppmtomd.o mddata.o photocolcor.o vphotocolcor.o dyesubcolcor.o` をコンパイルし、`gcc -o ppmtomd $(OBJS) -lnetpbm -lm` でリンク(`Makefile:39-41`)。
  - コンパイルオプション: `-O2 -W -Wall -Wstrict-prototypes`(`Makefile:29, 44`)。コメントに「最適化を有効にすると浮動小数点計算のわずかな違いにより出力が変わる」と明記(`Makefile:25-28`)。
  - `make install` で `/usr/local/bin` と `/usr/local/share/man/man1` にインストール(`Makefile:16-20, 48-51`)。
  - 既定モデルは `modelDP5000`(`ppmtomd.c:43-46`)で、`DEFAULTMODEL` マクロで変更可能。
  - 依存: netpbm ライブラリ(`pam.h`, `-lnetpbm`)、POSIX ヘッダ(`unistd.h`, `fcntl.h`)、`assert.h`, `math.h`。
- **Windows(MSYS2/MinGW等)でのビルド可否の見立て【推測】**: netpbm ライブラリの MSYS2 パッケージ(`mingw-w64-x86_64-netpbm` 等)が存在すれば `pam.h`/`libnetpbm` のリンクは可能と推測される。`unistd.h`/`fcntl.h`(`open`/`read`/`write`/`lseek`/`unlink`)は MSYS2/MinGW-w64 環境でも提供されるため、大きな障害にはならないと推測する。ただし本ソースを実際にビルド・実行する検証はタスク範囲外のため未実施であり、上記は【推測】に留まる。

## 4. Foilwright 設計への示唆

- インク一覧・機種差分をコードにハードコードしている点は ppmtomd の設計上の弱点であり、Foilwright の禁止事項(インク一覧を外部ファイル化、機種差分を L1 プロファイル限定)は妥当な反面教師的判断として裏付けられる。
- MD-5000 と MD-5500 の相違について、GPL 参照実装側にも情報がない(両者とも `model5k` で同一扱い)。DOMAIN §5.2 の `max_width_dots` / `lf_correction` を推測値で埋めず実測待ちとする方針は、この調査結果からも支持される(先行実装からの転記では埋まらない)。
- MD コマンド言語(RGL: 初期化 → プレーン選択 → 行データ転送 `\033*b{n}{V/W}` → 終了)の骨格は、Foilwright の L3(PPD 解釈より奥のドット座標系)設計における最下層プロトコルの参考骨組みとして利用できる。ただし本ソースはモデル横断・多数のプリントモードを1本のコードベースに詰め込んだ設計であり、そのままの移植は DOMAIN の層分離方針(機種差分を L1 に隔離)と衝突するため、コマンド生成ロジックの直接移植ではなく「コマンド語彙・シーケンスの参考」としての利用に留めるべきと考えられる。
- 圧縮(TIFF PackBits 相当)と空白行スキップ(`\033*b{skip}Y`)は golden 採取時の期待値(実機出力バイト列)を解釈する際の重要な手がかりになる。

## 不明として残った項目

- MD-5000 と MD-5500 の実機仕様上の相違(用紙幅・LF補正等) — ppmtomd ソースからは特定不可。実機実測が必要(DOMAIN §5.2 の既定方針と一致)。
- `-informat` オプションが受け付ける値の詳細一覧(ソース中の該当テーブルを本調査では追跡していない)。
- Windows(MSYS2/MinGW)での実際のビルド可否 — 未検証(【推測】のみ)。
