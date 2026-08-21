// Foilwright.Tray.Tests — プレビューに「インクを 1 つだけ表示する」機能の検出器(DOMAIN §7.2)。
//
// 見張るのは 2 点:
//   ①注意文(PreviewForm.BuildInkFilterNotice)から「印刷はすべてのインクで行われます」が
//     消えていないこと。表示を絞ったまま印刷ボタンを押した利用者が「これだけ刷られる」と
//     誤解すると、代替入手の困難なリボンと用紙を失う。
//   ②JobPipeline.RenderPreviewBitmap が **ジョブの中身を 1 バイトも変えない** こと。
//     これは見せ方だけの機能であり、Planes / JobInks / RequiredInks が書き換わったら
//     「表示を絞ったら刷られるものまで変わった」という最悪の事故になる。
//
// UI もプリンタも Ghostscript も要らない。PreviewResult は最小限のものをここで組み立てる。

using System.Drawing;
using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class InkFilterPreviewTests
{
    // 8x8 ドットの小さな絵。1 行 1 バイト(rowBytes = 1)。
    private const int ImageWidth = 8;
    private const int ImageHeight = 8;

    /// <summary>テスト用のパレット(palette/default.yaml の値を写したもの。
    /// ファイルに依存させないため、ここで組み立てる)。</summary>
    private static List<InkDefinition> BuildPalette() => new()
    {
        new InkDefinition
        {
            Name = "white",
            Label = "紙用特色ホワイト (MDC-SCWH)",
            PrinterCode = 0x0B,
            Order = 10,
            MagicRgb = new[] { 230, 230, 230 },
            Tolerance = 8,
            Barcode = 16,
            AutoUndercoat = true,
            Passes = 1,
        },
        new InkDefinition
        {
            Name = "metallic_gold",
            Label = "メタリックゴールド (MDC-METG)",
            PrinterCode = 0x04,
            Order = 50,
            MagicRgb = new[] { 225, 160, 0 },
            Tolerance = 12,
            Barcode = 8,
            Passes = 3,
        },
        new InkDefinition
        {
            Name = "black",
            Label = "紙用ブラック (MDC-FLBK)",
            PrinterCode = 0x00,
            Order = 70,
            Channel = "K",
            Barcode = 1,
            Passes = 1,
        },
    };

    /// <summary>最小限の PreviewResult。Ghostscript もプリンタも使わず、
    /// プレーンだけを自前で置く(上半分に白、下半分に黒)。</summary>
    private static PreviewResult BuildResult()
    {
        var palette = BuildPalette();
        var required = new List<InkDefinition> { palette[0], palette[2] };

        byte[] whitePlane = new byte[ImageHeight];
        byte[] blackPlane = new byte[ImageHeight];
        for (int y = 0; y < ImageHeight / 2; y++)
        {
            whitePlane[y] = 0xFF;
        }
        for (int y = ImageHeight / 2; y < ImageHeight; y++)
        {
            blackPlane[y] = 0xFF;
        }

        var resolution = new ResolutionEntry { DpiX = 600, DpiY = 600, IsDefault = true };
        var config = new JobConfig
        {
            Profile = new ProfileSpec
            {
                Model = "md-5500",
                PaperTable = "5000-series",
                Resolutions = new List<ResolutionEntry> { resolution },
            },
            Paper = new PaperSpec { Code = 0x04, Width = 4728, Length = 6800, LeftMargin = 0, TopMargin = 0 },
            Media = new MediaSpec { Label = "普通紙", Byte1 = 0x00, Byte2 = 0x00 },
            Palette = palette,
        };

        return new PreviewResult
        {
            Preview = new Bitmap(1, 1),
            Inks = required
                .Select(ink => new InkPreviewInfo
                {
                    Name = ink.Name,
                    Label = ink.Label,
                    Order = ink.Order,
                    Passes = ink.Passes,
                    PrinterCode = ink.PrinterCode,
                    Color = PreviewRenderer.ResolveDisplayColor(ink),
                })
                .ToList(),
            Width = ImageWidth,
            Height = ImageHeight,
            Resolution = resolution,
            Planes = new Dictionary<string, byte[]>
            {
                ["white"] = whitePlane,
                ["black"] = blackPlane,
            },
            JobInks = required
                .Select(ink => new JobInk { Name = ink.Name, PrinterCode = ink.PrinterCode, Passes = ink.Passes })
                .ToList(),
            RequiredInks = required,
            Image = new PpmImage(ImageWidth, ImageHeight, new byte[ImageWidth * ImageHeight * 3]),
            Config = config,
        };
    }

    /// <summary>絞っていない(= すべてのインクを表示している)ときは何も出さない。</summary>
    [Fact]
    public void NoticeIsEmptyWhenNothingIsFiltered()
    {
        Assert.Equal(string.Empty, PreviewForm.BuildInkFilterNotice(null));
    }

    /// <summary>絞っているときは、インク名と「印刷はすべてのインクで行われます」に
    /// 相当する一文の**両方**が出ること。**この一文が消えていないことの検出器**であり、
    /// ここが本質(誤解したまま印刷するとリボンと用紙を失う。DOMAIN §7.2)。</summary>
    [Fact]
    public void NoticeNamesTheInkAndSaysPrintingUsesAllInks()
    {
        string notice = PreviewForm.BuildInkFilterNotice("紙用特色ホワイト");

        Assert.Contains("紙用特色ホワイト", notice);
        Assert.Contains("印刷はすべてのインクで行われます", notice);
    }

    /// <summary>**この機能の一番の危険の検出器。** 描き直しはジョブの中身
    /// (Planes の各バイト列 / JobInks / RequiredInks)を一切変えてはならない。
    /// 変わると「表示を絞ったら刷られるものまで変わった」という事故になる。</summary>
    [Fact]
    public void RenderingDoesNotTouchTheJob()
    {
        using var result = BuildResult();

        var planesBefore = result.Planes.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        var jobInksBefore = result.JobInks.Select(ink => (ink.Name, ink.PrinterCode, ink.Passes)).ToList();
        int requiredCountBefore = result.RequiredInks.Count;
        var requiredNamesBefore = result.RequiredInks.Select(ink => ink.Name).ToList();

        using (var all = JobPipeline.RenderPreviewBitmap(result, null))
        using (var onlyWhite = JobPipeline.RenderPreviewBitmap(result, "white"))
        using (var missing = JobPipeline.RenderPreviewBitmap(result, "metallic_gold"))
        {
            Assert.NotNull(all);
            Assert.NotNull(onlyWhite);
            Assert.NotNull(missing);
        }

        Assert.Equal(planesBefore.Count, result.Planes.Count);
        foreach (var kv in planesBefore)
        {
            Assert.True(result.Planes.ContainsKey(kv.Key), $"プレーン '{kv.Key}' が消えている");
            Assert.Equal(kv.Value, result.Planes[kv.Key]);
        }

        Assert.Equal(jobInksBefore.Count, result.JobInks.Count);
        Assert.Equal(
            jobInksBefore,
            result.JobInks.Select(ink => (ink.Name, ink.PrinterCode, ink.Passes)).ToList());

        Assert.Equal(requiredCountBefore, result.RequiredInks.Count);
        Assert.Equal(requiredNamesBefore, result.RequiredInks.Select(ink => ink.Name).ToList());
    }

    /// <summary>絞ると、そのインクだけが絵に残る。白は市松模様で描かれる(明るい色は
    /// 背景に埋没するため)ので、色そのものではなく「黒の領域が背景に戻ったか」で見る。</summary>
    [Fact]
    public void FilteringLeavesOnlyTheChosenInk()
    {
        using var result = BuildResult();

        using var all = JobPipeline.RenderPreviewBitmap(result, null);
        using var onlyWhite = JobPipeline.RenderPreviewBitmap(result, "white");

        // 下半分(黒のプレーンが立っている側)の 1 点。
        int x = all.Width / 2;
        int y = all.Height * 3 / 4;

        var blackish = all.GetPixel(x, y);
        Assert.True(blackish.R < 100 && blackish.G < 100 && blackish.B < 100,
            $"全インク表示では黒が出ているはず: {blackish}");

        var filtered = onlyWhite.GetPixel(x, y);
        Assert.True(filtered.R > 200 && filtered.G > 200 && filtered.B > 200,
            $"白だけに絞ったら黒の領域は背景(薄いグレー)に戻るはず: {filtered}");
    }

    /// <summary>ジョブに無いインクを指定しても例外にしない(背景だけの絵になる)。</summary>
    [Fact]
    public void FilteringToAnInkThatIsNotInTheJobDrawsBackgroundOnly()
    {
        using var result = BuildResult();

        using var bitmap = JobPipeline.RenderPreviewBitmap(result, "metallic_gold");

        var pixel = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        Assert.Equal(Color.FromArgb(240, 240, 240).ToArgb(), pixel.ToArgb());
    }
}
