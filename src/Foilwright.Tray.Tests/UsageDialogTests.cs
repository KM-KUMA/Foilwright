// Foilwright.Tray.Tests — リボン消費の窓(UsageDialog)の、画面に触らない部分の検出器。
//
// 窓そのものは開かない。対象は純粋な処理だけ:
//   ① ResolveInkLabel — 引ければ表示名、引けなければ識別子をそのまま
//      (古い記録や、いまのパレットに無いインクを落とさないため。D-046 5)
//   ② EmptyMessage — 記録が無いときに無言にならないこと
//
// 並び(累計の多い順)は UsageLogTests.Summarise_OrdersByTotalDotsDescending が
// すでに見張っているため、ここでは重ねない。

using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class UsageDialogTests
{
    [Fact]
    public void ResolveInkLabel_UsesTheLabelWhenThePaletteHasTheInk()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["glossy_finish"] = "光沢仕上げ",
        };
        Assert.Equal("光沢仕上げ", UsageDialog.ResolveInkLabel("glossy_finish", labels));
    }

    [Fact]
    public void ResolveInkLabel_FallsBackToTheIdentifierWhenTheInkIsUnknown()
    {
        // いまのパレットに無いインク(古い記録・別のパレットで刷った記録)を
        // 落としてはならない。識別子のままでも「何をどれだけ使ったか」は伝わる。
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["black"] = "黒",
        };
        Assert.Equal("mdc_flcg", UsageDialog.ResolveInkLabel("mdc_flcg", labels));
    }

    [Fact]
    public void ResolveInkLabel_FallsBackWhenThePaletteCouldNotBeRead()
    {
        // パレットが読めなかったときは空の辞書で開く(トレイのメニュー経由)。
        // そのときも記録は見えること。
        Assert.Equal(
            "black",
            UsageDialog.ResolveInkLabel("black", new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    [Fact]
    public void EmptyMessage_IsNotBlank()
    {
        // 記録が 1 件も無いときに無言にならないことの検出器。
        Assert.False(string.IsNullOrWhiteSpace(UsageDialog.EmptyMessage));
    }
}
