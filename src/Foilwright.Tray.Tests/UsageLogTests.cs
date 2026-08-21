// Foilwright.Tray.Tests — リボン消費の記録(UsageLog)の単体テスト。
//
// 対象は画面に触らない純粋な処理(Summarise / ParseLines)と、記録 1 行の
// JSON 往復。PresetTests と同じ形で、ここが壊れの検出器になる。
//
// **いちばん大事なのは 2 つ:**
//   ① TotalDots が Dots × Passes で数えられていること
//      — 重ね塗りした回数ぶんリボンは減る。ここを Dots の合計にすると記録が嘘になる。
//   ② Outcome が "failed" の記録も数に入ること
//      — 途中で止まってもリボンは減っている。ここが意図の検出器。
//
// ファイルには触らない(%APPDATA% の実物を汚さない)。壊れ耐性は
// ParseLines に行を直接渡して確かめる。

using System.Text.Json;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class UsageLogTests
{
    private static UsageRecord Record(
        string ink,
        long dots,
        int passes = 1,
        string outcome = "completed",
        string timestamp = "2026-08-21T10:00:00+00:00") =>
        new()
        {
            Timestamp = DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture),
            Ink = ink,
            Dots = dots,
            Passes = passes,
            Copy = 1,
            Copies = 1,
            Paper = "a4",
            Media = "plain_paper",
            Resolution = "600",
            Outcome = outcome,
        };

    // --- Summarise ------------------------------------------------------------

    [Fact]
    public void Summarise_NoRecordsGivesAnEmptyList()
    {
        Assert.Empty(UsageLog.Summarise(new List<UsageRecord>()));
    }

    [Fact]
    public void Summarise_TotalDotsMultipliesByPasses()
    {
        // 重ね塗りの回数ぶん消費する。Dots=100 / Passes=3 なら 300。
        var summaries = UsageLog.Summarise(new[] { Record("white", dots: 100, passes: 3) });
        var white = Assert.Single(summaries);
        Assert.Equal(300, white.TotalDots);
        Assert.Equal(3, white.TotalPasses);
        Assert.Equal(1, white.Jobs);
    }

    [Fact]
    public void Summarise_MergesRecordsOfTheSameInk()
    {
        var summaries = UsageLog.Summarise(new[]
        {
            Record("black", dots: 100, passes: 1),
            Record("black", dots: 50, passes: 2),
        });
        var black = Assert.Single(summaries);
        Assert.Equal(200, black.TotalDots);   // 100*1 + 50*2
        Assert.Equal(3, black.TotalPasses);
        Assert.Equal(2, black.Jobs);
    }

    [Fact]
    public void Summarise_OrdersByTotalDotsDescending()
    {
        var summaries = UsageLog.Summarise(new[]
        {
            Record("black", dots: 10),
            Record("white", dots: 1000),
            Record("cyan", dots: 100),
        });
        Assert.Equal(new[] { "white", "cyan", "black" }, summaries.Select(s => s.Ink));
    }

    [Fact]
    public void Summarise_TiesAreOrderedByInkName()
    {
        var summaries = UsageLog.Summarise(new[]
        {
            Record("white", dots: 100),
            Record("black", dots: 100),
            Record("cyan", dots: 100),
        });
        // 同数ならインク名の順(Ordinal)。
        Assert.Equal(new[] { "black", "cyan", "white" }, summaries.Select(s => s.Ink));
    }

    [Fact]
    public void Summarise_CountsFailedRecordsToo()
    {
        // 途中で止まってもリボンは減っている。failed を落としてはならない。
        var summaries = UsageLog.Summarise(new[]
        {
            Record("white", dots: 100, outcome: "completed"),
            Record("white", dots: 400, outcome: "failed"),
        });
        var white = Assert.Single(summaries);
        Assert.Equal(500, white.TotalDots);
        Assert.Equal(2, white.Jobs);
    }

    [Fact]
    public void Summarise_FirstAndLastUsedAreTheMinimumAndMaximum()
    {
        // 順不同で入れても正しいこと(ファイルを手で編集して並びが崩れる可能性がある)。
        var summaries = UsageLog.Summarise(new[]
        {
            Record("white", dots: 1, timestamp: "2026-08-15T01:00:00+00:00"),
            Record("white", dots: 1, timestamp: "2026-08-10T01:00:00+00:00"),
            Record("white", dots: 1, timestamp: "2026-08-21T01:00:00+00:00"),
        });
        var white = Assert.Single(summaries);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-10T01:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            white.FirstUsed);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-21T01:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture),
            white.LastUsed);
    }

    // --- 壊れ耐性 -------------------------------------------------------------

    [Fact]
    public void ParseLines_SkipsBrokenLinesAndKeepsTheRest()
    {
        // 印刷のたびに書くファイルなので、1 行壊れて全履歴を失うのは困る。
        string good1 = JsonSerializer.Serialize(Record("white", dots: 10));
        string good2 = JsonSerializer.Serialize(Record("black", dots: 20));
        var parsed = UsageLog.ParseLines(new[] { good1, "{\"Ink\": broken", good2 });
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new[] { "white", "black" }, parsed.Select(r => r.Ink));
    }

    [Fact]
    public void ParseLines_SkipsLinesMissingRequiredFields()
    {
        // JSON としては読めるが中身が足りない行(手で編集して壊した場合)も落とす。
        var parsed = UsageLog.ParseLines(new[] { "{\"Ink\":\"white\"}", JsonSerializer.Serialize(Record("black", dots: 5)) });
        var only = Assert.Single(parsed);
        Assert.Equal("black", only.Ink);
    }

    [Fact]
    public void ParseLines_IgnoresBlankLines()
    {
        var parsed = UsageLog.ParseLines(new[] { string.Empty, "   ", JsonSerializer.Serialize(Record("white", dots: 1)) });
        Assert.Single(parsed);
    }

    // --- JSON 往復 ------------------------------------------------------------

    [Fact]
    public void UsageRecord_SurvivesTheJsonRoundTrip()
    {
        var original = new UsageRecord
        {
            Timestamp = DateTimeOffset.Parse(
                "2026-08-21T12:34:56.7890000+09:00", System.Globalization.CultureInfo.InvariantCulture),
            Ink = "white",
            Dots = 1234567,
            Passes = 2,
            Copy = 3,
            Copies = 5,
            Paper = "hagaki",
            Media = "decal_film",
            Resolution = "1200x600",
            Outcome = "failed",
        };

        string line = JsonSerializer.Serialize(original);
        // JSON Lines である以上、1 レコードが 1 行に収まっていること。
        Assert.DoesNotContain("\n", line);
        Assert.DoesNotContain("\r", line);

        var restored = Assert.Single(UsageLog.ParseLines(new[] { line }));
        Assert.Equal(original.Timestamp, restored.Timestamp);
        Assert.Equal(original.Ink, restored.Ink);
        Assert.Equal(original.Dots, restored.Dots);
        Assert.Equal(original.Passes, restored.Passes);
        Assert.Equal(original.Copy, restored.Copy);
        Assert.Equal(original.Copies, restored.Copies);
        Assert.Equal(original.Paper, restored.Paper);
        Assert.Equal(original.Media, restored.Media);
        Assert.Equal(original.Resolution, restored.Resolution);
        Assert.Equal(original.Outcome, restored.Outcome);
    }

    [Fact]
    public void UsageRecord_TimestampIsWrittenAsIso8601()
    {
        string line = JsonSerializer.Serialize(Record("white", dots: 1, timestamp: "2026-08-21T10:00:00+00:00"));
        Assert.Contains("2026-08-21T10:00:00", line);
    }

    // --- 場所 -----------------------------------------------------------------

    [Fact]
    public void FilePath_SitsNextToSettingsJson()
    {
        // settings.json / presets.json と同じフォルダ(パスを二重に書かない)。
        Assert.Equal(TraySettings.ConfigFolder, Path.GetDirectoryName(UsageLog.FilePath));
        Assert.Equal("usage.jsonl", Path.GetFileName(UsageLog.FilePath));
    }
}
