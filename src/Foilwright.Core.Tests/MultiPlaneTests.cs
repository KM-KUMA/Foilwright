// Foilwright.Core.Tests — 転送モード multiPlane の構造検証(DOMAIN §14.8)。
//
// ref/tests/test_multi_plane.py と同じ観点をここでも検査する。byte-exact な
// golden 検証は GoldenTests.cs の G25/G26/G27 で別途行っている。ここで見るのは
// 判断そのもの — いつ colourPlane から multiPlane へ切り替わるか、カセット
// 一覧がどんな形か、ppmtomd が敷いている 2 つの境界(バーコード必須・
// 印字色 7 本まで)— で、どれも単一の golden fixture には現れない。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class MultiPlaneTests
{
    private const byte Esc = 0x1B;

    // EmitterPassesTests と同じ 1x8 の全面黒プレーン。ラスタ本体を非空・
    // 決定的にするためだけの最小構成。
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

    private static JobInk Ink(int index, int passes = 1, int? barcode = null) => new()
    {
        Name = "ink" + index,
        PrinterCode = index,
        Barcode = barcode ?? 10 + index,
        Passes = passes,
    };

    private static List<JobInk> Inks(int count, int passes = 1)
    {
        var inks = new List<JobInk>();
        for (int i = 0; i < count; i++)
        {
            inks.Add(Ink(i, passes));
        }
        return inks;
    }

    private static byte[] Emit(IReadOnlyList<JobInk> inks, string transferMode = "colour_plane")
    {
        var planes = new Dictionary<string, byte[]>();
        foreach (var ink in inks)
        {
            planes[ink.Name] = Plane;
        }
        var job = new PrintJob
        {
            Resolution = 600,
            Paper = Paper,
            Media = Media,
            Inks = inks,
            Width = Width,
            Height = Height,
            TransferMode = transferMode,
        };
        return Emitter.EmitJob(planes, job);
    }

    /// <summary>ストリーム中に 1 つだけあるはずの `ESC * r {mode} U` の
    /// mode バイトを返す。</summary>
    private static byte TransferModeByte(byte[] stream)
    {
        var positions = new List<int>();
        for (int i = 0; i < stream.Length - 4; i++)
        {
            if (stream[i] == Esc && stream[i + 1] == 0x2A && stream[i + 2] == 0x72 && stream[i + 4] == 0x55)
            {
                positions.Add(i);
            }
        }
        Assert.Single(positions);
        return stream[positions[0] + 3];
    }

    /// <summary>`ESC &amp; l {本数} 00 C` コマンドのペイロードをすべて返す。
    ///
    /// バイト列をパターン検索するのではなく先頭から歩くので、ラスタデータを
    /// コマンドと取り違えることがない。</summary>
    private static List<byte[]> CassetteLists(byte[] stream)
    {
        var found = new List<byte[]>();
        int i = 0;
        while (i < stream.Length)
        {
            if (stream[i] != Esc)
            {
                i += 1;
                continue;
            }
            if (stream[i + 1] == 0x26 && stream[i + 5] == 0x43)
            {
                int count = stream[i + 3];
                Assert.Equal(0, stream[i + 4]); // 本数は 1 バイト
                found.Add(stream[(i + 6)..(i + 6 + count)]);
                i += 6 + count;
                continue;
            }
            i += 1;
        }
        return found;
    }

    private static int IndexOfSequence(byte[] stream, params byte[] needle)
    {
        for (int i = 0; i + needle.Length <= stream.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (stream[i + j] != needle[j])
                {
                    hit = false;
                    break;
                }
            }
            if (hit)
            {
                return i;
            }
        }
        return -1;
    }

    [Fact]
    public void FourInksStayColourPlane()
    {
        var stream = Emit(Inks(4));
        Assert.Equal(0x04, TransferModeByte(stream));
        Assert.Empty(CassetteLists(stream));
    }

    [Fact]
    public void FiveInksSwitchToMultiPlaneAndListCassettes()
    {
        var stream = Emit(Inks(5));
        Assert.Equal(0x08, TransferModeByte(stream));
        // インク 1 本につき 1 エントリ、印刷順、各インクの Barcode から取る。
        var lists = CassetteLists(stream);
        Assert.Single(lists);
        Assert.Equal(new byte[] { 10, 11, 12, 13, 14 }, lists[0]);
    }

    [Fact]
    public void CassetteListSitsBetweenPageWidthAndTheCurlCommand()
    {
        // 位置が意味を持つ: ppmtomd は rgl_init_page の中、ページ幅コマンドの
        // 後、x/y シフトとカールバイトの前に出す(ppmtomd.c:2526-2544)。
        var stream = Emit(Inks(5));
        int pageWidth = IndexOfSequence(stream, Esc, 0x26, 0x61);
        int cassette = IndexOfSequence(stream, Esc, 0x26, 0x6C, 5, 0, 0x43);
        int curl = IndexOfSequence(stream, Esc, 0x1A, 0, 0, 0x43);
        Assert.True(pageWidth >= 0 && cassette >= 0 && curl >= 0);
        Assert.True(pageWidth < cassette);
        Assert.True(cassette < curl);
    }

    [Fact]
    public void SevenInksAreAllowed()
    {
        Assert.Equal(0x08, TransferModeByte(Emit(Inks(7))));
    }

    [Fact]
    public void EightInksAreRejected()
    {
        // ppmtomd 自身の境界(ppmtomd.c:1778)。印字ヘッドに載るのは 7 本
        // までなので、8 本目は誤った印刷をせず失敗しなければならない。
        var ex = Assert.Throws<ArgumentException>(() => Emit(Inks(8)));
        Assert.Contains("too many printing colours", ex.Message);
    }

    [Fact]
    public void MissingBarcodeIsRejected()
    {
        var inks = Inks(5);
        inks[3] = new JobInk { Name = inks[3].Name, PrinterCode = inks[3].PrinterCode };
        var ex = Assert.Throws<ArgumentException>(() => Emit(inks));
        Assert.Contains("barcode", ex.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(256)]
    public void BadBarcodeIsRejected(int barcode)
    {
        var inks = Inks(5);
        inks[2] = Ink(2, barcode: barcode);
        var ex = Assert.Throws<ArgumentException>(() => Emit(inks));
        Assert.Contains("barcode", ex.Message);
    }

    [Fact]
    public void BarcodeNotRequiredBelowTheThreshold()
    {
        // 4 本ではカセット一覧を送らないので、バーコードを要求してはならない
        // — 要求すると既存の呼び出し元がすべて壊れる。
        var inks = new List<JobInk>();
        for (int i = 0; i < 4; i++)
        {
            inks.Add(new JobInk { Name = "ink" + i, PrinterCode = i });
        }
        Assert.Equal(0x04, TransferModeByte(Emit(inks)));
    }

    [Fact]
    public void PassesDoNotCountAsPrintingColours()
    {
        // passes(DOMAIN §6.2)は同じカセットを刷り直すだけなので、4 本の
        // ジョブを multiPlane の閾値の向こうへ押し出してはならない。
        // プリンタに載せてもらうカセットは依然として 4 本。
        var stream = Emit(Inks(4, passes: 3));
        Assert.Equal(0x04, TransferModeByte(stream));
        Assert.Empty(CassetteLists(stream));
    }

    [Fact]
    public void ExplicitBlackRasterIsNotUpgraded()
    {
        // 格上げされるのは colourPlane だけ(ppmtomd.c:1780-1783)。明示的に
        // 要求された単一プレーンのモードは要求どおりのまま。
        var stream = Emit(new List<JobInk> { Ink(0) }, transferMode: "black_raster");
        Assert.Equal(0x00, TransferModeByte(stream));
        Assert.Empty(CassetteLists(stream));
    }
}
