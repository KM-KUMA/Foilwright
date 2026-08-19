// Foilwright.Core — 色補正: ppmtomd の colcorPhoto ルックアップテーブル経路。
//
// ppmtomd 1.6 の色補正スイッチのうち mono ではなく opt_keepblack 無効の
// 分岐(vendor/ppmtomd-1.6/ppmtomd.c:2929-2960)、16^3 -> 64^3 の三重線形補間
// 展開(expand_lut、ppmtomd.c:3395-3444)、initgamma テーブルの構築
// (ppmtomd.c:1932-1957)を再現する:
//
//     c = initgamma[c] ; m = initgamma[m] ; y = initgamma[y]
//     idx = ((c & 0xFC) << 12) | ((m & 0xFC) << 6) | (y & 0xFC)
//     c, m, y, k = colconv[idx .. idx+3]
//
// opt_keepblack(ppmtomd.c:2944、「純黒は黒 100% に固定する」)は再現しない
// -- 既定で無効であり、D-029 が明示的に対象外としている。
//
// 参照実装: ref/foilwright_ref/colour.py。本ファイルは Raster.cs から
// 呼ばれる側であり、逆方向の依存は持たない。

namespace Foilwright.Core;

public static class Colour
{
    public static readonly IReadOnlyList<string> ValidColourCorrections = new[] { "none", "plain", "photo" };

    private const int LutBytes16 = 16 * 16 * 16 * 4;
    private const int LutBytes64 = 64 * 64 * 64 * 4;

    /// <summary>ppmtomd の 16x16x16x4 の photo_colcor テーブル(colour/README.md)を読む。
    ///
    /// 戻り値は inlut[c][m][y][成分] としてフラット化された生の 16,384 バイト
    /// (c が最も遅く変化する)で、まだ展開していない。ファイルがちょうど
    /// このサイズでなければ ArgumentException を送出する。</summary>
    public static byte[] LoadPhotoLut(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length != LutBytes16)
        {
            throw new ArgumentException(
                $"{path}: expected a {LutBytes16}-byte (16x16x16x4) photo colour-correction table, got {data.Length} bytes");
        }
        return data;
    }

    /// <summary>ppmtomd の expand_lut(ppmtomd.c:3395-3444)の移植。
    ///
    /// 16x16x16x4 のルックアップテーブル(LoadPhotoLut が返す inlut)を、
    /// 隣接格子点間の三重線形補間で 64x64x64x4 のテーブルへ展開する。
    /// ppmtomd の整数演算(0 方向への切り捨て除算)をそのまま使う
    /// (すべてのオペランドが非負なのでこれで厳密に一致する)。
    ///
    /// ppmtomd の文書化された癖(ppmtomd.c:3407-3409)を必ず再現する: いずれか
    /// の軸で原点添字が 15(最後の格子点)のとき、その軸の「次」の角は
    /// (添字 16 が存在しないため)参照されない -- 数学的に別の隣接点へ回り込む
    /// でもクランプするでもなく、原点そのものに置き換えられる。ここを外すと
    /// 立方体の 15/63 端の近くで ppmtomd の出力からずれる。
    ///
    /// 戻り値は outlut[i][j][k][成分] としてフラット化された 1,048,576 バイト
    /// (i が最も遅く変化し、各軸 64 エントリ)で、
    /// ((c & 0xFC) << 12) | ((m & 0xFC) << 6) | (y & 0xFC) で直接引ける
    /// (添字 i は c >> 2 に対応し、以下同様)。</summary>
    public static byte[] ExpandLut(byte[] inLut)
    {
        if (inLut.Length != LutBytes16)
        {
            throw new ArgumentException(
                $"ExpandLut: expected a {LutBytes16}-byte input table, got {inLut.Length} bytes");
        }

        int InAt(int i, int j, int k, int m) => inLut[i * 1024 + j * 64 + k * 4 + m];

        byte[] outLut = new byte[LutBytes64];

        for (int i = 0; i < 16; i++)
        {
            for (int j = 0; j < 16; j++)
            {
                for (int k = 0; k < 16; k++)
                {
                    // cube[ii][jj][kk][m]: このセルの 16 個の角の値。
                    // ppmtomd.c:3407-3409 -- 端(添字 15)では「次」の角は
                    // 存在しない添字 16 ではなく原点の角そのものになる。
                    var cube = new int[2, 2, 2, 4];
                    for (int ii = 0; ii < 2; ii++)
                    {
                        for (int jj = 0; jj < 2; jj++)
                        {
                            for (int kk = 0; kk < 2; kk++)
                            {
                                int ci = i + (i == 15 ? 0 : ii);
                                int cj = j + (j == 15 ? 0 : jj);
                                int ck = k + (k == 15 ? 0 : kk);
                                for (int m = 0; m < 4; m++)
                                {
                                    cube[ii, jj, kk, m] = InAt(ci, cj, ck, m);
                                }
                            }
                        }
                    }

                    for (int ii = 0; ii < 4; ii++)
                    {
                        for (int jj = 0; jj < 4; jj++)
                        {
                            for (int kk = 0; kk < 4; kk++)
                            {
                                int outBase =
                                    (i * 4 + ii) * 64 * 64 * 4
                                    + (j * 4 + jj) * 64 * 4
                                    + (k * 4 + kk) * 4;
                                for (int m = 0; m < 4; m++)
                                {
                                    int res =
                                        cube[0, 0, 0, m] * (4 - ii) * (4 - jj) * (4 - kk)
                                        + cube[0, 0, 1, m] * (4 - ii) * (4 - jj) * kk
                                        + cube[0, 1, 0, m] * (4 - ii) * jj * (4 - kk)
                                        + cube[0, 1, 1, m] * (4 - ii) * jj * kk
                                        + cube[1, 0, 0, m] * ii * (4 - jj) * (4 - kk)
                                        + cube[1, 0, 1, m] * ii * (4 - jj) * kk
                                        + cube[1, 1, 0, m] * ii * jj * (4 - kk)
                                        + cube[1, 1, 1, m] * ii * jj * kk;
                                    outLut[outBase + m] = (byte)(res / 64);
                                }
                            }
                        }
                    }
                }
            }
        }

        return outLut;
    }

    /// <summary>ppmtomd の initgamma 構築(ppmtomd.c:1949-1957)の移植。
    ///
    ///     ii = i / 255
    ///     ii = ii ** gamma            if gamma > 0
    ///     ii = 1 - (1 - ii) ** -gamma  if gamma < 0
    ///     table[i] = floor(255 * ii + 0.5)
    ///
    /// gamma == 0 はここでは無効な入力(ppmtomd はこの計算に到達する前に
    /// 必ず 0 をモード依存の既定値へ解決している。DefaultGamma を参照)。
    /// この計算だけは C の double / pow をそのまま使う(整数演算に置き換え
    /// ない -- D-015 が言う「1 画素ごとの演算」ではなく、golden fixture を
    /// 生成した同じ浮動小数点経路を再現する必要があるため)。
    /// **丸めは Math.Floor(255.0 * ii + 0.5) を double のまま行う。float
    /// にすると丸めが変わり golden が合わなくなる。**</summary>
    public static int[] BuildGammaTable(double gamma)
    {
        if (gamma == 0)
        {
            throw new ArgumentException("BuildGammaTable: gamma must not be 0");
        }

        int[] table = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double ii = i / 255.0;
            if (gamma > 0.0)
            {
                ii = Math.Pow(ii, gamma);
            }
            else
            {
                ii = 1.0 - Math.Pow(1.0 - ii, -gamma);
            }
            table[i] = (int)Math.Floor(255.0 * ii + 0.5);
        }
        return table;
    }

    /// <summary>-gamma が指定されなかったときに ppmtomd が選ぶ既定の initgam
    /// (ppmtomd.c:1932-1948)。nybble(多値)モードは対象外(未実装)。
    ///
    /// | dither              | resolution | initgam |
    /// |---------------------|------------|---------|
    /// | halftone/coarse     | 1200       | -0.9    |
    /// | halftone/coarse     | other      | 0.8     |
    /// | none                | any        | 1.2     |
    /// </summary>
    public static double DefaultGamma(string halftone, int resolution)
    {
        if (halftone is "halftone" or "coarse_halftone")
        {
            return resolution == 1200 ? -0.9 : 0.8;
        }
        return 1.2;
    }
}
