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
    private readonly string _repoRoot;

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
    private readonly Label _jobSummaryLabel;
    private readonly DataGridView _inkGrid;
    private readonly TextBox _statusText;
    private readonly Button _statusRefreshButton;
    private readonly ProgressBar _progressBar;
    private readonly Button _printButton;
    private readonly Button _cancelButton;

    private PreviewResult? _current;
    private bool _busy;

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
        _repoRoot = JobPipeline.FindRepoRoot();

        // D-030: パレットは機種・メディア・用紙に依存しないため、ここで
        // 一度だけ読み、許可リストの既定値解決に使う。
        _palette = ConfigLoader.LoadPalette(Path.Combine(_repoRoot, "palette", "default.yaml"));
        _usedInks = settings.ResolveUsedInks(_palette);
        // D-031: null(一度も触っていない)は空辞書として扱う — 空辞書は
        // 「このジョブでは上書き無し」を意味し、パレットの既定値がそのまま使われる。
        _passesOverride = settings.PassesOverride is { } passesOverride
            ? new Dictionary<string, int>(passesOverride)
            : new Dictionary<string, int>();

        Text = "Foilwright — 印刷プレビュー";
        Width = 1200;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        Controls.Add(root);

        // --- 左: プレビュー画像 ---------------------------------------------
        _previewBox = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.Gray,
        };
        var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        previewPanel.Controls.Add(_previewBox);
        root.Controls.Add(previewPanel, 0, 0);

        // --- 右: 設定・ジョブ内容・状態・操作 ---------------------------------
        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(8),
            AutoSize = false,
        };
        root.Controls.Add(right, 1, 0);

        // 設定(§7.1: ジョブごとの上書き)
        // 行を 1 つ増やした分だけ高さも足す(TableLayoutPanel は Dock=Fill なので、
        // ここを据え置くと最下段の保存ボタンが押し出されて見えなくなる)。
        var settingsGroup = new GroupBox { Text = "設定(このジョブに適用)", Dock = DockStyle.Top, Height = 355 };
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
        var jobGroup = new GroupBox { Text = "ジョブ内容", Dock = DockStyle.Top, Height = 240 };
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
        _inkGrid.Columns["Order"]!.ReadOnly = true;
        _inkGrid.Columns["Color"]!.ReadOnly = true;
        _inkGrid.Columns["Label"]!.ReadOnly = true;
        // D-031: パス数(重ね塗り回数)を編集可能にする。範囲は 1〜8
        // (TraySettings.MinPasses/MaxPasses)で、CellValidating がその場で拒否する。
        var passesColumn = _inkGrid.Columns["Passes"]!;
        passesColumn.ReadOnly = false;
        // チェックボックス列は確定(コミット)が 1 セル遅れる既知の挙動があるため、
        // CurrentCellDirtyStateChanged で即座にコミットしてから CellValueChanged を拾う。
        _inkGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_inkGrid.IsCurrentCellDirty && _inkGrid.CurrentCell is DataGridViewCheckBoxCell)
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
            if (e.RowIndex < 0 || e.ColumnIndex != passesColumn.Index)
            {
                return;
            }
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
        };
        _inkGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0)
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
        };
        jobGroup.Controls.Add(_inkGrid);
        jobGroup.Controls.Add(_jobSummaryLabel);
        right.Controls.Add(jobGroup);

        // プリンタ状態(§7.2 の 7)+ カセットの過不足表示(§7.3 / D-026)。
        var statusGroup = new GroupBox { Text = "プリンタ状態 / カセットの過不足", Dock = DockStyle.Top, Height = 190 };
        var statusLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        statusLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _statusRefreshButton = new Button { Text = "状態を読む(05 01)", AutoSize = true };
        _statusRefreshButton.Click += async (_, _) => await RefreshStatusAsync();
        statusLayout.Controls.Add(_statusRefreshButton, 0, 0);
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

        var buttonPanel = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.RightToLeft, Height = 44, AutoSize = false };
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
        right.Controls.Add(buttonPanel);

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
            var profile = ConfigLoader.LoadProfile(Path.Combine(_repoRoot, "profiles", route.ProfileFileName));
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
            var profile = ConfigLoader.LoadProfile(Path.Combine(_repoRoot, "profiles", route.ProfileFileName));
            var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(_repoRoot, "papers"));
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
            var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(_repoRoot, "media.yaml"));
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
        _settings.Save();
        MessageBox.Show(this, "既定値として保存しました。", "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // §7.2: プレビューは必須。誤爆はリボンと用紙を失うため、印刷開始できる
    // 状態は必ずこの再変換を経てから決める(古いプレビューのまま印刷ボタンを
    // 有効化しない)。
    private async Task RefreshPreviewAsync()
    {
        if (_busy)
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
                _psPath, _repoRoot, route, inkMode, paperName, mediaName, resolutionKey, halftone, whiteMode,
                _usedInks, _passesOverride, colourCorrection));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is GhostscriptException or ConfigException or PpmFormatException)
        {
            _current?.Dispose();
            _current = null;
            MessageBox.Show(this, $"プレビューの作成に失敗しました: {ex.Message}", "Foilwright",
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
                _passesOverride, colourCorrection));

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
            MessageBox.Show(this, $"ジョブの再構成に失敗しました: {ex.Message}", "Foilwright",
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
                _passesOverride, colourCorrection));

            ApplyPreviewResult(result);
        }
        catch (Exception ex) when (ex is ConfigException or PpmFormatException)
        {
            // OnInkUseChangedAsync と同じ流儀。_passesOverride は変更済みのまま
            // にする(グリッドの再構成に失敗しても、利用者が入力した値は
            // 次回の操作までそのまま保持する)。
            MessageBox.Show(this, $"ジョブの再構成に失敗しました: {ex.Message}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>新しい PreviewResult を画面へ反映し、古いものを破棄する
    /// (§7.2 補足: Bitmap だけでなく、切り出し済み画像・プレーンも解放する)。</summary>
    private void ApplyPreviewResult(PreviewResult result)
    {
        _current?.Dispose();
        _current = result;
        _previewBox.Image = result.Preview;
        _jobSummaryLabel.Text =
            $"パス数: {result.Inks.Count} / 解像度: {result.Resolution.Key} / サイズ: {result.Width}x{result.Height}";

        PopulateInkGrid(result);

        if (result.Inks.Count == 0)
        {
            MessageBox.Show(
                this, "印刷する内容がありません(全プレーンが空です)。", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>ジョブ内容のグリッドを作り直す。D-030: パレット全体を常に表示する
    /// (許可リストに無いインクも、いま原稿に現れていないインクも行を残す)。
    /// これにより、まだ一度も使われていないメタリックを「これから使う」意思表示
    /// としてチェックを入れることも、原稿にあるのに許可リストから外れている
    /// インクのチェックを外すことも、再ラスタライズ無しで自由に行き来できる。</summary>
    private void PopulateInkGrid(PreviewResult result)
    {
        // チェックボックスのセルが編集中(コミット直後で IsCurrentCellInEditMode
        // が残っている場合がある)のまま Rows.Clear() で行を消すと、
        // DataGridView の内部状態(現在セル・編集コントロール)が不正になる。
        // 行を作り直す前に必ず編集を終了させておく。
        _inkGrid.EndEdit();

        // Rows.Add/Clear は CellValueChanged を発火させうるが、この呼び出しは
        // 常に SetBusy(true) の内側(RefreshPreviewAsync / OnInkUseChangedAsync)
        // で行われるため、OnInkUseChangedAsync 先頭の _busy ガードで再入を防げる。
        _inkGrid.Rows.Clear();

        var activeByName = result.Inks.ToDictionary(ink => ink.Name);
        var rows = new List<(string Name, int Order, string Label, int Passes, Color Color, bool Used, bool Appeared)>();
        foreach (var def in result.Config.Palette)
        {
            bool used = _usedInks.Contains(def.Name);
            if (activeByName.TryGetValue(def.Name, out var active))
            {
                rows.Add((def.Name, def.Order, active.Label, active.Passes, active.Color, used, true));
            }
            else
            {
                rows.Add((def.Name, def.Order, def.Label, 0, PreviewRenderer.ResolveDisplayColor(def), used, false));
            }
        }

        foreach (var row in rows.OrderBy(r => r.Order))
        {
            int rowIndex = _inkGrid.Rows.Add(row.Used, row.Order, string.Empty, row.Label, row.Passes);
            var gridRow = _inkGrid.Rows[rowIndex];
            gridRow.Cells["Color"].Style.BackColor = row.Color;
            gridRow.Tag = row.Name;
            // ジョブに現れないインク(D-030: チェックが外れている、または内容が
            // 空)の行は灰色で並べる。パス数は 0。
            if (!row.Appeared)
            {
                gridRow.DefaultCellStyle.ForeColor = Color.Gray;
            }
        }
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

        var confirm = MessageBox.Show(
            this,
            "プレビューのとおりに印刷します。よろしいですか?\n" +
            "(マジックカラー方式は誤爆するとリボンと用紙を失います。プレビューを確認してください)",
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
            var route = MachineRoute.Resolve(machine);
            // §15.10.2: 送出する用紙は必ずコンボの選択値と一致させる。ここが
            // _settings.PaperName(保存済みの既定値)のままだと、プレビューで
            // 用紙を切り替えても実際の送出は古い既定値のまま行われ、
            // プレビューと送出結果がずれる(切り出し位置がずれる実害)。
            var config = JobPipeline.LoadJobConfig(_repoRoot, route, paperName, mediaName);
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

            await Task.Run(() => JobPipeline.Print(
                planes, job, route, route.Vid,
                (done, total) => BeginInvoke(() =>
                {
                    _progressBar.Maximum = Math.Max(total, 1);
                    _progressBar.Value = Math.Min(done, _progressBar.Maximum);
                })));

            MessageBox.Show(this, "送出が完了しました。", "Foilwright", MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is TransportException or ConfigException)
        {
            MessageBox.Show(this, $"印刷に失敗しました: {ex.Message}", "Foilwright",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

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
        _statusRefreshButton.Enabled = !busy;
        // D-028: 再構成中はチェック列(除外の切り替え)を編集不可にする。
        _inkGrid.Columns["Use"]!.ReadOnly = busy;
        // D-031: 再構成中はパス数列も編集不可にする(Use 列と同じ扱い)。
        _inkGrid.Columns["Passes"]!.ReadOnly = busy;
        _cancelButton.Enabled = !busy;
        _printButton.Enabled = !busy && _current is { Inks.Count: > 0 };
        Text = busy && statusMessage.Length > 0
            ? $"Foilwright — 印刷プレビュー ({statusMessage})"
            : "Foilwright — 印刷プレビュー";
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _current?.Dispose();
        base.OnFormClosed(e);
    }
}
