// Foilwright.Tray — ジョブ 1 件のプレビュー画面(DOMAIN §7.2)。
//
// 必須機能をここに集約する:
//   - インク割り当てプレビュー(色分け表示)
//   - ジョブ内容の表示(パス数・使用インク・順序)
//   - 設定の変更(インク指定方式・機種)。変更したら再描画する
//   - 印刷開始・取り消しボタン
//   - プリンタ状態の表示枠(今回は生の値のみ。デコーダは別エージェントが実装中)
//
// 変換(Ghostscript 呼び出しは約 2 秒、PPM は A4/600dpi で約 100MB)と送出は
// すべてワーカースレッドで行い、UI をブロックしない。
//
// 送出中(§15.2.1 の排他所有)は _busy フラグで機種・インク指定方式の変更、
// 状態問い合わせ、印刷ボタンの多重クリックをすべて禁止する。

using System.Drawing;
using System.Windows.Forms;
using Foilwright.Core;

namespace Foilwright.Tray;

public sealed class PreviewForm : Form
{
    private readonly string _psPath;
    private readonly TraySettings _settings;
    private readonly string _assetRoot;

    // 設定のプリセット: 用途ごとに「設定一式」へ名前を付けて保存し、一発で呼び出す。
    // 「この設定を既定値として保存」(_saveDefaultsButton)とは別物として併存する —
    // 既定値は「何も選ばなかったときの初期値」で 1 組しか持てないが、プリセットは
    // 何組でも持てる(用途を行き来しても上書きにならない)。
    private readonly ComboBox _presetCombo;
    private readonly Button _savePresetButton;
    private readonly Button _deletePresetButton;

    /// <summary>プリセットと既定値の違いを画面上で伝えるためのツールチップ。
    /// フィールドに持つのは、Component をローカル変数のままにすると回収されて
    /// ツールチップが出なくなるため。</summary>
    private readonly ToolTip _presetToolTip;

    private readonly ComboBox _machineCombo;
    private readonly ComboBox _inkModeCombo;
    private readonly ComboBox _resolutionCombo;
    private readonly ComboBox _paperCombo;
    private readonly ComboBox _mediaCombo;
    private readonly ComboBox _halftoneCombo;
    private readonly ComboBox _whiteModeCombo;
    private readonly ComboBox _colourCorrectionCombo;
    private readonly CheckBox _noCurlCheck;
    private readonly Button _saveDefaultsButton;
    private readonly PictureBox _previewBox;

    /// <summary>プレビューにインクを 1 つだけ表示するための選択(DOMAIN §7.2)。
    /// 全インクが重なったままでは「白がどこに乗るのか」「金が意図しない所を
    /// 拾っていないか」が見えず、マジックカラーの誤爆を発見できない。
    /// **見せ方だけの機能であり、ジョブの中身は 1 バイトも変わらない。**</summary>
    private readonly ComboBox _inkFilterCombo;

    /// <summary>表示を 1 インクに絞っているあいだだけ出す注意文。絞ったまま印刷ボタンを
    /// 押した利用者が「これだけ刷られる」と誤解すると、代替入手の困難なリボンと用紙を
    /// 失う(§7.2)。文言は BuildInkFilterNotice(純粋な処理)が作る。</summary>
    private readonly Label _inkFilterNoticeLabel;

    /// <summary>1 インクだけを描き直した画像。この画面が所有する。
    /// **二重破棄を避けるため、ここに _current.Preview(PreviewResult が所有し
    /// PreviewResult.Dispose が捨てるもの)を絶対に入れない。**
    /// 全インク表示へ戻すときは、これを破棄して null にしたうえで
    /// _previewBox.Image に _current.Preview を入れ直す。</summary>
    private Bitmap? _filteredPreview;

    /// <summary>インクの選択肢を作り直している最中かどうか(_applyingPreset と同じ流儀)。
    /// 項目の入れ替えは SelectedIndexChanged を発火させるため、そのあいだは描き直さない。</summary>
    private bool _populatingInkFilter;

    /// <summary>ジョブ内容のグリッドを作り直している最中かどうか(_populatingInkFilter と
    /// 同じ流儀)。行を足してセルへ値を入れる操作は CellValueChanged を発火させるが、
    /// それは利用者の操作ではないので組み直しの引き金にしてはならない。</summary>
    private bool _populatingInkGrid;

    private readonly Label _jobSummaryLabel;
    private readonly DataGridView _inkGrid;

    // D-042: マジックカラーの選択・既定への復帰・色の重複警告。
    private readonly Button _pickColorButton;
    private readonly Button _resetColorButton;
    private readonly Button _resetAllColorsButton;
    private readonly Label _magicRgbWarningLabel;

    /// <summary>警告ラベルの Control.Name。レイアウトの検出器から
    /// Controls.Find で引くためだけの名前(画面には出ない)。</summary>
    internal const string WarningLabelName = "WarningLabel";

    private readonly TextBox _statusText;
    private readonly Button _statusRefreshButton;

    /// <summary>リボン消費の記録(usage.jsonl)を見る窓を開くボタン。プリンタの
    /// 残量応答は意味が未解明で当てにならない(DOMAIN §11.4.3)ため、刷ったドット数を
    /// 自分で数えて貯めたものを見せる。状態の枠に置くのは、利用者にとって
    /// 「あとどれだけ刷れるか」を考える場面が同じだから。</summary>
    private readonly Button _usageButton;

    private readonly ProgressBar _progressBar;
    private readonly Button _printButton;
    private readonly Button _cancelButton;

    /// <summary>D-044: 部数。ドライバ側の部数は使えない(2 以上にすると PScript5 が
    /// 複数ページの PostScript を吐き、D-043 のエラーで止まる)ため、Foilwright 側で持つ。
    /// 「設定(このジョブに適用)」の枠には置かない — あの枠には「この設定を既定値として
    /// 保存」ボタンがあり、部数が保存されると誤解を招く(D-044 決定 3: 部数は保存しない)。
    /// 下端のボタン列に置くことで、構造的に保存の対象にならないようにしてある。</summary>
    private readonly NumericUpDown _copiesUpDown;

    /// <summary>D-044 改訂: 1 部刷り終わるたびに止まって人の確認を待つか。
    /// 既定は ON。OFF にすると部数ぶん続けて送る(自動給紙が使えるとき向け)。</summary>
    private readonly CheckBox _stopBetweenCopiesCheck;

    // D-038: 送出後、印刷が終わるまでプレビューを開いたまま見張る枠。
    private readonly GroupBox _monitorGroup;
    private readonly Label _monitorStatusLabel;
    private readonly Button _monitorAbortButton;
    private readonly Button _monitorCloseButton;

    private PreviewResult? _current;
    private bool _busy;

    /// <summary>保存済みのプリセット一覧(presets.json の中身)。名前順。
    /// 追加・削除のたびに <see cref="PresetStore"/> の純粋関数で作り直した
    /// 新しいリストを入れ直す(その場で書き換えない)。</summary>
    private List<SettingsPreset> _presets;

    /// <summary>プリセットの中身をコンボ群へ流し込んでいる最中かどうか。
    /// コンボを 1 つ書き換えるたびに SelectedIndexChanged が
    /// RefreshPreviewAsync(Ghostscript の再実行を含む)を呼んでしまうため、
    /// 流し込みのあいだは抑え、全部書き終えてから**一度だけ**再構成する。
    /// プリセットのコンボ自身を作り直すときにも使う(作り直しで「選ばれた」と
    /// みなして適用が走らないようにするため)。</summary>
    private bool _applyingPreset;

    /// <summary>プリセットのコンボの先頭に置く項目。起動時はこれが選ばれている
    /// (= プリセットを一度も使わない人の画面と挙動は今までどおり)。</summary>
    private const string NoPresetText = "(選択なし)";

    /// <summary>プリセットのコンボの 1 項目。表示は名前、実体はプリセット
    /// (先頭の「(選択なし)」だけ Preset が null)。名前をそのままコンボへ入れると、
    /// 「(選択なし)」という名前のプリセットと見分けが付かなくなるため包む。</summary>
    private sealed record PresetItem(SettingsPreset? Preset, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>表示するインクのコンボの先頭に置く項目(= 絞り込みなし)。
    /// プレビューを組み直すたびにここへ戻す。</summary>
    private const string AllInksText = "すべてのインク";

    /// <summary>表示するインクのコンボの 1 項目。表示は label、実体はインク名
    /// (先頭の「すべてのインク」だけ Name が null)。label をそのままコンボへ
    /// 入れると選択からインク名を引き直せなくなるため包む(PresetItem と同じ流儀)。</summary>
    private sealed record InkFilterItem(string? Name, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>D-038: 送出後の見張りが進行中(まだ完了/エラー/打ち切り/上限時間の
    /// いずれにも達していない)かどうか。見張りが終わるまでプレビューを閉じない
    /// ため、OnFormClosing がこれを見て閉じるのを止める。</summary>
    private bool _monitoring;

    /// <summary>D-038: 見張りループを打ち切るためのトークン。「見張りを中止」ボタンが
    /// これを Cancel する — 印刷そのものは止めない。見張りをやめるだけ。</summary>
    private CancellationTokenSource? _monitorCts;

    /// <summary>D-044: 部数として受け付ける範囲。打ち間違いで生産終了品のリボンを
    /// 大量に失わないよう上限を設ける(パス数の 1〜8 と同じ方針)。</summary>
    /// <summary>正常終了してから窓が自分で閉じるまでの時間(ミリ秒)。
    /// 当初 3 秒だったが、刷り終わった直後に消費や状態を確かめる間が無く、
    /// 実際に 3 回続けて取り逃がした(2026-08-22)。15 秒へ延ばした。</summary>
    private const int AutoCloseDelayMs = 15_000;

    private const int MinCopies = 1;

    /// <summary>D-044 改訂(2026-08-21): 部数の上限は設けない。当初は打ち間違い対策に
    /// 20 を上限としていたが、利用者の判断で撤廃した。歯止めは確認ダイアログが
    /// 部数を読み上げること(BuildPrintConfirmText)と、既定で 1 部ごとに止まることに任せる。</summary>
    private const int MaxCopies = int.MaxValue;

    /// <summary>D-044: 今何部目を刷っているか(1 始まり)と、全部で何部か。
    /// 見張りの表示に「N 部目 / 全 M 部」を出すためだけに使う。部数が 1 のときは
    /// 表示を一切変えない(1 部しか刷らない人の画面を変えないため)。</summary>
    private int _copyIndex = 1;
    private int _copyTotal = 1;

    /// <summary>D-044: まだ刷っていない部が残っているか。部と部のあいだ(見張りは
    /// 終わったが次の部をまだ送っていない状態)を表す。窓を閉じさせないためと、
    /// 「閉じる」ボタンを出さないための判定に使う。</summary>
    private bool HasRemainingCopies => HasRemainingCopiesFor(_copyIndex, _copyTotal);

    /// <summary>D-044: 「まだ刷っていない部が残っているか」の判定(画面に触らない
    /// 純粋な処理。ここが検出器になるよう切り出してある)。
    ///
    /// これが true のあいだは窓を閉じさせない。**印刷が終わったら必ず false に
    /// 戻ること**が要で、戻し忘れると窓が二度と閉じられなくなる — PrintAsync の
    /// finally が全部の抜け道(完走・エラー・中止・例外)で 1 に戻している。</summary>
    internal static bool HasRemainingCopiesFor(int copyIndex, int copyTotal) =>
        copyTotal > 1 && copyIndex < copyTotal;

    /// <summary>D-030: このジョブで使うインクの許可リスト(name の集合)。
    /// TraySettings.ResolveUsedInks で解決した既定値を初期状態とし、以後は
    /// プレビューのチェック列(D-028 の UI をそのまま使う)がジョブごとの
    /// 上書きとして書き換える。SaveAsDefaults を押さない限り TraySettings
    /// には反映しない。</summary>
    private readonly HashSet<string> _usedInks;

    /// <summary>D-031: パス数(重ね塗り回数)のジョブごとの上書き(ink 名 → 回数)。
    /// TraySettings.PassesOverride を初期状態とし(null なら空辞書、= 上書き無し)、
    /// 以後はプレビューの「パス数」列がジョブごとの上書きとして書き換える。
    /// SaveAsDefaults を押さない限り TraySettings には反映しない。ここに無い
    /// インクは JobPipeline がパレットの既定値(InkDefinition.Passes)を使う。</summary>
    private readonly Dictionary<string, int> _passesOverride;

    /// <summary>D-042: マジックカラーのジョブごとの上書き(ink 名 → RGB 3 値、
    /// null は「色を外す」)。TraySettings.MagicRgbOverride を初期状態とし
    /// (null なら空辞書、= 上書き無し)、以後はプレビューの「色」列がジョブごとの
    /// 上書きとして書き換える。SaveAsDefaults を押さない限り TraySettings には
    /// 反映しない。ここに項目が無いインクはパレットの magic_rgb をそのまま使う。</summary>
    private readonly Dictionary<string, int[]?> _magicRgbOverride;

    /// <summary>D-048: 塗る範囲のジョブごとの指定(ink 名 → "none" / "artwork" / "full")。
    /// TraySettings.CoverageModes を初期状態とし(null なら空辞書、= どのインクも
    /// 塗らない)、以後はプレビューの「塗る範囲」列が書き換える。SaveAsDefaults を
    /// 押さない限り TraySettings には反映しない。ここに無いインクは既定の
    /// "none"(= プレーンを作らない)として扱う。</summary>
    private readonly Dictionary<string, string> _coverageModes;

    /// <summary>_usedInks の既定値を解決するために先読みしたパレット
    /// (palette/default.yaml)。機種・メディア・用紙を変えても不変
    /// (パレットはこれらに依存しない)。</summary>
    private readonly List<InkDefinition> _palette;

    /// <summary>メディア種別コンボの 1 項目。表示は label(§5.5.2)、実体は name。</summary>
    private sealed record MediaItem(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>用紙コンボの 1 項目。用紙表(papers/{系列}.yaml)には label が無いため
    /// (メディア種別と違い日本語ラベルを持たない。§5.5)、表示は name をそのまま使う。</summary>
    private sealed record PaperItem(string Name)
    {
        public override string ToString() => Name;
    }

    public PreviewForm(string psPath, TraySettings settings)
    {
        _psPath = psPath;
        _settings = settings;
        _assetRoot = AssetRoot.ResolveDefault();

        // D-030: パレットは機種・メディア・用紙に依存しないため、ここで
        // 一度だけ読み、許可リストの既定値解決に使う。
        _palette = ConfigLoader.LoadPalette(Path.Combine(_assetRoot, "palette", "default.yaml"));
        _usedInks = settings.ResolveUsedInks(_palette);
        // D-031: null(一度も触っていない)は空辞書として扱う — 空辞書は
        // 「このジョブでは上書き無し」を意味し、パレットの既定値がそのまま使われる。
        _passesOverride = settings.PassesOverride is { } passesOverride
            ? new Dictionary<string, int>(passesOverride)
            : new Dictionary<string, int>();
        // D-042: パス数の上書きと同じ扱い(null = 一度も触っていない → 空辞書)。
        _magicRgbOverride = settings.MagicRgbOverride is { } magicRgbOverride
            ? new Dictionary<string, int[]?>(magicRgbOverride)
            : new Dictionary<string, int[]?>();
        // D-048: パス数・色の上書きと同じ扱い(null = 一度も触っていない → 空辞書)。
        // 空辞書は「どの coverage インクも塗らない」であり、D-048 以前と同じ出力になる。
        _coverageModes = settings.CoverageModes is { } coverageModes
            ? new Dictionary<string, string>(coverageModes)
            : new Dictionary<string, string>();
        // プリセット: ファイルが無い・壊れているときは空(印刷そのものは止めない)。
        _presets = PresetStore.Load();

        Text = "Foilwright — 印刷プレビュー";
        // ジョブ内容の表は列が 7 本(使う/順序/色/インク/パス数/塗る範囲/ドット数)
        // まで増えた。1200 幅・38% では右側が 450px ほどしか無く、1 列 65px で
        // インク名も「絵のあるところ」も読めなかった(2026-08-22 に利用者から指摘)。
        // 窓を広げ、右側の取り分も増やす。**最小サイズは変えない** — 小さい画面でも
        // 開けること自体は保つ(そのときは表を横スクロールして使う)。
        Width = 1400;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        Controls.Add(root);

        // --- 左: プレビュー画像 ---------------------------------------------
        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.Gray,
        };
        // 表示するインクの絞り込み(§7.2)。DockStyle は「後から追加したものほど
        // 外側」なので、_previewBox(Fill)→ 注意文(Bottom)→ 選択行(Top)の順に
        // 追加し、画像の上下に 1 行ずつ挟む形にする(数字の調整に頼らない)。
        _inkFilterCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 240,
        };
        _inkFilterCombo.SelectedIndexChanged += (_, _) => UpdatePreviewImage();
        var inkFilterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            // 窓を狭めたときはコンボが折り返して、行そのものが縦に伸びる。
            // 高さを決め打ちにすると選べなくなる(D-038 5.1 と同じ失敗を避ける)。
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        inkFilterRow.Controls.Add(new Label
        {
            Text = "表示:",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0),
        });
        inkFilterRow.Controls.Add(_inkFilterCombo);

        _inkFilterNoticeLabel = new Label
        {
            Dock = DockStyle.Bottom,
            // 1 行に収まらないことがあるため、内容にあわせて伸びるようにする
            // (_magicRgbWarningLabel と同じ扱い。切れて読めないと意味が無い)。
            AutoSize = true,
            ForeColor = Color.Red,
            Text = string.Empty,
        };

        var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        previewPanel.Controls.Add(_previewBox);
        previewPanel.Controls.Add(_inkFilterNoticeLabel);
        previewPanel.Controls.Add(inkFilterRow);
        root.Controls.Add(previewPanel, 0, 0);

        // --- 右: 設定・ジョブ内容・状態・操作 ---------------------------------
        // D-039: 印刷開始・取り消しボタンは常に窓の下端に固定し、上の枠
        // (設定・ジョブ内容・プリンタ状態・見張り)がどれだけ増えても押し出され
        // ないようにする。ボタン以外の中身は rightScroll(AutoScroll)に収め、
        // 窓を縮めてもスクロールで全部に到達できるようにする。
        //
        // WinForms の DockStyle はコントロールの追加順(の逆順)で「外側から」
        // 領域を切り出す(MSDN: 最後に追加したコントロールが最も外側の最小領域を
        // 占める)。rightContainer の子は rightScroll(Fill)と buttonPanel(Bottom)
        // の 2 つだけにし、buttonPanel を後から追加することで、rightScroll の
        // 中身がどれだけ増減しても buttonPanel が常に下端の外側に固定される
        // ことを保証する(数字の調整に頼らない)。
        var rightContainer = new Panel { Dock = DockStyle.Fill };
        root.Controls.Add(rightContainer, 1, 0);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8),
            AutoSize = false,
            // D-039: 中身の合計高さが窓を超えても、スクロールで設定・ジョブ内容・
            // プリンタ状態・見張りの全部にたどり着けるようにする。
            AutoScroll = true,
        };
        rightContainer.Controls.Add(right);

        // 設定(§7.1: ジョブごとの上書き)
        // 行を 1 つ増やした分だけ高さも足す(TableLayoutPanel は Dock=Fill なので、
        // ここを据え置くと最下段の保存ボタンが押し出されて見えなくなる)。
        // プリセットの行(1 行)を枠の中のいちばん上に足したぶん、高さも足す。
        var settingsGroup = new GroupBox { Text = "設定(このジョブに適用)", Dock = DockStyle.Top, Height = 395 };
        var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 10, Padding = new Padding(8) };
        settingsLayout.Controls.Add(new Label { Text = "機種:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _machineCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _machineCombo.Items.AddRange(MachineRoute.KnownMachinesDescription.Split('|'));
        _machineCombo.SelectedItem = _machineCombo.Items.Contains(settings.Machine) ? settings.Machine : MachineRoute.DefaultMachine;
        settingsLayout.Controls.Add(_machineCombo, 1, 0);

        settingsLayout.Controls.Add(new Label { Text = "インク指定方式:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _inkModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        // per_page は単一ページの PostScript しか受け取らないトレイアプリでは
        // 選ばせない(Foilwright.Cli.Program.RunListen と同じ制約。DOMAIN §6.6)。
        _inkModeCombo.Items.AddRange(new object[] { "auto", "spot_only" });
        _inkModeCombo.SelectedItem = settings.InkMode is "auto" or "spot_only" ? settings.InkMode : "auto";
        settingsLayout.Controls.Add(_inkModeCombo, 1, 1);

        // 解像度(§7.1)。選択肢はプロファイルの resolutions から読む
        // (機種によって変わりうるため、機種の変更時にも作り直す)。
        settingsLayout.Controls.Add(new Label { Text = "解像度:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _resolutionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        settingsLayout.Controls.Add(_resolutionCombo, 1, 2);

        // 用紙(§7.1 / §5.5 / §15.10.2)。選択肢は用紙表(papers/{系列}.yaml)から
        // 読む(DOMAIN §4.5: コードに用紙名を列挙しない)。位置は解像度の直後
        // (用紙 → メディア種別の並び)。
        settingsLayout.Controls.Add(new Label { Text = "用紙:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _paperCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        settingsLayout.Controls.Add(_paperCombo, 1, 3);

        // メディア種別(§7.1 / §5.5.2)。選択肢は media.yaml から読む。
        settingsLayout.Controls.Add(new Label { Text = "メディア種別:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        _mediaCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        settingsLayout.Controls.Add(_mediaCombo, 1, 4);

        // ハーフトーン(§7.1 / §4.2.1)。
        settingsLayout.Controls.Add(new Label { Text = "ハーフトーン:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        _halftoneCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _halftoneCombo.Items.AddRange(JobAssembly.ValidHalftones.Cast<object>().ToArray());
        _halftoneCombo.SelectedItem = JobAssembly.ValidHalftones.Contains(settings.Halftone) ? settings.Halftone : "none";
        settingsLayout.Controls.Add(_halftoneCombo, 1, 5);

        // 白版モード(§7.1 / D-027)。
        settingsLayout.Controls.Add(new Label { Text = "白版モード:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        _whiteModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _whiteModeCombo.Items.AddRange(JobAssembly.ValidWhiteModes.Cast<object>().ToArray());
        _whiteModeCombo.SelectedItem = JobAssembly.ValidWhiteModes.Contains(settings.WhiteMode) ? settings.WhiteMode : "auto";
        settingsLayout.Controls.Add(_whiteModeCombo, 1, 6);

        // 色補正(§7.1 / D-029)。既定は photo。選択肢は Colour.ValidColourCorrections
        // から読む(DOMAIN §4.5: コードに列挙しない)。
        settingsLayout.Controls.Add(new Label { Text = "色補正:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        _colourCorrectionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _colourCorrectionCombo.Items.AddRange(Colour.ValidColourCorrections.Cast<object>().ToArray());
        _colourCorrectionCombo.SelectedItem =
            Colour.ValidColourCorrections.Contains(settings.ColourCorrection) ? settings.ColourCorrection : "photo";
        settingsLayout.Controls.Add(_colourCorrectionCombo, 1, 7);

        // カール矯正の抑制(§7.1 / DOMAIN §10.10.4)。デカール・フィルム用に
        // 裏面印刷でカール矯正を止めたい場合に使う。
        settingsLayout.Controls.Add(new Label { Text = "カール矯正を止める:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 8);
        _noCurlCheck = new CheckBox { Text = "デカール・フィルム用(§10.10.4)", AutoSize = true, Checked = settings.NoCurlCorrection };
        settingsLayout.Controls.Add(_noCurlCheck, 1, 8);

        _saveDefaultsButton = new Button { Text = "この設定を既定値として保存", AutoSize = true };
        _saveDefaultsButton.Click += (_, _) => SaveAsDefaults();
        settingsLayout.Controls.Add(_saveDefaultsButton, 0, 9);
        settingsLayout.SetColumnSpan(_saveDefaultsButton, 2);

        settingsGroup.Controls.Add(settingsLayout);

        // プリセットの行。settingsLayout(Dock=Fill)を先に、この行(Dock=Top)を
        // 後に足すことで、DockStyle の切り出し順により**必ず枠の中のいちばん上**
        // (「機種:」の行より上)に来る(D-039 のコメントと同じ理屈。位置を数字で
        // 調整しない)。
        var presetPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            // 横に収まらないときは折り返してパネル自身が縦に伸びる。高さを決め打ちに
            // すると窓を狭めたときにボタンが切れて押せなくなる(D-044 改訂と同じ失敗)。
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 0),
        };
        var presetLabel = new Label
        {
            Text = "プリセット:",
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 3),
        };
        _presetCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        _savePresetButton = new Button { Text = "保存...", AutoSize = true };
        _deletePresetButton = new Button { Text = "削除", AutoSize = true };
        presetPanel.Controls.Add(presetLabel);
        presetPanel.Controls.Add(_presetCombo);
        presetPanel.Controls.Add(_savePresetButton);
        presetPanel.Controls.Add(_deletePresetButton);
        settingsGroup.Controls.Add(presetPanel);

        // 「既定値として保存」との違いを画面上で伝える(2 つ並んでいると、どちらが
        // 何をするのか名前だけでは分からない)。
        _presetToolTip = new ToolTip { AutoPopDelay = 20_000, InitialDelay = 400, ReshowDelay = 200 };
        const string presetHelp =
            "用途ごとに設定一式を保存して呼び出せます(例: 目デカール(フィルム)/ はがきテスト)。\n" +
            "「この設定を既定値として保存」は 1 組だけで、何も選ばなかったときの初期値になります。\n" +
            "部数と「1 部ずつ確認する」はプリセットに含みません(そのジョブ限りの指定のため)。";
        _presetToolTip.SetToolTip(presetLabel, presetHelp);
        _presetToolTip.SetToolTip(_presetCombo, presetHelp);
        _presetToolTip.SetToolTip(_savePresetButton, "いまの設定に名前を付けて保存します。");
        _presetToolTip.SetToolTip(_deletePresetButton, "選んでいるプリセットを削除します。");

        PopulatePresetCombo(null);
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_applyingPreset)
            {
                return;
            }
            if (_presetCombo.SelectedItem is not PresetItem { Preset: { } preset })
            {
                // 「(選択なし)」に戻しただけ。今の設定はそのままにする
                // (勝手に既定値へ戻すと、直前の手直しが黙って消える)。
                return;
            }
            BeginInvoke(async () =>
            {
                try
                {
                    await ApplyPresetAsync(preset);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"プリセットの適用に失敗しました: {ex.Message}", "Foilwright",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        };
        _savePresetButton.Click += (_, _) => SaveCurrentAsPreset();
        _deletePresetButton.Click += (_, _) => DeleteSelectedPreset();

        right.Controls.Add(settingsGroup);

        PopulateResolutionCombo(settings.Machine, settings.ResolutionKey);
        PopulatePaperCombo(settings.Machine, settings.PaperName);
        PopulateMediaCombo(settings.MediaName);

        _machineCombo.SelectedIndexChanged += (_, _) =>
        {
            // 機種が変わると選べる解像度が変わりうる(DOMAIN §5.1)ため作り直す。
            PopulateResolutionCombo((string)_machineCombo.SelectedItem!, (string?)_resolutionCombo.SelectedItem);
            // 用紙表もプロファイルの paper_table 参照(機種系列)で変わりうるため
            // 同様に作り直す(DOMAIN §5.1 / §5.5)。
            PopulatePaperCombo((string)_machineCombo.SelectedItem!, (_paperCombo.SelectedItem as PaperItem)?.Name);
            _ = RefreshPreviewAsync();
        };
        _inkModeCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _resolutionCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        // 用紙が変わると印字可能領域への切り出し位置が変わる(DOMAIN §3.6.1 /
        // §15.10.2)ため、メディア種別と同様に RefreshPreviewAsync
        // (Ghostscript の再実行を含む)を呼ぶ。インク除外・パス数上書き
        // (D-028/D-031)のように RebuildFromImage だけで済ませられない —
        // それらは切り出し済みの画像の中身(インク割り当て)しか変えないが、
        // 用紙の変更は画像そのものの切り出し範囲を変えるため。
        _paperCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _mediaCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _halftoneCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _whiteModeCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _colourCorrectionCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();

        // ジョブ内容(§7.2 の 2: パス数・使用インク・順序)
        // D-042: 色の選択ボタン(1 行)と重複警告(1 行)を足したぶん高さを増やす。
        var jobGroup = new GroupBox { Text = "ジョブ内容", Dock = DockStyle.Top, Height = 310 };
        _jobSummaryLabel = new Label { Dock = DockStyle.Top, Height = 24, AutoSize = false, Padding = new Padding(4) };
        _inkGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            // グリッド全体は読み取り専用にせず、"使う" 列だけを編集可能にする
            // (D-028: チェックを外すとそのジョブのパレットからそのインクを外す)。
            ReadOnly = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        var useColumn = new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "使う" };
        _inkGrid.Columns.Add(useColumn);
        _inkGrid.Columns.Add("Order", "順序");
        _inkGrid.Columns.Add("Color", "色");
        _inkGrid.Columns.Add("Label", "インク");
        _inkGrid.Columns.Add("Passes", "パス数");
        // D-038: 「刷る前に白の量を確認する」の手段として、各インクのプレーンの
        // 立っているビット数(ドット数)を表示する列を足す。既存の列の右に置く。
        _inkGrid.Columns.Add("DotCount", "ドット数");
        _inkGrid.Columns["Order"]!.ReadOnly = true;
        _inkGrid.Columns["Label"]!.ReadOnly = true;
        _inkGrid.Columns["DotCount"]!.ReadOnly = true;
        _inkGrid.Columns["DotCount"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        // 列の幅の配分。AutoSizeColumnsMode = Fill は既定で全列を等分するが、
        // **列が 7 本になった今、等分すると「紙用光沢仕上げ2 (MDC-FRVG)」のような
        // 長いインク名も、「絵のあるところ」も読めなくなる。**
        // 中身の長さに合わせて重みを付ける(数字は幅そのものではなく、余った幅の
        // 取り分の比)。**列を足したらここも見直すこと。**
        _inkGrid.Columns["Use"]!.FillWeight = 40;
        _inkGrid.Columns["Order"]!.FillWeight = 40;
        _inkGrid.Columns["Color"]!.FillWeight = 75;
        _inkGrid.Columns["Label"]!.FillWeight = 155;
        _inkGrid.Columns["Passes"]!.FillWeight = 50;
        // 「92,883 (30,961×3)」が入るので、単なる数字より広く取る。
        _inkGrid.Columns["DotCount"]!.FillWeight = 130;
        // D-031: パス数(重ね塗り回数)を編集可能にする。範囲は 1〜8
        // (TraySettings.MinPasses/MaxPasses)で、CellValidating がその場で拒否する。
        var passesColumn = _inkGrid.Columns["Passes"]!;
        passesColumn.ReadOnly = false;
        // D-042: マジックカラー(そのインクへ割り当てる色)を編集可能にする。
        // 書式は #RRGGBB(先頭の # は省略可)、空文字はそのインクの色を外す
        // (= マジック判定に参加させない)。CellValidating がその場で拒否する。
        var colorColumn = _inkGrid.Columns["Color"]!;
        colorColumn.ReadOnly = false;
        colorColumn.HeaderText = "色(#RRGGBB)";
        // D-048: 「塗る範囲」列は「パス数」の右隣に置く(どちらも「そのインクを
        // どう刷るか」の指定であり、隣り合っていたほうが見つけやすい)。
        // coverage インクの行だけがコンボで選べる — それ以外の行は
        // PopulateInkGrid が読み取り専用のテキストセルへ差し替える。
        var coverageColumn = CreateCoverageColumn();
        // 「絵のあるところ」が入る。上の配分と同じ考え方で、中身の長さに合わせる。
        coverageColumn.FillWeight = 110;
        _inkGrid.Columns.Insert(passesColumn.Index + 1, coverageColumn);
        // チェックボックス列は確定(コミット)が 1 セル遅れる既知の挙動があるため、
        // CurrentCellDirtyStateChanged で即座にコミットしてから CellValueChanged を拾う。
        // D-048: コンボボックスのセルも同じ挙動なので、同じ扱いにする。
        _inkGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_inkGrid.IsCurrentCellDirty
                && _inkGrid.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                _inkGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        // D-031: パス数は整数で 1〜8(TraySettings.MinPasses/MaxPasses)。範囲外・
        // 非整数はその場で拒否する(黙って丸めない。打ち間違いで生産終了品の
        // リボンを失わないため)。CellValidating はセルが編集を終えて確定しようと
        // した時点で、まだセルの値が書き換わる前に呼ばれる — ここで弾けば
        // CellValueChanged は発火しない。
        _inkGrid.CellValidating += (_, e) =>
        {
            if (e.RowIndex < 0)
            {
                return;
            }
            if (e.ColumnIndex == passesColumn.Index)
            {
                var cell = _inkGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                if (!int.TryParse(e.FormattedValue?.ToString(), out int value)
                    || value < TraySettings.MinPasses || value > TraySettings.MaxPasses)
                {
                    e.Cancel = true;
                    cell.ErrorText =
                        $"パス数は整数で {TraySettings.MinPasses}〜{TraySettings.MaxPasses} の範囲で指定してください(D-031)。";
                    return;
                }
                cell.ErrorText = string.Empty;
            }
            else if (e.ColumnIndex == colorColumn.Index)
            {
                // D-042: パス数と同じ流儀で、不正な色はその場で拒否する
                // (黙って近い色に丸めない。誤った色で刷るとリボンと用紙を失う)。
                var cell = _inkGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                // 注: このラムダの第 1 引数が `_` という名前のため、`out _` と書くと
                // 破棄ではなくその引数(object)への代入と解釈されて型が合わない。
                // 明示的な変数で受ける。
                if (!TryParseColorCell(e.FormattedValue?.ToString(), out int[]? _unused))
                {
                    e.Cancel = true;
                    cell.ErrorText =
                        "色は #RRGGBB(16 進 6 桁。先頭の # は省略可)で指定してください。" +
                        "空欄にするとそのインクの色を外します(D-042)。";
                    return;
                }
                cell.ErrorText = string.Empty;
            }
        };
        _inkGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0)
            {
                return;
            }
            // D-048: グリッドを作り直している最中の値の書き込み(「塗る範囲」列に
            // いま効いている値を入れる操作)は、利用者の操作ではない。ここで
            // 弾かないと **組み直す → 値を書く → また組み直す** の無限ループになる
            // (下の BeginInvoke は _busy が下りたあとに走るため、_busy では防げない)。
            if (_populatingInkGrid)
            {
                return;
            }
            // CellValueChanged はまだ DataGridView 自身の編集確定処理
            // (CurrentCellDirtyStateChanged → CommitEdit → CellValueChanged、
            // またはテキスト列の EndEdit)の呼び出しスタックの中で発火している。
            // ここで同期的に SetBusy(true)(列の ReadOnly 切り替えを含む)や
            // PopulateInkGrid の Rows.Clear() を行うと、グリッドが自分自身の
            // セル編集処理の途中で自分の行・列の状態を書き換えられることになり、
            // 内部状態が壊れる。BeginInvoke でいったんメッセージキューに積み直し、
            // グリッドが今回のセル編集処理を完全に終えてから非同期処理を実行する。
            int rowIndex = e.RowIndex;
            if (e.ColumnIndex == useColumn.Index)
            {
                BeginInvoke(async () =>
                {
                    try
                    {
                        await OnInkUseChangedAsync(rowIndex);
                    }
                    catch (Exception ex)
                    {
                        // async void 相当のハンドラで例外を握り潰すと、利用者には
                        // 何も表示されないまま UI が固まったように見える
                        // (今回の不具合調査で「何が起きているか分からない」原因の一つ)。
                        // 必ず捕まえて見せる。
                        MessageBox.Show(this, $"インクの使用可否の切り替えに失敗しました: {ex.Message}", "Foilwright",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }
            else if (e.ColumnIndex == passesColumn.Index)
            {
                BeginInvoke(async () =>
                {
                    try
                    {
                        await OnPassesChangedAsync(rowIndex);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"パス数の変更に失敗しました: {ex.Message}", "Foilwright",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }
            else if (e.ColumnIndex == colorColumn.Index)
            {
                BeginInvoke(async () =>
                {
                    try
                    {
                        await OnMagicRgbChangedAsync(rowIndex);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"色の変更に失敗しました: {ex.Message}", "Foilwright",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }
            else if (e.ColumnIndex == coverageColumn.Index)
            {
                BeginInvoke(async () =>
                {
                    try
                    {
                        await OnCoverageModeChangedAsync(rowIndex);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"塗る範囲の変更に失敗しました: {ex.Message}", "Foilwright",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            }
        };
        jobGroup.Controls.Add(_inkGrid);
        jobGroup.Controls.Add(_jobSummaryLabel);

        // D-042: 色が重複したときの警告(印刷は止めない。警告のみ)。
        // グリッドの下、色を選ぶボタンの上に置く。
        _magicRgbWarningLabel = new Label
        {
            Dock = DockStyle.Bottom,
            // 警告は 2 行になることがある(色の重複 + 白版モード)。D-048 で
            // 「塗る範囲が none のまま」の警告も同じラベルに相乗りするため、
            // 3 行以上になることもある。高さを決め打ちにすると下の行が切れて
            // 読めなくなるため、内容にあわせて伸びるようにする。
            AutoSize = true,
            ForeColor = Color.Red,
            Text = string.Empty,
            // レイアウトの検出器(PreviewFormLayoutTests)から Controls.Find で
            // 引けるようにする。警告文は空のことが多く、文字列では探せない。
            Name = WarningLabelName,
        };
        jobGroup.Controls.Add(_magicRgbWarningLabel);

        // D-042: 色の選択・既定へ戻すボタン。どちらも「選択中の行」に対して働く。
        var colorButtonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            // ボタンが横に収まらないときは折り返して、パネル自身が縦に伸びる。
            // 高さを決め打ちにすると、窓を狭めたときにボタンが切れて押せなくなる
            // (D-038 の 5.1 で印刷開始ボタンを画面外へ押し出したのと同じ失敗)。
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 2, 4, 2),
        };
        _pickColorButton = new Button { Text = "色を選ぶ...", AutoSize = true };
        _pickColorButton.Click += (_, _) => PickMagicRgbForSelectedRow();
        _resetColorButton = new Button { Text = "色を既定に戻す", AutoSize = true };
        _resetColorButton.Click += (_, _) =>
        {
            BeginInvoke(async () =>
            {
                try
                {
                    await ResetMagicRgbForSelectedRowAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"色を既定に戻せませんでした: {ex.Message}", "Foilwright",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        };
        // D-042: こちらは行を選ばなくても押せる。上書きを全部捨ててパレットの
        // 値に戻す — 何色か試したあと元へ戻すのに、行ごとに戻して回らずに済む。
        _resetAllColorsButton = new Button { Text = "全部の色を既定に戻す", AutoSize = true };
        _resetAllColorsButton.Click += (_, _) =>
        {
            BeginInvoke(async () =>
            {
                try
                {
                    await ResetAllMagicRgbAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"色を既定に戻せませんでした: {ex.Message}", "Foilwright",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            });
        };
        colorButtonPanel.Controls.Add(_pickColorButton);
        colorButtonPanel.Controls.Add(_resetColorButton);
        colorButtonPanel.Controls.Add(_resetAllColorsButton);
        jobGroup.Controls.Add(colorButtonPanel);

        right.Controls.Add(jobGroup);

        // プリンタ状態(§7.2 の 7)+ カセットの過不足表示(§7.3 / D-026)。
        var statusGroup = new GroupBox { Text = "プリンタ状態 / カセットの過不足", Dock = DockStyle.Top, Height = 190 };
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // ボタンは 2 つ並ぶ(状態を読む / リボン消費を見る)。D-044 改訂で下端の
        // ボタン列を高さ決め打ちにしていて窓の外へ押し出した失敗があるため、
        // ここも折り返し + 自動サイズにして、増えたぶんは横に流れるようにする。
        var statusButtonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        _statusRefreshButton = new Button { Text = "状態を読む(05 01)", AutoSize = true };
        _statusRefreshButton.Click += async (_, _) => await RefreshStatusAsync();
        statusButtonPanel.Controls.Add(_statusRefreshButton);
        // リボンは生産終了品で残量が最大の制約だが、プリンタに残量を尋ねる経路
        // (05 01 の応答の low / high)は意味が未解明である(DOMAIN §11.4.3)。
        // そこで自分で数えたぶんを見せる。
        _usageButton = new Button { Text = "リボン消費を見る", AutoSize = true };
        _usageButton.Click += (_, _) => ShowUsageDialog();
        statusButtonPanel.Controls.Add(_usageButton);
        statusLayout.Controls.Add(statusButtonPanel, 0, 0);
        _statusText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(FontFamily.GenericMonospace, 9),
        };
        statusLayout.Controls.Add(_statusText, 0, 1);
        statusGroup.Controls.Add(statusLayout);
        right.Controls.Add(statusGroup);

        _progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 24, Minimum = 0, Maximum = 100 };
        right.Controls.Add(_progressBar);

        // D-039: rightContainer の直接の子として、rightScroll(= right)の後に
        // 追加する。DockStyle.Bottom + 「後から追加」で、常に rightContainer の
        // 下端の外側に固定される(スクロール対象に入らない)。
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            // D-044 改訂: 中身が横に収まらないときは折り返し、パネル自身が縦に伸びる。
            // 高さを 44 で決め打ちにしていたため、「1 部ずつ確認する」を足した途端に
            // 部数とチェックが 2 行目へ回り、窓の外(Y=566 > 高さ 561)へ消えていた
            // ― D-038 5.1 と同じ失敗。レイアウトの検出器
            // (PreviewFormLayoutTests)がこれを捕まえた。
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8),
        };
        _cancelButton = new Button { Text = "取り消し", AutoSize = true, Height = 32 };
        _cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };
        _printButton = new Button { Text = "印刷開始", AutoSize = true, Height = 32, Enabled = false };
        _printButton.Click += async (_, _) => await PrintAsync();
        buttonPanel.Controls.Add(_cancelButton);
        buttonPanel.Controls.Add(_printButton);
        // D-044: 部数は「印刷開始」の左隣に置く。buttonPanel は RightToLeft 送りなので
        // 後から追加したものほど左へ並ぶ — 数値欄・ラベルの順に足すと、画面上は
        // 「部数: [1] 印刷開始 取り消し」の並びになる。
        _copiesUpDown = new NumericUpDown
        {
            Minimum = MinCopies,
            Maximum = MaxCopies,
            Value = MinCopies,
            Width = 72,
            // ボタン(Height = 32)と高さを揃えると縦位置が揃って見える。
            Margin = new Padding(3, 6, 3, 3),
        };
        buttonPanel.Controls.Add(_copiesUpDown);
        // D-044 改訂: 1 部ごとに止まるかどうかを選べるようにする。**既定は ON。**
        // この機械は手差し運用(給紙レバー M)で自動給紙は過去に失敗しており
        // (§11.1.1)、OFF にすると紙が無い状態で次の部が送られて給紙エラーになり、
        // 機構が動いて詰まる危険がある。OFF は「自動給紙が使える」と分かっている
        // 人が明示的に選ぶもの、という位置づけにする。
        _stopBetweenCopiesCheck = new CheckBox
        {
            Text = "1 部ずつ確認する",
            Checked = true,
            AutoSize = true,
            Margin = new Padding(3, 9, 3, 3),
        };
        buttonPanel.Controls.Add(_stopBetweenCopiesCheck);
        buttonPanel.Controls.Add(new Label
        {
            Text = "部数:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleRight,
            Margin = new Padding(3, 10, 0, 3),
        });
        rightContainer.Controls.Add(buttonPanel);

        // D-038: 送出後、印刷が終わるまでプレビューを開いたまま見張る枠。
        // 初期状態は非表示 — PrintAsync が送出を終えたあとに Visible = true にする
        // (送出中は §15.2.1 により状態を読んではならないため、見張りは送出の
        // 完了後にしか始められない)。
        _monitorGroup = new GroupBox { Text = "印刷の完了を見張る(D-038)", Dock = DockStyle.Top, Height = 130, Visible = false };
        var monitorLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        monitorLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        monitorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        monitorLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _monitorStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Text = string.Empty,
        };
        monitorLayout.Controls.Add(_monitorStatusLabel, 0, 0);
        var monitorNoteLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 32,
            ForeColor = Color.DarkRed,
            // D-038: 「中止」は見張りをやめるだけで、印刷そのものは止めない
            // ことをボタンの近くに明記する(仕様上の要求)。
            Text = "「見張りを中止」を押しても印刷は止まりません。見張りをやめるだけです。",
        };
        monitorLayout.Controls.Add(monitorNoteLabel, 0, 1);
        var monitorButtonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, Height = 36, AutoSize = false,
        };
        _monitorCloseButton = new Button { Text = "閉じる", AutoSize = true, Height = 28, Enabled = false };
        _monitorCloseButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };
        _monitorAbortButton = new Button { Text = "見張りを中止", AutoSize = true, Height = 28 };
        _monitorAbortButton.Click += (_, _) =>
        {
            _monitorAbortButton.Enabled = false;
            _monitorCts?.Cancel();
        };
        monitorButtonPanel.Controls.Add(_monitorCloseButton);
        monitorButtonPanel.Controls.Add(_monitorAbortButton);
        monitorLayout.Controls.Add(monitorButtonPanel, 0, 2);
        _monitorGroup.Controls.Add(monitorLayout);
        right.Controls.Add(_monitorGroup);

        // D-038: 見張りが終わる(完了/エラー/打ち切り/上限時間)までプレビューを
        // 閉じさせない。タイトルバーの × や Alt+F4 での即時クローズを防ぐ
        // (印刷を止めずに窓だけ消えると、結果を見逃す)。
        FormClosing += (_, e) =>
        {
            if (_monitoring)
            {
                e.Cancel = true;
                MessageBox.Show(
                    this,
                    "印刷の完了を見張っている間は閉じられません。「見張りを中止」を押してください" +
                    "(押しても印刷は止まりません。見張りをやめるだけです)。",
                    "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // D-044 決定 5: 部と部のあいだも閉じさせない。見張りが 1 部ぶん
            // 終わると _monitoring が false に戻るため、ここを見ないと
            // 「次の紙を入れてください」が出るまでの隙に閉じられてしまう。
            // 閉じた後もループは次の部を送りに行くので、破棄済みの窓へ触って
            // 落ちるうえ、残りの部が黙って失われる。
            if (HasRemainingCopies)
            {
                e.Cancel = true;
                MessageBox.Show(
                    this,
                    $"まだ {_copyTotal} 部のうち {_copyIndex} 部目までしか刷っていません。" +
                    "残りをやめるときは「次の紙を入れてください」の確認で「キャンセル」を押してください。",
                    "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        Load += async (_, _) => await RefreshPreviewAsync();
    }

    /// <summary>解像度コンボの選択肢を機種プロファイルの resolutions から作り直す
    /// (DOMAIN §4.5: コードに埋め込まない。機種で選べる解像度が変わりうるため
    /// 機種変更のたびに呼ぶ)。</summary>
    private void PopulateResolutionCombo(string machine, string? preferredKey)
    {
        try
        {
            var route = MachineRoute.Resolve(machine);
            var profile = ConfigLoader.LoadProfile(Path.Combine(_assetRoot, "profiles", route.ProfileFileName));
            _resolutionCombo.Items.Clear();
            foreach (var entry in profile.Resolutions)
            {
                _resolutionCombo.Items.Add(entry.Key);
            }
            string? fallback = profile.Resolutions.FirstOrDefault(r => r.IsDefault)?.Key
                ?? profile.Resolutions.FirstOrDefault()?.Key;
            _resolutionCombo.SelectedItem = preferredKey is not null && _resolutionCombo.Items.Contains(preferredKey)
                ? preferredKey
                : fallback;
        }
        catch (Exception ex) when (ex is ConfigException or MachineRouteException)
        {
            // 選択肢の作成に失敗しても致命的にはしない。RefreshPreviewAsync 側で
            // あらためてエラーを表示する。
        }
    }

    /// <summary>用紙コンボの選択肢を用紙表(papers/{系列}.yaml)から作り直す
    /// (DOMAIN §4.5: コードに用紙名を列挙しない)。用紙表はプロファイルの
    /// paper_table 参照(機種系列)で決まるため、機種の変更時にも作り直す
    /// (PopulateResolutionCombo と同じ理由)。</summary>
    private void PopulatePaperCombo(string machine, string? preferredName)
    {
        try
        {
            var route = MachineRoute.Resolve(machine);
            var profile = ConfigLoader.LoadProfile(Path.Combine(_assetRoot, "profiles", route.ProfileFileName));
            var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(_assetRoot, "papers"));
            _paperCombo.Items.Clear();
            PaperItem? preferred = null;
            foreach (var kv in paperTable.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var item = new PaperItem(kv.Key);
                _paperCombo.Items.Add(item);
                if (kv.Key == preferredName)
                {
                    preferred = item;
                }
            }
            _paperCombo.SelectedItem = preferred ?? (_paperCombo.Items.Count > 0 ? _paperCombo.Items[0] : null);
        }
        catch (Exception ex) when (ex is ConfigException or MachineRouteException)
        {
            // 選択肢の作成に失敗しても致命的にはしない。RefreshPreviewAsync 側で
            // あらためてエラーを表示する(PopulateResolutionCombo と同じ方針)。
        }
    }

    /// <summary>メディア種別コンボの選択肢を media.yaml から作り直す(DOMAIN §4.5)。</summary>
    private void PopulateMediaCombo(string preferredName)
    {
        try
        {
            var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(_assetRoot, "media.yaml"));
            _mediaCombo.Items.Clear();
            MediaItem? preferred = null;
            foreach (var kv in mediaTable.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var item = new MediaItem(kv.Key, kv.Value.Label);
                _mediaCombo.Items.Add(item);
                if (kv.Key == preferredName)
                {
                    preferred = item;
                }
            }
            _mediaCombo.SelectedItem = preferred ?? (_mediaCombo.Items.Count > 0 ? _mediaCombo.Items[0] : null);
        }
        catch (ConfigException)
        {
            // 選択肢の作成に失敗しても致命的にはしない。
        }
    }

    private void SaveAsDefaults()
    {
        _settings.Machine = (string)_machineCombo.SelectedItem!;
        _settings.InkMode = (string)_inkModeCombo.SelectedItem!;
        _settings.ResolutionKey = (string)_resolutionCombo.SelectedItem!;
        _settings.PaperName = ((PaperItem)_paperCombo.SelectedItem!).Name;
        _settings.MediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
        _settings.Halftone = (string)_halftoneCombo.SelectedItem!;
        _settings.WhiteMode = (string)_whiteModeCombo.SelectedItem!;
        _settings.ColourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
        _settings.NoCurlCorrection = _noCurlCheck.Checked;
        // D-030: このジョブの許可リストをそのまま既定値へ保存する
        // (他の設定項目と同じ「今の状態を既定にする」挙動)。
        _settings.UsedInks = new HashSet<string>(_usedInks);
        // D-031: パス数の上書きも同様に、このジョブの状態をそのまま既定値へ保存する。
        _settings.PassesOverride = new Dictionary<string, int>(_passesOverride);
        // D-042: マジックカラーの上書きも同様に、このジョブの状態をそのまま既定値へ保存する。
        _settings.MagicRgbOverride = new Dictionary<string, int[]?>(_magicRgbOverride);
        // D-048: 塗る範囲も同様に、このジョブの状態をそのまま既定値へ保存する。
        _settings.CoverageModes = new Dictionary<string, string>(_coverageModes);
        _settings.Save();
        MessageBox.Show(this, "既定値として保存しました。", "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>プリセットのコンボを作り直す(先頭は必ず「(選択なし)」)。
    /// 作り直しで SelectedIndexChanged が発火しても適用が走らないよう、
    /// _applyingPreset を立てたまま行う。</summary>
    private void PopulatePresetCombo(string? selectName)
    {
        _applyingPreset = true;
        try
        {
            _presetCombo.Items.Clear();
            var none = new PresetItem(null, NoPresetText);
            _presetCombo.Items.Add(none);
            PresetItem? selected = null;
            foreach (var preset in _presets)
            {
                var item = new PresetItem(preset, preset.Name);
                _presetCombo.Items.Add(item);
                if (selectName is not null && PresetStore.NameComparer.Equals(preset.Name, selectName))
                {
                    selected = item;
                }
            }
            _presetCombo.SelectedItem = selected ?? none;
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    /// <summary>いま選ばれているプリセット(「(選択なし)」なら null)。</summary>
    private SettingsPreset? SelectedPreset() =>
        (_presetCombo.SelectedItem as PresetItem)?.Preset;

    /// <summary>プリセットの中身を画面へ反映し、プレビューを組み直す。
    ///
    /// **どの経路で組み直すかは、既存のコンボの挙動にそのまま合わせる** —
    /// 機種・用紙・解像度・メディア種別を変えるコンボの SelectedIndexChanged は
    /// いずれも RefreshPreviewAsync(Ghostscript の再実行を含む)を呼んでいる。
    /// 切り出し範囲や JobConfig そのものが変わるためで(DOMAIN §3.6.1 / §15.10.2)、
    /// 切り出し済みの画像を使い回す RebuildFromImage では反映できない。
    /// そこでこの 4 つのどれかが変わるときだけ再変換し、変わらないとき
    /// (ハーフトーン・白版モード・色補正・インク指定方式・使うインク・パス数・色だけの
    /// 違い)は RebuildFromImage で済ませる — D-028 補足のとおり Ghostscript を
    /// 走らせ直さないぶん即座に返る。</summary>
    private async Task ApplyPresetAsync(SettingsPreset preset)
    {
        if (_busy)
        {
            return;
        }

        string previousMachine = (string)_machineCombo.SelectedItem!;
        string? previousResolution = _resolutionCombo.SelectedItem as string;
        string? previousPaper = (_paperCombo.SelectedItem as PaperItem)?.Name;
        string? previousMedia = (_mediaCombo.SelectedItem as MediaItem)?.Name;

        _applyingPreset = true;
        try
        {
            if (_machineCombo.Items.Contains(preset.Machine))
            {
                _machineCombo.SelectedItem = preset.Machine;
            }
            string machine = (string)_machineCombo.SelectedItem!;
            // 機種で選べる解像度・用紙表が変わりうる(DOMAIN §5.1 / §5.5)ため、
            // 機種のコンボと同じ手順で作り直してからプリセットの値を選ぶ。
            PopulateResolutionCombo(machine, preset.ResolutionKey);
            PopulatePaperCombo(machine, preset.PaperName);
            PopulateMediaCombo(preset.MediaName);

            if (_inkModeCombo.Items.Contains(preset.InkMode))
            {
                _inkModeCombo.SelectedItem = preset.InkMode;
            }
            if (_halftoneCombo.Items.Contains(preset.Halftone))
            {
                _halftoneCombo.SelectedItem = preset.Halftone;
            }
            if (_whiteModeCombo.Items.Contains(preset.WhiteMode))
            {
                _whiteModeCombo.SelectedItem = preset.WhiteMode;
            }
            if (_colourCorrectionCombo.Items.Contains(preset.ColourCorrection))
            {
                _colourCorrectionCombo.SelectedItem = preset.ColourCorrection;
            }
            _noCurlCheck.Checked = preset.NoCurlCorrection;

            // D-030 / D-031 / D-042 / D-048: null と空を区別する。null は「このプリセットは
            // 触っていない」であり、既定(パレットから導出 / 上書き無し)へ戻す。
            _usedInks.Clear();
            foreach (string name in preset.UsedInks ?? TraySettings.DefaultUsedInks(_palette))
            {
                _usedInks.Add(name);
            }
            _passesOverride.Clear();
            foreach (var (name, passes) in preset.PassesOverride ?? new Dictionary<string, int>())
            {
                _passesOverride[name] = passes;
            }
            _magicRgbOverride.Clear();
            foreach (var (name, rgb) in preset.MagicRgbOverride ?? new Dictionary<string, int[]?>())
            {
                _magicRgbOverride[name] = rgb;
            }
            // D-048: 塗る範囲も同じ扱い(null なら「指定無し」= どの coverage インクも塗らない)。
            _coverageModes.Clear();
            foreach (var (name, mode) in preset.CoverageModes ?? new Dictionary<string, string>())
            {
                _coverageModes[name] = mode;
            }
        }
        finally
        {
            _applyingPreset = false;
        }

        bool needsReconvert =
            _current is null
            || !string.Equals(previousMachine, (string)_machineCombo.SelectedItem!, StringComparison.Ordinal)
            || previousResolution != _resolutionCombo.SelectedItem as string
            || previousPaper != (_paperCombo.SelectedItem as PaperItem)?.Name
            || previousMedia != (_mediaCombo.SelectedItem as MediaItem)?.Name;

        if (needsReconvert)
        {
            await RefreshPreviewAsync();
        }
        else
        {
            await RebuildFromCurrentImageAsync();
        }
    }

    /// <summary>いまの画面の設定一式を <see cref="SettingsPreset"/> にする
    /// (SaveAsDefaults が既定値へ写しているのと同じ項目。部数と
    /// 「1 部ずつ確認する」は**意図的に含めない** — D-044 決定 3)。</summary>
    private SettingsPreset BuildPresetFromUi(string name) => new()
    {
        Name = name,
        Machine = (string)_machineCombo.SelectedItem!,
        InkMode = (string)_inkModeCombo.SelectedItem!,
        ResolutionKey = (string)_resolutionCombo.SelectedItem!,
        PaperName = ((PaperItem)_paperCombo.SelectedItem!).Name,
        MediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name,
        Halftone = (string)_halftoneCombo.SelectedItem!,
        WhiteMode = (string)_whiteModeCombo.SelectedItem!,
        ColourCorrection = (string)_colourCorrectionCombo.SelectedItem!,
        NoCurlCorrection = _noCurlCheck.Checked,
        UsedInks = new HashSet<string>(_usedInks),
        PassesOverride = new Dictionary<string, int>(_passesOverride),
        MagicRgbOverride = new Dictionary<string, int[]?>(_magicRgbOverride),
        CoverageModes = new Dictionary<string, string>(_coverageModes),
    };

    /// <summary>「保存...」ボタン。名前を尋ね(既定でいま選ばれているプリセット名)、
    /// 同名があれば上書きの確認を出してから presets.json へ書き出す。
    /// 保存したらコンボをその名前に切り替える。</summary>
    private void SaveCurrentAsPreset()
    {
        // _current(プレビューの結果)は見ない — プリセットに入るのはコンボの値だけで、
        // 「既定値として保存」が押せる状況ならこちらも押せる、と揃えてある。
        if (_busy)
        {
            return;
        }
        string? name = PromptForPresetName(SelectedPreset()?.Name ?? string.Empty);
        if (name is null)
        {
            return;
        }
        var existing = _presets.FirstOrDefault(p => PresetStore.NameComparer.Equals(p.Name, name));
        if (existing is not null)
        {
            var overwrite = MessageBox.Show(
                this,
                $"プリセット「{existing.Name}」はすでにあります。いまの設定で上書きしますか?",
                "Foilwright", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (overwrite != DialogResult.Yes)
            {
                return;
            }
        }

        var updated = PresetStore.Upsert(_presets, BuildPresetFromUi(name));
        if (!TrySavePresets(updated))
        {
            return;
        }
        _presets = updated;
        PopulatePresetCombo(name);
    }

    /// <summary>「削除」ボタン。「(選択なし)」のときは何もしない(何をすればよいかは伝える)。
    /// 選ばれているときは確認してから消す。</summary>
    private void DeleteSelectedPreset()
    {
        if (_busy)
        {
            return;
        }
        var selected = SelectedPreset();
        if (selected is null)
        {
            // 黙って帰るとボタンが壊れているように見える(D-042 のボタンと同じ流儀)。
            MessageBox.Show(
                this, "先にプリセットを選んでください。", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var confirm = MessageBox.Show(
            this, $"プリセット「{selected.Name}」を削除します。よろしいですか?", "Foilwright",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        var updated = PresetStore.Remove(_presets, selected.Name);
        if (!TrySavePresets(updated))
        {
            return;
        }
        _presets = updated;
        // 消したものは選べない。「(選択なし)」へ戻す(画面の設定はそのまま)。
        PopulatePresetCombo(null);
    }

    /// <summary>presets.json への書き出し。書けなかったときは、黙って成功したように
    /// 見せず理由を出す(次に起動したときプリセットが消えている、を防ぐ)。</summary>
    private bool TrySavePresets(IReadOnlyList<SettingsPreset> presets)
    {
        try
        {
            PresetStore.Save(presets);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"プリセットを保存できませんでした: {ex.Message}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>プリセット名を尋ねる。取り消されたら null を返す。
    /// WinForms に標準の入力ダイアログは無いため、小さな Form を自前で持つ
    /// (Microsoft.VisualBasic への参照追加も NuGet の追加もしない)。</summary>
    private string? PromptForPresetName(string initialName)
    {
        using var dialog = new PresetNameDialog(initialName);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.PresetName : null;
    }

    /// <summary>プリセット名の入力欄だけを持つ小さなダイアログ。
    /// 名前の妥当性は <see cref="PresetStore.IsValidPresetName"/> が決める
    /// (空白のみは不可・長すぎるものも不可)。OK を押しても妥当でなければ閉じない。</summary>
    private sealed class PresetNameDialog : Form
    {
        private readonly TextBox _nameBox;

        /// <summary>入力された名前(前後の空白は落とす)。</summary>
        internal string PresetName => _nameBox.Text.Trim();

        internal PresetNameDialog(string initialName)
        {
            Text = "Foilwright — プリセットの名前";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(440, 132);

            var label = new Label
            {
                Text = $"プリセットの名前({PresetStore.MaxPresetNameLength} 文字まで):",
                AutoSize = true,
                Location = new Point(12, 14),
            };
            _nameBox = new TextBox
            {
                Text = initialName,
                Location = new Point(12, 40),
                Width = 416,
                MaxLength = PresetStore.MaxPresetNameLength,
            };
            var okButton = new Button
            {
                Text = "OK",
                Location = new Point(244, 84),
                Size = new Size(88, 30),
            };
            var cancelButton = new Button
            {
                Text = "キャンセル",
                DialogResult = DialogResult.Cancel,
                Location = new Point(340, 84),
                Size = new Size(88, 30),
            };
            // 妥当でない名前のまま閉じない(DialogResult は妥当だったときだけ入れる)。
            okButton.Click += (_, _) =>
            {
                if (!PresetStore.IsValidPresetName(PresetName))
                {
                    MessageBox.Show(
                        this,
                        $"名前を入力してください(空白だけは使えません。{PresetStore.MaxPresetNameLength} 文字まで)。",
                        "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
            };
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Controls.Add(label);
            Controls.Add(_nameBox);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
        }
    }

    // §7.2: プレビューは必須。誤爆はリボンと用紙を失うため、印刷開始できる
    // 状態は必ずこの再変換を経てから決める(古いプレビューのまま印刷ボタンを
    // 有効化しない)。
    private async Task RefreshPreviewAsync()
    {
        // プリセットの流し込み中(_applyingPreset)は、コンボを 1 つ書き換えるたびに
        // ここへ来てしまう。全部書き終えてから ApplyPresetAsync が一度だけ呼ぶ。
        if (_busy || _applyingPreset)
        {
            return;
        }
        SetBusy(true, "プレビューを作成中...");
        try
        {
            string machine = (string)_machineCombo.SelectedItem!;
            string inkMode = (string)_inkModeCombo.SelectedItem!;
            string resolutionKey = (string)_resolutionCombo.SelectedItem!;
            string paperName = ((PaperItem)_paperCombo.SelectedItem!).Name;
            string mediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
            string halftone = (string)_halftoneCombo.SelectedItem!;
            string whiteMode = (string)_whiteModeCombo.SelectedItem!;
            string colourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
            var route = MachineRoute.Resolve(machine);

            // D-030: 許可リストは解像度・メディア・機種などを変えて再プレビューしても
            // そのまま持ち越す(許可されていないインクがもう現れなければ自然に消える)。
            var result = await Task.Run(() => JobPipeline.BuildPreview(
                _psPath, _assetRoot, route, inkMode, paperName, mediaName, resolutionKey, halftone, whiteMode,
                _usedInks, _passesOverride, colourCorrection, _magicRgbOverride, _coverageModes));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is GhostscriptException or ConfigException or PpmFormatException)
        {
            _current?.Dispose();
            _current = null;
            MessageBox.Show(this, $"プレビューの作成に失敗しました: {Program.DescribeUserError(ex)}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>チェック列(D-028)を切り替えたときのハンドラ。Ghostscript を
    /// 再実行せず、切り出し済みの画像を保持したまま JobPipeline.RebuildFromImage
    /// でジョブ組み立てだけをやり直す(D-028 補足)。</summary>
    private async Task OnInkUseChangedAsync(int rowIndex)
    {
        if (_busy || _current is null)
        {
            return;
        }
        if (rowIndex < 0 || rowIndex >= _inkGrid.Rows.Count)
        {
            return;
        }
        var row = _inkGrid.Rows[rowIndex];
        if (row.Tag is not string inkName)
        {
            return;
        }
        bool use = row.Cells["Use"].Value is bool b && b;
        if (use)
        {
            _usedInks.Add(inkName);
        }
        else
        {
            _usedInks.Remove(inkName);
        }

        SetBusy(true, "ジョブを再構成中...");
        try
        {
            string inkMode = (string)_inkModeCombo.SelectedItem!;
            string halftone = (string)_halftoneCombo.SelectedItem!;
            string whiteMode = (string)_whiteModeCombo.SelectedItem!;
            string colourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
            var previous = _current;

            var result = await Task.Run(() => JobPipeline.RebuildFromImage(
                previous.Image, previous.Config, previous.Resolution, inkMode, halftone, whiteMode, _usedInks,
                _passesOverride, colourCorrection, previous.AlphaImage, _magicRgbOverride, _coverageModes));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is ConfigException or PpmFormatException)
        {
            // async void 相当の呼び出し元(CellValueChanged)まで例外を伝播させると
            // 握り潰されて何も表示されないまま UI が固まって見える(今回の不具合調査
            // で「何が起きているか分からない」原因の一つだった)。RefreshPreviewAsync
            // と同じ流儀でここでも捕まえて見せる。_usedInks は変更済みのままにする
            // (グリッドの再構成に失敗しても、利用者が付けた/外したチェックの意思は
            // 次回の操作までそのまま保持する)。
            MessageBox.Show(this, $"ジョブの再構成に失敗しました: {Program.DescribeUserError(ex)}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>D-031: パス数(重ね塗り回数)列を編集したときのハンドラ。値は
    /// CellValidating で 1〜8 の整数であることを確認済み。Ghostscript を再実行せず、
    /// 切り出し済みの画像を保持したまま JobPipeline.RebuildFromImage でジョブ
    /// 組み立てだけをやり直す(D-028 補足と同じ扱い)。
    ///
    /// 「使わない」(チェックが外れている)インクのパス数編集について: D-030 の
    /// グリッドはパレット全体を常に表示し、いま使っていないインクのチェックも
    /// 自由に切り替えられる(PopulateInkGrid のコメント参照)。パス数もこれに
    /// 揃え、チェックの有無に関わらず編集を受け付け、_passesOverride へ記録する。
    /// そのジョブで実際に使われるかどうか(JobInk / RGL に反映されるか)は
    /// あくまで「使う」チェックが決める — 使っていないインクの上書きは、
    /// そのインクを後で有効にしたとき、またはこの設定を既定値として保存し
    /// 次回別のジョブで使ったときに効いてくる。</summary>
    private async Task OnPassesChangedAsync(int rowIndex)
    {
        if (_busy || _current is null)
        {
            return;
        }
        if (rowIndex < 0 || rowIndex >= _inkGrid.Rows.Count)
        {
            return;
        }
        var row = _inkGrid.Rows[rowIndex];
        if (row.Tag is not string inkName)
        {
            return;
        }
        // CellValidating がここに到達する前に範囲を確認済みだが、念のため
        // もう一度検証する(黙って丸めない。D-031)。
        if (!int.TryParse(row.Cells["Passes"].Value?.ToString(), out int passes)
            || passes < TraySettings.MinPasses || passes > TraySettings.MaxPasses)
        {
            return;
        }
        _passesOverride[inkName] = passes;

        SetBusy(true, "ジョブを再構成中...");
        try
        {
            string inkMode = (string)_inkModeCombo.SelectedItem!;
            string halftone = (string)_halftoneCombo.SelectedItem!;
            string whiteMode = (string)_whiteModeCombo.SelectedItem!;
            string colourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
            var previous = _current;

            var result = await Task.Run(() => JobPipeline.RebuildFromImage(
                previous.Image, previous.Config, previous.Resolution, inkMode, halftone, whiteMode, _usedInks,
                _passesOverride, colourCorrection, previous.AlphaImage, _magicRgbOverride, _coverageModes));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is ConfigException or PpmFormatException)
        {
            // OnInkUseChangedAsync と同じ流儀。_passesOverride は変更済みのまま
            // にする(グリッドの再構成に失敗しても、利用者が入力した値は
            // 次回の操作までそのまま保持する)。
            MessageBox.Show(this, $"ジョブの再構成に失敗しました: {Program.DescribeUserError(ex)}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>D-042: 「色」列の入力を RGB 3 値へ解析する。受け付ける書式は
    /// `#RRGGBB` / `RRGGBB`(先頭の `#` は省略可)と、空文字(= 色なし。そのインクを
    /// マジック判定に参加させない)。それ以外は false を返し、CellValidating が
    /// その場で拒否する。</summary>
    internal static bool TryParseColorCell(string? text, out int[]? rgb)
    {
        rgb = null;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || value == NoColourCellText)
        {
            // 空欄と「(なし)」はどちらも「色なし」。null を入れる意思表示であり、
            // 不正入力ではない。表示された文字をそのまま打ち直せるようにするため、
            // 「(なし)」も受け付ける。
            return true;
        }
        string hex = value.StartsWith('#') ? value[1..] : value;
        if (hex.Length != 6
            || !int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out int r)
            || !int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g)
            || !int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
        {
            return false;
        }
        rgb = new[] { r, g, b };
        return true;
    }

    /// <summary>D-042: 色が割り当たっていない行に出す文字列。空欄にすると
    /// 「まだ読み込めていない」のか「色が無い」のか見分けが付かず、プロセス
    /// インク(シアン・マゼンタ・イエロー)の行が全部空欄に見えてしまう。</summary>
    internal const string NoColourCellText = "(なし)";

    /// <summary>D-042: RGB 3 値を「色」列の表示文字列にする。</summary>
    internal static string FormatColorCell(int[]? rgb) =>
        rgb is null ? NoColourCellText : $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}";

    /// <summary>D-042: そのインクに実際に効くマジックカラー。ジョブごとの上書きが
    /// あればそれを、無ければパレットの magic_rgb を返す(TraySettings.ResolvePasses
    /// と同じ流儀)。</summary>
    private int[]? ResolveMagicRgb(InkDefinition def) =>
        _magicRgbOverride.TryGetValue(def.Name, out int[]? rgb) ? rgb : def.MagicRgb;

    /// <summary>D-042: 「色」列を編集したときのハンドラ。値は CellValidating で
    /// 書式を確認済み。Ghostscript を再実行せず、切り出し済みの画像を保持したまま
    /// JobPipeline.RebuildFromImage でジョブ組み立てだけをやり直す
    /// (D-028 補足 / OnPassesChangedAsync と同じ扱い)。</summary>
    private async Task OnMagicRgbChangedAsync(int rowIndex)
    {
        if (_busy || _current is null)
        {
            return;
        }
        if (rowIndex < 0 || rowIndex >= _inkGrid.Rows.Count)
        {
            return;
        }
        var row = _inkGrid.Rows[rowIndex];
        if (row.Tag is not string inkName)
        {
            return;
        }
        // CellValidating で確認済みだが、念のためもう一度検証する(黙って丸めない)。
        if (!TryParseColorCell(row.Cells["Color"].Value?.ToString(), out int[]? rgb))
        {
            return;
        }
        // 空欄(rgb == null)は「色を明示的に外す」。項目そのものを消すのは
        // 「色を既定に戻す」ボタンの役目であり、ここでは消さない。
        _magicRgbOverride[inkName] = rgb;

        await RebuildFromCurrentImageAsync();
    }

    /// <summary>D-048: 「塗る範囲」列を変えたときのハンドラ。Ghostscript を再実行せず、
    /// 切り出し済みの画像を保持したまま JobPipeline.RebuildFromImage でジョブ組み立て
    /// だけをやり直す(色・パス数の変更とまったく同じ経路。新しい分岐を作らない)。
    ///
    /// coverage でない行のセルは PopulateInkGrid が読み取り専用のテキストセルへ
    /// 差し替えてあるため、そもそもここへ来ない。それでも来た場合(将来の変更で
    /// 差し替えが漏れた場合)は、内部値へ直せない文字列として弾かれる。</summary>
    private async Task OnCoverageModeChangedAsync(int rowIndex)
    {
        if (_busy || _current is null)
        {
            return;
        }
        if (rowIndex < 0 || rowIndex >= _inkGrid.Rows.Count)
        {
            return;
        }
        var row = _inkGrid.Rows[rowIndex];
        if (row.Tag is not string inkName)
        {
            return;
        }
        // 知らない文字列は黙って "none" に落とさない(D-048)。
        if (!TryParseCoverageModeLabel(row.Cells["Coverage"].Value?.ToString(), out string mode))
        {
            return;
        }
        // "none" も明示的に記録する(D-042 が「色を外す」を null として記録するのと
        // 同じ。JobAssembly 側は "none" のインクのプレーンを作らない)。
        _coverageModes[inkName] = mode;

        await RebuildFromCurrentImageAsync();
    }

    /// <summary>D-048: 「塗る範囲」列を作る。選択肢は 3 つで、画面には日本語で出す
    /// (内部値との対応は <see cref="CoverageModeLabel"/> /
    /// <see cref="TryParseCoverageModeLabel"/> が持つ)。
    ///
    /// 列そのものはコンボだが、**coverage でない行のセルは PopulateInkGrid が
    /// 読み取り専用のテキストセルへ差し替える**(<see cref="ApplyCoverageCell"/>)。
    /// 列を丸ごとコンボにしたまま「選ばせない」を色や有効・無効で表現すると、
    /// クリックでドロップダウンが開いてしまい「選べそうなのに効かない」になる。</summary>
    internal static DataGridViewComboBoxColumn CreateCoverageColumn()
    {
        var column = new DataGridViewComboBoxColumn
        {
            Name = "Coverage",
            HeaderText = "塗る範囲",
            // 三角ボタンを常時出さない(選べない行と見分けが付くようにする)。
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            FlatStyle = FlatStyle.Flat,
        };
        foreach (string mode in TraySettings.CoverageModeValues)
        {
            column.Items.Add(CoverageModeLabel(mode));
        }
        return column;
    }

    /// <summary>D-048: 1 行ぶんの「塗る範囲」セルを整える。
    ///
    /// coverage インクの行 — コンボのまま、いま効いている値を日本語で入れる。
    /// それ以外の行   — 読み取り専用のテキストセルへ差し替え、"—" を出す。
    ///     **セルの型そのものを変える**ので、クリックしてもドロップダウンは開かない
    ///     (ReadOnly を立てるだけではセルの編集開始を止められるが、コンボの
    ///     見た目は残る)。空欄にしないのは D-042 と同じ理由 — 空欄だと
    ///     「まだ読み込めていない」のか「選べない」のか状態が読めない。
    ///
    /// PopulateInkGrid とテストの両方から呼ぶ(選べない行の作り方を 2 箇所に
    /// 書かないため)。</summary>
    internal static void ApplyCoverageCell(DataGridViewRow row, bool isCoverage, string mode)
    {
        var cell = row.Cells["Coverage"];
        if (isCoverage)
        {
            cell.Value = CoverageModeLabel(mode);
            cell.ReadOnly = false;
            return;
        }
        var textCell = new DataGridViewTextBoxCell { Value = NotCoverageCellText };
        row.Cells["Coverage"] = textCell;
        textCell.ReadOnly = true;
        textCell.Style.ForeColor = Color.Gray;
    }

    /// <summary>D-042: 「色を選ぶ...」ボタン。選択中の行に対して ColorDialog を開き、
    /// 選ばれた色をそのセルへ入れる — 値の反映は手入力とまったく同じ経路
    /// (CellValueChanged → OnMagicRgbChangedAsync)を通す。行が選ばれていなければ
    /// 何もしない。</summary>
    private void PickMagicRgbForSelectedRow()
    {
        if (_busy)
        {
            return;
        }
        var row = _inkGrid.CurrentRow;
        if (row is null || row.Index < 0 || row.Tag is not string)
        {
            return;
        }
        using var dialog = new ColorDialog { FullOpen = true };
        if (TryParseColorCell(row.Cells["Color"].Value?.ToString(), out int[]? current) && current is not null)
        {
            dialog.Color = Color.FromArgb(current[0], current[1], current[2]);
        }
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }
        var picked = dialog.Color;
        row.Cells["Color"].Value = FormatColorCell(new[] { (int)picked.R, (int)picked.G, (int)picked.B });
    }

    /// <summary>D-042: 「色を既定に戻す」ボタン。選択中の行の上書きを
    /// _magicRgbOverride から取り除き、パレット(palette/default.yaml)の
    /// magic_rgb に戻す。</summary>
    private async Task ResetMagicRgbForSelectedRowAsync()
    {
        if (_busy || _current is null)
        {
            return;
        }
        var row = _inkGrid.CurrentRow;
        if (row is null || row.Index < 0 || row.Tag is not string inkName)
        {
            // 黙って帰るとボタンが壊れているように見える。何をすればよいか伝える。
            MessageBox.Show(
                this, "先に、色を戻したいインクの行をクリックして選んでください。\n" +
                      "全部まとめて戻すなら「全部の色を既定に戻す」を押してください。",
                "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!_magicRgbOverride.Remove(inkName))
        {
            // もともと上書きが無ければ何も変わらない(再構成もしない)。
            // ここも黙って帰らない — 「押したのに戻らない」の正体になるため。
            MessageBox.Show(
                this, $"「{row.Cells["Label"].Value}」の色は、もう既定のままです(変更されていません)。",
                "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await RebuildFromCurrentImageAsync();
    }

    /// <summary>D-042: 「全部の色を既定に戻す」ボタン。上書きを全部捨てて
    /// パレット(palette/default.yaml)の magic_rgb に戻す。行を選ばなくても
    /// 押せる — 何色か試したあと元へ戻すのに、行ごとに戻して回らずに済む。
    /// 上書きが 1 件も無ければ何もしない(再構成もしない)。</summary>
    private async Task ResetAllMagicRgbAsync()
    {
        if (_busy || _current is null)
        {
            return;
        }
        if (_magicRgbOverride.Count == 0)
        {
            // 黙って帰るとボタンが壊れているように見える(選択行版と同じ理由)。
            MessageBox.Show(
                this, "色の変更はありません。すべて palette/default.yaml の既定のままです。",
                "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _magicRgbOverride.Clear();
        await RebuildFromCurrentImageAsync();
    }

    /// <summary>切り出し済みの画像を使い回したままジョブ組み立てだけをやり直す
    /// (Ghostscript は再実行しない。D-028 補足)。OnInkUseChangedAsync /
    /// OnPassesChangedAsync と同じ流儀。D-042 の色の変更と、プリセットの適用
    /// (ApplyPresetAsync)が共有する経路であり、名前を「色を変えたあと」から
    /// 一般化してある。</summary>
    private async Task RebuildFromCurrentImageAsync()
    {
        SetBusy(true, "ジョブを再構成中...");
        try
        {
            string inkMode = (string)_inkModeCombo.SelectedItem!;
            string halftone = (string)_halftoneCombo.SelectedItem!;
            string whiteMode = (string)_whiteModeCombo.SelectedItem!;
            string colourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
            var previous = _current!;

            var result = await Task.Run(() => JobPipeline.RebuildFromImage(
                previous.Image, previous.Config, previous.Resolution, inkMode, halftone, whiteMode, _usedInks,
                _passesOverride, colourCorrection, previous.AlphaImage, _magicRgbOverride, _coverageModes));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is ConfigException or PpmFormatException)
        {
            // OnPassesChangedAsync と同じ流儀。_magicRgbOverride は変更済みのまま
            // にする(利用者が選んだ色は次回の操作までそのまま保持する)。
            MessageBox.Show(this, $"ジョブの再構成に失敗しました: {Program.DescribeUserError(ex)}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>D-042: 使用中のインク 2 つ以上に同じ色が割り当たっていたら赤字で
    /// 警告する。画素がどちらのインクへ行くかは判定の順序で決まり、利用者からは
    /// 分かりにくいため — ただし**印刷は止めない**(警告のみ。D-042)。</summary>
    private void UpdateMagicRgbWarning()
    {
        var inks = _palette
            .Where(def => _usedInks.Contains(def.Name))
            .Select(def => (
                def.Name,
                Rgb: ResolveMagicRgb(def),
                IsUndercoat: def.AutoUndercoat,
                HasColourOverride: _magicRgbOverride.TryGetValue(def.Name, out int[]? o) && o is not null))
            .ToList();

        // D-048: 塗る範囲の警告も同じラベルへ相乗りさせる(警告の置き場所を
        // 2 つに増やさない)。両方出るときは 2 行以上になるが、ラベルは AutoSize
        // なので伸びて全部読める。
        var coverageInks = _palette
            .Select(def => (
                def.Name,
                def.Label,
                IsCoverage: def.Coverage,
                Used: _usedInks.Contains(def.Name),
                Mode: ResolveCoverageMode(def.Name)))
            .ToList();

        // D-051: 1200dpi は特色インクに効かない(横幅 2 倍で刷られる)。
        // 種別はパレットの印から取る — **インク名で判定しない**(DOMAIN §4.5)。
        // CMYK のいずれでもない = 特色(magic_rgb を持つ)または塗る範囲で決まるインク。
        var resolutionInks = _palette
            .Select(def => (
                def.Label,
                IsNonProcess: def.Channel is null,
                Used: _usedInks.Contains(def.Name)))
            .ToList();

        string[] warnings =
        {
            BuildMagicRgbWarning(inks, _whiteModeCombo.SelectedItem as string),
            BuildCoverageWarning(coverageInks),
            BuildResolutionWarning(_resolutionCombo.SelectedItem as string, resolutionInks),
        };
        _magicRgbWarningLabel.Text = string.Join(
            Environment.NewLine, warnings.Where(text => text.Length > 0));
    }

    /// <summary>D-048: そのインクに実際に効く塗る範囲。ジョブごとの指定があれば
    /// それを、無ければ既定の "none"(= プレーンを作らない)を返す
    /// (<see cref="ResolveMagicRgb"/> と同じ流儀)。</summary>
    private string ResolveCoverageMode(string inkName) =>
        _coverageModes.TryGetValue(inkName, out string? mode) ? mode : TraySettings.DefaultCoverageMode;

    /// <summary>D-042: 「色」の割り当てから警告文を組み立てる(画面に触らない純粋な処理。
    /// ここが検出器になるよう Form から切り出してある)。警告が無ければ空文字。
    ///
    /// 2 種類を見る:
    ///   ①同じ色が使用中のインク 2 つ以上に割り当たっている。画素がどちらへ行くかは
    ///     判定の順序で決まり、利用者からは分かりにくい。
    ///   ②下地インク(白)に色を割り当てたのに、白版モードが "none" になっている。
    ///     "none" は「白版を作らない」であり magic_rgb への直接一致すら作らないため、
    ///     **割り当てても 1 ドットも出ない**(2026-08-21 実測 / DOMAIN §14.6)。
    ///     割り当てたのに何も出ないという気づきにくい形になるので、その場で伝える。
    ///
    /// どちらも**印刷は止めない**(警告のみ。D-042)。</summary>
    internal static string BuildMagicRgbWarning(
        IReadOnlyList<(string Name, int[]? Rgb, bool IsUndercoat, bool HasColourOverride)> usedInks,
        string? whiteMode)
    {
        var messages = new List<string>();

        var duplicates = usedInks
            .Where(x => x.Rgb is not null)
            .GroupBy(x => FormatColorCell(x.Rgb), StringComparer.Ordinal)
            .Where(g => g.Count() >= 2)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(x => x.Name))})")
            .ToList();
        if (duplicates.Count > 0)
        {
            messages.Add(
                $"⚠ 同じ色が複数のインクに割り当てられています: {string.Join(" / ", duplicates)}" +
                "(使わないインクはチェックを外してください)");
        }

        if (whiteMode == "none")
        {
            var blocked = usedInks
                .Where(x => x.IsUndercoat && x.HasColourOverride)
                .Select(x => x.Name)
                .ToList();
            if (blocked.Count > 0)
            {
                messages.Add(
                    $"⚠ 白版モードが none のため、{string.Join(", ", blocked)} に割り当てた色は無視されます" +
                    "(1 ドットも刷られません)。白版モードを magic にしてください。");
            }
        }

        return string.Join(Environment.NewLine, messages);
    }

    /// <summary>D-048: 「塗る範囲」列に出す文字列(グリッドで編集できない行に出す)。
    /// 空欄にすると「まだ読み込めていない」のか「そもそも選べない」のか見分けが
    /// 付かない(D-042 の「(なし)」と同じ理由)。</summary>
    internal const string NotCoverageCellText = "—";

    /// <summary>D-048: 塗る範囲の内部値 → 画面に出す日本語。知らない値は
    /// そのまま返す(黙って「なし」に化けさせない — 化けると
    /// 「指定したのに何も出ない」を利用者が追えなくなる)。</summary>
    internal static string CoverageModeLabel(string mode) => mode switch
    {
        "none" => "なし",
        "artwork" => "絵のあるところ",
        "full" => "全面",
        _ => mode,
    };

    /// <summary>D-048: 画面に出した日本語 → 内部値。知らない文字列は
    /// **"none" に落とさず false を返し、呼び出し側に拒否させる** —
    /// 黙って既定へ落とすと「選んだのに何も出ない」という追いにくい失敗になる
    /// (この案件では白版モード none と --magic-rgb の綴り間違いで 2 回作っている)。</summary>
    internal static bool TryParseCoverageModeLabel(string? label, out string mode)
    {
        mode = TraySettings.DefaultCoverageMode;
        string value = (label ?? string.Empty).Trim();
        foreach (string candidate in TraySettings.CoverageModeValues)
        {
            if (string.Equals(CoverageModeLabel(candidate), value, StringComparison.Ordinal))
            {
                mode = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>D-048: 塗る範囲が「なし」のまま使われている coverage インクの警告
    /// (画面に触らない純粋な処理。BuildMagicRgbWarning と同じ流儀で切り出してある)。
    /// 該当が無ければ空文字。
    ///
    /// **これが要る理由:** coverage インクは magic_rgb も channel も持たないため、
    /// 「塗る範囲」を選ばない限り**チェックを入れても 1 ドットも刷られない**。
    /// 白版モードの none(D-042)とまったく同じ形の、気づきにくい失敗である。
    ///
    /// チェックの外れているインク(Used == false)と、coverage でないインクは
    /// 警告しない — 前者は刷らない意思表示であり、後者はこの列と無関係。
    /// **印刷は止めない**(警告のみ。D-042 と同じ方針)。</summary>
    internal static string BuildCoverageWarning(
        IReadOnlyList<(string Name, string Label, bool IsCoverage, bool Used, string Mode)> inks)
    {
        var messages = inks
            .Where(ink => ink.IsCoverage && ink.Used && ink.Mode == TraySettings.DefaultCoverageMode)
            .Select(ink => $"⚠ {ink.Label} は「塗る範囲」が「{CoverageModeLabel(TraySettings.DefaultCoverageMode)}」のため刷られません。")
            .ToList();

        return string.Join(Environment.NewLine, messages);
    }

    /// <summary>D-051: 1200dpi と特色・塗る範囲インクが混ざっていることの警告。
    /// 混ざっていなければ空文字(画面に触らない純粋な処理)。
    ///
    /// **実測で起きたこと(2026-08-22):** 5 層構成を 1200x600 で刷ったところ
    /// **白だけが横幅 2 倍になった**。同じ構成を 600dpi で刷ると正しく出る。
    ///
    /// **機序(§14.7.1):** プリンタは**カセットのバーコード(インク種別 ID)で走査解像度を
    /// 決めており、特色は 600dpi で走る**。インクの供給元(象のロケット)が
    /// 「特色ホワイトを 600dpi、特色インクを 1200dpi で印刷しているためにずれる」と明記し、
    /// **同じ物理カセットをバーコード 0 番へ貼り替えると 1200dpi で刷れる**という対応表も
    /// 公開している。**1200dpi 幅のドット列を 600dpi ピッチで打つから 2 倍幅になる。**
    /// ただし **ALPS の一次資料には明文の制限が無い**(公式ドライバのヘルプが挙げる
    /// 制限要因は［ドキュメント設定］と［用紙の種類］だけ)。
    ///
    /// こちらのラスタも送出バイトもインクによらず同一であることは確かめてある
    /// (1200dpi で黒だけ / 白だけのプレビューは画素単位で一致し、RGL のバイト数も一致)。
    /// 当初「特色は Standard モードなので 600dpi に落ちる」と
    /// 説明したが、**ppmtomd のソースと実走で否定された** — 印刷モードはビット深度を
    /// 決めるもので走査解像度とは無関係だった(D-051 の訂正)。
    ///
    /// **文言は観測だけを述べる。** 機構を断定すると、後で違うと分かったときに
    /// 利用者が誤った理解のまま残る。**「起きたこと」と「避け方」で足りる。**
    ///
    /// **止めはしない。** 混ぜられること自体は害ではなく、知らずに刷ることが害である。
    ///
    /// isNonProcess: そのインクが CMYK のいずれでもないこと(特色 = magic_rgb を持つ、
    /// または塗る範囲で決まるインク)。**インク名で判定しない**(DOMAIN §4.5)。</summary>
    internal static string BuildResolutionWarning(
        string? resolutionKey,
        IReadOnlyList<(string Label, bool IsNonProcess, bool Used)> inks)
    {
        // 1200 が横方向にしか効かないことは §5.5 / D-051。判定は解像度キーの先頭で足りる。
        if (resolutionKey is null || !resolutionKey.StartsWith("1200", StringComparison.Ordinal))
        {
            return string.Empty;
        }
        var affected = inks.Where(i => i.IsNonProcess && i.Used).Select(i => i.Label).ToList();
        if (affected.Count == 0)
        {
            return string.Empty;
        }
        return
            $"⚠ 1200dpi では次のインクが横幅 2 倍で刷られます: {string.Join(", ", affected)}" +
            Environment.NewLine +
            "  プリンタはカセットのバーコードで走査解像度を決めており、特色は 600dpi で走ります。" +
            Environment.NewLine +
            "  解像度を 600 にしてください(§14.7.1 / D-051)。";
    }

    /// <summary>新しい PreviewResult を画面へ反映し、古いものを破棄する
    /// (§7.2 補足: Bitmap だけでなく、切り出し済み画像・プレーンも解放する)。</summary>
    private void ApplyPreviewResult(PreviewResult result)
    {
        _current?.Dispose();
        _current = result;
        // 1 インクだけの画像は前のジョブのもの。ここで捨てる(この画面が自分で作った
        // Bitmap であり、いま捨てた _current.Preview とは別物 = 二重破棄にならない)。
        DisposeFilteredPreview();
        _previewBox.Image = result.Preview;
        _jobSummaryLabel.Text =
            $"パス数: {result.Inks.Count} / 解像度: {result.Resolution.Key} / サイズ: {result.Width}x{result.Height}";

        PopulateInkFilter(result);
        PopulateInkGrid(result);

        if (result.Inks.Count == 0)
        {
            MessageBox.Show(
                this, "印刷する内容がありません(全プレーンが空です)。", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>表示するインクの選択肢を作り直す。**必ず「すべてのインク」へ戻す** —
    /// インクの構成はプレビューを組み直すたびに変わるため、古い選択が残ると
    /// もう存在しないインクを指しうる。並びは result.Inks の順(= 印刷順)。</summary>
    private void PopulateInkFilter(PreviewResult result)
    {
        _populatingInkFilter = true;
        try
        {
            _inkFilterCombo.Items.Clear();
            _inkFilterCombo.Items.Add(new InkFilterItem(null, AllInksText));
            foreach (var ink in result.Inks)
            {
                _inkFilterCombo.Items.Add(new InkFilterItem(ink.Name, ink.Label));
            }
            _inkFilterCombo.SelectedIndex = 0;
        }
        finally
        {
            _populatingInkFilter = false;
        }

        // 「すべてのインク」へ戻したので注意文も消す。画像は ApplyPreviewResult が
        // 入れた全インクのものがそのまま正しい(描き直す必要は無い)。
        _inkFilterNoticeLabel.Text = BuildInkFilterNotice(null);
    }

    /// <summary>表示の絞り込みを画像へ反映する。**描き直すだけ** —
    /// Ghostscript もジョブ組み立ても走らせず、ジョブの中身(Planes / JobInks /
    /// RequiredInks)は変わらない(JobPipeline.RenderPreviewBitmap)。
    /// したがって、絞った状態で印刷しても刷られるものは全インクのままである。</summary>
    private void UpdatePreviewImage()
    {
        if (_populatingInkFilter || _current is null)
        {
            return;
        }

        if (_inkFilterCombo.SelectedItem is not InkFilterItem item || item.Name is null)
        {
            // 全インク表示。描き直さず、ジョブが持っている画像へ戻す。
            _previewBox.Image = _current.Preview;
            DisposeFilteredPreview();
            _inkFilterNoticeLabel.Text = BuildInkFilterNotice(null);
            return;
        }

        var bitmap = JobPipeline.RenderPreviewBitmap(_current, item.Name);
        // 画面が新しい方を指してから、古い 1 インク画像を捨てる。
        _previewBox.Image = bitmap;
        DisposeFilteredPreview();
        _filteredPreview = bitmap;
        _inkFilterNoticeLabel.Text = BuildInkFilterNotice(item.Text);
    }

    /// <summary>1 インクだけの画像を捨てる。捨てるのはこの画面が自分で作ったものだけで、
    /// PreviewResult が所有する _current.Preview には触れない(二重破棄の防止)。</summary>
    private void DisposeFilteredPreview()
    {
        _filteredPreview?.Dispose();
        _filteredPreview = null;
    }

    /// <summary>表示を 1 インクに絞っているときの注意文(画面に触らない純粋な処理。
    /// BuildMagicRgbWarning / BuildPrintConfirmText と同じ流儀で切り出してある)。
    /// 絞っていなければ空文字。
    ///
    /// **「印刷はすべてのインクで行われます」の一文を消してはならない。**
    /// 絞った状態で印刷ボタンを押した利用者が「これだけ刷られる」と誤解すると、
    /// 代替入手の困難なリボンと用紙を失う(DOMAIN §7.2)。</summary>
    internal static string BuildInkFilterNotice(string? onlyInkLabel)
    {
        if (string.IsNullOrEmpty(onlyInkLabel))
        {
            return string.Empty;
        }
        return $"※ 表示を「{onlyInkLabel}」だけに絞っています。印刷はすべてのインクで行われます。";
    }

    /// <summary>ジョブ内容のグリッドを作り直す。D-030: パレット全体を常に表示する
    /// (許可リストに無いインクも、いま原稿に現れていないインクも行を残す)。
    /// これにより、まだ一度も使われていないメタリックを「これから使う」意思表示
    /// としてチェックを入れることも、原稿にあるのに許可リストから外れている
    /// インクのチェックを外すことも、再ラスタライズ無しで自由に行き来できる。</summary>
    /// <summary>ジョブに出ていないインクの「パス数」欄に出す値。
    ///
    /// **必ず 1〜8(TraySettings.MinPasses/MaxPasses)に収まること。**
    /// この欄は編集でき、CellValidating が範囲外を拒否する。**範囲外の値を置くと、
    /// 利用者が触っていないセルでも確定も中止もできなくなり、表を作り直す
    /// Rows.Clear() が落ちる**(2026-08-22 に実機で発生。以前はここが 0 だった)。
    ///
    /// 上書きがあればそれ、無ければパレットの既定を出す — そのインクを有効に
    /// したとき実際に使われる値であり、表示として正しい。</summary>
    internal static int ResolveDisplayedPasses(
        InkDefinition def, IReadOnlyDictionary<string, int> passesOverride) =>
        passesOverride.TryGetValue(def.Name, out int overridden) ? overridden : def.Passes;

    /// <summary>「ドット数」欄の表示。**そのジョブで実際に消費する量**を出す。
    ///
    /// 版の点の数はパス数を変えても変わらない(同じ版を同じ場所へ重ねるだけ)。
    /// ところが**この道具でいちばんきつい制約はリボン**であり、刷る前に知りたいのは
    /// 「このジョブでどれだけ使うか」= 版の点の数 × パス数 のほうである。
    /// 2026-08-22 に利用者から「パス数を変えてもドット数が変わらないが、それでよいのか」
    /// と問われて足した — **疑問を持たれた時点で、列が仕事をしていなかった。**
    ///
    /// 掛け算の内訳も括弧で残す。版の形が合っているかの確認には、こちらが要る。
    /// パス数が 1 のときは掛け算を出さない(内訳が同じ数字の繰り返しになるため)。</summary>
    internal static string FormatDotCount(long dotsPerPass, int passes)
    {
        if (passes <= 1)
        {
            return dotsPerPass.ToString("N0");
        }
        long total = dotsPerPass * passes;
        return $"{total:N0} ({dotsPerPass:N0}×{passes})";
    }

    private void PopulateInkGrid(PreviewResult result)
    {
        // チェックボックスのセルが編集中(コミット直後で IsCurrentCellInEditMode
        // が残っている場合がある)のまま Rows.Clear() で行を消すと、
        // DataGridView の内部状態(現在セル・編集コントロール)が不正になる。
        // 行を作り直す前に必ず編集を終了させておく。
        //
        // **EndEdit() だけでは足りない。** 現在セルの値が CellValidating を通らない
        // 場合、EndEdit は確定できずに false を返し、続く Rows.Clear() が
        // 「セル値の変更をコミットまたは中止できないため、操作は成功しませんでした」
        // で落ちる(2026-08-22 に実機で発生)。値の側は「入力欄に不正な値を置かない」
        // ことで直したが、**確定できないセルが残っていても表の作り直しは通るべき**
        // なので、通らなかったら編集を捨てて現在セルも外す。
        if (!_inkGrid.EndEdit())
        {
            _inkGrid.CancelEdit();
            _inkGrid.CurrentCell = null;
        }

        // Rows.Add/Clear と「塗る範囲」セルへの値の書き込みは CellValueChanged を
        // 発火させる。_busy だけでは足りない(ハンドラは BeginInvoke で後回しにされ、
        // そのときには _busy が下りている)ので、作り直しの間は専用のフラグで抑える。
        _populatingInkGrid = true;
        try
        {
            _inkGrid.Rows.Clear();

            var activeByName = result.Inks.ToDictionary(ink => ink.Name);
            var rows = new List<(string Name, int Order, string Label, int Passes, Color Color, bool Used, bool Appeared, long DotCount, string MagicText, bool IsCoverage, string CoverageMode)>();
            foreach (var def in result.Config.Palette)
            {
                bool used = _usedInks.Contains(def.Name);
                long dotCount = result.Planes.TryGetValue(def.Name, out var plane) ? CountSetBits(plane) : 0;
                // D-042: 「色」列にはそのインクに実際に効くマジックカラーを出す
                // (上書きがあれば上書き後、無ければパレットの magic_rgb。色なしは空文字)。
                string magicText = FormatColorCell(ResolveMagicRgb(def));
                // D-048: coverage インクかどうかはパレットの印から取る(名前で判定しない。
                // DOMAIN §4.5)。塗る範囲はジョブごとの指定、無ければ既定の "none"。
                string coverageMode = ResolveCoverageMode(def.Name);
                if (activeByName.TryGetValue(def.Name, out var active))
                {
                    rows.Add((def.Name, def.Order, active.Label, active.Passes, active.Color, used, true, dotCount, magicText, def.Coverage, coverageMode));
                }
                else
                {
                    // ジョブに出ていないインクでも、パス数の欄には**必ず 1〜8 の値**を置く。
                    //
                    // 以前はここが 0 だった。ところが「パス数」は編集できる欄で、
                    // CellValidating が 1〜8 の外を拒否する。**利用者が触っていない 0 の
                    // セルへ現在セルが移ると、そのセルを確定も中止もできなくなり、
                    // 表を作り直す Rows.Clear() が
                    // 「セル値の変更をコミットまたは中止できないため、操作は成功しませんでした」
                    // で落ちる**(2026-08-22 に実機で発生。別の行のパス数を変えた直後、
                    // Enter で選択が 0 の行へ移ったことが引き金だった)。
                    //
                    // **入力欄に、入力として不正な値を置かない。** そのインクを有効にしたら
                    // 実際に使われる値(上書きがあればそれ、無ければパレットの既定)を出す。
                    // ジョブに出ているかどうかは「ドット数」と行の灰色が示す。
                    int inactivePasses = ResolveDisplayedPasses(def, _passesOverride);
                    rows.Add((def.Name, def.Order, def.Label, inactivePasses, PreviewRenderer.ResolveDisplayColor(def), used, false, dotCount, magicText, def.Coverage, coverageMode));
                }
            }

            foreach (var row in rows.OrderBy(r => r.Order))
            {
                // D-038: 桁区切り(例 181,422)で表示する。
                // D-048: 「塗る範囲」の値はセルの型ごと決まるため、ここでは空のまま
                // 足して ApplyCoverageCell に任せる(コンボの選択肢に無い値を先に
                // 入れると DataError になる)。
                int rowIndex = _inkGrid.Rows.Add(
                    row.Used, row.Order, row.MagicText, row.Label, row.Passes, null!,
                    FormatDotCount(row.DotCount, row.Passes));
                var gridRow = _inkGrid.Rows[rowIndex];
                ApplyCoverageCell(gridRow, row.IsCoverage, row.CoverageMode);
                // D-042: セルの背景色は「プレビューでそのインクを描いている色」のまま
                // にする(凡例としての役割。上書きしても変えない — JobPipeline が
                // 表示色を上書き前のパレットから引くのと揃える)。文字は背景に埋もれ
                // ないよう明暗で反転させる。
                gridRow.Cells["Color"].Style.BackColor = row.Color;
                gridRow.Cells["Color"].Style.ForeColor = ContrastingTextColor(row.Color);
                gridRow.Tag = row.Name;
                // ジョブに現れないインク(D-030: チェックが外れている、または内容が
                // 空)の行は灰色で並べる。パス数は 0。
                if (!row.Appeared)
                {
                    gridRow.DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
        }
        finally
        {
            _populatingInkGrid = false;
        }

        // D-042: 色の重複警告は、グリッドを作り直すたびに出し直す。
        // D-048: 塗る範囲の警告も同じラベルへ相乗りする。
        UpdateMagicRgbWarning();
    }

    /// <summary>D-042: 背景色の上に置く文字の色。明るい背景なら黒、暗い背景なら白
    /// (「色」列は背景に実際の色を出すため、同系色の文字だと読めなくなる)。</summary>
    private static Color ContrastingTextColor(Color background) =>
        (background.R + background.G + background.B) / 3 >= 128 ? Color.Black : Color.White;

    /// <summary>D-038: プレーン(1 ビット = 1 ドット)の立っているビット数を数える。
    /// 「刷る前に白の量を確認する」に使う(DOMAIN §10.5)。</summary>
    private static long CountSetBits(byte[] plane)
    {
        long count = 0;
        foreach (byte b in plane)
        {
            count += System.Numerics.BitOperations.PopCount(b);
        }
        return count;
    }

    /// <summary>1 部ぶんのリボン消費の記録を組み立てる(インク 1 色につき 1 レコード)。
    /// ドット数は「刷る前に量を確認する」のと同じ数え方(<see cref="CountSetBits"/>)を
    /// そのまま使う — 数え方を二重に書くと、片方だけ直したときに記録が黙って嘘になる。
    ///
    /// **プレーンが空(ドット 0)のインクは記録しない。** 選択コマンドは出ても点は
    /// 打っていないため、消費として数える意味がない。</summary>
    private static List<UsageRecord> BuildUsageRecords(
        PreviewResult current, int copy, int copies,
        string paper, string media, string resolution, string outcome)
    {
        var now = DateTimeOffset.UtcNow;
        var records = new List<UsageRecord>();
        foreach (var ink in current.JobInks)
        {
            if (!current.Planes.TryGetValue(ink.Name, out var plane))
            {
                continue;
            }
            long dots = CountSetBits(plane);
            if (dots == 0)
            {
                continue;
            }
            records.Add(new UsageRecord
            {
                Timestamp = now,
                Ink = ink.Name,
                Dots = dots,
                // D-031: 重ね塗りの回数。消費は Dots × Passes で効く。
                Passes = ink.Passes,
                Copy = copy,
                Copies = copies,
                Paper = paper,
                Media = media,
                Resolution = resolution,
                Outcome = outcome,
            });
        }
        return records;
    }

    /// <summary>リボン消費の記録を見せる。窓そのものは UsageDialog にある
    /// (タスクトレイのメニューからも同じ窓を開くため。D-046)。ここでやるのは
    /// 「いま読み込んでいるパレットから表示名を引けるようにする」ことだけ。</summary>
    private void ShowUsageDialog()
    {
        // インクの表示名(label)は、いま読み込んでいるパレットから引く。引けない
        // ものは記録に入っている識別子をそのまま出す — 古い記録や、いまのパレットに
        // 無いインクを落とさないため。
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_current is not null)
        {
            foreach (var def in _current.Config.Palette)
            {
                labels[def.Name] = def.Label;
            }
        }

        UsageDialog.Show(this, labels);
    }

    private async Task RefreshStatusAsync()
    {
        if (_busy)
        {
            return;
        }
        SetBusy(true, "状態を読み取り中...");
        try
        {
            string machine = (string)_machineCombo.SelectedItem!;
            var route = MachineRoute.Resolve(machine);
            var status = await Task.Run(() => JobPipeline.ReadRawStatus(route, route.Vid));
            string raw =
                $"ヘッダ: {Convert.ToHexString(status.Header)}\r\n" +
                $"状態バイト: 0x{status.StatusByte:x2}\r\n" +
                "スロット(先頭バイトがバーコード番号、0xff = 未装着):\r\n" +
                string.Join(
                    "\r\n",
                    status.SlotBarcodes.Select((b, i) =>
                        $"  slot[{i,2}] = 0x{b:x2}{(i == CassetteStatus.HeadSlotIndex ? "  <- ヘッドに装着中" : string.Empty)}"));

            _statusText.Text = raw + "\r\n\r\n" + BuildCassetteCheckText(status);
        }
        catch (TransportException ex)
        {
            _statusText.Text = $"状態の読み取りに失敗しました: {ex.Message}";
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    // §7.3 / D-026: ジョブが必要とするインクと状態応答の装填状況を突き合わせ、
    // 利用者に見せる文言を組み立てる。エラー中や barcode 未設定のインクは
    // 「足りない」と誤って言わないよう、判定不能であることを明示する。
    private string BuildCassetteCheckText(CassetteStatus status)
    {
        if (_current is null)
        {
            return "カセットの過不足: このジョブの内容が未確定のため判定できません(プレビューの作成を待ってください)。";
        }

        var result = CassetteCheck.Evaluate(_current.RequiredInks, status);

        string line;
        switch (result.Status)
        {
            case CassetteCheckStatus.Indeterminate:
                return "カセットの過不足: 判定できません(エラー中のため、カセット情報が現物と一致しない可能性があります)。";
            case CassetteCheckStatus.Sufficient:
                line = "カセットの過不足: 必要なインクはすべて装填されています。";
                break;
            default:
                string missingLabels = string.Join("、", result.MissingInks.Select(i => i.Label));
                line = $"カセットの過不足: 不足しているインクがあります — {missingLabels}";
                break;
        }

        if (result.UndeterminableInks.Count > 0)
        {
            string undeterminableLabels = string.Join("、", result.UndeterminableInks.Select(i => i.Label));
            line += $"\r\n(バーコード未設定のため判定できないインク: {undeterminableLabels})";
        }

        return line;
    }

    private async Task PrintAsync()
    {
        if (_busy || _current is null || _current.Inks.Count == 0)
        {
            return;
        }

        // D-044: 部数はこのジョブ限りの量であり、保存しない(毎回 1 に戻る)。
        int copies = (int)_copiesUpDown.Value;
        bool stopBetweenCopies = _stopBetweenCopiesCheck.Checked;

        var confirm = MessageBox.Show(
            this,
            BuildPrintConfirmText(copies, stopBetweenCopies),
            "Foilwright — 印刷確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        // DOMAIN §15.2.1: 送出中はトレイアプリが排他的に所有する。busy の間は
        // 機種・インク指定方式の変更、状態問い合わせを一切許可しない
        // (SetBusy が UI 側をすべて無効化する)。
        SetBusy(true, "印刷中...");
        try
        {
            string machine = (string)_machineCombo.SelectedItem!;
            string paperName = ((PaperItem)_paperCombo.SelectedItem!).Name;
            string mediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
            // 消費の記録に残す解像度も、送出に使う値(コンボの選択値)から取る。
            // _settings.ResolutionKey(保存済みの既定値)から取ると、切り替えた
            // ときに記録だけが古い値のままになる — §15.10.2 と同じ理由。
            string resolutionKey = (string)_resolutionCombo.SelectedItem!;
            var route = MachineRoute.Resolve(machine);
            // §15.10.2: 送出する用紙は必ずコンボの選択値と一致させる。ここが
            // _settings.PaperName(保存済みの既定値)のままだと、プレビューで
            // 用紙を切り替えても実際の送出は古い既定値のまま行われ、
            // プレビューと送出結果がずれる(切り出し位置がずれる実害)。
            var config = JobPipeline.LoadJobConfig(_assetRoot, route, paperName, mediaName);
            var job = new PrintJob
            {
                // Emitter.EmitJob は Paper を常に 600dpi 基準の値として受け取り、
                // Resolution に応じた換算を内部で行う(config.Paper を未換算のまま渡す)。
                Resolution = _current.Resolution.DpiX,
                Paper = config.Paper,
                Media = config.Media,
                Inks = _current.JobInks,
                Width = _current.Width,
                Height = _current.Height,
                NoCurlCorrection = _noCurlCheck.Checked,
            };
            var planes = _current.Planes;

            // D-044: 部数ぶん繰り返す。planes / job は送出で消費されないので
            // そのまま使い回せる(Ghostscript を走らせ直す必要も無い)。
            _copyTotal = copies;
            for (int copy = 1; copy <= copies; copy++)
            {
                _copyIndex = copy;

                // 2 部目以降は見張りが進捗バーを隠したままなので、出し直して 0 に戻す。
                _progressBar.Value = 0;
                _progressBar.Visible = true;

                try
                {
                    await Task.Run(() => JobPipeline.Print(
                        planes, job, route, route.Vid,
                        (done, total) => BeginInvoke(() =>
                        {
                            _progressBar.Maximum = Math.Max(total, 1);
                            _progressBar.Value = Math.Min(done, _progressBar.Maximum);
                        })));
                }
                catch
                {
                    // 送出の途中で落ちても、**そこまでに送ったぶんは刷られている**
                    // 可能性がある(プリンタは受け取った分を溜めて刷り続ける。
                    // §15.2.2)。記録を残さないと、その消費が帳簿から消えてしまう。
                    // 記録してから、例外はそのまま外の catch へ通す。
                    UsageLog.Append(BuildUsageRecords(
                        _current, copy, copies, paperName, mediaName, resolutionKey,
                        UsageLog.OutcomeFailed));
                    throw;
                }

                // D-038: 送出が終わっても印刷はまだこれから進む(プリンタは受け取った
                // 分を溜めて刷り続ける)。ここで閉じずに見張りへ移る。送出中に状態を
                // 読んではならない(§15.2.1)ため、見張りは Print が返った後にしか
                // 始められない。
                // D-044: 最後の部だけ従来どおり自動で閉じる(D-039)。
                bool ok = await MonitorPrintCompletionAsync(route, isLastCopy: copy == copies);

                // リボン消費の記録。**成功でも失敗でも書く** — 途中で止まっても
                // リボンは減っているため(DOMAIN §11.4.3: プリンタの残量応答は
                // 意味が未解明で当てにならないので、自分で数えたものだけが頼り)。
                // 中止・エラーで break する前に置くこと。書き込みで例外が出ても
                // UsageLog.Append の中で握りつぶすので、印刷は止まらない。
                UsageLog.Append(BuildUsageRecords(
                    _current, copy, copies, paperName, mediaName, resolutionKey,
                    ok ? UsageLog.OutcomeCompleted : UsageLog.OutcomeFailed));

                if (!ok)
                {
                    // D-044 決定 4: 途中でエラー(や打ち切り・上限時間)が出たら残りを中止する。
                    // 何部刷って何部やめたかを画面に残す — 黙って終わらない。
                    if (copies > 1)
                    {
                        // 見張りが残した文言(エラーの中身・上限時間切れ等)は消さずに
                        // 下へ足す — 原因が読めなくなると直しようが無い。
                        _monitorStatusLabel.Text += Environment.NewLine + BuildCopiesStoppedText(
                            copy - 1, copies, "印刷の完了を確認できませんでした");
                    }
                    break;
                }

                // D-044 改訂: 確認が OFF のときは止まらずに次の部を送る。
                // 手差しのままこれを選ぶと紙が無い状態で機構が動くため、
                // 危険は確認ダイアログで先に伝えてある(BuildPrintConfirmText)。
                if (copy < copies && stopBetweenCopies)
                {
                    // D-044 決定 2: この機械は手差し運用(給紙レバー M)であり、紙が無い状態で
                    // 連続送出すると給紙エラーで機構が動き、詰まる危険がある。次の紙が
                    // 入ったことを人に確認してもらうまで次の部を送らない。
                    var next = MessageBox.Show(
                        this,
                        BuildNextCopyPrompt(copy, copies),
                        "Foilwright — 次の紙を入れてください",
                        MessageBoxButtons.OKCancel,
                        MessageBoxIcon.Information);
                    if (next != DialogResult.OK)
                    {
                        _monitorStatusLabel.Text += Environment.NewLine + BuildCopiesStoppedText(
                            copy, copies, "利用者が中止しました");
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is TransportException or ConfigException)
        {
            MessageBox.Show(this, $"印刷に失敗しました: {ex.Message}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            // D-044: ループを抜けたら(全部刷った/エラー/中止/例外のいずれでも)
            // 「部が残っている」状態を必ず解く。ここで戻さないと、途中で中止した
            // ときに HasRemainingCopies が true のまま残り、**窓が二度と閉じられなく
            // なる**(FormClosing がずっと止め続ける)。
            _copyTotal = 1;
            _copyIndex = 1;
            // 最後の部が成功すると、見張りの中で 3 秒待ってから Close() している
            // (D-039)。その場合ここへ来たときには窓が閉じ始めているため、
            // 破棄済みのコントロールに触らないよう確認する(1615 行付近と同じ流儀)。
            if (!IsDisposed)
            {
                _monitorCloseButton.Enabled = true;
                SetBusy(false, string.Empty);
            }
        }
    }

    /// <summary>D-038: 送出後、印刷が終わるまでプレビューを開いたまま見張る。
    /// 4 秒おきに状態(`05 01`)を読み、StatusDecoder.Describe の結果を
    /// PrintWatchDecision に渡して次の一手(継続/完了/エラー)を決める。
    /// 上限時間・中止ボタン・猶予期間は下の定数を参照。
    ///
    /// D-044: 戻り値は「正常に刷り終わったか」。エラー・打ち切り・上限時間切れは false で、
    /// 呼び出し元(PrintAsync)は残りの部を中止する。isLastCopy が false のときは
    /// 自動で閉じない — まだ次の部を送るため。</summary>
    private async Task<bool> MonitorPrintCompletionAsync(MachineRoute route, bool isLastCopy)
    {
        // D-038: 4 秒周期は純正のステータスモニタと同じ(§11.1.1 の USBPcap 採取で確認済み)。
        const int PollIntervalMs = 4_000;
        // D-038: 送出直後はまだ印刷が始まっておらず、状態バイトが「待機」の
        // ことがある。いきなり「完了」と判定しないための猶予
        // 【推測: 20 秒という具体的な長さは実測の裏付けが無い。送出〜印字開始の
        // 遅延がこの範囲に収まるだろうという見込みで決めた値】。
        const int GraceMs = 20_000;
        // D-038: 上限時間。超えたら黙って待ち続けず「確認できませんでした」と出す。
        const int TimeoutMs = 15 * 60_000;

        _monitoring = true;
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;

        _progressBar.Visible = false;
        _monitorGroup.Visible = true;
        _monitorAbortButton.Enabled = true;
        _monitorCloseButton.Enabled = false;
        _monitorStatusLabel.Text = $"経過: 00:00{CopyProgressSuffix()} — 見張りを開始しました。";

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string resultText;
        // D-039: 正常に完了したときだけ自動で閉じる(利用者からの要望 —
        // 「正常終了したときには消えてくれないの?」。紙が出てくるので結果は
        // 目に見えており、待たせる理由が無い)。エラー・打ち切り・上限時間の
        // ときは見逃すと困るため、これまでどおり開いたままにし「閉じる」
        // ボタンを待つ。
        bool completedSuccessfully = false;
        try
        {
            while (true)
            {
                if (stopwatch.ElapsedMilliseconds >= TimeoutMs)
                {
                    resultText = "印刷の完了を確認できませんでした(上限の 15 分を超えました)。";
                    break;
                }
                if (token.IsCancellationRequested)
                {
                    resultText = "見張りを中止しました。印刷そのものは止まっていません(見張りをやめただけです)。";
                    break;
                }

                _monitorStatusLabel.Text = $"経過: {FormatElapsed(stopwatch.Elapsed)}{CopyProgressSuffix()} — 状態を確認中...";

                PrinterStatusReport? report = null;
                try
                {
                    var status = await Task.Run(() => JobPipeline.ReadRawStatus(route, route.Vid));
                    report = StatusDecoder.Describe(status);
                }
                catch (TransportException ex)
                {
                    // 読み取りに失敗しても見張りは続ける(一時的な通信の乱れの
                    // 可能性がある)。次の周期でまた読み直す。
                    _monitorStatusLabel.Text =
                        $"経過: {FormatElapsed(stopwatch.Elapsed)}{CopyProgressSuffix()} — 状態の読み取りに失敗しました: {ex.Message}";
                }

                if (report is not null)
                {
                    bool graceActive = stopwatch.ElapsedMilliseconds < GraceMs;
                    var outcome = PrintWatchDecision.Evaluate(report, graceActive);
                    if (outcome == PrintWatchOutcome.Error)
                    {
                        resultText = $"エラー — {report.ErrorDetail}";
                        break;
                    }
                    if (outcome == PrintWatchOutcome.Completed)
                    {
                        resultText = "印刷が完了しました。";
                        completedSuccessfully = true;
                        break;
                    }
                    _monitorStatusLabel.Text =
                        $"経過: {FormatElapsed(stopwatch.Elapsed)}{CopyProgressSuffix()} — 直近の状態: {report.StatusSummary}";
                }

                try
                {
                    await Task.Delay(PollIntervalMs, token);
                }
                catch (OperationCanceledException)
                {
                    // ループ先頭の token.IsCancellationRequested 判定で中止として扱う。
                }
            }
        }
        finally
        {
            _monitoring = false;
            _monitorAbortButton.Enabled = false;
            // D-044: まだ次の部が残っているなら「閉じる」も出さない。ここを
            // 無条件に true にすると、部と部のあいだだけ押せる状態が生まれる
            // (FormClosing 側でも止めてはいるが、押せるボタンが何も起きない
            // のは分かりにくい)。閉じられるのは最後の部が終わってから。
            _monitorCloseButton.Enabled = isLastCopy;
            _monitorCts?.Dispose();
            _monitorCts = null;
        }

        _monitorStatusLabel.Text = $"経過: {FormatElapsed(stopwatch.Elapsed)}{CopyProgressSuffix()} — {resultText}";

        // D-044: まだ次の部が残っているときは閉じない。閉じるのは最後の部だけ。
        if (completedSuccessfully && isLastCopy)
        {
            // D-039: すぐには閉じない — 結果の文言を読めるだけの間を置く。
            //
            // **当初 3 秒にしたが短すぎた。** 刷り終わった直後に確かめたいものが
            // 文言だけではなかった — 「リボン消費を見る」「状態を読む」に手が届かず、
            // 2026-08-22 に 3 回続けて取り逃がした(1200dpi の切り分けの最中)。
            // どちらもタスクトレイのメニューへ出したうえで、**ここも 15 秒に延ばす**
            // (利用者の判断)。放っておけば閉じる、という性質は保つ。
            //
            // **待つ前に「処理中」を解く。** ここへ来た時点で送出も見張りも終わって
            // おり、SetBusy(true) を掛けたままだと**画面の全ボタンが押せないまま 15 秒
            // 過ぎる** — 延ばした意味が無くなる(3 秒のときは短くて気づかなかった。
            // 2026-08-22 に利用者から「状態を見るボタンが押せない」と指摘された)。
            //
            // ただし**印刷開始だけは伏せたままにする。** ここで押されると、
            // 数秒後の自動クローズが送出の途中に重なる。
            SetBusy(false, string.Empty);
            _printButton.Enabled = false;
            await Task.Delay(AutoCloseDelayMs);
            // 待っている間に利用者が「閉じる」やタイトルバーの × を押して
            // 既に閉じている場合がある(FormClosing は _monitoring=false の
            // 今は通す)。二重に Close すると例外になるため確認する。
            if (!IsDisposed)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        return completedSuccessfully;
    }

    /// <summary>D-044: 見張りの表示に添える「N 部目 / 全 M 部」。部数が 1 のときは
    /// 空文字を返し、表示を今までどおりに保つ(1 部しか刷らない人の画面を変えない)。</summary>
    private string CopyProgressSuffix() =>
        _copyTotal <= 1 ? string.Empty : $"({_copyIndex} 部目 / 全 {_copyTotal} 部)";

    /// <summary>D-044: 印刷前の確認文。部数が 1 のときは従来の文言のまま。
    /// 2 部以上のときは、部数・1 部ごとに止まること・カセット交換が部数ぶん増えることを
    /// 伝える(D-044 補足: 5 部刷れば交換も 5 回。機械の摩耗としては安くない)。
    /// 画面に触らない純粋な処理として切り出してあり、ここが検出器になる。</summary>
    internal static string BuildPrintConfirmText(int copies, bool stopBetweenCopies = true)
    {
        const string baseText =
            "プレビューのとおりに印刷します。よろしいですか?\n" +
            "(マジックカラー方式は誤爆するとリボンと用紙を失います。プレビューを確認してください)";
        if (copies <= 1)
        {
            return baseText;
        }

        // D-044 改訂: 止まらない設定のときは、そのことと危険をはっきり伝える。
        // 手差しのままこれを選ぶと、紙が無い状態で次の部が送られて給紙エラーに
        // なり、機構が動いて詰まる(§11.1.1 / D-044)。
        string mode = stopBetweenCopies
            ? "1 部ごとに止まります。次の紙を入れてから続きを進めてください。"
            : "1 部ずつの確認は行いません。続けて " + copies + " 部を送ります。\n" +
              "紙が自動で送られない場合は給紙エラーになり、紙詰まりの危険があります。";

        return
            $"プレビューのとおりに {copies} 部印刷します。よろしいですか?\n" +
            "(マジックカラー方式は誤爆するとリボンと用紙を失います。プレビューを確認してください)\n" +
            "\n" +
            mode + "\n" +
            $"カセットの交換も {copies} 回ぶん増えます。";
    }

    /// <summary>D-044: 1 部刷り終わったあと、次の紙を入れてもらうための文。
    /// この機械は手差し運用(給紙レバー M)であり、紙が無い状態で送ると詰まる危険がある。</summary>
    internal static string BuildNextCopyPrompt(int finished, int total) =>
        $"{finished} 部目が終わりました(全 {total} 部)。\n" +
        $"次の紙を入れてから「OK」を押してください。残り {total - finished} 部です。\n" +
        "やめるときは「キャンセル」を押してください。";

    /// <summary>D-044: 途中でやめたときに残す文。何部刷って何部やめたかを画面に残す
    /// (黙って終わらない)。</summary>
    internal static string BuildCopiesStoppedText(int finished, int total, string reason) =>
        $"{total} 部のうち {finished} 部を刷ったところで中止しました({reason})。";

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.ToString(@"mm\:ss");

    private void SetBusy(bool busy, string statusMessage)
    {
        _busy = busy;
        _machineCombo.Enabled = !busy;
        _inkModeCombo.Enabled = !busy;
        _resolutionCombo.Enabled = !busy;
        _paperCombo.Enabled = !busy;
        _mediaCombo.Enabled = !busy;
        _halftoneCombo.Enabled = !busy;
        _whiteModeCombo.Enabled = !busy;
        _colourCorrectionCombo.Enabled = !busy;
        _noCurlCheck.Enabled = !busy;
        _saveDefaultsButton.Enabled = !busy;
        // プリセットも他の設定と同じ扱い(送出・再構成の最中は触らせない)。
        _presetCombo.Enabled = !busy;
        // 表示の絞り込みも他の操作と同じ扱い(送出・再構成の最中は触らせない)。
        _inkFilterCombo.Enabled = !busy;
        _savePresetButton.Enabled = !busy;
        _deletePresetButton.Enabled = !busy;
        _statusRefreshButton.Enabled = !busy;
        // リボン消費の窓も他の操作と同じ扱い(送出・再構成の最中は開かせない)。
        _usageButton.Enabled = !busy;
        // D-028: 再構成中はチェック列(除外の切り替え)を編集不可にする。
        _inkGrid.Columns["Use"]!.ReadOnly = busy;
        // D-031: 再構成中はパス数列も編集不可にする(Use 列と同じ扱い)。
        _inkGrid.Columns["Passes"]!.ReadOnly = busy;
        // D-042: 再構成中は「色」列と色の操作ボタンも触れなくする(Use 列と同じ扱い)。
        _inkGrid.Columns["Color"]!.ReadOnly = busy;
        // D-048: 「塗る範囲」列も同じ扱い。ただし列の ReadOnly を false に戻すと
        // **各セルの ReadOnly も一括で false に戻る**ため、coverage でない行の
        // 「選べない」を必ず付け直す(付け直さないと再構成のたびに全行が選べる
        // ようになり、選べないはずの行でドロップダウンが開く)。
        _inkGrid.Columns["Coverage"]!.ReadOnly = busy;
        if (!busy)
        {
            RestoreCoverageCellReadOnly();
        }
        _pickColorButton.Enabled = !busy;
        _resetColorButton.Enabled = !busy;
        _resetAllColorsButton.Enabled = !busy;
        _cancelButton.Enabled = !busy;
        // D-044: 送出中に部数を変えられると、途中で刷る枚数が変わって混乱する。
        _copiesUpDown.Enabled = !busy;
        _stopBetweenCopiesCheck.Enabled = !busy;
        _printButton.Enabled = !busy && _current is { Inks.Count: > 0 };
        Text = busy && statusMessage.Length > 0
            ? $"Foilwright — 印刷プレビュー ({statusMessage})"
            : "Foilwright — 印刷プレビュー";
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    /// <summary>D-048: coverage でない行の「塗る範囲」セルを読み取り専用に戻す。
    /// 列全体の ReadOnly を false にすると各セルの ReadOnly も一括で解除されるため、
    /// SetBusy(false) のたびに呼ぶ。セルの型(テキストセルへの差し替え)は
    /// PopulateInkGrid が済ませているので、ここでは ReadOnly だけを付け直す。</summary>
    private void RestoreCoverageCellReadOnly()
    {
        foreach (DataGridViewRow row in _inkGrid.Rows)
        {
            if (row.Cells["Coverage"] is not DataGridViewComboBoxCell cell)
            {
                row.Cells["Coverage"].ReadOnly = true;
            }
            else
            {
                cell.ReadOnly = false;
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeFilteredPreview();
        _current?.Dispose();
        base.OnFormClosed(e);
    }
}
