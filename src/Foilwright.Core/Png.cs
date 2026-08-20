// Foilwright.Core — 最小の PNG(RGBA)デコーダ。Ghostscript の pngalpha
// デバイスが出す形だけに対応する(D-036)。
// 参照実装: ref/foilwright_ref/png.py の read_png_rgba。
//
// 汎用 PNG デコーダではない。カラータイプ 6(RGBA)/ ビット深度 8 /
// インタレース無し / 圧縮方式 0 / フィルタ方式 0 だけを受け付ける。
// それ以外は明確な例外で止める(既製品(System.Drawing 等)は使わない。
// 理由は D-036)。展開は標準機能(System.IO.Compression.ZLibStream)のみ
// を使い、新しいパッケージは追加しない。

using System.Buffers.Binary;
using System.IO.Compression;

namespace Foilwright.Core;

/// <summary>
/// 対応していない PNG、または壊れた PNG(CRC 不一致・チャンク欠落等)を
/// 読んだときに送出する。
/// </summary>
public sealed class PngFormatException : Exception
{
    public PngFormatException(string message) : base(message) { }
}

/// <summary>
/// 読み込み済みの PNG(RGBA)画像。Pixels は行優先、1 画素あたり
/// R/G/B/A の 4 バイト。
/// </summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public PngImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    private const byte SupportedColourType = 6; // RGBA
    private const byte SupportedBitDepth = 8;
    private const byte SupportedCompressionMethod = 0;
    private const byte SupportedFilterMethod = 0;
    private const byte SupportedInterlaceMethod = 0;
    private const int BytesPerPixel = 4; // RGBA, 8bit

    private readonly record struct Chunk(string Type, byte[] Data);

    /// <summary>
    /// Ghostscript の pngalpha デバイスが出す PNG を読む。カラータイプ 6
    /// (RGBA)/ ビット深度 8 / インタレース無し / 圧縮方式 0 / フィルタ方式 0
    /// 以外は PngFormatException で止める(D-036: 汎用デコーダではない)。
    /// </summary>
    public static PngImage Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        List<Chunk> chunks = ReadChunks(data);

        if (chunks.Count == 0 || chunks[0].Type != "IHDR")
        {
            throw new PngFormatException("malformed PNG: first chunk is not IHDR");
        }

        byte[] ihdr = chunks[0].Data;
        if (ihdr.Length != 13)
        {
            throw new PngFormatException($"malformed PNG: IHDR length {ihdr.Length} != 13");
        }

        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(0, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(ihdr.AsSpan(4, 4));
        byte bitDepth = ihdr[8];
        byte colourType = ihdr[9];
        byte compressionMethod = ihdr[10];
        byte filterMethod = ihdr[11];
        byte interlaceMethod = ihdr[12];

        if (colourType != SupportedColourType)
        {
            throw new PngFormatException(
                $"unsupported PNG colour type {colourType}; only colour type 6 (RGBA) is supported (D-036)");
        }
        if (bitDepth != SupportedBitDepth)
        {
            throw new PngFormatException($"unsupported PNG bit depth {bitDepth}; only 8-bit is supported (D-036)");
        }
        if (compressionMethod != SupportedCompressionMethod)
        {
            throw new PngFormatException(
                $"unsupported PNG compression method {compressionMethod}; only 0 is supported");
        }
        if (filterMethod != SupportedFilterMethod)
        {
            throw new PngFormatException($"unsupported PNG filter method {filterMethod}; only 0 is supported");
        }
        if (interlaceMethod != SupportedInterlaceMethod)
        {
            throw new PngFormatException(
                $"unsupported PNG interlace method {interlaceMethod}; interlacing is not supported (D-036)");
        }
        if (width <= 0 || height <= 0)
        {
            throw new PngFormatException($"malformed PNG: non-positive dimensions {width}x{height}");
        }

        // IDAT を全部つないでから展開する(Ghostscript は 47 個に分割して
        // 出すことが実測されている。D-036)。補助チャンク(iCCP/bKGD/pHYs/
        // tEXt 等)は読み飛ばす。
        using var compressed = new MemoryStream();
        bool anyIdat = false;
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Type == "IDAT")
            {
                anyIdat = true;
                compressed.Write(chunk.Data, 0, chunk.Data.Length);
            }
        }
        if (!anyIdat)
        {
            throw new PngFormatException("malformed PNG: no IDAT chunk");
        }
        compressed.Position = 0;

        byte[] raw;
        try
        {
            // PNG の zlib ストリームは先頭 2 バイトの zlib ヘッダを含むため
            // ZLibStream(deflate 系だが zlib ヘッダ/フッタ込み)で読む。
            using var zlibStream = new ZLibStream(compressed, CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            zlibStream.CopyTo(inflated);
            raw = inflated.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new PngFormatException($"corrupt PNG: zlib decompression failed: {ex.Message}");
        }

        byte[] pixels = Unfilter(raw, width, height);
        return new PngImage(width, height, pixels);
    }

    private static List<Chunk> ReadChunks(byte[] data)
    {
        if (data.Length < Signature.Length || !data.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new PngFormatException("not a PNG file (bad signature)");
        }

        var chunks = new List<Chunk>();
        int pos = Signature.Length;
        bool sawIend = false;
        while (pos < data.Length)
        {
            if (pos + 8 > data.Length)
            {
                throw new PngFormatException("truncated PNG: incomplete chunk header");
            }
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            string chunkType = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
            int chunkStart = pos + 8;
            long chunkEnd = (long)chunkStart + length;
            if (chunkEnd + 4 > data.Length)
            {
                throw new PngFormatException($"truncated PNG: chunk '{chunkType}' runs past end of file");
            }
            byte[] chunkData = new byte[length];
            Array.Copy(data, chunkStart, chunkData, 0, (int)length);
            uint storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)chunkEnd, 4));
            uint computedCrc = ComputeCrc32(chunkType, chunkData);
            if (storedCrc != computedCrc)
            {
                throw new PngFormatException(
                    $"corrupt PNG: CRC mismatch in chunk '{chunkType}' " +
                    $"(stored {storedCrc:x8}, computed {computedCrc:x8})");
            }
            chunks.Add(new Chunk(chunkType, chunkData));
            pos = (int)chunkEnd + 4;
            if (chunkType == "IEND")
            {
                sawIend = true;
                break;
            }
        }
        if (!sawIend)
        {
            throw new PngFormatException("truncated PNG: missing IEND chunk");
        }

        return chunks;
    }

    // PNG 仕様付録 D の CRC-32 (zlib と同一多項式)。System.IO.Hashing は
    // 新規パッケージになるため使わず、自前実装する(D-036: PyYAML 以外の
    // 依存を増やさない)。
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

    private static uint UpdateCrc32(uint crc, byte[] buf)
    {
        uint c = crc;
        foreach (byte b in buf)
        {
            c = Crc32Table[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c;
    }

    private static uint ComputeCrc32(string chunkType, byte[] chunkData)
    {
        uint crc = 0xFFFFFFFF;
        crc = UpdateCrc32(crc, System.Text.Encoding.ASCII.GetBytes(chunkType));
        crc = UpdateCrc32(crc, chunkData);
        return crc ^ 0xFFFFFFFF;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }
        if (pb <= pc)
        {
            return b;
        }
        return c;
    }

    /// <summary>行ごとのフィルタ(PNG 仕様 §6)を巻き戻す。raw は展開済みの
    /// IDAT ストリーム(height 行、各行はフィルタ種別 1 バイト + width*4
    /// バイトの RGBA サンプル)。1 画素ずつではなく行単位のバイト列で処理する
    /// (A4 600dpi = 展開後 128MB が現実的な時間で読める必要がある)。</summary>
    private static byte[] Unfilter(byte[] raw, int width, int height)
    {
        int rowBytes = width * BytesPerPixel;
        int stride = rowBytes + 1;
        long expectedLength = (long)stride * height;
        if (raw.Length != expectedLength)
        {
            throw new PngFormatException(
                $"corrupt PNG: expected {expectedLength} bytes of unfiltered scanline data, got {raw.Length}");
        }

        byte[] outPixels = new byte[rowBytes * height];
        byte[] prevRow = new byte[rowBytes]; // 先頭行の「上の行」は全 0
        byte[] cur = new byte[rowBytes];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            byte filterType = raw[rowStart];
            Array.Copy(raw, rowStart + 1, cur, 0, rowBytes);

            switch (filterType)
            {
                case 0: // None
                    break;
                case 1: // Sub
                    for (int i = BytesPerPixel; i < rowBytes; i++)
                    {
                        cur[i] = (byte)(cur[i] + cur[i - BytesPerPixel]);
                    }
                    break;
                case 2: // Up
                    for (int i = 0; i < rowBytes; i++)
                    {
                        cur[i] = (byte)(cur[i] + prevRow[i]);
                    }
                    break;
                case 3: // Average
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= BytesPerPixel ? cur[i - BytesPerPixel] : 0;
                        int up = prevRow[i];
                        cur[i] = (byte)(cur[i] + ((left + up) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < rowBytes; i++)
                    {
                        int left = i >= BytesPerPixel ? cur[i - BytesPerPixel] : 0;
                        int up = prevRow[i];
                        int upperLeft = i >= BytesPerPixel ? prevRow[i - BytesPerPixel] : 0;
                        cur[i] = (byte)(cur[i] + PaethPredictor(left, up, upperLeft));
                    }
                    break;
                default:
                    throw new PngFormatException($"corrupt PNG: unknown filter type {filterType} on row {y}");
            }

            Array.Copy(cur, 0, outPixels, y * rowBytes, rowBytes);
            (prevRow, cur) = (cur, prevRow);
        }

        return outPixels;
    }
}
