// Foilwright.Core.Tests — PngImage.Read の検証(D-036)。
//
// tools/make-png-fixtures.py が生成する tests/cases/png/*.png を使う。
// フィルタ 5 種・IDAT 分割・補助チャンク・(あれば)実際の Ghostscript
// pngalpha 出力を確認する。対応外の形式を弾くパスも確認する。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class PngTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CasesDir = Path.Combine(RepoRoot, "tests", "cases", "png");

    private static string FindRepoRoot()
    {
        // src/Foilwright.Core.Tests/bin/Debug/net10.0 から 5 階層上がる。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir.Parent is null)
            {
                break;
            }
            dir = dir.Parent;
        }
        return dir.FullName;
    }

    private static (byte, byte, byte, byte) PixelAt(byte[] pixels, int width, int x, int y)
    {
        int idx = (y * width + x) * 4;
        return (pixels[idx], pixels[idx + 1], pixels[idx + 2], pixels[idx + 3]);
    }

    [Theory]
    [InlineData("filter0_none.png")]
    [InlineData("filter1_sub.png")]
    [InlineData("filter2_up.png")]
    [InlineData("filter3_average.png")]
    [InlineData("filter4_paeth.png")]
    public void FilterFixtures_DecodeToSamePixels(string name)
    {
        // 5 つのフィルタフィクスチャは同じ模様を別々のフィルタで符号化
        // したもの(tools/make-png-fixtures.py の _patterned_pixels)。
        // どのフィルタでも、巻き戻し後の画素は一致するはず。
        var image = PngImage.Read(Path.Combine(CasesDir, name));
        Assert.Equal(16, image.Width);
        Assert.Equal(16, image.Height);
        Assert.Equal(16 * 16 * 4, image.Pixels.Length);

        var reference = PngImage.Read(Path.Combine(CasesDir, "filter0_none.png"));
        Assert.Equal(reference.Width, image.Width);
        Assert.Equal(reference.Height, image.Height);
        Assert.Equal(reference.Pixels, image.Pixels);
    }

    [Fact]
    public void IdatSplit_Decodes()
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(CasesDir, "idat_split.png"));
        int idatCount = CountOccurrences(raw, "IDAT"u8.ToArray());
        Assert.True(idatCount >= 3, "fixture must actually exercise multi-IDAT concatenation");

        var image = PngImage.Read(Path.Combine(CasesDir, "idat_split.png"));
        Assert.Equal(24, image.Width);
        Assert.Equal(24, image.Height);
        Assert.Equal(24 * 24 * 4, image.Pixels.Length);
    }

    [Fact]
    public void AncillaryChunks_AreSkipped()
    {
        byte[] raw = File.ReadAllBytes(Path.Combine(CasesDir, "ancillary.png"));
        Assert.True(CountOccurrences(raw, "tEXt"u8.ToArray()) >= 2);

        var image = PngImage.Read(Path.Combine(CasesDir, "ancillary.png"));
        Assert.Equal(12, image.Width);
        Assert.Equal(12, image.Height);
        Assert.Equal(12 * 12 * 4, image.Pixels.Length);
    }

    private static int CountOccurrences(byte[] haystack, byte[] needle)
    {
        int count = 0;
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                count++;
                i += needle.Length - 1;
            }
        }
        return count;
    }

    [Fact]
    public void GsAlpha_DistinguishesPaintedWhiteFromUntouched()
    {
        // D-036 の動機そのもの: alpha=255(白を塗った)と alpha=0(何も
        // 描いていない)が区別できること。
        string path = Path.Combine(CasesDir, "gs_alpha.png");
        if (!File.Exists(path))
        {
            // Ghostscript が無い環境でフィクスチャが生成されなかった場合は
            // スキップする(tools/make-png-fixtures.py の方針に合わせる)。
            return;
        }

        var image = PngImage.Read(path);
        Assert.Equal(200, image.Width);
        Assert.Equal(200, image.Height);

        var painted = PixelAt(image.Pixels, image.Width, 100, 100);
        var untouched = PixelAt(image.Pixels, image.Width, 5, 5);

        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)255), painted);
        Assert.Equal(((byte)255, (byte)255, (byte)255, (byte)0), untouched);
    }

    [Fact]
    public void RejectsBadSignature()
    {
        string path = WriteTempFile("not a png at all"u8.ToArray());
        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void RejectsNonRgbaColourType()
    {
        // カラータイプ 2 = RGB(アルファ無し)、ビット深度 8、1x1、フィルタ None。
        byte[] ihdr = BuildIhdr(1, 1, 8, 2, 0, 0, 0);
        byte[] raw = { 0, 0, 0, 0 }; // フィルタバイト + RGB 1 画素
        byte[] idat = ZlibCompress(raw);
        byte[] data = AssemblePng(ihdr, new[] { idat });
        string path = WriteTempFile(data);

        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("colour type", ex.Message);
    }

    [Fact]
    public void RejectsBadBitDepth()
    {
        byte[] ihdr = BuildIhdr(1, 1, 16, 6, 0, 0, 0);
        byte[] raw = new byte[1 + 8]; // フィルタバイト + RGBA16 1 画素
        byte[] idat = ZlibCompress(raw);
        byte[] data = AssemblePng(ihdr, new[] { idat });
        string path = WriteTempFile(data);

        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("bit depth", ex.Message);
    }

    [Fact]
    public void RejectsInterlaced()
    {
        byte[] ihdr = BuildIhdr(1, 1, 8, 6, 0, 0, 1);
        byte[] raw = new byte[5];
        byte[] idat = ZlibCompress(raw);
        byte[] data = AssemblePng(ihdr, new[] { idat });
        string path = WriteTempFile(data);

        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("interlace", ex.Message);
    }

    [Fact]
    public void RejectsBadCrc()
    {
        byte[] ihdr = BuildIhdr(1, 1, 8, 6, 0, 0, 0);
        byte[] raw = new byte[5];
        byte[] idat = ZlibCompress(raw);
        byte[] data = AssemblePng(ihdr, new[] { idat });
        // IHDR チャンクのデータ部を(CRC は書き換えずに)1 バイト壊す。
        // シグネチャ(8) + 長さ(4) + "IHDR"(4) = オフセット 16 からが IHDR データ。
        data[20] ^= 0xFF;
        string path = WriteTempFile(data);

        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public void RejectsMissingIdat()
    {
        byte[] ihdr = BuildIhdr(1, 1, 8, 6, 0, 0, 0);
        byte[] data = AssemblePng(ihdr, Array.Empty<byte[]>());
        string path = WriteTempFile(data);

        var ex = Assert.Throws<PngFormatException>(() => PngImage.Read(path));
        Assert.Contains("IDAT", ex.Message);
    }

    private static byte[] BuildIhdr(int width, int height, byte bitDepth, byte colourType,
        byte compressionMethod, byte filterMethod, byte interlaceMethod)
    {
        byte[] ihdr = new byte[13];
        WriteUInt32BigEndian(ihdr, 0, (uint)width);
        WriteUInt32BigEndian(ihdr, 4, (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colourType;
        ihdr[10] = compressionMethod;
        ihdr[11] = filterMethod;
        ihdr[12] = interlaceMethod;
        return ihdr;
    }

    private static void WriteUInt32BigEndian(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] Chunk(string chunkType, byte[] data)
    {
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(chunkType);
        using var buf = new MemoryStream();
        byte[] lenBytes = new byte[4];
        WriteUInt32BigEndian(lenBytes, 0, (uint)data.Length);
        buf.Write(lenBytes, 0, 4);
        buf.Write(typeBytes, 0, 4);
        buf.Write(data, 0, data.Length);
        uint crc = ComputeCrc32(typeBytes, data);
        byte[] crcBytes = new byte[4];
        WriteUInt32BigEndian(crcBytes, 0, crc);
        buf.Write(crcBytes, 0, 4);
        return buf.ToArray();
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(byte[] chunkType, byte[] chunkData)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in chunkType)
        {
            c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        foreach (byte b in chunkData)
        {
            c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFF;
    }

    private static byte[] AssemblePng(byte[] ihdr, byte[][] idatParts)
    {
        using var buf = new MemoryStream();
        byte[] signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        buf.Write(signature, 0, signature.Length);
        byte[] ihdrChunk = Chunk("IHDR", ihdr);
        buf.Write(ihdrChunk, 0, ihdrChunk.Length);
        foreach (byte[] part in idatParts)
        {
            byte[] idatChunk = Chunk("IDAT", part);
            buf.Write(idatChunk, 0, idatChunk.Length);
        }
        byte[] iendChunk = Chunk("IEND", Array.Empty<byte>());
        buf.Write(iendChunk, 0, iendChunk.Length);
        return buf.ToArray();
    }

    private static string WriteTempFile(byte[] data)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, data);
        return path;
    }
}
