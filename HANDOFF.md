# HANDOFF — last updated: 2026-07-29

## 今どこ(現在の作業対象)

**Phase 1 未達。原因は機械または消耗品側にあると確定した**(DOMAIN §11.1.1)。

決定打はセルフテスト — ホストを一切介さない経路で実行しても途中停止し、**黒が印刷されない**ことが判明した。**ホスト側の実装を修正しても症状は変わらない。**

`ref/` の実装は一通り完成しており、pytest 76 件が通る。**ソフトウェア側でやるべきことは残っていない。**

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

**Phase 1 が通っていない。原因は機械側。**

- 送出側は完全にシロ。5 パターン(黒/排出のみ/白紙/テキスト/シアン)を 2 経路で送出し、内容・データ量・経路に関係なく同一症状
- **セルフテストが途中停止。黒が印刷されない** → 機械または消耗品の問題
- **エラー原因はホストから読めない**(§11.4 の壁に実際に当たった)。`ReadPrinter` は Win32 error 6、ポート直接オープンは `usbprint.sys` の排他保持で失敗
- **ベースドホワイトの色選択コードが不明**(§11 #11)。実物の入手待ち

## 次セッションの推奨着手点

**機械側の解決が先。ソフトウェアでは進まない。**

1. **黒カートリッジの確認・交換**。他の色は印字されるため黒固有の問題の可能性が高い
2. セルフテストが完走するまで機械を整える。**完走が Phase 1 再試行の前提条件**
3. 改善しない場合はヘッドの状態を疑う(§10.9.5 に 6 年使用で交換した外部事例。費用約 8 万円)

**機械が直らなくても進められる作業:**

- `src/`(C#)の着手。golden 15 種があるので ref/ とまったく同じ検証ができ、実機は不要
- PPD の作成(ただし §9.4 は Phase 1 成立まで作り込まない方針。判断が要る)

## このセッションで下した設計判断

D-013(YAML)/ D-014(用紙表の分離)/ D-015(色マッチング)/ D-016(インク指定 3 方式)
→ DECISIONS.md に追記済み

## 触ると危険な箇所 / 現在の一時的回避策

- `tests/golden/*.bin` は基準。**絶対に書き換えない**
- `tools/send-raw.ps1` は **UTF-8 BOM + CRLF が必須**。PowerShell 5.1 が BOM なしを Shift_JIS として読み、here-string は CRLF でないと解釈されない
- プリンタキュー `Foilwright-Test`(Generic / Text Only / USB002)を作成済み。不要なら `Remove-Printer` で削除可
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
