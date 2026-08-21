// Foilwright.Core — L2: PPM (P6) 読み込み。
// 参照実装: ref/foilwright_ref/raster.py の read_ppm。
// 内部座標は常にドット(DOMAIN §4.1)。本ファイルは mm を一切扱わない。

namespace Foilwright.Core;

/// <summary>
/// サポート対象外の PPM (P6 以外・maxval != 255・壊れたヘッダ等) を読んだときに送出する。
/// </summary>
public sealed class PpmFormatException : Exception
{
    /// <summary>複数ページ (PPM が連結されている) が原因のときだけ true。
    /// 呼び出し側 (トレイアプリ) が利用者向けの補足を出し分けるための目印。
    /// 文言そのものを見て判定しない — 文言を変えたら壊れるため。</summary>
    public bool IsMultiPage { get; }

    public PpmFormatException(string message, bool isMultiPage = false) : base(message)
    {
        IsMultiPage = isMultiPage;
    }
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

        // p が「P6 + 空白」で始まるか。連結された次の PPM の始まりを見分ける
        // ためだけに使う(ヘッダの読み進めと組で使うので、画素データ中の
        // 偶然の "P6" を拾うことはない)。
        bool StartsWithPpmMagic(int p) =>
            p + 2 < data.Length && data[p] == (byte)'P' && data[p + 1] == (byte)'6' && IsWhitespace(data[p + 2]);

        // p 以降に連結されている PPM の数を数える。戻り値の exact は
        // 「最後まで矛盾なくヘッダを読み切れたか」。読み切れなかった場合は
        // 数えられた分だけを下限として返し、呼び出し側が「以上」と表示する
        // (嘘の枚数を出さない)。
        (int Count, bool Exact) CountConcatenatedImages(int p)
        {
            int count = 0;
            while (p < data.Length)
            {
                if (!StartsWithPpmMagic(p))
                {
                    return (count, false);
                }

                int q = p;
                ReadToken(ref q); // "P6"(StartsWithPpmMagic で確認済み)
                string w = ReadToken(ref q);
                string h = ReadToken(ref q);
                string mv = ReadToken(ref q);
                if (!int.TryParse(w, out int imageWidth) || !int.TryParse(h, out int imageHeight)
                    || !int.TryParse(mv, out _) || imageWidth < 0 || imageHeight < 0)
                {
                    return (count, false);
                }
                if (q >= data.Length || !IsWhitespace(data[q]))
                {
                    return (count, false);
                }
                q += 1;

                count++;
                long bytes = (long)imageWidth * imageHeight * 3;
                if (q + bytes > data.Length)
                {
                    // 末尾のページが途中で切れている。枚数は下限として扱う。
                    return (count, false);
                }
                p = (int)(q + bytes);
            }
            return (count, true);
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
        int remaining = data.Length - pos;

        if (remaining < pixelBytes)
        {
            throw new PpmFormatException(
                $"truncated PPM data: expected {pixelBytes} bytes, got {Math.Max(0, remaining)}");
        }

        if (remaining > pixelBytes)
        {
            // Ghostscript は -sOutputFile に %d が無いと複数ページを 1 つの
            // ppmraw ファイルへ連結して書く。余りの先頭がそのまま次の PPM の
            // ヘッダなら「複数ページ」であり、単なるゴミの付着とは区別する
            // (前者は利用者の操作が原因、後者は壊れたファイルが原因)。
            int trailingStart = pos + pixelBytes;
            if (StartsWithPpmMagic(trailingStart))
            {
                // ページ数は正確に数える。画素データを走査して "P6" を探すと
                // 偶然一致するバイト列を数えてしまうため、各画像のヘッダを
                // 読んで「次の画像の開始位置」を計算しながら進む。この方法は
                // ページ数に比例するだけの手間(数十バイトの読み取り x ページ数)
                // で済み、100MB 級の画素データには一切触れない。
                var (followingCount, exact) = CountConcatenatedImages(trailingStart);
                int pageCount = 1 + followingCount; // 1 は今読んだ先頭ページ
                string found = exact ? $"found {pageCount} pages" : $"found at least {pageCount} pages";
                throw new PpmFormatException(
                    "multi-page PPM: the document has more than one page; " +
                    $"Foilwright prints one page per job ({found})",
                    isMultiPage: true);
            }

            throw new PpmFormatException(
                $"unexpected trailing data after PPM image: expected {pixelBytes} bytes, got {remaining}");
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
