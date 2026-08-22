// Foilwright.Tray.Tests — ジョブ内容の表の列幅の配分を固定する検出器。
//
// 列は 使う/順序/色/インク/パス数/塗る範囲/ドット数 の 7 本まで増えた。
// AutoSizeColumnsMode = Fill は既定で等分するため、**列を足すたびに 1 列あたりが
// 痩せ、長い値(「紙用光沢仕上げ2 (MDC-FRVG)」「絵のあるところ」)が読めなくなる。**
// 2026-08-22 に利用者から「表が手狭」と指摘され、窓幅と FillWeight を直した。
//
// **画素の幅は測っていない。** DataGridView の Fill は実際に表示された領域を見て
// 配分するため、窓を Show しないと全列が既定の 100px のままになる。そして
// **窓は Show できない** — Load でプレビュー生成(Ghostscript)が走ってしまう
// (PreviewFormLayoutTests と同じ制約)。CreateControl / PerformLayout /
// AutoSizeColumnsMode の付け直しをいずれも試したが、値は変わらなかった。
//
// そこで**こちらが決められる量である FillWeight を丸ごと固定する**。
// 列を足したり配分を変えたりすると必ず赤くなるので、そのとき配分を考え直すことになる。
// **見た目そのものは人が確認する**(この検出器は「考え直す機会」を作るだけ)。

using System.Windows.Forms;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class InkGridWidthTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>列の名前 → 幅の取り分。中身の長さに合わせてある。
    /// **列を足したらここへも足すこと**(足さないとこのテストが赤くなる)。</summary>
    private static readonly Dictionary<string, float> ExpectedFillWeights = new(StringComparer.Ordinal)
    {
        ["Use"] = 40,        // チェックボックス
        ["Order"] = 40,      // 2 桁の数字
        ["Color"] = 75,      // #RRGGBB / (なし)
        ["Label"] = 155,     // 紙用光沢仕上げ2 (MDC-FRVG) — いちばん長い
        ["Passes"] = 50,     // 1 桁の数字
        ["Coverage"] = 110,  // 絵のあるところ
        ["DotCount"] = 130,  // 92,883 (30,961×3) — 消費と内訳の両方が入る
    };

    [Fact]
    public void InkGridFillWeightsAreDeliberate()
    {
        var actual = new Dictionary<string, float>(StringComparer.Ordinal);
        var report = new List<string>();

        // WinForms は STA を前提にしている。xunit の既定は STA ではない。
        var thread = new Thread(() =>
        {
            using var form = new PreviewForm("dummy.ps", new TraySettings());
            form.CreateControl();
            var grid = FindGrid(form);
            Assert.NotNull(grid);
            foreach (DataGridViewColumn column in grid!.Columns)
            {
                actual[column.Name] = column.FillWeight;
                report.Add($"  {column.Name,-10} {column.HeaderText,-12} FillWeight={column.FillWeight}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        output.WriteLine(string.Join("\n", report));

        // 列そのものが増減したら、まずここで気づく。
        Assert.Equal(
            ExpectedFillWeights.Keys.OrderBy(k => k, StringComparer.Ordinal),
            actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (name, weight) in ExpectedFillWeights)
        {
            Assert.True(
                actual[name] == weight,
                $"'{name}' の FillWeight が {actual[name]}(期待 {weight})\n{string.Join("\n", report)}");
        }

        // インク名の列がいちばん広いこと。ここが痩せると、どの行がどのインクか
        // 分からなくなる(表としての用をなさない)。
        float widest = actual.Values.Max();
        Assert.True(
            actual["Label"] == widest,
            $"'Label' がいちばん広くない\n{string.Join("\n", report)}");
    }

    private static DataGridView? FindGrid(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is DataGridView grid && grid.Columns.Contains("Label"))
            {
                return grid;
            }
            var found = FindGrid(child);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }
}
