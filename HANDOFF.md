# HANDOFF — last updated: 2026-08-01

## 今どこ(現在の作業対象)

**Phase 1 未達。最有力候補は送出しているバイト列そのもの**(DOMAIN §11.1.1)。

**MD-5000(変換ケーブル)と MD-5500(USB 直結)で症状が完全に一致した。** 機体・ケーブル・接続方式のすべてが異なるのに挙動が同じ。共通するのは私が生成しているバイト列と送出方法のみ。

**前回の「原因は機械側」という判断は撤回した。** 黒リボンを交換したらセルフテストが完走した時点で、機械側の結論は崩れていた。golden との一致は「ppmtomd と同じバイト列である」ことの証明であって、**「そのバイト列で実機が動く」ことの証明ではない**。

`ref/` の実装は golden 15 種と一致し pytest 76 件が通る。**ただし実機で動く保証はまだない。**

## 完了したこと(このセッション)

- **ref/ の L1/L2 が完成。** golden 15 種とバイト一致
  - L2 の入口 3 方式(`auto` / `per_page` / `spot_only`)。D-016
  - ハーフトーン 3 方式(none / halftone / coarse_halftone)
  - 設定ファイル層(profiles / palette / papers / media)。D-013 / D-014
  - マジックカラーの判定規則を確定(D-015。整数演算のみ)
- **Phase 0 完了。** ELECOM UC-PGT を特定、双方向通信の成立を確認(§10.1.1)
- **Phase 1 第 1 回を実施 → 未達。** 送出側がシロと確定(§11.1.1)
- 外部の実践知見を 2 件記録(§10.8 / §10.9)。ホワイト 2 種類の性質、剥離を防ぐ層構成、バーコードの 2 層構造など
- DOMAIN が 0.1.0 → 0.2.19-draft、DECISIONS が D-016 まで

## 未完了 とその理由 / 詰まっている点

**Phase 1 が通っていない。バイト列を疑う段階。**

- **2 機種 x 別経路で同一症状。** MD-5000(変換ケーブル/VID 056E/USB002)と MD-5500(直結/VID 044E/USB003)のどちらも、全量送出・無反応・エラーなし
- **ppmtomd の既定オプションのまま golden を採取している。** 実運用で必須の指定が抜けている可能性(§10.10.4 のカール矯正停止など)。転送モード(colourPlane)が正しいかも未検証
- **公式ドライバの出力を捕捉できていない。** ポートを FILE: にすると保存ダイアログは出るが 0 バイト。ドライバが専用ポート MD_LPT1: を前提としており、存在確認に失敗して打ち切っているとみられる(§13.5.1)
- **エラー原因はホストから読めない**(§11.4 の壁)。ReadPrinter は Win32 error 6、ポート直接オープンは usbprint.sys の排他保持で失敗
- **ベースドホワイトの色選択コードが不明**(§11 #11)。MD-5000 用ドライバには選択肢が存在しないことを確認済み。品番は MDC-OPWH

## 次セッションの推奨着手点

**最優先: 公式ドライバの出力バイト列を手に入れる。** これがあれば推測なしで差分を見られる。

1. **仮想 PC で ALPS のポートモニタを探す。** プリンタのプロパティ → ポート → ポートの追加 の一覧に ALPS 系の項目があるか。あれば MD_LPT1: を作成し、そのポート指定で FILE: 出力を再試行
2. 見つからなければ**ドライバ一式の再インストール**(ポートモニタが同梱されているはず)。インストール作業なので落ち着いた状態でやること
3. それでも取れない場合、ppmtomd のオプションを変えた golden を採って実機に送る総当たり。ただし消耗品を消費するため優先度は低い

**実機に触れずに進められる作業:**

- ppmtomd の `-nocurl` で golden を採り、カール補正コマンドの差分を確認(§10.10.4)。WSL があればできる
- `src/`(C#)の着手。golden 15 種があるので ref/ と同じ検証ができる
- PPD の作成(§9.4 は Phase 1 成立まで作り込まない方針。判断が要る)

## このセッションで下した設計判断

D-013(YAML)/ D-014(用紙表の分離)/ D-015(色マッチング)/ D-016(インク指定 3 方式)
→ DECISIONS.md に追記済み

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
