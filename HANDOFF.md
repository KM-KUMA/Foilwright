# HANDOFF — last updated: 2026-07-29

## 今どこ(現在の作業対象)

**Phase 1(実機で 5mm 角を刷る)が未達。** プリンタが無反応。原因は本体側か USB の印刷データ経路のどちらかに絞られている(DOMAIN §11.1.1)。

`ref/` の実装は一通り完成しており、pytest 76 件が通る。

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

**Phase 1 が通っていない。** 4 パターン送っていずれも無反応・エラーなし。

- 送出側は完全にシロ(自作ツール・Windows 標準経路の両方で同一症状、データ量無関係)
- **セルフテストの手順が不明**。所持マニュアル(106 ページ版)に本体操作の章がない
- **ベースドホワイトの色選択コードが不明**(§11 #11)。実物の入手待ち

## 次セッションの推奨着手点

1. **プリンタ本体のセルフテスト**。機械側か通信側かを確定させる。手順の入手が前提
2. 本体操作を含む完全版マニュアルの入手(現在のものは「各種印刷操作」と「プリンタドライバ」の章のみ)
3. それでも進まない場合、MD の運用経験者に確認

**実機が動かない間も進められる作業:**

- PPD の作成(ただし §9.4 は Phase 1 成立まで作り込まない方針)
- `src/`(C#)の着手。golden があるので ref/ と同じ検証ができる

## このセッションで下した設計判断

D-013(YAML)/ D-014(用紙表の分離)/ D-015(色マッチング)/ D-016(インク指定 3 方式)
→ DECISIONS.md に追記済み

## 触ると危険な箇所 / 現在の一時的回避策

- `tests/golden/*.bin` は基準。**絶対に書き換えない**
- `tools/send-raw.ps1` は **UTF-8 BOM + CRLF が必須**。PowerShell 5.1 が BOM なしを Shift_JIS として読み、here-string は CRLF でないと解釈されない
- プリンタキュー `Foilwright-Test`(Generic / Text Only / USB002)を作成済み。不要なら `Remove-Printer` で削除可
- WSL が頻繁に落ちる(HCS タイムアウト)。`wsl --shutdown` → 管理者 PowerShell で `Restart-Service vmcompute` の順で復旧
- **ジョブが `Retained` で残る**。次の送出前に `Remove-PrintJob` で消さないと切り分けが濁る

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
