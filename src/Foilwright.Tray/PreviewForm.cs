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

    /// <summary>D-028: プレビューでチェックを外したインクの名前(name)。
    /// 「そのジョブのパレットからそのインクを外す」の上書き集合であり、
    /// ジョブごとの上書きに留まる(TraySettings には保存しない)。</summary>
    private readonly HashSet<string> _excludedInks = new();

    /// <summary>メディア種別コンボの 1 項目。表示は label(§5.5.2)、実体は name。</summary>
    private sealed record MediaItem(string Name, string Label)
    {
        public override string ToString() => Label;
    }

    public PreviewForm(string psPath, TraySettings settings)
    {
        _psPath = psPath;
        _settings = settings;
        _repoRoot = JobPipeline.FindRepoRoot();

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
        var settingsGroup = new GroupBox { Text = "設定(このジョブに適用)", Dock = DockStyle.Top, Height = 320 };
        var settingsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 9, Padding = new Padding(8) };
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

        // メディア種別(§7.1 / §5.5.2)。選択肢は media.yaml から読む。
        settingsLayout.Controls.Add(new Label { Text = "メディア種別:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        _mediaCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        settingsLayout.Controls.Add(_mediaCombo, 1, 3);

        // ハーフトーン(§7.1 / §4.2.1)。
        settingsLayout.Controls.Add(new Label { Text = "ハーフトーン:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        _halftoneCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _halftoneCombo.Items.AddRange(JobAssembly.ValidHalftones.Cast<object>().ToArray());
        _halftoneCombo.SelectedItem = JobAssembly.ValidHalftones.Contains(settings.Halftone) ? settings.Halftone : "none";
        settingsLayout.Controls.Add(_halftoneCombo, 1, 4);

        // 白版モード(§7.1 / D-027)。
        settingsLayout.Controls.Add(new Label { Text = "白版モード:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        _whiteModeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _whiteModeCombo.Items.AddRange(JobAssembly.ValidWhiteModes.Cast<object>().ToArray());
        _whiteModeCombo.SelectedItem = JobAssembly.ValidWhiteModes.Contains(settings.WhiteMode) ? settings.WhiteMode : "auto";
        settingsLayout.Controls.Add(_whiteModeCombo, 1, 5);

        // 色補正(§7.1 / D-029)。既定は photo。選択肢は Colour.ValidColourCorrections
        // から読む(DOMAIN §4.5: コードに列挙しない)。
        settingsLayout.Controls.Add(new Label { Text = "色補正:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 6);
        _colourCorrectionCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _colourCorrectionCombo.Items.AddRange(Colour.ValidColourCorrections.Cast<object>().ToArray());
        _colourCorrectionCombo.SelectedItem =
            Colour.ValidColourCorrections.Contains(settings.ColourCorrection) ? settings.ColourCorrection : "photo";
        settingsLayout.Controls.Add(_colourCorrectionCombo, 1, 6);

        // カール矯正の抑制(§7.1 / DOMAIN §10.10.4)。デカール・フィルム用に
        // 裏面印刷でカール矯正を止めたい場合に使う。
        settingsLayout.Controls.Add(new Label { Text = "カール矯正を止める:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 7);
        _noCurlCheck = new CheckBox { Text = "デカール・フィルム用(§10.10.4)", AutoSize = true, Checked = settings.NoCurlCorrection };
        settingsLayout.Controls.Add(_noCurlCheck, 1, 7);

        _saveDefaultsButton = new Button { Text = "この設定を既定値として保存", AutoSize = true };
        _saveDefaultsButton.Click += (_, _) => SaveAsDefaults();
        settingsLayout.Controls.Add(_saveDefaultsButton, 0, 8);
        settingsLayout.SetColumnSpan(_saveDefaultsButton, 2);

        settingsGroup.Controls.Add(settingsLayout);
        right.Controls.Add(settingsGroup);

        PopulateResolutionCombo(settings.Machine, settings.ResolutionKey);
        PopulateMediaCombo(settings.MediaName);

        _machineCombo.SelectedIndexChanged += (_, _) =>
        {
            // 機種が変わると選べる解像度が変わりうる(DOMAIN §5.1)ため作り直す。
            PopulateResolutionCombo((string)_machineCombo.SelectedItem!, (string?)_resolutionCombo.SelectedItem);
            _ = RefreshPreviewAsync();
        };
        _inkModeCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
        _resolutionCombo.SelectedIndexChanged += (_, _) => _ = RefreshPreviewAsync();
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
        _inkGrid.Columns["Passes"]!.ReadOnly = true;
        // チェックボックス列は確定(コミット)が 1 セル遅れる既知の挙動があるため、
        // CurrentCellDirtyStateChanged で即座にコミットしてから CellValueChanged を拾う。
        _inkGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_inkGrid.IsCurrentCellDirty && _inkGrid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _inkGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        _inkGrid.CellValueChanged += async (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != useColumn.Index)
            {
                return;
            }
            await OnInkUseChangedAsync(e.RowIndex);
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
        _settings.MediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
        _settings.Halftone = (string)_halftoneCombo.SelectedItem!;
        _settings.WhiteMode = (string)_whiteModeCombo.SelectedItem!;
        _settings.ColourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
        _settings.NoCurlCorrection = _noCurlCheck.Checked;
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
            string mediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
            string halftone = (string)_halftoneCombo.SelectedItem!;
            string whiteMode = (string)_whiteModeCombo.SelectedItem!;
            string colourCorrection = (string)_colourCorrectionCombo.SelectedItem!;
            var route = MachineRoute.Resolve(machine);

            // D-028: 除外集合は解像度・メディア・機種などを変えて再プレビューしても
            // そのまま持ち越す(除外したインクがもう現れなければ自然に消える)。
            var result = await Task.Run(() => JobPipeline.BuildPreview(
                _psPath, _repoRoot, route, inkMode, _settings.PaperName, mediaName, resolutionKey, halftone, whiteMode,
                _excludedInks, colourCorrection));

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
            _excludedInks.Remove(inkName);
        }
        else
        {
            _excludedInks.Add(inkName);
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
                previous.Image, previous.Config, previous.Resolution, inkMode, halftone, whiteMode, _excludedInks,
                colourCorrection));

            ApplyPreviewResult(result);
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

    /// <summary>ジョブ内容のグリッドを作り直す。D-028: チェックを外した(除外した)
    /// インクは jobPlanes に現れなくなるため、行が消えて再チェックできなくなって
    /// しまう。そこで「現在使われているインク」に加え、「除外集合に入っているが
    /// 現在は現れていないインク」もパレット定義から拾って行を残す(チェックを
    /// 外したり戻したりを、再ラスタライズ無しで自由に行き来できるようにするため)。</summary>
    private void PopulateInkGrid(PreviewResult result)
    {
        // Rows.Add/Clear は CellValueChanged を発火させうるが、この呼び出しは
        // 常に SetBusy(true) の内側(RefreshPreviewAsync / OnInkUseChangedAsync)
        // で行われるため、OnInkUseChangedAsync 先頭の _busy ガードで再入を防げる。
        _inkGrid.Rows.Clear();

        var activeNames = new HashSet<string>(result.Inks.Select(ink => ink.Name));
        var rows = new List<(string Name, int Order, string Label, int Passes, Color Color, bool Used)>();
        foreach (var ink in result.Inks)
        {
            rows.Add((ink.Name, ink.Order, ink.Label, ink.Passes, ink.Color, true));
        }
        foreach (var def in result.Config.Palette)
        {
            if (_excludedInks.Contains(def.Name) && !activeNames.Contains(def.Name))
            {
                rows.Add((def.Name, def.Order, def.Label, 0, PreviewRenderer.ResolveDisplayColor(def), false));
            }
        }

        foreach (var row in rows.OrderBy(r => r.Order))
        {
            int rowIndex = _inkGrid.Rows.Add(row.Used, row.Order, string.Empty, row.Label, row.Passes);
            var gridRow = _inkGrid.Rows[rowIndex];
            gridRow.Cells["Color"].Style.BackColor = row.Color;
            gridRow.Tag = row.Name;
            if (!row.Used)
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
            string mediaName = ((MediaItem)_mediaCombo.SelectedItem!).Name;
            var route = MachineRoute.Resolve(machine);
            var config = JobPipeline.LoadJobConfig(_repoRoot, route, _settings.PaperName, mediaName);
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
        _mediaCombo.Enabled = !busy;
        _halftoneCombo.Enabled = !busy;
        _whiteModeCombo.Enabled = !busy;
        _colourCorrectionCombo.Enabled = !busy;
        _noCurlCheck.Enabled = !busy;
        _saveDefaultsButton.Enabled = !busy;
        _statusRefreshButton.Enabled = !busy;
        // D-028: 再構成中はチェック列(除外の切り替え)を編集不可にする。
        _inkGrid.Columns["Use"]!.ReadOnly = busy;
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
