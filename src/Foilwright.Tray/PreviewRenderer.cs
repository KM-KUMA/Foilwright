// Foilwright.Tray — インク割り当てプレビューの描画(DOMAIN §7.2)。
//
// jobPlanes は JobAssembly.BuildJobPlanes が返す印刷順(order 昇順)の並びを
// 前提にする。DOMAIN §4.3 の「order は昇順に実行され、先に刷ったものが下の
// 層になる」に従い、後の要素ほど上に重ねて描く(先勝ちではなく後勝ち)。
//
// 白のように背景(未印字の紙)と見分けにくい明るい色は、そのままでは
// 「見えているのに見えない」事故につながる(タスク仕様の指摘)。市松模様で
// 目印色と交互に塗ることで、明るい色でも領域の輪郭が見えるようにする。

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

    public static Bitmap Render(
        int sourceWidth, int sourceHeight,
        IReadOnlyList<(InkDefinition Ink, byte[] Plane)> jobPlanes,
        int maxPreviewWidth)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return new Bitmap(1, 1, PixelFormat.Format24bppRgb);
        }

        double scale = Math.Min(1.0, (double)maxPreviewWidth / sourceWidth);
        int previewWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int previewHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));

        int rowBytes = (sourceWidth + 7) / 8;

        var bitmap = new Bitmap(previewWidth, previewHeight, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, previewWidth, previewHeight);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            byte[] buffer = new byte[data.Stride * previewHeight];
            for (int py = 0; py < previewHeight; py++)
            {
                int sy = Math.Min(sourceHeight - 1, (int)(py / scale));
                int rowOffset = py * data.Stride;
                for (int px = 0; px < previewWidth; px++)
                {
                    int sx = Math.Min(sourceWidth - 1, (int)(px / scale));
                    Color color = SampleColor(sx, sy, rowBytes, jobPlanes);
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

    private static Color SampleColor(
        int sx, int sy, int rowBytes, IReadOnlyList<(InkDefinition Ink, byte[] Plane)> jobPlanes)
    {
        Color result = BackgroundColor;
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
            result = ResolvePixelColor(ink, sx, sy);
        }
        return result;
    }

    private static Color ResolvePixelColor(InkDefinition ink, int sx, int sy)
    {
        Color baseColor = ResolveDisplayColor(ink);
        if (!IsHardToSeeOnBackground(baseColor))
        {
            return baseColor;
        }
        // 白などの明色は薄いグレーの背景に埋没するため、市松模様で目印色と
        // 交互に塗って輪郭を可視化する。
        return (sx + sy) % 2 == 0 ? baseColor : LightInkMarkerColor;
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
        return ink.Channel switch
        {
            "C" => Color.FromArgb(0, 255, 255),
            "M" => Color.FromArgb(255, 0, 255),
            "Y" => Color.FromArgb(255, 255, 0),
            "K" => Color.FromArgb(30, 30, 30),
            _ => Color.FromArgb(255, 0, 128), // 到達しないはず(palette 検証で弾かれる)
        };
    }

    private static bool IsHardToSeeOnBackground(Color c)
    {
        int brightness = (c.R + c.G + c.B) / 3;
        return brightness >= LightThreshold;
    }
}
