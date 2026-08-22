// Foilwright.Tray.Tests — 「ドット数」欄がそのジョブの消費量を出すことの検出器。
//
// 版の点の数はパス数を変えても変わらない(同じ版を同じ場所へ重ねるだけ)。
// ところがこの道具でいちばんきつい制約はリボンであり、刷る前に知りたいのは
// 「このジョブでどれだけ使うか」= 版の点の数 × パス数 のほうである。
// 2026-08-22 に利用者から「パス数を変えてもドット数が変わらないが、それでよいのか」
// と問われて足した。疑問を持たれた時点で、列が仕事をしていなかった。
//
// リボン消費の帳簿(D-046)も同じ掛け算をしている。**表示と帳簿がずれてはいけない。**

using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class DotCountFormatTests
{
    [Fact]
    public void SinglePassShowsJustTheNumber()
    {
        // 1 回なら内訳は同じ数字の繰り返しになるので出さない。
        Assert.Equal("30,961", PreviewForm.FormatDotCount(30_961, 1));
    }

    [Fact]
    public void MultiplePassesShowTheTotalFirst()
    {
        // 先頭が消費量。括弧の中が版の点の数と回数。
        Assert.Equal("92,883 (30,961×3)", PreviewForm.FormatDotCount(30_961, 3));
    }

    [Fact]
    public void ZeroDotsStaysReadable()
    {
        // ジョブに出ていないインク。掛けても 0 なので内訳は出す意味がある
        // (「パス数は 3 だが 1 ドットも刷らない」ことが読み取れる)。
        Assert.Equal("0 (0×3)", PreviewForm.FormatDotCount(0, 3));
        Assert.Equal("0", PreviewForm.FormatDotCount(0, 1));
    }

    [Theory]
    [InlineData(30_961, 3)]
    [InlineData(181_654, 2)]
    [InlineData(1, 8)]
    public void TheLeadingNumberIsAlwaysDotsTimesPasses(long dots, int passes)
    {
        // ここが帳簿(UsageLog.Summarise の TotalDots = Dots × Passes)と同じ
        // 掛け算であること。表示と帳簿がずれると、どちらを信じてよいか分からなくなる。
        string text = PreviewForm.FormatDotCount(dots, passes);
        string leading = text.Split(' ')[0];

        Assert.Equal((dots * passes).ToString("N0"), leading);
    }

    [Fact]
    public void PassesBelowOneIsTreatedAsASinglePass()
    {
        // 0 や負が来ることは無いはずだが、来ても掛けて 0 にしない
        // (欄が「刷らない」と読めてしまう)。
        Assert.Equal("30,961", PreviewForm.FormatDotCount(30_961, 0));
    }
}
