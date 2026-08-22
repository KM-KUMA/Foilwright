// Foilwright.Tray.Tests — 「パス数」欄に不正な値を置かないことの検出器。
//
// 「パス数」は編集できる欄で、CellValidating が 1〜8 の外を拒否する。
// ところが以前は、ジョブに出ていないインクの欄に **0** を出していた。
// 利用者が触っていない 0 のセルへ現在セルが移ると、そのセルを確定も中止もできず、
// 表を作り直す Rows.Clear() が
// 「セル値の変更をコミットまたは中止できないため、操作は成功しませんでした」
// で落ちる。2026-08-22 に実機で発生した(別の行のパス数を変えた直後、Enter で
// 選択が 0 の行へ移ったことが引き金)。
//
// **入力欄に、入力として不正な値を置かない。** それをここで固定する。

using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class DisplayedPassesTests
{
    private static InkDefinition Ink(string name, int passes) => new()
    {
        Name = name,
        Label = name,
        PrinterCode = 0x00,
        Order = 10,
        Channel = "K",
        Passes = passes,
    };

    [Fact]
    public void FallsBackToThePaletteValueWhenThereIsNoOverride()
    {
        var passes = PreviewForm.ResolveDisplayedPasses(
            Ink("black", 2), new Dictionary<string, int>());

        Assert.Equal(2, passes);
    }

    [Fact]
    public void TheOverrideWins()
    {
        var passes = PreviewForm.ResolveDisplayedPasses(
            Ink("white", 1), new Dictionary<string, int> { ["white"] = 4 });

        Assert.Equal(4, passes);
    }

    /// <summary>実物のパレットに対する検出器。**どのインクの欄も、そのまま確定できる
    /// 値であること。** ここが 0 に戻ると、実機で踏んだ「表が作り直せない」不具合が
    /// 再発する。</summary>
    [Fact]
    public void EveryInkInTheRealPaletteShowsAValueTheGridWillAccept()
    {
        string assetRoot = AssetRoot.ResolveDefault();
        var palette = ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml"));
        var noOverrides = new Dictionary<string, int>();

        var offenders = palette
            .Select(ink => (ink.Name, Passes: PreviewForm.ResolveDisplayedPasses(ink, noOverrides)))
            .Where(x => x.Passes < TraySettings.MinPasses || x.Passes > TraySettings.MaxPasses)
            .Select(x => $"{x.Name}={x.Passes}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"パス数の欄に確定できない値が出るインク: {string.Join(", ", offenders)}");
    }
}
