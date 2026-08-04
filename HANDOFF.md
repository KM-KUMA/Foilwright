# HANDOFF — last updated: 2026-08-04 (3)

## 今どこ(現在の作業対象)

**Phase 1 成立。`ref/` の出力が実機 MD-5500 で印刷された(2026-08-04)。**

**USB の転送プロトコルを実測で解読した(DOMAIN §15)。** バルクエンドポイント上に ALPS 独自のパケット層があり、RGL の生ストリームは受け付けない。`05 ff`(送信要求)→ `06`(許可)→ `02 01 {len-1} {最大32764B}`(データ)→ `06`(受理)の往復で運ぶ。実装は `tools/alps_send.py`。**ppmtomd 形式の RGL がそのまま通ったため L1 / L2 は無変更でよい。** D-018 採択。

**カセット状態の読み出しも同経路で解決(§11.4 / §11 #9)。** `GET_DEVICE_ID`(制御 0xA1,0)→ `05 01` → 38 バイト(ヘッダ 5 + 11 スロット×3、スロット先頭がバーコード番号)。ほかに `05 02`(機体情報 256B)/ `05 03` / `05 04`。

**実機作業の作法(重要):**

- バルク IN を読んでよいのは**応答を返すコマンドの直後だけ**。空読みするとインターフェースがウェッジする
- ウェッジからの回復は `usbipd detach` → `attach`(物理再接続は不要。約 10 秒)
- 手順: `tools/usbip-bind.cmd` を管理者で実行(USBPcap 導入後は `--force` 必須)→ `usbipd attach --busid 1-2 --wsl` → WSL を起こしたまま `wsl -u root` で送出
- USBPcap 採取は `tools/capture-usb.cmd` / `capture-usb2.cmd` を管理者で実行。**古い 0 バイトの pcap が残っていると "invalid write handle" で失敗する**ので先に消す。バッチは ASCII のみ(日本語コメントは cp932 で化ける)

**多色刷りも実機で成立(§15.5)。** CMYK 4 色ジョブ(色選択 4 + バックフィード 3)を送出し、印刷中のステータス追跡で**カセットが C→M→Y→K の順に入れ替わるのを実観測**。ジョブ指定順と一致。あわせて状態応答の 9 番目のレコード = **ヘッドに装着中のカセット**(Holder Position)と判明。

**印刷ダイアログ経路も成立(§3.5.2 / D-022)。** ただし当初計画からの変更あり — **Windows 11 は第三者製プリンタドライバの導入を拒否する**(`0x80070BC6`。署名しても不可、in-box は可。§3.5.1)。独自 PPD の同梱を断念し、in-box の `MS Publisher Color Printer` を間借りする構成に変更した。**印刷ダイアログのオプションはトレイアプリの UI へ移設**(§7.1 は撤回)。

現在この PC には仮想プリンタ **「Foilwright MD-5500」** が存在する(ポートは開発用に `dumps\spool.ps` へ固定出力)。アプリから印刷 → PostScript → Ghostscript → PPM → L2 のプレーン分離まで実証済みで、**マジックカラーは経路を通っても 1 ビットも変質しない**(中間色の混入も皆無)。

**次にやること:**

1. **変換器の実装** — Ghostscript を呼び、L2 でプレーンを作り、L1 でジョブを組み、キューフォルダへ置く常駐処理。ここが未実装の本体
2. **ポートモニタ(mfilemon)の導入** — 現在は固定ファイルへの書き出しで代用している
3. **トレイアプリ** — 設定 UI(D-022 で PPD から移設)+ プレビュー + 交換指示 + 送出
4. **残る未検証パスの実機確認**(特色ホワイト = カセット未装填で保留 / カール補正 / メディア種別)。版ずれの定量測定も未実施(§11.5 の判断材料)
2. **ベースドホワイトの色選択コード確定**(§11 #11)。候補は 0x1c / 0x1e。MD-5500 ドライバでベースドホワイトを有効にして印刷し USBPcap で採取すれば実測できる — **もはや FILE: 出力の捕捉に頼る必要はない**
3. **状態バイトの意味を特定**(応答 5 バイト目。送出前 00 → 実行中 09 → 完了 01、過去に 10 も観測)。用紙・カバーを操作しながら採取する
4. src/(C#)への移植。L0 は D-018 / §15 の仕様どおり実装する

**参考資料:** 純正ドライバのフルカラー印刷 1 枚分の RGL(7.26MB)を再構成済み(scratchpad の `official_print.rgl`、pcap は `dumps/vm_print2.pcap`)。L1 の答え合わせに使える。

`ref/` は golden 15 種と一致し pytest 76 件が通る。**実機印刷でも成立を確認済み(単色 blackRaster / 多色 colourPlane)。**

**未解決の設計論点:** `palette/default.yaml` に素の C/M/Y が無い(特色系と黒のみ)。auto 方式の CMYK フォールバックが使うインクも設定ファイルから読む必要があるが(§4.5)、単純追加すると `magic_rgb` マッチングの対象になり auto の挙動が変わる。**特色インクとプロセスインクの区別をスキーマに入れるかが未決**(実装前に判断が要る)。

## 完了したこと(このセッション)

- **ref/ の L1/L2 が完成。** golden 15 種とバイト一致
  - L2 の入口 3 方式(`auto` / `per_page` / `spot_only`)。D-016
  - ハーフトーン 3 方式(none / halftone / coarse_halftone)
  - 設定ファイル層(profiles / palette / papers / media)。D-013 / D-014
  - マジックカラーの判定規則を確定(D-015。整数演算のみ)
- **Phase 0 を 2 機種で完了。** MD-5000 + ELECOM UC-PGT(§10.1.1)と MD-5500 の USB 直結(§10.1.0)。どちらも双方向通信が成立
- **§11.3 / §11 #8 が解決。** MD-5500 の USB 直結は標準 `usbprint.sys` が掴む「楽なケース」で、L0 の追加実装は不要
- **Phase 1 を 2 機種で実施 → いずれも未達。** 症状が完全に一致したため、原因の見立てを機械側からバイト列へ変更(§11.1.1)
- 外部の実践知見を 3 件記録(§10.8 / §10.9 / §10.10)。ホワイト 2 種類の性質、剥離を防ぐ層構成、バーコードの 2 層構造、サードパーティインクの運用など
- **日本語版マニュアルを入手し §13.5 に記録。** 公式ドライバが専用ポート `MD_LPT1:` を使う設計であること、国内版のインク名称が海外版と異なること(palette の label を修正)など
- DOMAIN が 0.1.0 → 0.2.27-draft、DECISIONS が D-017 まで

## 未完了 とその理由 / 詰まっている点

**Phase 1 が通っていない。バイト列を疑う段階。**

- **2 機種 x 別経路で同一症状。** MD-5000(変換ケーブル/VID 056E/USB002)と MD-5500(直結/VID 044E/USB003)のどちらも、全量送出・無反応・エラーなし
- **転送モードは原因ではない(検証済み)。** blackRaster(0x00)で送っても変わらなかった
- **MD-5500 が異音で停止中。** セルフテスト実行時に発生。原因未特定。リボンを外すとエラーは解消した
- **公式ドライバの出力を捕捉できていない。** ポートを FILE: にすると保存ダイアログは出るが 0 バイト。ドライバが専用ポート MD_LPT1: を前提としており、存在確認に失敗して打ち切っているとみられる(§13.5.1)
- **エラー原因はホストから読めない**(§11.4)。3 経路とも未達 — ReadPrinter は Win32 error 6、ポート直接オープンは失敗、**usbprint.sys の IOCTL は開けるが値が固定**(異音時・リボン除去後・再接続後・カバー開放時のすべてで 0x01)。ただし **CreateFile でデバイスを開けた**のは成果で、L0 の作り直しは不要かもしれない
- **汎用ドライバ経由では Windows もエラーを検知しない。** カバー開放時も DetectedErrorState=2(エラーなし)のまま
- **ベースドホワイトの色選択コードが不明**(§11 #11)。MD-5000 用ドライバには選択肢が存在しないことを確認済み。品番は MDC-OPWH

## 次セッションの推奨着手点

**最優先: USB 2.0 ハブを入手して挟む(PC → ハブ → MD-5500)。** 手持ちのどんなハブでも試す価値がある(ルートポート直結を避けるだけで意味がある)。効かなければ別マシン(Windows 10 機 / Raspberry Pi 等の素の Linux)からの送出が決定打。

接続後の判定は `tools/write-direct.ps1`(紙は減らない):

```powershell
powershell -ExecutionPolicy Bypass -Command "& 'E:/build/Foilwright/tools/write-direct.ps1' -Path 'E:/build/Foilwright/dumps/phase1_blackraster.bin' -VidMatch 'VID_044E'"
```

`WRITE_OK` が出れば経路開通。そのまま印刷が始まる可能性もある(黒リボン装着済みなら)。

**usbipd-win は導入済み。** 使い方(`usbipd.exe` は Program Files 配下、フルパス実行):

- `list` で BUSID 確認(MD-5500 = 1-2、bind 済み)
- `attach --wsl --busid 1-2` の前に **WSL を起動しておく**(`wsl -e sleep 180` をバックグラウンドで)
- Linux 側は python3-usb 導入済み。**バルク失敗後は物理再接続が必要**
- 終わったら `detach --busid 1-2` で Windows に返す

補足の課題(優先度低): 公式ドライバの出力捕捉(ALPS ポートモニタで `MD_LPT1:` を作る)/ 状態読み出し IOCTL の調べ直し(現在は値固定で機能せず。§11.4)/ MD-5500 の異音の原因特定

**実機に触れずに進められる作業:**

- **golden の正式採取**(WSL 復旧待ち)。`make_golden.sh` に g16(blackRaster)/ g17(nocurl)を追記済み。手元の `dumps/probe/` に ppmtomd 出力があり ref/ と一致済みなので、採取すればテストを追加できる
- `src/`(C#)の着手。golden 15 種があるので ref/ と同じ検証ができる
- PPD の作成(§9.4 は Phase 1 成立まで作り込まない方針。判断が要る)

## このセッションで下した設計判断

D-013(YAML)/ D-014(用紙表の分離)/ D-015(色マッチング)/ D-016(インク指定 3 方式)/
D-017(先行機を MD-5500 の USB 直結へ変更。D-012 を差し替え)
→ DECISIONS.md に追記済み

2026-08-03 は設計判断なし(調査と実装のみ)

## 触ると危険な箇所 / 現在の一時的回避策

- `tests/golden/*.bin` は基準。**絶対に書き換えない**
- `tools/send-raw.ps1` は **UTF-8 BOM + CRLF が必須**。PowerShell 5.1 が BOM なしを Shift_JIS として読み、here-string は CRLF でないと解釈されない
- プリンタキューを 2 つ作成済み。不要なら `Remove-Printer` で削除可
  - `Foilwright-Test`(Generic / Text Only / USB002 / MD-5000 + 変換ケーブル)
  - `Foilwright-MD5500`(Generic / Text Only / USB003 / MD-5500 直結)
- WSL が頻繁に落ちる(HCS タイムアウト)。`wsl --shutdown` → 管理者 PowerShell で `Restart-Service vmcompute` の順で復旧
- **ジョブが `Retained` で残る**。次の送出前に `Remove-PrintJob` で消さないと切り分けが濁る
- **DOMAIN の変更履歴は必ずファイル末尾に追記**。表の途中に挿入すると版の順序が崩れる(今回 2 回やり直した)
- **bash 経由で PowerShell/Python に文字列を渡すとバッククォートが展開される**。識別子を含む文書を書くときは Edit ツールを使う(コミットメッセージで 1 回被害)
- **バックスラッシュを含む文字列(デバイス ID など)を bash 経由の Python heredoc で書かない。** `\6` が 8 進エスケープとして解釈され制御文字 `\x06` になる事故が発生(2026-08-01)。表示上は見えないため気づきにくい。**Edit ツールを使うか、`chr(92)` で組み立てる**
- **`git add -A` を使わない。** 追加するファイルを明示すること。2026-08-01 に著作物(MD-5000 マニュアル PDF)を公開リポジトリへ誤コミットし、`git filter-repo` + force push で履歴から除去する事故を起こした。`.tokensave/` も同じ原因で一度混入しかけている。**公開リポジトリでは事故の代償が大きい**(DOMAIN §12.6 / §12.7)
- **履歴を書き換える前に必ず `git bundle create` でバックアップを取る。** `--force` は防護フックが止めるので `--force-with-lease` を使う。filter-repo はリモート設定を削除するため、実行後に `git remote add` と `git fetch` が必要

## 検証コマンド(このプロジェクトで実際に使った手順)

```powershell
.venv\Scripts\python.exe -m pytest ref/tests/ -v     # 76 件
.venv\Scripts\ruff.exe check ref/
wsl -e bash tests/cases/make_golden.sh               # golden 再生成(WSL 必須)
```

実機への送出(Phase 1):

```powershell
powershell -ExecutionPolicy Bypass -Command "& 'E:/build/Foilwright/tools/send-raw.ps1' -Path 'E:/build/Foilwright/dumps/phase1_5mm_black.bin' -PrinterName 'Foilwright-Test'"
```

`-WhatIf` を付けると送信せず内容確認のみ。
