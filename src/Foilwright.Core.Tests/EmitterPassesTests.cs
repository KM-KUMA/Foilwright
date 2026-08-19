// Foilwright.Core.Tests — インク `passes`(重ね塗り; DOMAIN §6.2)の構造検証。
//
// ref/tests/test_passes.py と同じ観点をここでも検査する。ここではコマンド
// ストリームの構造だけを見る: (色選択+ラスタ)を passes 回繰り返し、出現の
// 間はすべてバックフィードで区切り、最終フラグ(0x80)はジョブ全体で最後の
// 1 回だけ、排出は 1 回だけ。byte-exact な golden 検証は GoldenTests.cs の
// G21WhiteTwiceMd5000_600 / G22WhiteThriceMd5000_600 で別途行っている。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class EmitterPassesTests
{
    private const byte Esc = 0x1B;

    // 1x8 の全面黒プレーン(1 行 1 バイト、全ビット立て)。ラスタ本体を
    // 非空・決定的にするためだけの最小構成(GoldenTests.cs のような
    // 実画像・パレットは不要)。
    private const int Width = 8;
    private const int Height = 1;
    private static readonly byte[] Plane = { 0xFF };

    private static readonly PaperSpec Paper = new()
    {
        Code = 4,
        Width = 100,
        Length = 100,
        LeftMargin = 0,
        TopMargin = 0,
    };

    private static readonly MediaSpec Media = new()
    {
        Label = "plain",
        Byte1 = 0,
        Byte2 = 0,
    };

    private static PrintJob BuildJob(IReadOnlyList<JobInk> inks) => new()
    {
        Resolution = 600,
        Paper = Paper,
        Media = Media,
        Inks = inks,
        Width = Width,
        Height = Height,
    };

    private static Dictionary<string, byte[]> PlanesFor(IReadOnlyList<JobInk> inks)
    {
        var planes = new Dictionary<string, byte[]>();
        foreach (var ink in inks)
        {
            planes[ink.Name] = Plane;
        }
        return planes;
    }

    /// <summary>ストリーム中の色選択コマンド(`\x1b\x1a{code}{flag}r`)を
    /// (printerCode, flag) の並びとして抽出する。末尾オペコードで
    /// バックフィード(0x0C)と区別する。</summary>
    private static List<(byte Code, byte Flag)> FindSelections(byte[] stream)
    {
        var selections = new List<(byte, byte)>();
        int i = 0;
        while (i < stream.Length - 4)
        {
            if (stream[i] == Esc && stream[i + 1] == 0x1A && stream[i + 4] == 0x72)
            {
                selections.Add((stream[i + 2], stream[i + 3]));
                i += 5;
            }
            else
            {
                i += 1;
            }
        }
        return selections;
    }

    private static int CountBackfeeds(byte[] stream)
    {
        byte[] target = { Esc, 0x1A, 0x00, 0x00, 0x0C };
        int count = 0;
        for (int i = 0; i + target.Length <= stream.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < target.Length; j++)
            {
                if (stream[i + j] != target[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                count += 1;
            }
        }
        return count;
    }

    /// <summary>バックフィードの一部ではない、単独のフォームフィード
    /// (0x0C)を数える。</summary>
    private static int CountEjects(byte[] stream)
    {
        byte[] backfeed = { Esc, 0x1A, 0x00, 0x00, 0x0C };
        var backfeedPositions = new HashSet<int>();
        for (int i = 0; i + backfeed.Length <= stream.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < backfeed.Length; j++)
            {
                if (stream[i + j] != backfeed[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                backfeedPositions.Add(i + 4);
            }
        }

        int count = 0;
        for (int i = 0; i < stream.Length; i++)
        {
            if (stream[i] == 0x0C && !backfeedPositions.Contains(i))
            {
                count += 1;
            }
        }
        return count;
    }

    [Fact]
    public void SingleInkPasses2_SelectsTwice_OneBackfeed_FinalFlagOnce()
    {
        var inks = new List<JobInk> { new() { Name = "white", PrinterCode = 0x0B, Passes = 2 } };
        var job = BuildJob(inks);
        var stream = Emitter.EmitJob(PlanesFor(inks), job);

        var selections = FindSelections(stream);
        Assert.Equal(new List<(byte, byte)> { (0x0B, 0x00), (0x0B, 0x80) }, selections);
        Assert.Equal(1, CountBackfeeds(stream));
        Assert.Equal(1, CountEjects(stream));
    }

    [Fact]
    public void SingleInkPasses3_SelectsThrice_TwoBackfeeds()
    {
        var inks = new List<JobInk> { new() { Name = "white", PrinterCode = 0x0B, Passes = 3 } };
        var job = BuildJob(inks);
        var stream = Emitter.EmitJob(PlanesFor(inks), job);

        var selections = FindSelections(stream);
        Assert.Equal(new List<(byte, byte)> { (0x0B, 0x00), (0x0B, 0x00), (0x0B, 0x80) }, selections);
        Assert.Equal(2, CountBackfeeds(stream));
        Assert.Equal(1, CountEjects(stream));
    }

    [Fact]
    public void Passes1_MatchesOmittedPasses_ByteForByte()
    {
        var inksExplicit = new List<JobInk> { new() { Name = "white", PrinterCode = 0x0B, Passes = 1 } };
        var inksDefault = new List<JobInk> { new() { Name = "white", PrinterCode = 0x0B } };

        var streamExplicit = Emitter.EmitJob(PlanesFor(inksExplicit), BuildJob(inksExplicit));
        var streamDefault = Emitter.EmitJob(PlanesFor(inksDefault), BuildJob(inksDefault));

        Assert.Equal(streamDefault, streamExplicit);
    }

    [Fact]
    public void MultiInkMultiPasses_EjectsExactlyOnce()
    {
        var inks = new List<JobInk>
        {
            new() { Name = "white", PrinterCode = 0x0B, Passes = 2 },
            new() { Name = "cyan", PrinterCode = 0x01, Passes = 3 },
            new() { Name = "black", PrinterCode = 0x00 }, // 既定 passes=1
        };
        var job = BuildJob(inks);
        var stream = Emitter.EmitJob(PlanesFor(inks), job);

        var selections = FindSelections(stream);
        // white 2 + cyan 3 + black 1 = 6 出現、最終フラグは最後だけ。
        Assert.Equal(
            new List<(byte, byte)>
            {
                (0x0B, 0x00),
                (0x0B, 0x00),
                (0x01, 0x00),
                (0x01, 0x00),
                (0x01, 0x00),
                (0x00, 0x80),
            },
            selections);
        Assert.Equal(5, CountBackfeeds(stream)); // 6 出現 -> 出現間のバックフィード 5
        Assert.Equal(1, CountEjects(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5)]
    public void PassesZeroOrNegative_Throws(int badPasses)
    {
        var inks = new List<JobInk> { new() { Name = "white", PrinterCode = 0x0B, Passes = badPasses } };
        var job = BuildJob(inks);
        Assert.Throws<ArgumentException>(() => Emitter.EmitJob(PlanesFor(inks), job));
    }
}
