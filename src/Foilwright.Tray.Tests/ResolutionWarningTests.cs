// Foilwright.Tray.Tests — 「1200dpi は特色インクに効かない」の警告の検出器(D-051)。
//
// 実測(2026-08-22): 5 層構成を 1200x600 で刷ると、白だけが横幅 2 倍になった。
// こちらのラスタも送出バイトもインクによらず同一であることは確かめてあり
// (プレビューの絵の広がりが画素単位で一致、RGL のバイト数も一致)、
// プリンタ側が特色のパスを 600dpi で刷っているとみられる。
// ppmtomd の man も「undercolours と spot colours は常に Standard モードで
// 刷られる」と書いている。
//
// 止めはしない。混ぜられること自体は害ではなく、知らずに刷ることが害である。

using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class ResolutionWarningTests
{
    private static IReadOnlyList<(string Label, bool IsNonProcess, bool Used)> Inks(
        params (string, bool, bool)[] items) =>
        items.Select(x => (Label: x.Item1, IsNonProcess: x.Item2, Used: x.Item3)).ToList();

    [Fact]
    public void SixHundredDpiNeverWarns()
    {
        var text = PreviewForm.BuildResolutionWarning(
            "600", Inks(("紙用特色ホワイト", true, true)));

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void TwelveHundredWithOnlyProcessInksIsFine()
    {
        // CMYK だけなら 1200dpi はそのまま使える。
        var text = PreviewForm.BuildResolutionWarning(
            "1200x600", Inks(("紙用ブラック", false, true), ("紙用シアン", false, true)));

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void TwelveHundredWithASpotInkNamesIt()
    {
        var text = PreviewForm.BuildResolutionWarning(
            "1200x600",
            Inks(("紙用ブラック", false, true), ("紙用特色ホワイト", true, true)));

        Assert.Contains("紙用特色ホワイト", text);
        // 何が起きるかを言うこと。「効きません」だけでは何を直せばよいか分からない。
        Assert.Contains("2 倍", text);
        Assert.Contains("600", text);
        // 巻き込まれていないインクの名前は出さない。
        Assert.DoesNotContain("紙用ブラック", text);
    }

    [Fact]
    public void UncheckedInksAreNotWarnedAbout()
    {
        // 使わないインクは刷られないので関係ない。
        var text = PreviewForm.BuildResolutionWarning(
            "1200x600", Inks(("紙用特色ホワイト", true, false)));

        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void NullResolutionDoesNotThrow()
    {
        // コンボがまだ埋まっていない時点で呼ばれても落ちないこと。
        Assert.Equal(
            string.Empty,
            PreviewForm.BuildResolutionWarning(null, Inks(("紙用特色ホワイト", true, true))));
    }
}
