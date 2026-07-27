# HANDOFF — last updated: 2026-07-27

## 今どこ(現在の作業対象)

ドキュメント整備フェーズ完了。実装(コード)は未着手。src/・ref/・tests/golden/ 未作成。

## 完了したこと(このセッション)

- docs/PROJECT.md 初版作成(fable-framework 雛形準拠。実装未着手を明記)
- docs/DECISIONS.md に D-001〜D-010 を正式登録+索引更新(DOMAIN §3〜§12 の本文から抽出。APPENDIX 節は DOMAIN に存在しなかった)
- README.md 初版(背景/特徴/ステータス/ロードマップ/サポート方針/ドキュメント/ライセンス/作者。実装未着手を明示)
- CLAUDE.md を L0 雛形で初版化(環境/鉄則/禁止事項/参照マップ)

## 未完了 とその理由 / 詰まっている点

- 実装は全面未着手(このセッションのスコープ外)

## 次セッションの推奨着手点

1. Phase 0(環境調査: ポート一覧・変換ケーブルのデバイス ID 取得)または ref/ の骨組み着手。ただし DOMAIN §9.4: Phase 1 成立前に UI・トレイ・PPD の作り込み禁止

## このセッションで下した設計判断

- 新規の設計判断なし(D-001〜D-010 は DOMAIN 既存決定の転記であり新規判断ではない)
- → DECISIONS.md への追記状況: 転記完了(D-001〜D-010)

## 触ると危険な箇所 / 現在の一時的回避策

- tests/fixtures/ と docs/DOMAIN.md.bak は保護パス(.claude/protected_paths.txt)
- DOMAIN.md §5.2: プロファイルの null(lf_correction / max_width_dots)を推測値で埋めない
- 運用注意: 完了直前の subagent への SendMessage はキュー消失しうる(このセッションで1回発生。CLAUDE.md タスクが未着手のまま消え、親が直接実装した)

## 検証コマンド(このプロジェクトで実際に使った検証手順)

- `git -C e:/build/Foilwright status --short` / `git diff --stat` — 変更実態の照合
- ドキュメントのみのセッションのためテストコマンドなし。markdownlint は IDE 診断で確認(表の区切りは `| --- |` スタイル)
