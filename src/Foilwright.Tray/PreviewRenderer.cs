// Foilwright.Tray — インク割り当てプレビューの描画(DOMAIN §7.2)。
//
// jobPlanes は JobAssembly.BuildJobPlanes が返す印刷順(order 昇順)の並びを
// 前提にする。DOMAIN §4.3 の「order は昇順に実行され、先に刷ったものが下の
// 層になる」に従い、後の要素ほど上に重ねて描く(先勝ちではなく後勝ち)。
//
// 白のように背景(未印字の紙)と見分けにくい明るい色は、そのままでは
// 「見えているのに見えない」事故につながる(タスク仕様の指摘)。市松模様で
// 目印色と交互に塗ることで、明るい色でも領域の輪郭が見えるようにする。
//
// D-038: alpha モード(D-037)では白が絵の全面(切り出し範囲全体)に
// ほぼ隙間なく敷かれるのに対し、CMYK は網点(ハーフトーン)で疎に打たれる。
// 「その画素だけを見て白が単独か」を判定すると、網点の隙間(=どのインクも
// 打たれていない画素)がすべて市松にされ、絵全体が青緑がかって見える事故に
// なる(2026-08-20 に実際に発生)。これを避けるため、白が単独で乗る画素の
// 判定は「その画素の近傍(HatchOtherInkNeighborhoodRadius)に他のインクが
// まったく無いか」まで見る — 近傍に他インクがあれば「網点の隙間」とみなし
// 市松を描かない(その領域の輪郭は隣接する他インクの色で既に見えている)。
// 近傍が完全に他インクを含まない場合だけ、これまでどおり市松で輪郭を出す。

using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Foilwright.Core;

namespace Foilwright.Tray;

public static class PreviewRenderer
{
    /// <summary>未印字(紙のまま)の背景色。白インクの市松模様と見分けが付くよう、
    /// 純白ではなく薄いグレーにしてある。</summary>
    private static readonly Color BackgroundColor = Color.FromArgb(240, 240, 240);

    /// <summary>白などの明るいインクを可視化するための目印色(市松模様の片側)。</summary>
    private static readonly Color LightInkMarkerColor = Color.FromArgb(0, 120, 215);

    /// <summary>この輝度以上のインク色は背景に埋没するとみなし、市松模様で
    /// 目印色と交互に塗る。白(230,230,230)を確実に拾う値。</summary>
    private const int LightThreshold = 210;

    /// <summary>白などの「単独か」を判定する近傍の半径(元画像の座標系、単位: 画素)。
    /// Raster.cs のハーフトーン行列(600dpi の Halftone は 12x12、CoarseHalftone は
    /// 10x10)がおおよそ 1 サイクルとみなせる大きさを覆うよう選んだ
    /// 【推測: 見た目で妥当と判断した値であり、実測で最適値を検証したわけではない】。
    /// 半径 6 で 13x13 画素の窓になり、10〜12 画素周期の網点なら必ず 1 個以上の
    /// 他インクの点を窓内に含められる。</summary>
    private const int HatchOtherInkNeighborhoodRadius = 6;

    public static Bitmap Render(
        int sourceWidth, int sourceHeight,
        IReadOnlyList<(InkDefinition Ink, byte[] Plane)> jobPlanes,
        int maxPreviewWidth,
        int dpiX = 1,
        int dpiY = 1)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new Bitmap(1, 1, PixelFormat.Format24bppRgb);
        }

        double scaleX = Math.Min(1.0, (double)maxPreviewWidth / sourceWidth);
        // D-038: 1200x600 のように画素が正方形でない解像度(横 1/1200 インチ、
        // 縦 1/600 インチ)では、縦横に同じ倍率をかけると縦に潰れて見える。
        // 縦の倍率にだけ dpiX/dpiY を掛け、見た目のアスペクト比を実寸に合わせる
        // (dpiX == dpiY の解像度では 1 倍のまま、従来どおり)。
        double dpiRatio = dpiY > 0 ? (double)dpiX / dpiY : 1.0;
        double scaleY = scaleX * dpiRatio;
        int previewWidth = Math.Max(1, (int)Math.Round(sourceWidth * scaleX));
        int previewHeight = Math.Max(1, (int)Math.Round(sourceHeight * scaleY));

        int rowBytes = (sourceWidth + 7) / 8;

        // D-038: 「白(などの明るいインク)が単独で乗る画素」の判定に使う、
        // インクごとの「他の全インクの OR」平面をあらかじめ作っておく
        // (ジョブに現れるインクは高々十数種のため、コストは無視できる)。
        var otherCoverageByLightInk = BuildOtherCoverageForLightInks(sourceHeight, rowBytes, jobPlanes);

        var bitmap = new Bitmap(previewWidth, previewHeight, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, previewWidth, previewHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] buffer = new byte[data.Stride * previewHeight];
            for (int py = 0; py < previewHeight; py++)
            {
                int sy = Math.Min(sourceHeight - 1, (int)(py / scaleY));
                int rowOffset = py * data.Stride;
                for (int px = 0; px < previewWidth; px++)
                {
                    int sx = Math.Min(sourceWidth - 1, (int)(px / scaleX));
                    Color color = SampleColor(
                        sx, sy, px, py, rowBytes, sourceWidth, sourceHeight, jobPlanes, otherCoverageByLightInk);
                    int idx = rowOffset + px * 3;
                    buffer[idx] = color.B;
                    buffer[idx + 1] = color.G;
                    buffer[idx + 2] = color.R;
                }
            }
            Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        return bitmap;
    }

    /// <summary>明るい(背景に埋没する)インクごとに、「そのインク以外の全インク」の
    /// ビット面を OR して 1 枚にまとめたものを返す。市松の要否判定(近傍に他インクが
    /// あるか)をビット単位で速く見られるようにする前処理。</summary>
    private static Dictionary<string, byte[]> BuildOtherCoverageForLightInks(
        int sourceHeight, int rowBytes, IReadOnlyList<(InkDefinition Ink, byte[] Plane)> jobPlanes)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var (ink, _) in jobPlanes)
        {
            if (result.ContainsKey(ink.Name) || !IsHardToSeeOnBackground(ResolveDisplayColor(ink)))
            {
                continue;
            }
            byte[] combined = new byte[rowBytes * sourceHeight];
            foreach (var (otherInk, otherPlane) in jobPlanes)
            {
                if (ReferenceEquals(otherInk, ink) || otherInk.Name == ink.Name)
                {
                    continue;
                }
                int length = Math.Min(combined.Length, otherPlane.Length);
                for (int i = 0; i < length; i++)
                {
                    combined[i] |= otherPlane[i];
                }
            }
            result[ink.Name] = combined;
        }
        return result;
    }

    /// <summary>otherCoverage(そのインク以外の全インクの OR 平面)に、(sx, sy) を
    /// 中心とする近傍(HatchOtherInkNeighborhoodRadius)のどこかにビットが立って
    /// いるかを調べる。立っていれば「近くに他インクがある」= 網点の隙間とみなす。</summary>
    private static bool HasOtherInkNearby(
        byte[] otherCoverage, int rowBytes, int sourceWidth, int sourceHeight, int sx, int sy)
    {
        int y0 = Math.Max(0, sy - HatchOtherInkNeighborhoodRadius);
        int y1 = Math.Min(sourceHeight - 1, sy + HatchOtherInkNeighborhoodRadius);
        int x0 = Math.Max(0, sx - HatchOtherInkNeighborhoodRadius);
        int x1 = Math.Min(sourceWidth - 1, sx + HatchOtherInkNeighborhoodRadius);

        for (int y = y0; y <= y1; y++)
        {
            int rowOffset = y * rowBytes;
            for (int x = x0; x <= x1; x++)
            {
                int byteIndex = rowOffset + x / 8;
                if (byteIndex >= otherCoverage.Length)
                {
                    continue;
                }
                int bitMask = 1 << (7 - (x % 8));
                if ((otherCoverage[byteIndex] & bitMask) != 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Color SampleColor(
        int sx, int sy, int px, int py, int rowBytes, int sourceWidth, int sourceHeight,
        IReadOnlyList<(InkDefinition Ink, byte[] Plane)> jobPlanes,
        IReadOnlyDictionary<string, byte[]> otherCoverageByLightInk)
    {
        Color result = BackgroundColor;
        InkDefinition? resultInk = null;
        int byteIndex = sy * rowBytes + sx / 8;
        int bitMask = 1 << (7 - (sx % 8));

        // order 昇順(下から上)に走査し、立っているビットで上書きする —
        // 最後に見つかったもの(最も上の層)が最終的な表示色になる。
        foreach (var (ink, plane) in jobPlanes)
        {
            if (byteIndex >= plane.Length)
            {
                continue;
            }
            if ((plane[byteIndex] & bitMask) == 0)
            {
                continue;
            }
            resultInk = ink;
        }

        if (resultInk is null)
        {
            return result;
        }

        bool suppressHatch = otherCoverageByLightInk.TryGetValue(resultInk.Name, out var otherCoverage)
            && HasOtherInkNearby(otherCoverage, rowBytes, sourceWidth, sourceHeight, sx, sy);
        return ResolvePixelColor(resultInk, px, py, suppressHatch);
    }

    /// <summary>市松の 1 マスの辺(プレビュー画素)。1 では縮小時に潰れて
    /// 見えなくなるため、縮小率に依存せず目視できる幅を取る。</summary>
    private const int HatchCellPixels = 4;

    /// <param name="px">プレビュー側の X。**元画像の座標ではない。**
    /// 元画像の座標で位相を決めると、縮小のサンプリングで奇偶が規則的に
    /// 間引かれ、市松が単色に潰れる(実測で確認)。</param>
    /// <param name="py">プレビュー側の Y。同上。</param>
    private static Color ResolvePixelColor(InkDefinition ink, int px, int py, bool suppressHatch)
    {
        Color baseColor = ResolveDisplayColor(ink);
        if (!IsHardToSeeOnBackground(baseColor))
        {
            return baseColor;
        }
        // D-038: 近傍に他インクがある(=網点の隙間である可能性が高い)場合は
        // 市松を描かない。その領域の輪郭は隣接する他インクの色で既に見えている
        // ため、ここでさらに目印色を重ねると alpha モードで絵全体が
        // 青緑がかって見える事故になる(ファイル冒頭コメント参照)。
        if (suppressHatch)
        {
            return baseColor;
        }
        // 白などの明色は薄いグレーの背景に埋没するため、市松模様で目印色と
        // 交互に塗って輪郭を可視化する(DOMAIN §7.2: プレビューは必須機能で
        // あり、白が見えないと誤爆を検出できない)。
        int cell = (px / HatchCellPixels) + (py / HatchCellPixels);
        return cell % 2 == 0 ? baseColor : LightInkMarkerColor;
    }

    /// <summary>ジョブ内容リスト(凡例)にも使う代表色。palette/*.yaml の
    /// magic_rgb をそのまま使う(タスク仕様)。magic_rgb を持たないプロセス
    /// インク(既定パレットでは C/M/Y)は channel から一般的な表示色を当てる —
    /// パレットに実測の表示色が無いための代替であり、印刷結果の色そのものを
    /// 表すものではない【推測: 表示専用の便宜色】。</summary>
    public static Color ResolveDisplayColor(InkDefinition ink)
    {
        if (ink.MagicRgb is not null)
        {
            return Color.FromArgb(ink.MagicRgb[0], ink.MagicRgb[1], ink.MagicRgb[2]);
        }
        // D-048: 塗る範囲で決まるインク(光沢仕上げ・MF インク)は magic_rgb も
        // channel も持たない。**紙の上ではほぼ無色**(上掛け・下地)なので、
        // 薄い色を当てて市松模様で見えるようにする(IsHardToSeeOnBackground が拾う)。
        //
        // **同種のインクは同じ色になる。** 2 色を重ねたときに見分けるには
        // 「表示: インクを 1 つだけ」(D-047)を使う。ここで名前ごとに色を
        // 振り分けないのは、**インク名をコードに書かない**という約束(§4.5)を
        // 守るため。
        if (ink.Coverage)
        {
            return Color.FromArgb(214, 224, 238);
        }
        return ink.Channel switch
        {
            "C" => Color.FromArgb(0, 255, 255),
            "M" => Color.FromArgb(255, 0, 255),
            "Y" => Color.FromArgb(255, 255, 0),
            "K" => Color.FromArgb(30, 30, 30),
            // magic_rgb / channel / coverage のいずれも持たないインクは
            // パレットの検証(D-019 / D-048)で弾かれるため、ここへは来ない。
            // 来たときに黙って白く塗ると誤爆に気づけないので、目立つ色を出す。
            _ => Color.FromArgb(255, 0, 128),
        };
    }

    private static bool IsHardToSeeOnBackground(Color c)
    {
        int brightness = (c.R + c.G + c.B) / 3;
        return brightness >= LightThreshold;
    }
}
