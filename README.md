# Foilwright

ALPS MD シリーズプリンタ(MD-5000 / MD-5500)を 64bit Windows からネイティブに駆動する仮想プリンタシステム。

## 背景

ALPS MD シリーズの公式 Windows ドライバは 32bit 版のみで、64bit Windows では動作しない。64bit の spooler プロセスは 32bit DLL をロードできないため、これは原理的な制約であり、パッチや設定では回避できない。既存の回避策は 32bit 仮想マシンの運用か、印刷ダイアログから使えないスタンドアロンユーティリティに限られる。

Foilwright は、任意のアプリケーションの印刷ダイアログから通常のプリンタとして選択でき、白・金銀を含む特色印刷が可能な 64bit ネイティブの印刷経路を提供する。

## 特徴

- 任意のアプリケーション(Photoshop / Illustrator / ペイント等)の印刷ダイアログから通常のプリンタとして選択できる
- 白・メタリック金銀を含む特色印刷に対応する
- マジックカラー方式(原稿中の特定 RGB 値をインクプレーンに振り分ける)を採用し、機能しない場合の代替としてページ分割方式も用意する

## ステータス

**開発初期・実装未着手(設計文書のみ)。動くものはまだ無い。**

現在のリポジトリには設計ドキュメントのみが存在し、`src/`・`ref/`・`tests/golden/` は未作成。実機での動作確認も未実施。

## ロードマップ

| 段階 | 内容 | 実機リスク |
| --- | --- | --- |
| Phase 0 | 環境調査。ポート一覧と変換ケーブルのデバイス ID を取得するのみ | なし |
| Phase 1 | 5mm 角のベタ 1 色を 1 枚印刷。プロジェクトの成否を決める関門 | 最小 |
| Phase 2 | 仮想プリンタ経由でメモ帳から印刷 | 小 |
| Phase 3 | 白版を含むフルパス | 中 |
| Phase 4 | MD-5500 の USB 直結対応(Phase 3 成立後に着手) | 未定 |

外部の協力者への配布は、作者の環境で Phase 3 が成立した後に限る。

## サポート方針

想定利用者は作者自身と、ALPS MD プリンタを所有する少数の知人であり、想定台数は 10 台未満である。**エンタープライズ品質のサポートは提供しない。** 動作確認済みの構成以外は保証しない。

## ドキュメント

- [docs/DOMAIN.md](docs/DOMAIN.md) — 仕様・不変条件・実測値・経験知の正。実装上の判断で迷ったらまずここを参照する
- [docs/PROJECT.md](docs/PROJECT.md) — アーキテクチャの現在地(レイヤ構成・データフロー)
- [docs/DECISIONS.md](docs/DECISIONS.md) — 設計判断の記録(ADR-lite)

## ライセンス

GPL-3.0-or-later。

### 同梱している第三者の成果物

- **`colour/photo_colcor.bin`** — ppmtomd 1.6(J. C. Bradfield 作、GPL-2.0-or-later)の
  `photocolcor.c` に含まれる色補正テーブル。純正ドライバに近い色を出すために使う。
  出典と取り込みの根拠は [colour/README.md](colour/README.md) と DECISIONS の D-029 に記載

- **`tests/golden/*.bin`** — ppmtomd 1.6 の出力。実装の正しさをバイト単位で検証する基準。
  詳細は [tests/golden/README.md](tests/golden/README.md)

### 同梱せず、別途入手が必要

- **Ghostscript**(AGPL-3.0)— PostScript のラスタライズに使う。実行時に必要
- **Microsoft PostScript Printer Driver V3**(Windows 同梱品)— 仮想プリンタの土台
- **ppmtomd**(GPL-2.0-or-later)— golden の再生成に使う。利用するだけなら不要

## 作者

JunkQuality(GitHub: km_kuma)
