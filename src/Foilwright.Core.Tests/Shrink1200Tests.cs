// Foilwright.Core.Tests — D-052 の不変条件: 1200dpi ではプロセスインク以外の
// プレーンだけ横 1/2 に縮む。
//
// プリンタは走査解像度をカセットのバーコードで決めるため、特色 / 塗る範囲の
// カセットはジョブが 1200 を指定していても 600dpi で走る(DOMAIN §14.7.1)。
// emitter はそのプレーンを横 1/2(横に並ぶ 2 ドットの OR)にして打ち消す。
//
// **ppmtomd はこれをやらないので golden では守れない**(D-052)。この構造検証が
// その代わり。MultiPlaneTests.cs / EmitterPassesTests.cs と同じ流儀で、
// コマンド列を直接読んで判定する。
//
// ref/tests/test_shrink_1200.py が対になる(D-006)。片方だけ直さないこと。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class Shrink1200Tests
{
    private const byte Esc = 0x1B;
    private const int SpotCode = 0x0B;
    private const int ProcessCode = 0x00;

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

    /// <summary>Emitter の PackBits 圧縮の逆変換(ppmtomd の形式)。
    /// カウントバイト c(符号付き)が c &gt;= 0 なら次の c+1 バイトがそのまま、
    /// c &lt; 0 なら次の 1 バイトが -c+1 回繰り返す。</summary>
    private static byte[] UnpackPackBits(ReadOnlySpan<byte> data)
    {
        var outBytes = new List<byte>();
        int i = 0;
        while (i < data.Length)
        {
            int count = (sbyte)data[i];
            i += 1;
            if (count >= 0)
            {
                for (int j = 0; j <= count; j++)
                {
                    outBytes.Add(data[i + j]);
                }
                i += count + 1;
            }
            else
            {
                for (int j = 0; j <= -count; j++)
                {
                    outBytes.Add(data[i]);
                }
                i += 1;
            }
        }
        return outBytes.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int from = 0)
    {
        for (int i = from; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            }
            if (hit) { return i; }
        }
        return -1;
    }

    /// <summary>ラスタ部を読んで {printer_code: [行, ...]} を返す。
    ///
    /// 行は rowBytesByCode[code] の長さへ 0 で埋め戻し(emitter は末尾ゼロを
    /// 切り詰める)、空白行スキップコマンドの分は全 0 の行として補う。
    /// 走査は完全に逐次 — すべてのコマンドの長さを消費する — なので、
    /// ラスタのデータバイトをコマンドと読み違えることはない。</summary>
    private static Dictionary<int, List<byte[]>> RowsByInk(
        byte[] stream, IReadOnlyDictionary<int, int> rowBytesByCode)
    {
        int start = IndexOf(stream, new byte[] { Esc, 0x2A, 0x72, 0x00, 0x41 });
        Assert.True(start >= 0, "start-raster-graphics command not found");
        int i = start + 5;

        var rows = new Dictionary<int, List<byte[]>>();
        int current = -1;
        bool compressed = false;
        var end = new byte[] { Esc, 0x2A, 0x72, 0x43 };

        while (i < stream.Length)
        {
            if (IndexOf(stream, end, i) == i) { break; }
            Assert.Equal(Esc, stream[i]);
            if (stream[i + 1] == 0x1A)
            {
                // 色選択('r' で終わる)かバックフィード(0x0C で終わる)
                if (stream[i + 4] == 0x72)
                {
                    current = stream[i + 2];
                    if (!rows.ContainsKey(current)) { rows[current] = new List<byte[]>(); }
                    compressed = false;
                }
                i += 5;
                continue;
            }
            Assert.Equal(0x2A, stream[i + 1]);
            Assert.Equal(0x62, stream[i + 2]);
            int value = stream[i + 3] + stream[i + 4] * 256;
            byte opcode = stream[i + 5];
            i += 6;
            if (opcode == 0x4D)
            {
                compressed = value == 2;
            }
            else if (opcode == 0x59)
            {
                for (int n = 0; n < value; n++)
                {
                    rows[current].Add(new byte[rowBytesByCode[current]]);
                }
            }
            else if (opcode == 0x56 || opcode == 0x57)
            {
                var data = new ReadOnlySpan<byte>(stream, i, value);
                i += value;
                byte[] raw = compressed ? UnpackPackBits(data) : data.ToArray();
                int width = rowBytesByCode[current];
                Assert.True(raw.Length <= width, $"row longer than the plane's row: {raw.Length} > {width}");
                var padded = new byte[width];
                raw.CopyTo(padded, 0);
                rows[current].Add(padded);
            }
            else
            {
                Assert.Fail($"unknown raster opcode 0x{opcode:x2} at {i - 1}");
            }
        }
        return rows;
    }

    private static byte[] Emit(byte[] plane, int width, int height, int resolution, bool spotIsProcess = false)
    {
        var inks = new List<JobInk>
        {
            new() { Name = "spot", PrinterCode = SpotCode, IsProcess = spotIsProcess },
            new() { Name = "process", PrinterCode = ProcessCode, IsProcess = true },
        };
        var job = new PrintJob
        {
            Resolution = resolution,
            Paper = Paper,
            Media = Media,
            Inks = inks,
            Width = width,
            Height = height,
        };
        var planes = new Dictionary<string, byte[]> { ["spot"] = plane, ["process"] = plane };
        return Emitter.EmitJob(planes, job);
    }

    /// <summary>JobInk.IsProcess を一切指定しない(既定のまま)ジョブ。</summary>
    private static byte[] EmitWithoutInkKind(byte[] plane, int width, int height, int resolution)
    {
        var inks = new List<JobInk>
        {
            new() { Name = "spot", PrinterCode = SpotCode },
            new() { Name = "process", PrinterCode = ProcessCode },
        };
        var job = new PrintJob
        {
            Resolution = resolution,
            Paper = Paper,
            Media = Media,
            Inks = inks,
            Width = width,
            Height = height,
        };
        var planes = new Dictionary<string, byte[]> { ["spot"] = plane, ["process"] = plane };
        return Emitter.EmitJob(planes, job);
    }

    private static (List<byte[]> Spot, List<byte[]> Process) Rows(
        byte[] plane, int width, int height, int resolution, bool spotIsProcess = false)
    {
        byte[] stream = Emit(plane, width, height, resolution, spotIsProcess);
        int srcRowBytes = (width + 7) / 8;
        bool shrunk = resolution == 1200 && !spotIsProcess;
        int dstRowBytes = shrunk ? ((width + 1) / 2 + 7) / 8 : srcRowBytes;
        var parsed = RowsByInk(stream, new Dictionary<int, int>
        {
            [SpotCode] = dstRowBytes,
            [ProcessCode] = srcRowBytes,
        });
        return (parsed[SpotCode], parsed[ProcessCode]);
    }

    private static void AssertRows(List<byte[]> actual, params byte[][] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    [Fact]
    public void Halves1200SpotPlaneWidth()
    {
        var (spot, _) = Rows(new byte[] { 0xFF, 0xFF }, 16, 1, 1200);
        AssertRows(spot, new byte[] { 0xFF });
    }

    [Fact]
    public void Leaves1200ProcessPlaneAlone()
    {
        var (_, process) = Rows(new byte[] { 0xFF, 0xFF }, 16, 1, 1200);
        AssertRows(process, new byte[] { 0xFF, 0xFF });
    }

    [Fact]
    public void Leaves600SpotPlaneAlone()
    {
        var (spot, process) = Rows(new byte[] { 0xFF, 0xFF }, 16, 1, 600);
        AssertRows(spot, new byte[] { 0xFF, 0xFF });
        AssertRows(process, new byte[] { 0xFF, 0xFF });
    }

    [Fact]
    public void Leaves300SpotPlaneAlone()
    {
        var (spot, _) = Rows(new byte[] { 0xFF, 0xFF }, 16, 1, 300);
        AssertRows(spot, new byte[] { 0xFF, 0xFF });
    }

    /// <summary>「1200 以外では 1 バイトも変わらない」の一番強い形(D-052)。</summary>
    [Theory]
    [InlineData(300)]
    [InlineData(600)]
    public void OutputDoesNotDependOnInkKindBelow1200(int resolution)
    {
        byte[] asSpot = Emit(new byte[] { 0x55, 0xAA }, 16, 1, resolution, spotIsProcess: false);
        byte[] asProcess = Emit(new byte[] { 0x55, 0xAA }, 16, 1, resolution, spotIsProcess: true);
        Assert.Equal(asProcess, asSpot);
    }

    /// <summary>種別を知らない呼び出し元は ppmtomd と同じバイトのまま。</summary>
    [Fact]
    public void MissingInkKindDefaultsToProcessAndDoesNotShrink()
    {
        byte[] omitted = EmitWithoutInkKind(new byte[] { 0xFF, 0xFF }, 16, 1, 1200);
        byte[] asProcess = Emit(new byte[] { 0xFF, 0xFF }, 16, 1, 1200, spotIsProcess: true);
        Assert.Equal(asProcess, omitted);
    }

    /// <summary>縮め方が OR であることの検出器。
    ///
    /// 0x55 は奇数列(1, 3, 5, 7)だけを立てる。OR なら 4 組すべてが残るが、
    /// 偶数列を採る間引きだと 4 ドットとも落ちて行ごと消える。</summary>
    [Fact]
    public void ShrinkIsOrNotDecimation()
    {
        var (spot, _) = Rows(new byte[] { 0x55, 0x00 }, 16, 1, 1200);
        AssertRows(spot, new byte[] { 0xF0 });
    }

    /// <summary>0xAA は偶数列だけを立てる。OR はこちらも残す。</summary>
    [Fact]
    public void ShrinkIsOrTheOtherWayRoundToo()
    {
        var (spot, _) = Rows(new byte[] { 0xAA, 0x00 }, 16, 1, 1200);
        AssertRows(spot, new byte[] { 0xF0 });
    }

    [Fact]
    public void RowPaddingBitsStayZero()
    {
        // 幅 10 -> 出力 5 ドットが 1 バイトの行に入る。ビット 5..7 がパディング。
        var (spot, _) = Rows(new byte[] { 0xFF, 0xC0 }, 10, 1, 1200);
        AssertRows(spot, new byte[] { 0xF8 });
        Assert.Equal(0, spot[0][0] & 0x07);
    }

    /// <summary>幅 9: ソースのドット 8 には相棒がいない。単独で残す
    /// (出力幅 ceil(9/2) = 5)ので、インクの列が欠けない。</summary>
    [Fact]
    public void OddWidthKeepsTheLastLoneDot()
    {
        var (spot, _) = Rows(new byte[] { 0x00, 0x80 }, 9, 1, 1200);
        AssertRows(spot, new byte[] { 0x08 });
    }

    [Fact]
    public void OddWidthOutputIsCeilHalf()
    {
        var (spot, _) = Rows(new byte[] { 0xFF, 0x80 }, 9, 1, 1200);
        AssertRows(spot, new byte[] { 0xF8 });
    }

    /// <summary>ページ幅コマンドはジョブ単位で 1200 のまま。縮んだインクの
    /// 行の長さだけが変わる(D-052)。</summary>
    [Fact]
    public void PageWidthCommandIsUnaffectedByShrinking()
    {
        byte[] asSpot = Emit(new byte[] { 0xFF, 0xFF }, 16, 1, 1200, spotIsProcess: false);
        byte[] asProcess = Emit(new byte[] { 0xFF, 0xFF }, 16, 1, 1200, spotIsProcess: true);
        var marker = new byte[] { Esc, 0x26, 0x61 };
        int a = IndexOf(asSpot, marker);
        int b = IndexOf(asProcess, marker);
        Assert.True(a >= 0);
        Assert.Equal(asProcess[b..(b + 6)], asSpot[a..(a + 6)]);
        Assert.Equal(0x4D, asSpot[a + 5]);
        // 一方でストリーム全体は違う = 上の一致は「何も起きていない」せいではない。
        Assert.NotEqual(asProcess, asSpot);
    }

    [Fact]
    public void MultiRowPlaneShrinksEveryRow()
    {
        var plane = new byte[] { 0x55, 0x00, 0xAA, 0x00, 0xFF, 0xFF };
        var (spot, _) = Rows(plane, 16, 3, 1200);
        AssertRows(spot, new byte[] { 0xF0 }, new byte[] { 0xF0 }, new byte[] { 0xFF });
    }

    [Fact]
    public void BlackRasterModeShrinksToo()
    {
        var job = new PrintJob
        {
            Resolution = 1200,
            Paper = Paper,
            Media = Media,
            Inks = new List<JobInk> { new() { Name = "spot", PrinterCode = SpotCode, IsProcess = false } },
            Width = 16,
            Height = 1,
            TransferMode = "black_raster",
        };
        var planes = new Dictionary<string, byte[]> { ["spot"] = new byte[] { 0x55, 0x00 } };
        byte[] stream = Emitter.EmitJob(planes, job);
        // black_raster は色選択コマンドを出さないので、RowsByInk ではなく
        // ラスタ部を直接見る。
        int start = IndexOf(stream, new byte[] { Esc, 0x2A, 0x72, 0x00, 0x41 }) + 5;
        int end = IndexOf(stream, new byte[] { Esc, 0x2A, 0x72, 0x43 }, start);
        byte[] body = stream[start..end];
        Assert.Equal(0xF0, body[^1]);
    }
}
