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
}
