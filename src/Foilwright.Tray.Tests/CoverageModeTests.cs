// Foilwright.Tray.Tests — 「塗る範囲」(D-048)の検出器。
//
// この機能の失敗は **「指定したのに何も出ない」** という形で現れる。この案件では
// 同じ形の失敗を既に 2 回作っている(D-042 の白版モード none / --magic-rgb の
// 綴り間違い)ため、黙って既定へ落ちる経路をここで全部潰しておく:
//
//   ①日本語ラベル ⇔ 内部値 の往復が壊れていないこと(TryParseCoverageModeLabel が
//     知らない文字列で **false** を返すこと。"none" を返して true にしないこと)。
//   ②「塗る範囲」が「なし」のまま使われている coverage インクを警告すること。
//   ③--coverage の書式・モード・インク名・coverage かどうかを、その場で拒否すること。
//   ④選べない行(coverage でないインク)のセルが、そもそもコンボでないこと。
//
// UI もプリンタも Ghostscript も要らない。④だけ WinForms のコントロールを組み立てる
// (STA スレッドの中で完結させる。窓は開かない)。

using System.Drawing;
using System.Windows.Forms;
using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class CoverageModeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>テスト用のパレット(palette/default.yaml の値を写したもの。
    /// ファイルに依存させないため、ここで組み立てる)。coverage インク 2 種と、
    /// coverage でないインク 2 種を入れてある。</summary>
    private static List<InkDefinition> BuildPalette() => new()
    {
        new InkDefinition
        {
            Name = "mf_ink",
            Label = "紙用 MF インク (MDC-PREP)",
            PrinterCode = 0x10,
            Order = 5,
            Barcode = 18,
            Coverage = true,
            Passes = 1,
        },
        new InkDefinition
        {
            Name = "white",
            Label = "紙用特色ホワイト (MDC-SCWH)",
            PrinterCode = 0x0B,
            Order = 10,
            MagicRgb = new[] { 230, 230, 230 },
            Tolerance = 8,
            Barcode = 16,
            AutoUndercoat = true,
            Passes = 1,
        },
        new InkDefinition
        {
            Name = "black",
            Label = "紙用ブラック (MDC-SCBK)",
            PrinterCode = 0x00,
            Order = 90,
            MagicRgb = new[] { 0, 0, 0 },
            Tolerance = 8,
            Channel = "K",
            Barcode = 0,
            Passes = 1,
        },
        new InkDefinition
        {
            Name = "glossy_finish",
            Label = "紙用光沢仕上げ2 (MDC-FRVG)",
            PrinterCode = 0x0E,
            Order = 95,
            Barcode = 19,
            Coverage = true,
            Passes = 1,
        },
    };

    // --- ラベルと内部値の往復 -------------------------------------------------

    /// <summary>3 値すべてが「内部値 → 日本語 → 内部値」で戻ること。
    /// ここが崩れると、画面で選んだものと実際に刷られるものが食い違う。</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("artwork")]
    [InlineData("full")]
    public void CoverageModeLabelRoundTrips(string mode)
    {
        string label = PreviewForm.CoverageModeLabel(mode);

        Assert.True(PreviewForm.TryParseCoverageModeLabel(label, out string parsed));
        Assert.Equal(mode, parsed);
    }

    /// <summary>画面に出るのは日本語であること(内部値がそのまま漏れていない)。</summary>
    [Fact]
    public void CoverageModeLabelsAreJapanese()
    {
        Assert.Equal("なし", PreviewForm.CoverageModeLabel("none"));
        Assert.Equal("絵のあるところ", PreviewForm.CoverageModeLabel("artwork"));
        Assert.Equal("全面", PreviewForm.CoverageModeLabel("full"));
    }

    /// <summary>受け付ける値の並びが JobAssembly 側と同じであること。
    /// 片方だけ増えると「選べるのに弾かれる」「選べないのに通る」になる。</summary>
    [Fact]
    public void CoverageModeValuesMatchJobAssembly()
    {
        Assert.Equal(JobAssembly.ValidCoverageModes, TraySettings.CoverageModeValues);
        Assert.Contains(TraySettings.DefaultCoverageMode, TraySettings.CoverageModeValues);
    }

    /// <summary>**知らない文字列は false。** 黙って "none" を返して true にしない
    /// (落とすと「選んだのに何も出ない」を利用者が追えなくなる)。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("なし ")]           // 前後の空白は Trim するので、これは通る側の境界確認用ではない
    [InlineData("ぜんめん")]
    [InlineData("artwork")]         // 内部値そのものは「画面に出した日本語」ではない
    [InlineData("全面にする")]
    public void TryParseCoverageModeLabelRejectsUnknownText(string? label)
    {
        // "なし " だけは Trim して通る。それ以外は false。
        bool expected = label?.Trim() == "なし";

        bool actual = PreviewForm.TryParseCoverageModeLabel(label, out string mode);

        Assert.Equal(expected, actual);
        if (!actual)
        {
            // false のときの out 値は使われてはならないが、既定を返しておく約束。
            Assert.Equal(TraySettings.DefaultCoverageMode, mode);
        }
    }

    // --- 「なし」のままの警告 -------------------------------------------------

    private static List<(string Name, string Label, bool IsCoverage, bool Used, string Mode)> Inks(
        params (string Name, bool IsCoverage, bool Used, string Mode)[] items) =>
        items.Select(x => (x.Name, Label: $"{x.Name} のラベル", x.IsCoverage, x.Used, x.Mode)).ToList();

    [Fact]
    public void CoverageWarningNamesTheInkLeftAtNone()
    {
        string warning = PreviewForm.BuildCoverageWarning(
            Inks(("glossy_finish", true, true, "none")));

        Assert.Contains("glossy_finish のラベル", warning);
        Assert.Contains("塗る範囲", warning);
        Assert.Contains("なし", warning);
        Assert.Contains("刷られません", warning);
    }

    [Theory]
    [InlineData("artwork")]
    [InlineData("full")]
    public void CoverageWarningIsSilentWhenAModeIsChosen(string mode)
    {
        Assert.Equal(
            string.Empty,
            PreviewForm.BuildCoverageWarning(Inks(("glossy_finish", true, true, mode))));
    }

    /// <summary>チェックの外れているインクは警告しない(刷らない意思表示であり、
    /// 何も出ないのは当たり前)。</summary>
    [Fact]
    public void CoverageWarningIgnoresUncheckedInks()
    {
        Assert.Equal(
            string.Empty,
            PreviewForm.BuildCoverageWarning(Inks(("glossy_finish", true, false, "none"))));
    }

    /// <summary>coverage でないインクは警告しない(この列と無関係。黒や白が
    /// 「なし」だからといって刷られないわけではない)。</summary>
    [Fact]
    public void CoverageWarningIgnoresNonCoverageInks()
    {
        Assert.Equal(
            string.Empty,
            PreviewForm.BuildCoverageWarning(Inks(("black", false, true, "none"))));
    }

    /// <summary>該当が複数あれば全部の名前が出ること(1 件だけ直して満足させない)。</summary>
    [Fact]
    public void CoverageWarningListsEveryAffectedInk()
    {
        string warning = PreviewForm.BuildCoverageWarning(Inks(
            ("mf_ink", true, true, "none"),
            ("black", false, true, "none"),
            ("glossy_finish", true, true, "none")));

        Assert.Contains("mf_ink のラベル", warning);
        Assert.Contains("glossy_finish のラベル", warning);
        Assert.DoesNotContain("black のラベル", warning);
    }

    [Fact]
    public void CoverageWarningIsEmptyWhenThereIsNothingToSay()
    {
        Assert.Equal(string.Empty, PreviewForm.BuildCoverageWarning(Inks()));
    }

    // --- Program.ParseCoverageArg --------------------------------------------

    [Fact]
    public void ParseCoverageArgParsesEveryMode()
    {
        var result = Program.ParseCoverageArg("glossy_finish=artwork,mf_ink=full,white=none");

        Assert.Equal(3, result.Count);
        Assert.Equal("artwork", result["glossy_finish"]);
        Assert.Equal("full", result["mf_ink"]);
        Assert.Equal("none", result["white"]);
    }

    [Fact]
    public void ParseCoverageArgEmptyArgumentGivesEmptyDictionary()
    {
        Assert.Empty(Program.ParseCoverageArg(string.Empty));
    }

    [Theory]
    [InlineData("glossy_finish")]            // = が無い
    [InlineData("=artwork")]                 // インク名が無い
    [InlineData("glossy_finish=")]           // モードが無い
    [InlineData("glossy_finish=ARTWORK")]    // 大文字小文字は吸収しない
    [InlineData("glossy_finish=絵のあるところ")] // 画面の日本語は CLI では受け付けない
    [InlineData("glossy_finish=all")]        // 知らないモード
    [InlineData("glossy_finish=artwork,mf_ink=whole")]
    public void ParseCoverageArgRejectsBadInput(string arg)
    {
        Assert.Throws<ConfigException>(() => Program.ParseCoverageArg(arg));
    }

    // --- パレットとの照合 -----------------------------------------------------

    /// <summary>綴り間違いはその場で止める(--magic-rgb と同じ検出器を使い回す)。</summary>
    [Fact]
    public void UnknownInkNamesAreRejectedForCoverage()
    {
        var arg = Program.ParseCoverageArg("glosy_finish=artwork");

        var ex = Assert.Throws<ConfigException>(
            () => Program.RejectUnknownInkNames(arg, BuildPalette(), "--coverage", "D-048"));

        Assert.Contains("glosy_finish", ex.Message);
        Assert.Contains("glossy_finish", ex.Message);
        Assert.Contains("--coverage", ex.Message);
    }

    [Fact]
    public void KnownCoverageInkNamesAreAccepted()
    {
        var arg = Program.ParseCoverageArg("glossy_finish=artwork,mf_ink=full");

        Program.RejectUnknownInkNames(arg, BuildPalette(), "--coverage", "D-048");
        Program.RejectNonCoverageInks(arg, BuildPalette());
    }

    /// <summary>coverage でないインクへの指定は黙って無視されると
    /// 「指定したのに何も出ない」になる。その場で止める。</summary>
    [Fact]
    public void NonCoverageInksAreRejected()
    {
        var arg = Program.ParseCoverageArg("black=full");

        var ex = Assert.Throws<ConfigException>(() => Program.RejectNonCoverageInks(arg, BuildPalette()));

        Assert.Contains("black", ex.Message);
        // 指定できる名前(= coverage インク)が出ること。
        Assert.Contains("glossy_finish", ex.Message);
        Assert.Contains("mf_ink", ex.Message);
    }

    /// <summary>パレットに無い名前は RejectNonCoverageInks の担当ではない
    /// (先に RejectUnknownInkNames が止める。二重に文句を言わない)。</summary>
    [Fact]
    public void NonCoverageCheckLeavesUnknownNamesToTheOtherCheck()
    {
        Program.RejectNonCoverageInks(
            new Dictionary<string, string> { ["no_such_ink"] = "full" }, BuildPalette());
    }

    // --- グリッドのセル(選べない行でドロップダウンが開かないこと)-------------

    /// <summary>coverage でない行のセルは **コンボではなくなる**(読み取り専用の
    /// テキストセルへ差し替わる)こと。ReadOnly を立てるだけではコンボの見た目が
    /// 残り、「選べそうなのに効かない」になる。
    ///
    /// 列とセルの作りは PreviewForm の本番コード(CreateCoverageColumn /
    /// ApplyCoverageCell)をそのまま呼んで確かめる — 作り方を 2 箇所に書くと、
    /// 片方だけ直したときにこの検出器が嘘になる。</summary>
    [Fact]
    public void NonCoverageRowsCannotOpenTheDropDown()
    {
        var failures = new List<string>();
        var report = new List<string>();

        var thread = new Thread(() =>
        {
            using var grid = new DataGridView { AllowUserToAddRows = false };
            grid.Columns.Add(CreatePlaceholderColumn("Label"));
            grid.Columns.Add(PreviewForm.CreateCoverageColumn());
            grid.CreateControl();

            foreach (var def in BuildPalette())
            {
                int index = grid.Rows.Add(def.Label, null!);
                var row = grid.Rows[index];
                PreviewForm.ApplyCoverageCell(row, def.Coverage, def.Coverage ? "artwork" : "none");

                var cell = row.Cells["Coverage"];
                report.Add(
                    $"{def.Name}: coverage={def.Coverage} cell={cell.GetType().Name} " +
                    $"readOnly={cell.ReadOnly} value='{cell.Value}'");

                // 実際に編集を始められるかを見る(構造だけでなく振る舞いで確かめる)。
                grid.CurrentCell = cell;
                bool began = grid.BeginEdit(true);
                grid.EndEdit();

                if (def.Coverage)
                {
                    if (cell is not DataGridViewComboBoxCell) { failures.Add($"{def.Name}: コンボのままであるべき"); }
                    if (cell.ReadOnly) { failures.Add($"{def.Name}: 選べるべき行が読み取り専用"); }
                    if (!began) { failures.Add($"{def.Name}: 編集を始められない(選べない)"); }
                    if ((string?)cell.Value != "絵のあるところ") { failures.Add($"{def.Name}: 値が日本語で入っていない"); }
                }
                else
                {
                    if (cell is DataGridViewComboBoxCell) { failures.Add($"{def.Name}: コンボが残っている(ドロップダウンが開く)"); }
                    if (!cell.ReadOnly) { failures.Add($"{def.Name}: 選べない行が読み取り専用になっていない"); }
                    if (began) { failures.Add($"{def.Name}: 選べない行で編集が始まった"); }
                    if ((string?)cell.Value != PreviewForm.NotCoverageCellText) { failures.Add($"{def.Name}: 空欄になっている"); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // セルの型・読み取り専用・編集開始の可否は成功時も残す(この確認の根拠になる)。
        Assert.True(failures.Count == 0, string.Join("\n", failures.Concat(report)));
        output.WriteLine(string.Join("\n", report));
    }

    private static DataGridViewTextBoxColumn CreatePlaceholderColumn(string name) =>
        new() { Name = name, HeaderText = name };

    // --- 選択肢 ---------------------------------------------------------------

    /// <summary>コンボに並ぶのは 3 つで、すべて日本語であること。</summary>
    [Fact]
    public void CoverageColumnOffersExactlyTheThreeModes()
    {
        var items = new List<string>();
        var thread = new Thread(() =>
        {
            var column = PreviewForm.CreateCoverageColumn();
            foreach (object item in column.Items)
            {
                items.Add((string)item);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Equal(new[] { "なし", "絵のあるところ", "全面" }, items);
    }

    // --- 警告ラベルが 2 種類以上を同時に出しても切れないこと -------------------

    /// <summary>色の重複警告(D-042)と塗る範囲の警告(D-048)が同時に出ても、
    /// ラベルが伸びて全部読めること。**高さを決め打ちにすると下の行が消える** —
    /// この案件ではボタンを 2 回画面外へ押し出している。</summary>
    [Fact]
    public void WarningLabelGrowsForSeveralWarnings()
    {
        var failures = new List<string>();
        var report = new List<string>();

        var thread = new Thread(() =>
        {
            using var form = new PreviewForm("dummy.ps", new TraySettings());
            form.CreateControl();
            form.Size = new Size(900, 600);
            form.PerformLayout();

            var found = form.Controls.Find(PreviewForm.WarningLabelName, searchAllChildren: true);
            if (found.Length != 1)
            {
                failures.Add($"警告ラベルが見つかりません(見つかった数: {found.Length})");
                return;
            }
            var label = found[0];

            string[] lines =
            {
                "⚠ 同じ色が複数のインクに割り当てられています: #000000 (white, black)(使わないインクはチェックを外してください)",
                "⚠ 紙用 MF インク (MDC-PREP) は「塗る範囲」が「なし」のため刷られません。",
                "⚠ 紙用光沢仕上げ2 (MDC-FRVG) は「塗る範囲」が「なし」のため刷られません。",
            };
            int oneLine = MeasureLabelHeight(label, form, lines[0]);
            int twoLines = MeasureLabelHeight(label, form, string.Join(Environment.NewLine, lines[..2]));
            int threeLines = MeasureLabelHeight(label, form, string.Join(Environment.NewLine, lines));

            int fontHeight = label.Font.Height;
            report.Add(
                $"文字の高さ = {fontHeight} / 1 行 = {oneLine} / 2 行 = {twoLines} / 3 行 = {threeLines}");

            // 行が増えるたびに実際に伸びること(高さが頭打ちになっていない)。
            if (!(oneLine < twoLines && twoLines < threeLines))
            {
                failures.Add($"行が増えても伸びていません: {oneLine} / {twoLines} / {threeLines}");
            }
            // 3 行ぶんの文字が入る高さがあること(下の行が切れて読めない状態でない)。
            if (threeLines < fontHeight * 3)
            {
                failures.Add($"3 行ぶんの文字が入りません(文字の高さ {fontHeight} に対し {threeLines})");
            }
            // 伸びた結果がジョブ内容の枠からはみ出していないこと。
            var parent = label.Parent!;
            report.Add($"親({parent.GetType().Name})の高さ = {parent.Height} / ラベルの上端 = {label.Top}");
            if (label.Top < 0 || label.Bottom > parent.Height)
            {
                failures.Add($"警告ラベルが親の外へはみ出しています: {label.Bounds} not in {parent.ClientRectangle}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 高さは成功時も残す(この確認の根拠になる)。
        Assert.True(failures.Count == 0, string.Join("\n", failures.Concat(report)));
        output.WriteLine(string.Join("\n", report));
    }

    private static int MeasureLabelHeight(Control label, Form form, string text)
    {
        label.Text = text;
        form.PerformLayout();
        return label.Height;
    }
}
