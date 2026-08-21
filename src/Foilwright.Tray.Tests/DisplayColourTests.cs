// Foilwright.Tray.Tests — 凡例・プレビューの表示色(D-048 で穴が開いた箇所)の検出器。
//
// ResolveDisplayColor は「magic_rgb があればそれ、無ければ channel から」という
// 2 段構えで、最後の分岐には「到達しないはず」と書いてあった。ところが D-048 で
// **magic_rgb も channel も持たないインク**(塗る範囲で決まるインク)が入り、
// 光沢仕上げ2 と MF インクが**その到達しないはずの色 #ff0080 で描かれていた**。
// コメントが嘘になっていたうえ、2 色が同じピンクで見分けられなかった。

using System.Drawing;
using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class DisplayColourTests
{
    private static InkDefinition Ink(
        string name, int[]? magicRgb = null, string? channel = null, bool coverage = false) => new()
    {
        Name = name,
        Label = name,
        PrinterCode = 0x00,
        Order = 50,
        MagicRgb = magicRgb,
        Tolerance = magicRgb is null ? null : 8,
        Channel = channel,
        Coverage = coverage,
    };

    /// <summary>「到達しないはず」の色。これが実際に出てきたら、
    /// パレットに新しい種別が増えたのに表示色を決めていない、ということ。</summary>
    private static readonly Color UnreachableMarker = Color.FromArgb(255, 0, 128);

    [Fact]
    public void CoverageInksDoNotFallThroughToTheUnreachableMarker()
    {
        var colour = PreviewRenderer.ResolveDisplayColor(Ink("glossy_finish", coverage: true));

        Assert.NotEqual(UnreachableMarker.ToArgb(), colour.ToArgb());
    }

    [Fact]
    public void CoverageInksAreLightEnoughToNeedTheCheckerboard()
    {
        // 上掛け・下地は紙の上でほぼ無色。薄い色を当てて市松模様で見えるようにする
        // 決まりなので、明るさがその閾値の側にあること(白と同じ扱いになること)。
        var coverage = PreviewRenderer.ResolveDisplayColor(Ink("glossy_finish", coverage: true));
        var white = PreviewRenderer.ResolveDisplayColor(Ink("white", magicRgb: new[] { 230, 230, 230 }));

        int Brightness(Color c) => c.R + c.G + c.B;
        Assert.True(
            Brightness(coverage) > Brightness(Color.FromArgb(128, 128, 128)),
            $"coverage ink colour {coverage} is not light; the checkerboard marker will not kick in");
        Assert.True(Brightness(white) > Brightness(Color.FromArgb(128, 128, 128)));
    }

    [Fact]
    public void MagicRgbStillWinsOverEverythingElse()
    {
        var colour = PreviewRenderer.ResolveDisplayColor(Ink("gold", magicRgb: new[] { 225, 160, 0 }));

        Assert.Equal(Color.FromArgb(225, 160, 0).ToArgb(), colour.ToArgb());
    }

    [Theory]
    [InlineData("C")]
    [InlineData("M")]
    [InlineData("Y")]
    [InlineData("K")]
    public void ProcessInksKeepTheirChannelColour(string channel)
    {
        var colour = PreviewRenderer.ResolveDisplayColor(Ink("process", channel: channel));

        Assert.NotEqual(UnreachableMarker.ToArgb(), colour.ToArgb());
    }

    /// <summary>実物のパレットに対する検出器。**インクを足したときにここが赤くなる**ので、
    /// 表示色を決めないまま入ってしまうことがない。</summary>
    [Fact]
    public void NoInkInTheRealPaletteFallsThroughToTheUnreachableMarker()
    {
        string assetRoot = AssetRoot.ResolveDefault();
        var palette = ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml"));

        var offenders = palette
            .Where(ink => PreviewRenderer.ResolveDisplayColor(ink).ToArgb() == UnreachableMarker.ToArgb())
            .Select(ink => ink.Name)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"表示色が決まっていないインク: {string.Join(", ", offenders)}");
    }
}
