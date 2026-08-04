// Foilwright.Core — L2: PPM (P6) 読み込み。
// 参照実装: ref/foilwright_ref/raster.py の read_ppm。
// 内部座標は常にドット(DOMAIN §4.1)。本ファイルは mm を一切扱わない。

namespace Foilwright.Core;

/// <summary>
/// サポート対象外の PPM (P6 以外・maxval != 255・壊れたヘッダ等) を読んだときに送出する。
/// </summary>
public sealed class PpmFormatException : Exception
{
    public PpmFormatException(string message) : base(message) { }
}

/// <summary>
/// 読み込み済みの PPM (P6) 画像。Pixels は行優先、1 画素あたり R/G/B の 3 バイト。
/// </summary>
public sealed class PpmImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public PpmImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// バイナリ (P6) PPM を読む。maxval 255 のみ対応(ppmtomd が内部で
    /// 255 に正規化するため、golden fixture もすべてこれ)。
    /// </summary>
    public static PpmImage Read(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int pos = 0;

        bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\v' || b == (byte)'\f';

        int SkipWsAndComments(int p)
        {
            while (true)
            {
                while (p < data.Length && IsWhitespace(data[p]))
                {
                    p++;
                }
                if (p < data.Length && data[p] == (byte)'#')
                {
                    int nl = Array.IndexOf(data, (byte)'\n', p);
                    p = nl < 0 ? data.Length : nl + 1;
                    continue;
                }
                return p;
            }
        }

        string ReadToken(ref int p)
        {
            p = SkipWsAndComments(p);
            int start = p;
            while (p < data.Length && !IsWhitespace(data[p]))
            {
                p++;
            }
            return System.Text.Encoding.ASCII.GetString(data, start, p - start);
        }

        string magic = ReadToken(ref pos);
        if (magic != "P6")
        {
            throw new PpmFormatException($"unsupported PPM magic '{magic}'; only P6 is supported");
        }

        string widthTok = ReadToken(ref pos);
        string heightTok = ReadToken(ref pos);
        string maxvalTok = ReadToken(ref pos);
        int width = int.Parse(widthTok);
        int height = int.Parse(heightTok);
        int maxval = int.Parse(maxvalTok);
        if (maxval != 255)
        {
            throw new PpmFormatException($"unsupported maxval {maxval}; only 255 is supported");
        }

        // ヘッダとバイナリラスタデータの間は空白 1 バイトのみ。ここは
        // SkipWsAndComments を使わず、生の画素バイトをコメント等として
        // 誤読しないよう厳密に 1 バイトだけ消費する。
        if (pos >= data.Length || !IsWhitespace(data[pos]))
        {
            throw new PpmFormatException("malformed PPM header: missing whitespace before raster data");
        }
        pos += 1;

        int pixelBytes = width * height * 3;
        if (data.Length - pos != pixelBytes)
        {
            throw new PpmFormatException(
                $"truncated PPM data: expected {pixelBytes} bytes, got {Math.Max(0, data.Length - pos)}");
        }

        byte[] pixels = new byte[pixelBytes];
        Array.Copy(data, pos, pixels, 0, pixelBytes);
        return new PpmImage(width, height, pixels);
    }

    /// <summary>
    /// (x, y) を左上とする width x height の矩形を切り出す。純粋な幾何操作
    /// であり、用紙や余白の意味は一切知らない(DOMAIN §4.1 — mm・用紙寸法は
    /// L3 の入口より奥へ持ち込まない。矩形の算出は呼び出し側 = L3 の責務)。
    ///
    /// 元画像が要求矩形より小さい場合は、利用可能な範囲へ切り詰める
    /// (例外にしない — 小さい用紙で刷る場合がある)。x/y が画像の外側なら
    /// 幅・高さ 0 の画像を返す。
    ///
    /// 行単位でコピーする(A4/600dpi の PPM は約 99.5MB あり、全体を複製
    /// すると倍のメモリを食うため。DOMAIN §3.6)。
    /// </summary>
    public PpmImage Crop(int x, int y, int width, int height)
    {
        if (x < 0 || y < 0)
        {
            throw new ArgumentException($"crop origin must be non-negative, got ({x}, {y})");
        }
        if (width < 0 || height < 0)
        {
            throw new ArgumentException($"crop size must be non-negative, got ({width}, {height})");
        }

        int availableWidth = Math.Max(0, Width - x);
        int availableHeight = Math.Max(0, Height - y);
        int outWidth = Math.Min(width, availableWidth);
        int outHeight = Math.Min(height, availableHeight);

        byte[] outPixels = new byte[outWidth * outHeight * 3];
        int srcRowBytes = Width * 3;
        int dstRowBytes = outWidth * 3;
        for (int row = 0; row < outHeight; row++)
        {
            int srcOffset = (y + row) * srcRowBytes + x * 3;
            int dstOffset = row * dstRowBytes;
            Array.Copy(Pixels, srcOffset, outPixels, dstOffset, dstRowBytes);
        }

        return new PpmImage(outWidth, outHeight, outPixels);
    }
}
