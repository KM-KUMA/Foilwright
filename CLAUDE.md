# PROJECT: Foilwright / ALPS MD-5000/MD-5500 を 64bit Windows から駆動する仮想プリンタシステム

## 環境

- ref/: Python 3.x — 参照実装(恒久維持)。.venv あり、ruff 導入済み
- src/: C# — 本番実装
- 現状: 実装未着手(2026-07-27 時点)。src/・ref/・tests/golden/ は未作成
- 起動コマンド: 未定(実装着手時に確定)

## 鉄則

1. 実装前に docs/PROJECT.md の該当セクションと docs/DOMAIN.md を読む
2. 設計に関わる変更(新規依存・公開API変更・ファイル構成変更・アルゴリズム選定)は
   実装せず、選択肢とトレードオフを提示して停止する
3. docs/DECISIONS.md に反する実装をしない。矛盾を見つけたら指摘して停止
4. 不明点を推測で埋めない。埋めた場合は回答冒頭に【推測】と明記
5. 本書と実装が矛盾する場合は docs/DOMAIN.md が正(DOMAIN §0)

## 禁止事項(DOMAIN の不変条件から昇格)

- 機種による分岐を L1 プロファイルの外に書かない(DOMAIN §4.4)
- mm を L3 入口(PPD 解釈)より奥に持ち回らない。内部座標は常にドット(DOMAIN §4.1)
- インク一覧をコードにハードコードしない。必ず外部ファイルから読む(DOMAIN §4.5)
- 機種プロファイルの null(lf_correction / max_width_dots)を推測値で埋めない(DOMAIN §5.2)
- Phase 1 成立前に UI・トレイアプリ・PPD を作り込まない(DOMAIN §9.4 / §11.2)

## 参照マップ

| タスク種別 | 必読 |
| --- | --- |
| 仕様・用語・不変条件の確認 | docs/DOMAIN.md(仕様と経験知の正) |
| アーキテクチャ・レイヤ境界 | docs/PROJECT.md |
| 新機能・設計変更 | docs/DECISIONS.md 全体 |
| セッション開始 | HANDOFF.md |
