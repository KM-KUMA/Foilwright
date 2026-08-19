// Foilwright.Core — L2: PPM (P6) 入力 -> インクごとの 1bit プレーン。
//
// 内部座標は常にドット(DOMAIN §4.1)。本ファイルは mm を一切扱わない。
//
// インク分解の式は ppmtomd 1.6 の colcorPlain(色補正)と ditherNone
// (2 値化しきい値)を再現する(vendor/ppmtomd-1.6/ppmtomd.c:2897,
// 2933-2937, 3052-3058):
//
//     c = maxval - r; m = maxval - g; y = maxval - b
//     k = min(c, m, y); c -= k; m -= k; y -= k
//     bit = 1 if value >= (maxval + 1) / 2 else 0
//
// Halftone / CoarseHalftone(ppmtomd の順序ディザ、DOMAIN §4.2.1)は上の
// 固定しきい値 128 を、行ごとに回転するスクリーン位置から読んだディザ
// 行列のしきい値に置き換える(ppmtomd.c:2851-3093、ht_init/ht_inc マクロ
// は ppmtomd.c:549-613 — その上の #if 0 ブロック(517-548)はデッドコード
// なので再現しない):
//
//     bit = 1 if value > dither_matrix[hrow, hcol] else 0
//
// maxval == 255(8bit/サンプル)の入力のみサポートする — golden fixture
// はすべてこれであり、ppmtomd も内部ですべてを 255 に正規化する
// (ppmtomd.c:2063 "let's change everything to 255")。
//
// 参照実装: ref/foilwright_ref/raster.py。マジックカラーのマッチングは
// D-015 のとおり整数演算のみで行う(浮動小数点は使わない)。

namespace Foilwright.Core;

public static class Raster
{
    private static readonly HashSet<string> ValidHalftones = new() { "none", "halftone", "coarse_halftone" };

    // colour/photo_colcor.bin を、このファイル(src/Foilwright.Core/Raster.cs)
    // からリポジトリルート相対で解決した既定パス。ToPlanes / ToPlanesAuto の
    // photoLutPath 引数で呼び出しごとに上書きできる(DOMAIN §4.5: テーブルの
    // 場所をこの 1 箇所より奥へハードコードしない)。
    private static readonly string DefaultPhotoLutPath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "colour", "photo_colcor.bin");

    // 展開済み(64^3x4、約 1MB)の photo 色補正テーブルをパスごとにキャッシュする
    // (ref/foilwright_ref/raster.py の functools.cache 相当。展開自体は使い回す
    // 価値があるほど重い一方、Colour.cs 自体はキャッシュを持たない)。
    private static readonly Dictionary<string, byte[]> ExpandedPhotoLutCache = new();

    private static byte[] ExpandedPhotoLut(string? photoLutPath)
    {
        string path = photoLutPath ?? DefaultPhotoLutPath;
        lock (ExpandedPhotoLutCache)
        {
            if (!ExpandedPhotoLutCache.TryGetValue(path, out var cached))
            {
                cached = Colour.ExpandLut(Colour.LoadPhotoLut(path));
                ExpandedPhotoLutCache[path] = cached;
            }
            return cached;
        }
    }

    /// <summary>colour_correction == "none"/"plain"/"photo" いずれかの式で、
    /// 1 画素の R/G/B から C/M/Y/K の 4 値を作る。ToPlanes と ToPlanesAuto の
    /// 両方から呼ばれる共通のインク分解式(ref/foilwright_ref/raster.py の
    /// 各関数内に重複している式に対応)。</summary>
    private static (int C, int M, int Y, int K) SeparateColour(
        int r, int g, int b, string colourCorrection, int[]? gammaTable, byte[]? colConv)
    {
        int c, m, yv, k;
        if (colourCorrection == "none")
        {
            c = 255 - r;
            m = 255 - g;
            yv = 255 - b;
            k = 0;
        }
        else if (colourCorrection == "photo")
        {
            c = gammaTable![255 - r];
            m = gammaTable[255 - g];
            yv = gammaTable[255 - b];
            int lutIndex = ((c & 0xFC) << 12) | ((m & 0xFC) << 6) | (yv & 0xFC);
            c = colConv![lutIndex];
            m = colConv[lutIndex + 1];
            yv = colConv[lutIndex + 2];
            k = colConv[lutIndex + 3];
        }
        else
        {
            c = 255 - r;
            m = 255 - g;
            yv = 255 - b;
            k = Math.Min(c, Math.Min(m, yv));
            c -= k;
            m -= k;
            yv -= k;
        }
        return (c, m, yv, k);
    }

    // ppmtomd.c:986 "four" -- build_dith が n x n セルを 2x2 ブロックへ
    // 展開するときの補間重み。
    private static readonly int[] BuildDithFour = { 0, 2, 3, 1 };

    // ppmtomd.c:666-673 -- Halftone(細かい方)の C/M "line" スクリーン用の
    // 元行列。
    private static readonly int[] DithMat6Line =
    {
        56 * 4, 48 * 4, 52 * 4, 58 * 4, 50 * 4, 54 * 4,
        32 * 4, 24 * 4, 28 * 4, 34 * 4, 26 * 4, 30 * 4,
        8 * 4, 0 * 4, 4 * 4, 10 * 4, 2 * 4, 6 * 4,
        20 * 4, 12 * 4, 16 * 4, 22 * 4, 14 * 4, 18 * 4,
        44 * 4, 36 * 4, 40 * 4, 46 * 4, 38 * 4, 42 * 4,
        63 * 4, 59 * 4, 61 * 4, 63 * 4, 60 * 4, 62 * 4,
    };

    // ppmtomd.c:693-701 -- Halftone の Y/K "dot" スクリーン用の元行列。
    // これは #if 1 側(有効)の分岐で、#else 側(702-721、254 という迷い込みの
    // リテラルを含む)はコンパイルされないデッドコードなので再現しない。
    private static readonly int[] DithMat6Dot =
    {
        100, 40, 140, 176, 222, 144,
        20, 0, 60, 234, 246, 208,
        160, 80, 120, 128, 192, 160,
        184, 228, 152, 110, 50, 150,
        240, 252, 216, 30, 10, 70,
        136, 200, 168, 170, 90, 130,
    };

    // ppmtomd.c:737-746 -- CoarseHalftone は展開せず、この行列を CMYK 4
    // 成分すべてで共有する(ppmtomd.c:725 の #if 0 に対する #else 側)。
    private static readonly int[] DithMat10 =
    {
        27 * 4, 19 * 4, 15 * 4, 23 * 4, 31 * 4, 41 * 4, 52 * 4, 55 * 4, 49 * 4, 37 * 4,
        25 * 4, 10 * 4, 4 * 4, 12 * 4, 21 * 4, 43 * 4, 58 * 4, 62 * 4, 60 * 4, 48 * 4,
        17 * 4, 2 * 4, 0 * 4, 6 * 4, 18 * 4, 53 * 4, 64 * 4, 64 * 4, 64 * 4, 54 * 4,
        22 * 4, 13 * 4, 8 * 4, 14 * 4, 26 * 4, 47 * 4, 61 * 4, 63 * 4, 59 * 4, 45 * 4,
        33 * 4, 24 * 4, 16 * 4, 20 * 4, 29 * 4, 35 * 4, 50 * 4, 56 * 4, 51 * 4, 39 * 4,
        42 * 4, 52 * 4, 55 * 4, 49 * 4, 38 * 4, 28 * 4, 19 * 4, 15 * 4, 23 * 4, 32 * 4,
        44 * 4, 58 * 4, 62 * 4, 60 * 4, 48 * 4, 25 * 4, 11 * 4, 5 * 4, 12 * 4, 21 * 4,
        53 * 4, 64 * 4, 64 * 4, 64 * 4, 54 * 4, 17 * 4, 3 * 4, 1 * 4, 7 * 4, 18 * 4,
        47 * 4, 61 * 4, 63 * 4, 59 * 4, 46 * 4, 22 * 4, 13 * 4, 9 * 4, 14 * 4, 26 * 4,
        36 * 4, 50 * 4, 57 * 4, 51 * 4, 40 * 4, 34 * 4, 24 * 4, 16 * 4, 20 * 4, 30 * 4,
    };

    /// <summary>C の rint(numerator / denom)(最近接偶数への丸め)の整数版。
    /// ppmtomd の build_dith(ppmtomd.c:987)は浮動小数点の rint を使うが、
    /// これはコンパイル時定数からディザ行列を作る場面でしか使われず、
    /// 画素ごとの計算には一切現れない。ここを浮動小数点ではなく厳密な整数
    /// 演算で再現することで、同じ行列を得つつ本ファイル全体を浮動小数点
    /// フリーに保つ(DOMAIN §4.9 / D-015)。</summary>
    private static int RoundHalfEvenDiv(int numerator, int denom)
    {
        // denom > 0 前提。C# の '/' は 0 方向丸めなので、Python の divmod
        // (floor 除算)相当を FloorDiv で整数演算のみにより再現する。
        int quot = FloorDiv(numerator, denom);
        int rem = numerator - quot * denom;
        int twiceRem = 2 * rem;
        if (twiceRem < denom)
        {
            return quot;
        }
        if (twiceRem > denom)
        {
            return quot + 1;
        }
        return quot % 2 == 0 ? quot : quot + 1;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        int r = a % b;
        if (r != 0 && (r < 0) != (b < 0))
        {
            q -= 1;
        }
        return q;
    }

    private static int[] BuildDith(int n, int[] indith, int m, int[] condith)
    {
        var sortedVals = (int[])indith.Clone();
        Array.Sort(sortedVals);
        var result = new int[m * n * m * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                int val = indith[i * n + j];
                int k = Array.IndexOf(sortedVals, val);
                while (k < n * n && sortedVals[k] == val)
                {
                    k += 1;
                }
                int nval = k == n * n ? 256 : sortedVals[k];
                for (int p = 0; p < m; p++)
                {
                    for (int q = 0; q < m; q++)
                    {
                        int weight = condith[p * m + q];
                        int numerator = (m * m - weight) * val + weight * nval;
                        int res = RoundHalfEvenDiv(numerator, m * m);
                        res = Math.Min(res, 254);
                        result[(n * p + i) * m * n + (n * q + j)] = res;
                    }
                }
            }
        }
        return result;
    }

    private static readonly int[] DithMatLine12 = BuildDith(6, DithMat6Line, 2, BuildDithFour);
    private static readonly int[] DithMatDot12 = BuildDith(6, DithMat6Dot, 2, BuildDithFour);

    // ppmtomd.c:2726-2733 -- 1200dpi("photo-realistic" モード。原文コメント曰く
    // "still very much in beta")では、dithmat10 を build_dith で展開した
    // (ppmtomd.c:2729: build_dith(10, dithmat10, 2, four))この 20x20 行列を
    // CMYK 4 成分すべてで共有する。これは 600dpi の Halftone(12x12、
    // チャンネルごとの line/dot 行列)と CoarseHalftone(10x10 dithmat10、
    // 展開なしで使用)の選択を無条件に上書きする -- ditherHT と ditherHTcoarse
    // の区別は 1200dpi ではスクリーン角(kht.comps[*].x/y/z、解像度に関係なく
    // ppmtomd.c:1986-1991 のまま)にのみ残り、ディザ行列には残らない
    // (ResolveHalftoneMode 参照)。
    private static readonly int[] DithMatPhotorealistic20 = BuildDith(10, DithMat10, 2, BuildDithFour);

    private readonly record struct ScreenAngle(int X, int Y, int Z, bool YNeg);

    // ppmtomd.c:1978-1992 -- CMYK 各成分のデフォルトハーフトーンスクリーン角
    // (x, y, z, yneg)。y は htset に渡した値の絶対値で、負の値は yneg だけを
    // 立てる(ppmtomd.c:491-493 htset マクロ)。
    private static readonly Dictionary<string, ScreenAngle> ScreenHalftone = new()
    {
        ["C"] = new ScreenAngle(12, 5, 13, false),
        ["M"] = new ScreenAngle(12, 5, 13, true),
        ["Y"] = new ScreenAngle(3, 4, 5, false),
        ["K"] = new ScreenAngle(1, 0, 1, false),
    };

    private static readonly Dictionary<string, ScreenAngle> ScreenCoarseHalftone = new()
    {
        ["C"] = new ScreenAngle(12, 5, 13, false),
        ["M"] = new ScreenAngle(5, 12, 13, true), // ppmtomd.c:1991 coarse-mode override
        ["Y"] = new ScreenAngle(3, 4, 5, false),
        ["K"] = new ScreenAngle(1, 0, 1, false),
    };

    private sealed class HalftoneMode
    {
        public required Dictionary<string, (int CellSize, int[] Matrix)> Channels { get; init; }
        public required Dictionary<string, ScreenAngle> Screens { get; init; }
    }

    // ハーフトーンモードごとの、CMYK 各チャンネルの (セルサイズ, ディザ行列) と
    // 使用するスクリーン角テーブル(ppmtomd.c:2761-2779 の「通常 600dpi
    // モード」分岐)。1200dpi での上書き(DithMatPhotorealistic20 コメント参照)は
    // ResolveHalftoneMode が別途行う。
    private static readonly Dictionary<string, HalftoneMode> HalftoneModes = new()
    {
        ["halftone"] = new HalftoneMode
        {
            Channels = new Dictionary<string, (int, int[])>
            {
                ["C"] = (12, DithMatLine12),
                ["M"] = (12, DithMatLine12),
                ["Y"] = (12, DithMatDot12),
                ["K"] = (12, DithMatDot12),
            },
            Screens = ScreenHalftone,
        },
        ["coarse_halftone"] = new HalftoneMode
        {
            Channels = new Dictionary<string, (int, int[])>
            {
                ["C"] = (10, DithMat10),
                ["M"] = (10, DithMat10),
                ["Y"] = (10, DithMat10),
                ["K"] = (10, DithMat10),
            },
            Screens = ScreenCoarseHalftone,
        },
    };

    /// <summary>halftone と resolution から実際に使うハーフトーンモードを解決する
    /// (ppmtomd.c:2721-2779。ref/raster.py の _halftone_mode と同一)。
    /// halftone == "none" なら null。resolution == 1200 のときは、
    /// DithMatPhotorealistic20 のコメントにある通り CMYK 4 成分すべてが
    /// (20, DithMatPhotorealistic20) へ無条件に上書きされる -- halftone /
    /// coarse_halftone のどちらでも同じ行列になる。スクリーン角テーブルだけは
    /// HalftoneModes のまま(解像度で変わらない)。</summary>
    private static HalftoneMode? ResolveHalftoneMode(string halftone, int resolution)
    {
        if (!HalftoneModes.TryGetValue(halftone, out var baseMode))
        {
            return null;
        }
        if (resolution != 1200)
        {
            return baseMode;
        }
        return new HalftoneMode
        {
            Channels = new Dictionary<string, (int, int[])>
            {
                ["C"] = (20, DithMatPhotorealistic20),
                ["M"] = (20, DithMatPhotorealistic20),
                ["Y"] = (20, DithMatPhotorealistic20),
                ["K"] = (20, DithMatPhotorealistic20),
            },
            Screens = baseMode.Screens,
        };
    }

    /// <summary>C 言語式の整数除算(0 方向への切り捨て)。b は常に正
    /// (呼び出し元は常に 2*y または 2*z(y, z > 0)しか渡さない)。</summary>
    private static int CDiv(int a, int b)
    {
        return a >= 0 ? a / b : -((-a) / b);
    }

    /// <summary>ppmtomd の ht_init/ht_inc マクロ(ppmtomd.c:549-613、有効な
    /// 分岐のみ — その上の #if 0 の回転漸化式(517-548)はデッドコード)を
    /// 1 行分再現し、各列 0..width-1 に使うディザ行列インデックス
    /// (hrow, hcol) を返す。
    ///
    /// ppmtomd は行ごとに ht_init を 1 回、列ごとに ht_inc を 1 回呼び、
    /// ht_inc の直前に ht_elt(行列参照)を読む(ppmtomd.c:2851-2864,
    /// 3069-3092)。この関数はその「インクリメント前」の位置列をそのまま
    /// 返す。</summary>
    private static (int Row, int Col)[] HtRowPositions(int x, int y, int z, bool yneg, int row, int cellSize, int width)
    {
        var positions = new (int Row, int Col)[width];

        if (y == 0)
        {
            // ppmtomd.c:556 -- 回転なし: hrow はその行で固定、hcol は 0 から
            // 数え上げるだけなので col % cellSize になる。
            int hrowFixed = ((row % cellSize) + cellSize) % cellSize;
            for (int col = 0; col < width; col++)
            {
                positions[col] = (hrowFixed, ((col % cellSize) + cellSize) % cellSize);
            }
            return positions;
        }

        // ht_init (ppmtomd.c:557-576)
        int rowEff = yneg ? 10000 - row : row;
        int s1xf = 2 * rowEff * (x - z);
        int s1xi = CDiv(s1xf - y + 1, 2 * y);
        int s1yi = rowEff;
        int s2xi = s1xi;
        int s2yf = 2 * y * s1xi + 2 * z * s1yi;
        int s2yi;
        if (s2yf >= 0)
        {
            s2yi = CDiv(s2yf + z, 2 * z);
        }
        else
        {
            s2yi = CDiv(s2yf + 1 - z, 2 * z);
        }
        s2yf -= 2 * z * s2yi;
        int s3xf = 2 * y * s2xi + 2 * (x - z) * s2yi;
        int hcol;
        if (s3xf >= 0)
        {
            hcol = CDiv(s3xf + y, 2 * y);
        }
        else
        {
            hcol = CDiv(s3xf - y + 1, 2 * y);
        }
        s3xf -= 2 * y * hcol;
        int hrow = s2yi;

        for (int col = 0; col < width; col++)
        {
            int normRow = ((hrow % cellSize) + cellSize) % cellSize;
            int normCol = ((hcol % cellSize) + cellSize) % cellSize;
            positions[col] = (normRow, normCol);

            // ht_inc (ppmtomd.c:580-612). s1xi/s2xi はソース内でインクリメント
            // されるが以後読まれないので(デッドステート)、ここでは省く。
            s2yf += 2 * y;
            if (s2yf >= z)
            {
                s2yi += 1;
                s2yf -= 2 * z;
                hcol += 1;
                s3xf += 2 * (x - z);
                while (s3xf < -y)
                {
                    hcol -= 1;
                    s3xf += 2 * y;
                }
                hrow += 1;
            }
            else if (s2yf >= -z)
            {
                hcol += 1;
            }
            else
            {
                s2yi -= 1;
                s2yf += 2 * z;
                hcol -= 1;
                s3xf -= 2 * (x - z);
                while (s3xf >= y)
                {
                    hcol += 1;
                    s3xf -= 2 * y;
                }
                hrow -= 1;
            }
        }

        return positions;
    }

    /// <summary>画像をインクごとの 1bit プレーンへ変換する。
    ///
    /// palette: インク名 -> "C"/"M"/"Y"/"K" のいずれか(そのインクが
    ///     ppmtomd 式 CMYK 分解のどのチャンネルを受け持つか)。インク一覧を
    ///     ハードコードしないため、この辞書は常に呼び出し側が渡す
    ///     (DOMAIN §4.5)。
    /// halftone: "none"(既定。フラットな 128 しきい値、ppmtomd の
    ///     ditherNone)、"halftone"(ppmtomd の -dither Halftone)、
    ///     "coarse_halftone"(ppmtomd の -dither CoarseHalftone)のいずれか。
    ///     FloydSteinberg と Square は未実装(DOMAIN §4.2.1)。
    /// colourCorrection: "none"、"plain"(既定 -- ppmtomd の colcorPlain。
    ///     D-029 以前のこの関数の挙動とバイト一致)、"photo"(ppmtomd の
    ///     colcorPhoto)のいずれか。式は Colour.cs / SeparateColour を参照。
    /// resolution: colourCorrection == "photo" かつ halftone != "none" の
    ///     ときだけ参照する(Colour.DefaultGamma の解像度依存の既定値、
    ///     DOMAIN D-029)。それ以外では無視する。
    /// photoLutPath: colourCorrection == "photo" のときだけ参照する。
    ///     16x16x16x4 の photo 色補正テーブル(Colour.LoadPhotoLut の形式)の
    ///     パス。既定は同梱の colour/photo_colcor.bin。
    ///
    /// 戻り値はインク名 -> バイト列。各行は MSB ファーストでバイト境界まで
    /// パディングし(行長 = ceil(width/8) バイト)、画像順に連結する。</summary>
    public static Dictionary<string, byte[]> ToPlanes(
        PpmImage image, IReadOnlyDictionary<string, string> palette, string halftone = "none",
        string colourCorrection = "plain", int resolution = 600, string? photoLutPath = null)
    {
        if (!ValidHalftones.Contains(halftone))
        {
            throw new ArgumentException($"unknown halftone mode '{halftone}'; expected one of coarse_halftone, halftone, none");
        }
        if (!Colour.ValidColourCorrections.Contains(colourCorrection))
        {
            throw new ArgumentException(
                $"unknown colour correction '{colourCorrection}'; expected one of {string.Join(", ", Colour.ValidColourCorrections)}");
        }

        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels;
        int rowBytes = (width + 7) / 8;
        var planes = palette.Keys.ToDictionary(name => name, _ => new byte[rowBytes * height]);

        HalftoneMode? mode = ResolveHalftoneMode(halftone, resolution);
        var channelsNeeded = mode is not null ? palette.Values.Distinct().ToArray() : Array.Empty<string>();

        // ppmtomd.c:2585-2636,3130-3190 -- 1200dpi("highres")では 1 出力行あたり
        // row_factor=2 本のサブローを別々にディザ判定し(スクリーンの位相は
        // 絶対サブロー番号 y*row_factor+subrow で初期化する。ppmtomd.c:2854-2862
        // の ht_init(&kht, comp, row*row_factor+subrow) に対応)、その 2 つの
        // 0/maxval 判定結果を合成する。合成方法は ppmtomd.c:3174-3187 の
        // 「サブローの値(0 か maxval)を足して row_factor*col_factor で整数除算し、
        // (maxval+1)/2 で再閾値化する」を素直に読むと、(255+0)/2 = 127 < 128 に
        // なるため、結果として両方のサブローが立ったときだけ 1 になる(AND)。
        // ハーフトーン無し(mode is null)では影響しない -- 固定値に対しては
        // どちらのサブローも同じ >=128 判定になるため。col_factor は常に 1
        // (-inresolution 600 を明示したときのみ意味を持ち、本プロジェクトの
        // golden では効かない)。
        int rowFactor = (mode is not null && resolution == 1200) ? 2 : 1;

        int[]? gammaTable = null;
        byte[]? colConv = null;
        if (colourCorrection == "photo")
        {
            gammaTable = Colour.BuildGammaTable(Colour.DefaultGamma(halftone, resolution));
            colConv = ExpandedPhotoLut(photoLutPath);
        }

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 3;
            int planeRowBase = y * rowBytes;

            Dictionary<string, ((int Row, int Col)[][] SubrowPositions, int[] Matrix, int CellSize)>? rowHalftone = null;
            if (mode is not null)
            {
                rowHalftone = new();
                foreach (var channel in channelsNeeded)
                {
                    var (cellSize, matrix) = mode.Channels[channel];
                    var screen = mode.Screens[channel];
                    var subrowPositions = new (int Row, int Col)[rowFactor][];
                    for (int subrow = 0; subrow < rowFactor; subrow++)
                    {
                        subrowPositions[subrow] = HtRowPositions(
                            screen.X, screen.Y, screen.Z, screen.YNeg, y * rowFactor + subrow, cellSize, width);
                    }
                    rowHalftone[channel] = (subrowPositions, matrix, cellSize);
                }
            }

            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x * 3;
                int r = pixels[idx], g = pixels[idx + 1], b = pixels[idx + 2];
                var (c, m, yv, k) = SeparateColour(r, g, b, colourCorrection, gammaTable, colConv);
                var values = new Dictionary<string, int> { ["C"] = c, ["M"] = m, ["Y"] = yv, ["K"] = k };

                int byteIndex = planeRowBase + (x >> 3);
                int bitMask = 0x80 >> (x & 7);

                foreach (var (name, channel) in palette)
                {
                    int value = values[channel];
                    bool hit;
                    if (mode is null)
                    {
                        hit = value >= 128;
                    }
                    else
                    {
                        var (subrowPositions, matrix, cellSize) = rowHalftone![channel];
                        hit = true;
                        foreach (var positions in subrowPositions)
                        {
                            var (hrow, hcol) = positions[x];
                            int threshold = matrix[cellSize * hrow + hcol];
                            if (!(value > threshold))
                            {
                                hit = false;
                                break;
                            }
                        }
                    }
                    if (hit)
                    {
                        planes[name][byteIndex] |= (byte)bitMask;
                    }
                }
            }
        }

        return planes;
    }

    /// <summary>マジックカラーのマッチングでインクごとの 1bit プレーンへ変換する
    /// (DOMAIN §6, D-015)。
    ///
    /// inks: config.LoadPalette が返す、実行順にソート済みのインク一覧。
    ///     各インクは MagicRgb(3 値、0..255)・Tolerance(0 以上の整数)・
    ///     Order・AutoUndercoat を持つ必要がある。
    ///
    /// マッチング規則(DOMAIN §6.3.2 / D-015):
    ///   - |r-mr| <= tolerance かつ |g-mg| <= tolerance かつ |b-mb| <= tolerance
    ///     ならマッチ(整数演算のみ)。
    ///   - 複数マッチした場合、距離(3 チャンネルの偏差の最大値)が最小の
    ///     ものが勝つ。同着は order の昇順、さらに inks 内の並び順で決める。
    ///   - どれにもマッチしない画素はどのプレーンにも立たない。
    ///   - 各画素は(アンダーコートを除き)高々 1 インクに属する。
    ///
    /// AutoUndercoat が立っているインク(DOMAIN §6.2)は、他の全インクに
    /// 割り当てられた画素の和集合 + 自身の MagicRgb に直接マッチした画素、
    /// を受け取る。AutoUndercoat を持つインクは高々 1 つ(2 つ目は例外)。</summary>
    public static Dictionary<string, byte[]> ToPlanesMagic(PpmImage image, IReadOnlyList<InkDefinition> inks)
    {
        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels;
        int rowBytes = (width + 7) / 8;

        // プロセスインク(CMYK 分解の受け皿)はマジックカラーの対象ではない。
        // パレット全体を渡されても特色だけを見る(D-019)。
        var spotInks = inks.Where(ink => ink.MagicRgb is not null).ToList();

        var undercoatNames = spotInks.Where(ink => ink.AutoUndercoat).Select(ink => ink.Name).ToList();
        if (undercoatNames.Count > 1)
        {
            throw new ArgumentException(
                $"auto_undercoat is set on more than one ink: [{string.Join(", ", undercoatNames)}]; this is undefined (DOMAIN.md §6.2)");
        }
        string? undercoatName = undercoatNames.Count > 0 ? undercoatNames[0] : null;

        var planes = spotInks.ToDictionary(ink => ink.Name, _ => new byte[rowBytes * height]);

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 3;
            int planeRowBase = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x * 3;
                int r = pixels[idx], g = pixels[idx + 1], b = pixels[idx + 2];

                InkDefinition? bestInk = null;
                int bestDistance = int.MaxValue;
                bool hasBest = false;
                foreach (var ink in spotInks)
                {
                    var mrgb = ink.MagicRgb!;
                    int tolerance = ink.Tolerance!.Value;
                    int dr = Math.Abs(r - mrgb[0]);
                    int dg = Math.Abs(g - mrgb[1]);
                    int db = Math.Abs(b - mrgb[2]);
                    if (dr > tolerance || dg > tolerance || db > tolerance)
                    {
                        continue;
                    }
                    int distance = Math.Max(dr, Math.Max(dg, db));
                    if (!hasBest || distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestInk = ink;
                        hasBest = true;
                    }
                    else if (distance == bestDistance && bestInk is not null)
                    {
                        if (ink.Order < bestInk.Order)
                        {
                            bestInk = ink;
                        }
                    }
                }

                if (bestInk is not null)
                {
                    int byteIndex = planeRowBase + (x >> 3);
                    int bitMask = 0x80 >> (x & 7);
                    planes[bestInk.Name][byteIndex] |= (byte)bitMask;
                }
            }
        }

        if (undercoatName is not null)
        {
            var union = new byte[rowBytes * height];
            foreach (var (name, buf) in planes)
            {
                if (name == undercoatName)
                {
                    continue;
                }
                for (int i = 0; i < buf.Length; i++)
                {
                    union[i] |= buf[i];
                }
            }
            var undercoatBuf = planes[undercoatName];
            for (int i = 0; i < undercoatBuf.Length; i++)
            {
                union[i] |= undercoatBuf[i];
            }
            planes[undercoatName] = union;
        }

        return planes;
    }

    /// <summary>"auto" インク指定方式(DOMAIN §6.6)で画素ごとに特色/CMYK 分解を
    /// 混在させて 1bit プレーンへ変換する。
    ///
    /// cmykMap: CMYK チャンネル("C"/"M"/"Y"/"K") -> そのチャンネルを受け取る
    ///     インク名。ToPlanes の palette とは向きが逆(チャンネル -> 名前)。
    ///
    /// 画素ごとの規則(DOMAIN §6.6):
    ///   1. ToPlanesMagic と同じ規則で特色インクにマッチを試みる。
    ///   2. マッチしたらそのインクのプレーンのみに属する(DOMAIN §4.3:
    ///      1 パス = 1 カートリッジ。CMYK 分解には決して回されない)。
    ///   3. マッチしなければ CMYK 分解式(ToPlanes と同一)にかけ、
    ///      cmykMap が指すプレーンへ立てる。colourCorrection / resolution /
    ///      photoLutPath は CMYK 分解側にのみ効く(ToPlanes と同じ意味 --
    ///      特色マッチング(手順 1)は色補正の影響を受けない)。
    ///   4. AutoUndercoat(高々 1 インク、ToPlanesMagic と同じ制約)は
    ///      最後に、特色・CMYK 両方を含む他の全プレーンの和集合 + 自身の
    ///      MagicRgb に直接マッチした画素として計算する。</summary>
    public static Dictionary<string, byte[]> ToPlanesAuto(
        PpmImage image, IReadOnlyList<InkDefinition> inks, IReadOnlyDictionary<string, string> cmykMap, string halftone = "none",
        string colourCorrection = "plain", int resolution = 600, string? photoLutPath = null)
    {
        if (!ValidHalftones.Contains(halftone))
        {
            throw new ArgumentException($"unknown halftone mode '{halftone}'; expected one of coarse_halftone, halftone, none");
        }
        if (!Colour.ValidColourCorrections.Contains(colourCorrection))
        {
            throw new ArgumentException(
                $"unknown colour correction '{colourCorrection}'; expected one of {string.Join(", ", Colour.ValidColourCorrections)}");
        }

        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels;
        int rowBytes = (width + 7) / 8;

        var spotInks = inks.Where(ink => ink.MagicRgb is not null).ToList();

        var undercoatNames = spotInks.Where(ink => ink.AutoUndercoat).Select(ink => ink.Name).ToList();
        if (undercoatNames.Count > 1)
        {
            throw new ArgumentException(
                $"auto_undercoat is set on more than one ink: [{string.Join(", ", undercoatNames)}]; this is undefined (DOMAIN.md §6.2)");
        }
        string? undercoatName = undercoatNames.Count > 0 ? undercoatNames[0] : null;

        var spotPlanes = spotInks.ToDictionary(ink => ink.Name, _ => new byte[rowBytes * height]);
        var cmykPlanes = cmykMap.Values.Distinct().ToDictionary(name => name, _ => new byte[rowBytes * height]);

        HalftoneMode? mode = ResolveHalftoneMode(halftone, resolution);
        var channelsNeeded = mode is not null ? cmykMap.Keys.ToArray() : Array.Empty<string>();

        // ToPlanes の同一コメント(ppmtomd.c:2585-2636,3130-3190)を参照。
        // 1200dpi + ハーフトーンのときだけ row_factor=2 になり、AND 合成
        // (整数除算 (255+0)/2=127<128 による再閾値化)になる理由も同じ。
        int rowFactor = (mode is not null && resolution == 1200) ? 2 : 1;

        int[]? gammaTable = null;
        byte[]? colConv = null;
        if (colourCorrection == "photo")
        {
            gammaTable = Colour.BuildGammaTable(Colour.DefaultGamma(halftone, resolution));
            colConv = ExpandedPhotoLut(photoLutPath);
        }

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 3;
            int planeRowBase = y * rowBytes;

            Dictionary<string, ((int Row, int Col)[][] SubrowPositions, int[] Matrix, int CellSize)>? rowHalftone = null;
            if (mode is not null)
            {
                rowHalftone = new();
                foreach (var channel in channelsNeeded)
                {
                    var (cellSize, matrix) = mode.Channels[channel];
                    var screen = mode.Screens[channel];
                    var subrowPositions = new (int Row, int Col)[rowFactor][];
                    for (int subrow = 0; subrow < rowFactor; subrow++)
                    {
                        subrowPositions[subrow] = HtRowPositions(
                            screen.X, screen.Y, screen.Z, screen.YNeg, y * rowFactor + subrow, cellSize, width);
                    }
                    rowHalftone[channel] = (subrowPositions, matrix, cellSize);
                }
            }

            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x * 3;
                int r = pixels[idx], g = pixels[idx + 1], b = pixels[idx + 2];

                InkDefinition? bestInk = null;
                int bestDistance = int.MaxValue;
                bool hasBest = false;
                foreach (var ink in spotInks)
                {
                    var mrgb = ink.MagicRgb!;
                    int tolerance = ink.Tolerance!.Value;
                    int dr = Math.Abs(r - mrgb[0]);
                    int dg = Math.Abs(g - mrgb[1]);
                    int db = Math.Abs(b - mrgb[2]);
                    if (dr > tolerance || dg > tolerance || db > tolerance)
                    {
                        continue;
                    }
                    int distance = Math.Max(dr, Math.Max(dg, db));
                    if (!hasBest || distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestInk = ink;
                        hasBest = true;
                    }
                    else if (distance == bestDistance && bestInk is not null)
                    {
                        if (ink.Order < bestInk.Order)
                        {
                            bestInk = ink;
                        }
                    }
                }

                int byteIndex = planeRowBase + (x >> 3);
                int bitMask = 0x80 >> (x & 7);

                if (bestInk is not null)
                {
                    spotPlanes[bestInk.Name][byteIndex] |= (byte)bitMask;
                    continue;
                }

                var (c, m, yv, k) = SeparateColour(r, g, b, colourCorrection, gammaTable, colConv);
                var values = new Dictionary<string, int> { ["C"] = c, ["M"] = m, ["Y"] = yv, ["K"] = k };

                foreach (var (channel, name) in cmykMap)
                {
                    int value = values[channel];
                    bool hit;
                    if (mode is null)
                    {
                        hit = value >= 128;
                    }
                    else
                    {
                        var (subrowPositions, matrix, cellSize) = rowHalftone![channel];
                        hit = true;
                        foreach (var positions in subrowPositions)
                        {
                            var (hrow, hcol) = positions[x];
                            int threshold = matrix[cellSize * hrow + hcol];
                            if (!(value > threshold))
                            {
                                hit = false;
                                break;
                            }
                        }
                    }
                    if (hit)
                    {
                        cmykPlanes[name][byteIndex] |= (byte)bitMask;
                    }
                }
            }
        }

        if (undercoatName is not null)
        {
            var union = new byte[rowBytes * height];
            foreach (var (name, buf) in spotPlanes)
            {
                if (name == undercoatName)
                {
                    continue;
                }
                for (int i = 0; i < buf.Length; i++)
                {
                    union[i] |= buf[i];
                }
            }
            foreach (var buf in cmykPlanes.Values)
            {
                for (int i = 0; i < buf.Length; i++)
                {
                    union[i] |= buf[i];
                }
            }
            var undercoatBuf = spotPlanes[undercoatName];
            for (int i = 0; i < undercoatBuf.Length; i++)
            {
                union[i] |= undercoatBuf[i];
            }
            spotPlanes[undercoatName] = union;
        }

        var result = new Dictionary<string, byte[]>(spotPlanes);
        foreach (var (name, buf) in cmykPlanes)
        {
            result[name] = buf;
        }
        return result;
    }

    /// <summary>複数ページ("per_page" インク指定方式、DOMAIN §6.4.1 / §6.6)を
    /// インクごとの 1bit プレーンへ変換する。1 ページ = 1 インクなので色判定は
    /// 行わない — 割り当ては与えられるものであり、推測するものではない。
    ///
    /// images: 各ページの (width, height, pixels)。全ページ同一寸法が必要
    ///     (同じ紙に刷り重ねるため位置合わせが要る)。
    /// pageInks: images と同じ長さの、ページごとのインク名。
    ///
    /// 各ページはその K(黒)成分を ToPlanes と同じ式で 2 値化する:
    /// 暗い部分がそのページのインクとして印刷される。</summary>
    public static Dictionary<string, byte[]> ToPlanesPerPage(IReadOnlyList<PpmImage> images, IReadOnlyList<string> pageInks)
    {
        if (images.Count == 0)
        {
            throw new ArgumentException("per_page needs at least one page");
        }
        if (images.Count != pageInks.Count)
        {
            throw new ArgumentException($"page/ink count mismatch: {images.Count} pages, {pageInks.Count} inks");
        }

        var duplicates = pageInks.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException($"an ink may only be assigned to one page; repeated: [{string.Join(", ", duplicates)}]");
        }

        int width = images[0].Width, height = images[0].Height;
        for (int index = 0; index < images.Count; index++)
        {
            if (images[index].Width != width || images[index].Height != height)
            {
                throw new ArgumentException(
                    $"page {index} is {images[index].Width}x{images[index].Height}, expected {width}x{height}; " +
                    "pages print onto one sheet and must register with each other");
            }
        }

        int rowBytes = (width + 7) / 8;
        var planes = new Dictionary<string, byte[]>();

        for (int page = 0; page < images.Count; page++)
        {
            var image = images[page];
            string name = pageInks[page];
            var buf = new byte[rowBytes * image.Height];
            for (int y = 0; y < image.Height; y++)
            {
                int rowBase = y * image.Width * 3;
                int planeRowBase = y * rowBytes;
                for (int x = 0; x < image.Width; x++)
                {
                    int idx = rowBase + x * 3;
                    int c = 255 - image.Pixels[idx];
                    int m = 255 - image.Pixels[idx + 1];
                    int yv = 255 - image.Pixels[idx + 2];
                    int k = Math.Min(c, Math.Min(m, yv));
                    if (k >= 128)
                    {
                        buf[planeRowBase + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                }
            }
            planes[name] = buf;
        }

        return planes;
    }
}
