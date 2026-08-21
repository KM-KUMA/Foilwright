// Foilwright.Tray.Tests — 部数(D-044)の単体テスト。
//
// 対象は PreviewForm から切り出した 3 つの文言組み立て
// (BuildPrintConfirmText / BuildNextCopyPrompt / BuildCopiesStoppedText)。
// どれも画面に触らない純粋な処理であり
// (BuildMagicRgbWarning / DescribeUserError と同じ形)、ここで壊れを検出できる。
//
// 最も大事なのは「部数 1 の体験を変えていない」ことの検出器 —
// BuildPrintConfirmText(1) は従来の文言と一字も違わないことを固定する
// (期待値はこちらにリテラルで書く)。

using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class CopiesTests
{
    // --- PreviewForm.HasRemainingCopiesFor -----------------------------------
    //
    // これが true のあいだは窓を閉じさせない(部と部のあいだに閉じられると、
    // 残りの部が黙って失われ、破棄済みの窓へ触って落ちる)。
    // 逆に false へ戻し損ねると窓が二度と閉じられなくなるので、両方向を固定する。

    [Theory]
    [InlineData(1, 1, false)] // 1 部だけ。最初から残りは無い
    [InlineData(1, 3, true)]  // 3 部中 1 部目が終わった直後 = 残っている
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)] // 最後の部。ここで閉じられるようになる
    [InlineData(1, 0, false)] // 後始末で 0 や 1 に戻された状態でも閉じられること
    public void HasRemainingCopiesFor_KnowsWhenTheWindowMayClose(int index, int total, bool expected)
    {
        Assert.Equal(expected, PreviewForm.HasRemainingCopiesFor(index, total));
    }

    /// <summary>D-044 の実装前から PrintAsync が出していた確認文。
    /// 部数が 1 のときはこれから一字も変えてはならない。</summary>
    private const string LegacyConfirmText =
        "プレビューのとおりに印刷します。よろしいですか?\n" +
        "(マジックカラー方式は誤爆するとリボンと用紙を失います。プレビューを確認してください)";

    [Fact]
    public void BuildPrintConfirmText_OneCopyKeepsTheLegacyWording()
    {
        Assert.Equal(LegacyConfirmText, PreviewForm.BuildPrintConfirmText(1));
    }

    [Fact]
    public void BuildPrintConfirmText_MultipleCopiesMentionsCountAndTheStopBetweenCopies()
    {
        string text = PreviewForm.BuildPrintConfirmText(3);
        Assert.Contains("3 部", text);
        Assert.Contains("1 部ごとに止まります", text);
        // D-044 補足: カセットの交換回数が部数ぶん増えることも伝える。
        Assert.Contains("カセット", text);
    }

    [Fact]
    public void BuildNextCopyPrompt_ReportsHowManyCopiesRemain()
    {
        Assert.Contains("残り 2 部", PreviewForm.BuildNextCopyPrompt(1, 3));
        Assert.Contains("残り 1 部", PreviewForm.BuildNextCopyPrompt(2, 3));
    }

    [Fact]
    public void BuildNextCopyPrompt_ShowsTheFinishedCountAndTheTotal()
    {
        string text = PreviewForm.BuildNextCopyPrompt(1, 3);
        Assert.Contains("1 部目", text);
        Assert.Contains("全 3 部", text);
    }

    [Fact]
    public void BuildCopiesStoppedText_ShowsBothTheFinishedCountAndTheTotal()
    {
        string text = PreviewForm.BuildCopiesStoppedText(1, 3, "利用者が中止しました");
        Assert.Contains("3 部のうち", text);
        Assert.Contains("1 部を刷った", text);
        Assert.Contains("利用者が中止しました", text);
    }
}
