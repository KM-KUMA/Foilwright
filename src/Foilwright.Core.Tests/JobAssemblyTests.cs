// Foilwright.Core.Tests — JobAssembly(D-016 のインク指定方式選択、D-019
// の二役インク合成、空プレーン除外)の検証。
//
// 実機を要さない: PpmImage を直接合成し、Raster / ConfigLoader だけで
// 検証できる範囲に閉じる。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class JobAssemblyTests
{
    // テスト用の小さなパレット(3 x 1 画素の画像に対応):
    //   red    : 特色のみ(magic_rgb=[255,0,0], tolerance=0)
    //   black  : 二役(magic_rgb=[0,0,0], tolerance=0, channel=K) — D-019 の二役インク
    //   cyan/magenta/yellow: プロセスのみ(channel=C/M/Y)
    private static List<InkDefinition> MakePalette()
    {
        return new List<InkDefinition>
        {
            new InkDefinition { Name = "red", Label = "red", PrinterCode = 0x10, Order = 10, MagicRgb = new[] { 255, 0, 0 }, Tolerance = 0 },
            new InkDefinition { Name = "cyan", Label = "cyan", PrinterCode = 0x01, Order = 60, Channel = "C" },
            new InkDefinition { Name = "magenta", Label = "magenta", PrinterCode = 0x02, Order = 70, Channel = "M" },
            new InkDefinition { Name = "yellow", Label = "yellow", PrinterCode = 0x03, Order = 80, Channel = "Y" },
            new InkDefinition { Name = "black", Label = "black", PrinterCode = 0x00, Order = 90, MagicRgb = new[] { 0, 0, 0 }, Tolerance = 0, Channel = "K" },
        };
    }

    // 3 画素 x 1 行:
    //   x=0: (255,0,0) -- red の magic_rgb に厳密一致 -> 特色マッチで red へ
    //   x=1: (50,50,50) -- どの特色にも一致しない灰色。CMYK 分解では
    //        c=m=y=0 (差分吸収後)、k=205 (>=128 のしきい値超え) -> K チャンネル
    //        経由で black(二役)の CMYK 側プレーンへ
    //   x=2: (0,0,0) -- black の magic_rgb に厳密一致 -> 特色マッチで black へ
    private static PpmImage MakeMixedImage()
    {
        byte[] pixels =
        {
            255, 0, 0,
            50, 50, 50,
            0, 0, 0,
        };
        return new PpmImage(3, 1, pixels);
    }

    // 全画素が白(255,255,255): どの特色にも一致せず、CMYK 分解も全チャンネル
    // 0(しきい値未満)になるため、全インクのプレーンが空になる。
    private static PpmImage MakeBlankImage()
    {
        byte[] pixels = { 255, 255, 255, 255, 255, 255, 255, 255, 255 };
        return new PpmImage(3, 1, pixels);
    }

    private static bool BitSet(byte[] plane, int x) => (plane[0] & (0x80 >> x)) != 0;

    [Fact]
    public void Auto_SpotMatchGoesToSpotPlane_RemainderGoesToCmykPlane()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto");
        var byName = result.ToDictionary(r => r.Ink.Name, r => r.Plane);

        // red は x=0 のみ(特色マッチ)。
        Assert.True(byName.ContainsKey("red"));
        Assert.True(BitSet(byName["red"], 0));
        Assert.False(BitSet(byName["red"], 1));
        Assert.False(BitSet(byName["red"], 2));

        // cyan/magenta/yellow はどの画素にも一致しないため、ジョブから除外される。
        Assert.False(byName.ContainsKey("cyan"));
        Assert.False(byName.ContainsKey("magenta"));
        Assert.False(byName.ContainsKey("yellow"));
    }

    [Fact]
    public void Auto_TwoRoleInk_PlanesAreMergedIntoSingleEntry()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto");

        // black(二役)は結果に 1 エントリのみ(2 エントリに分裂しない)。
        var blackEntries = result.Where(r => r.Ink.Name == "black").ToList();
        Assert.Single(blackEntries);

        // x=1(CMYK 分解の K 経由)と x=2(特色マッチ)の両方のビットが、
        // 合成後の同一プレーンに立っている。
        byte[] blackPlane = blackEntries[0].Plane;
        Assert.False(BitSet(blackPlane, 0));
        Assert.True(BitSet(blackPlane, 1));
        Assert.True(BitSet(blackPlane, 2));
    }

    [Fact]
    public void Auto_EmptyPlanes_AreExcludedFromJob()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto");
        var names = result.Select(r => r.Ink.Name).ToHashSet();

        Assert.DoesNotContain("cyan", names);
        Assert.DoesNotContain("magenta", names);
        Assert.DoesNotContain("yellow", names);
    }

    [Fact]
    public void Auto_AllPlanesEmpty_ReturnsEmptyJob()
    {
        var palette = MakePalette();
        var image = MakeBlankImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto");

        Assert.Empty(result);
    }

    [Fact]
    public void SpotOnly_UsesMagicColourMatchingOnly()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "spot_only");
        var byName = result.ToDictionary(r => r.Ink.Name, r => r.Plane);

        // spot_only は特色マッチのみ。x=1 はどの特色にも一致しないため無視される。
        Assert.True(byName.ContainsKey("red"));
        Assert.True(BitSet(byName["red"], 0));

        Assert.True(byName.ContainsKey("black"));
        Assert.False(BitSet(byName["black"], 1));
        Assert.True(BitSet(byName["black"], 2));

        // プロセス専用インクは spot_only では一切現れない。
        Assert.False(byName.ContainsKey("cyan"));
        Assert.False(byName.ContainsKey("magenta"));
        Assert.False(byName.ContainsKey("yellow"));
    }

    [Fact]
    public void ResultOrder_FollowsPaletteOrderAscending()
    {
        var palette = MakePalette();
        // 全インクに中身が入るよう、cyan(order=60) にも一致する画素を足す。
        // c=255,m=0,y=0,k=0 になる (0,255,255) を追加。
        byte[] pixels =
        {
            255, 0, 0,   // red
            0, 255, 255, // cyan (c=255 after complement/subtract-min)
            0, 0, 0,     // black
        };
        var image = new PpmImage(3, 1, pixels);

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto");
        var orders = result.Select(r => r.Ink.Order).ToList();

        var sorted = orders.OrderBy(o => o).ToList();
        Assert.Equal(sorted, orders);
    }

    [Fact]
    public void BuildJobPlanes_PerPageMode_Throws()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(image, palette, "per_page"));
    }

    [Fact]
    public void BuildJobPlanes_UnknownMode_Throws()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(image, palette, "bogus"));
    }

    // --- 白版モード(D-027)の検証用パレット・画像 --------------------------
    //
    // white は auto_undercoat: true(実パレットの default.yaml と同じ判別方法。
    // 名前を決め打ちしない)。4 画素 x 1 行:
    //   x=0: (255,0,0)     -- red の magic_rgb に一致
    //   x=1: (0,255,255)   -- どの特色にも一致しない -> CMYK 分解で cyan(C)へ
    //   x=2: (230,230,230) -- white の magic_rgb に直接一致
    //   x=3: (255,255,255) -- どの特色にも CMYK にも一致しない(空)
    private static List<InkDefinition> MakePaletteWithWhite()
    {
        return new List<InkDefinition>
        {
            new InkDefinition { Name = "white", Label = "white", PrinterCode = 0x0b, Order = 10, MagicRgb = new[] { 230, 230, 230 }, Tolerance = 0, AutoUndercoat = true },
            new InkDefinition { Name = "red", Label = "red", PrinterCode = 0x10, Order = 20, MagicRgb = new[] { 255, 0, 0 }, Tolerance = 0 },
            new InkDefinition { Name = "cyan", Label = "cyan", PrinterCode = 0x01, Order = 60, Channel = "C" },
        };
    }

    private static PpmImage MakeWhiteTestImage()
    {
        byte[] pixels =
        {
            255, 0, 0,
            0, 255, 255,
            230, 230, 230,
            255, 255, 255,
        };
        return new PpmImage(4, 1, pixels);
    }

    [Fact]
    public void WhiteMode_None_ExcludesWhitePlaneEntirely()
    {
        var palette = MakePaletteWithWhite();
        var image = MakeWhiteTestImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto", whiteMode: "none");
        var names = result.Select(r => r.Ink.Name).ToHashSet();

        Assert.DoesNotContain("white", names);
        Assert.Contains("red", names);
        Assert.Contains("cyan", names);
    }

    [Fact]
    public void WhiteMode_Auto_IsUnionOfOtherInksPlusOwnMagicMatch()
    {
        var palette = MakePaletteWithWhite();
        var image = MakeWhiteTestImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto", whiteMode: "auto");
        var byName = result.ToDictionary(r => r.Ink.Name, r => r.Plane);

        Assert.True(byName.ContainsKey("white"));
        // x=0(red 分)、x=1(cyan/CMYK 分)、x=2(white 自身の直接一致)の和集合。
        Assert.True(BitSet(byName["white"], 0));
        Assert.True(BitSet(byName["white"], 1));
        Assert.True(BitSet(byName["white"], 2));
        Assert.False(BitSet(byName["white"], 3));
    }

    [Fact]
    public void WhiteMode_Magic_OnlyDirectMagicColourMatches()
    {
        var palette = MakePaletteWithWhite();
        var image = MakeWhiteTestImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto", whiteMode: "magic");
        var byName = result.ToDictionary(r => r.Ink.Name, r => r.Plane);

        Assert.True(byName.ContainsKey("white"));
        // x=2(white の magic_rgb への直接一致)のみ。他インクの画素は
        // 巻き込まない(auto_undercoat の和集合を作らない)。
        Assert.False(BitSet(byName["white"], 0));
        Assert.False(BitSet(byName["white"], 1));
        Assert.True(BitSet(byName["white"], 2));
        Assert.False(BitSet(byName["white"], 3));
    }

    [Fact]
    public void BuildJobPlanes_UnknownWhiteMode_Throws()
    {
        var palette = MakePaletteWithWhite();
        var image = MakeWhiteTestImage();

        Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(image, palette, "auto", whiteMode: "bogus"));
    }

    // --- ハーフトーン(DOMAIN §4.2.1)の選択可否の検証 ------------------------
    // Raster.cs のハーフトーン展開ロジック自体は変更していないため、ここでは
    // JobAssembly 経由で 3 モードすべてが選択でき、例外なく完了することだけを
    // 確認する(展開結果の画素パターン自体は Raster.cs 側の責務)。
    [Theory]
    [InlineData("none")]
    [InlineData("halftone")]
    [InlineData("coarse_halftone")]
    public void BuildJobPlanes_AllHalftoneModes_AreSelectable(string halftone)
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        var result = JobAssembly.BuildJobPlanes(image, palette, "auto", halftone: halftone);

        // 少なくとも特色一致分(red・black)は残る。例外が出ないことが本題。
        var names = result.Select(r => r.Ink.Name).ToHashSet();
        Assert.Contains("red", names);
    }

    [Fact]
    public void BuildJobPlanes_UnknownHalftone_Throws()
    {
        var palette = MakePalette();
        var image = MakeMixedImage();

        Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(image, palette, "auto", halftone: "bogus"));
    }
}
