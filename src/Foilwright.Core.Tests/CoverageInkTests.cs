// Foilwright.Core.Tests — 「塗る範囲で決まるインク」(D-048 / DOMAIN §14.7)の検証。
//
// 対象は 2 層:
//   - ConfigLoader.LoadPalette の緩めた規則。インクは magic_rgb / channel /
//     coverage のいずれかを持てばよい。coverage と他の 2 つの併用は
//     組み合わせの意味を決めていないため、はっきりエラーにする。
//   - JobAssembly.BuildJobPlanes の coverageModes 引数。
//     "none"(既定)/ "artwork"(純白でない画素)/ "full"(全画素)。
//
// このファイルで最も重要なのは NoCoverageModes_BuildsNoCoveragePlane —
// 「使わなければ何も変わらない」(D-048 決定 3)の検出器。
//
// 末尾の 2 件は ref/ と src/ を同じバイトに縛る(D-006)。突き合わせに使う
// build-rgl(D-033)には coverage を渡す経路が無いため、代わりに両言語が
// 同じ実物フィクスチャからプレーンを作ってハッシュを突き合わせる。
// ref/tests/test_coverage_ink.py に同じ 16 進定数が書いてあり、
// どちらか一方だけが変われば片側が赤くなる。
//
// 実機を要さない: PpmImage を直接合成するか tests/cases/ のフィクスチャを読む。

using System.Security.Cryptography;
using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class CoverageInkTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string PaletteDir = Path.Combine(RepoRoot, "palette");
    private static readonly string CasesDir = Path.Combine(RepoRoot, "tests", "cases");

    // tests/cases/c8_cmyk4_598x1208.ppm を使う理由: 598 は 8 の倍数ではない。
    // 行末の余りビット(width 以降)が 0 のままであることは、幅が 8 で割り切れる
    // フィクスチャでは検出できない。
    private const string CrossLanguageFixture = "c8_cmyk4_598x1208.ppm";
    private const string CrossLanguageArtworkSha256 =
        "dcb5f662930681ae74fcdac25e4bccf7c22d91f4f54412589817ecb9e0c444c5";
    private const string CrossLanguageFullSha256 =
        "f8fcbec82d4fd3e5c676ad0500e1c5c38c6e5e5f505c0b1f1c529ea4fdea2e16";

    // GoldenTests.cs / CassetteCheckTests.cs と同じ規則。
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("リポジトリ直下(CLAUDE.md のある場所)が見つからない");
        }
        return dir.FullName;
    }

    private static string WritePalette(string body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fw-palette-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, body);
        return path;
    }

    private static List<InkDefinition> DefaultPalette() =>
        ConfigLoader.LoadPalette(Path.Combine(PaletteDir, "default.yaml"));

    private static PpmImage FillImage(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 3];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 3] = r;
            pixels[i * 3 + 1] = g;
            pixels[i * 3 + 2] = b;
        }
        return new PpmImage(width, height, pixels);
    }

    /// <summary>左半分が純黒(既定パレットの black に厳密一致)、右半分が純白。
    /// これでジョブが空にならないので、coverage インクの有無だけを観測できる。</summary>
    private static PpmImage ArtworkImage(int width = 4, int height = 2)
    {
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 3;
                byte v = x < width / 2 ? (byte)0 : (byte)255;
                pixels[idx] = v;
                pixels[idx + 1] = v;
                pixels[idx + 2] = v;
            }
        }
        return new PpmImage(width, height, pixels);
    }

    private static int Bit(byte[] plane, int rowBytes, int x, int y) =>
        (plane[y * rowBytes + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 1 : 0;

    private static int PopCount(byte[] plane)
    {
        int total = 0;
        foreach (byte b in plane)
        {
            total += System.Numerics.BitOperations.PopCount(b);
        }
        return total;
    }

    private static byte[]? PlaneOf(List<(InkDefinition Ink, byte[] Plane)> result, string name)
    {
        foreach (var (ink, plane) in result)
        {
            if (ink.Name == name)
            {
                return plane;
            }
        }
        return null;
    }

    // --- ConfigLoader: 緩めたインク種別の規則(D-048 決定 1) -------------------

    [Fact]
    public void CoverageOnlyInk_IsAccepted()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: glossy\n" +
            "    label: gloss\n" +
            "    printer_code: 0x0E\n" +
            "    order: 95\n" +
            "    coverage: true\n");
        try
        {
            var inks = ConfigLoader.LoadPalette(path);
            Assert.Single(inks);
            Assert.True(inks[0].Coverage);
            Assert.Null(inks[0].MagicRgb);
            Assert.Null(inks[0].Channel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Coverage_DefaultsToFalse()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: black\n" +
            "    label: k\n" +
            "    printer_code: 0x00\n" +
            "    order: 90\n" +
            "    magic_rgb: [0, 0, 0]\n" +
            "    tolerance: 8\n");
        try
        {
            var inks = ConfigLoader.LoadPalette(path);
            Assert.False(inks[0].Coverage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CoverageWithMagicRgb_ThrowsConfigException()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: glossy\n" +
            "    label: gloss\n" +
            "    printer_code: 0x0E\n" +
            "    order: 95\n" +
            "    coverage: true\n" +
            "    magic_rgb: [1, 2, 3]\n" +
            "    tolerance: 8\n");
        try
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadPalette(path));
            Assert.Contains("cannot be combined", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CoverageWithChannel_ThrowsConfigException()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: glossy\n" +
            "    label: gloss\n" +
            "    printer_code: 0x0E\n" +
            "    order: 95\n" +
            "    coverage: true\n" +
            "    channel: K\n");
        try
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadPalette(path));
            Assert.Contains("cannot be combined", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InkWithNoneOfTheThree_StillThrowsConfigException()
    {
        // D-019 の規則は緩めたのであって撤廃したのではない。
        string path = WritePalette(
            "inks:\n" +
            "  - name: nothing\n" +
            "    label: n\n" +
            "    printer_code: 0x00\n" +
            "    order: 10\n");
        try
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadPalette(path));
            Assert.Contains("must have 'magic_rgb'", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CoverageFalseInkWithNoneOfTheThree_ThrowsConfigException()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: nothing\n" +
            "    label: n\n" +
            "    printer_code: 0x00\n" +
            "    order: 10\n" +
            "    coverage: false\n");
        try
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadPalette(path));
            Assert.Contains("must have 'magic_rgb'", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CoverageMustBeBoolean()
    {
        string path = WritePalette(
            "inks:\n" +
            "  - name: glossy\n" +
            "    label: gloss\n" +
            "    printer_code: 0x0E\n" +
            "    order: 95\n" +
            "    coverage: yes please\n");
        try
        {
            var ex = Assert.Throws<ConfigException>(() => ConfigLoader.LoadPalette(path));
            Assert.Contains("must be true or false", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DefaultPalette_CarriesTheTwoCoverageInks()
    {
        var byName = DefaultPalette().ToDictionary(ink => ink.Name, ink => ink);

        var mf = byName["mf_ink"];
        Assert.True(mf.Coverage);
        Assert.Equal(0x10, mf.PrinterCode);
        Assert.Equal(18, mf.Barcode);
        Assert.Equal(5, mf.Order);
        Assert.Equal(1, mf.Passes);
        Assert.Null(mf.MagicRgb);
        Assert.Null(mf.Channel);

        var glossy = byName["glossy_finish"];
        Assert.True(glossy.Coverage);
        Assert.Equal(0x0E, glossy.PrinterCode);
        Assert.Equal(19, glossy.Barcode);
        Assert.Equal(95, glossy.Order);

        // 既存 9 色は触っていない。**塗る範囲で決まるインクだけが coverage** で
        // あること — D-050 で 5 層構成用の重複項目が増えたので、名前で数え上げる
        // のではなく「9 色の側」を列挙して確かめる(インクが増えても、
        // **既存 9 色が巻き込まれていないこと**だけを見張れば足りる)。
        string[] originalNine =
        {
            "white", "metallic_gold", "metallic_silver", "metallic_magenta",
            "metallic_cyan", "cyan", "magenta", "yellow", "black",
        };
        foreach (string name in originalNine)
        {
            Assert.False(byName[name].Coverage, name);
        }

        // D-050: 5 層構成用の項目も coverage であること(既定で無効になり、
        // 塗る範囲を選ぶまでプレーンが作られない)。
        foreach (string name in new[] { "mf_ink_under_gloss", "glossy_mid", "mf_ink_under_white", "white_over" })
        {
            Assert.True(byName[name].Coverage, name);
        }

        // 同じカセットを違う位置で使うので、printer_code と barcode は重複する。
        Assert.Equal(0x10, byName["mf_ink_under_gloss"].PrinterCode);
        Assert.Equal(0x10, byName["mf_ink_under_white"].PrinterCode);
        Assert.Equal(0x0B, byName["white_over"].PrinterCode);
        Assert.Equal(0x0E, byName["glossy_mid"].PrinterCode);

        // 層の順序: 黒(90) → 下地(91) → 光沢(92) → 下地(93) → 白(94)。
        // **ここを間違えると下地が絵の上に来る。**
        Assert.Equal(90, byName["black"].Order);
        Assert.Equal(91, byName["mf_ink_under_gloss"].Order);
        Assert.Equal(92, byName["glossy_mid"].Order);
        Assert.Equal(93, byName["mf_ink_under_white"].Order);
        Assert.Equal(94, byName["white_over"].Order);
    }

    // --- BuildJobPlanes: coverageModes(D-048 決定 2/3) -----------------------

    [Fact]
    public void NoCoverageModes_BuildsNoCoveragePlane()
    {
        // 「使わなければ何も変わらない」の検出器(D-048 決定 3)。
        var result = JobAssembly.BuildJobPlanes(
            ArtworkImage(), DefaultPalette(), "spot_only", whiteMode: "none");

        Assert.Equal(new[] { "black" }, result.Select(r => r.Ink.Name).ToArray());
    }

    [Fact]
    public void ExplicitNone_BuildsNoCoveragePlane()
    {
        var result = JobAssembly.BuildJobPlanes(
            ArtworkImage(), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "none", ["glossy_finish"] = "none" });

        Assert.Equal(new[] { "black" }, result.Select(r => r.Ink.Name).ToArray());
    }

    [Fact]
    public void Artwork_SetsEveryNonPureWhitePixel()
    {
        var result = JobAssembly.BuildJobPlanes(
            ArtworkImage(4, 2), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "artwork" });

        byte[] plane = PlaneOf(result, "glossy_finish")!;
        Assert.NotNull(plane);
        for (int y = 0; y < 2; y++)
        {
            Assert.Equal(1, Bit(plane, 1, 0, y));
            Assert.Equal(1, Bit(plane, 1, 1, y));
            Assert.Equal(0, Bit(plane, 1, 2, y));
            Assert.Equal(0, Bit(plane, 1, 3, y));
        }
    }

    [Fact]
    public void ArtworkOnPureWhiteImage_LeavesTheInkOutOfTheJob()
    {
        // 空プレーンはパスとカセットを無駄にするので落とす(他インクと同じ規則)。
        var result = JobAssembly.BuildJobPlanes(
            FillImage(8, 2, 255, 255, 255), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "artwork" });

        Assert.Empty(result);
    }

    [Fact]
    public void Full_SetsEveryPixel()
    {
        var result = JobAssembly.BuildJobPlanes(
            FillImage(8, 3, 255, 255, 255), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "full" });

        byte[] plane = PlaneOf(result, "mf_ink")!;
        // "full" は白紙でも必ず中身がある("artwork" と違う点)。
        Assert.Equal(8 * 3, PopCount(plane));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, plane);
    }

    [Fact]
    public void Full_LeavesRowPaddingBitsClear()
    {
        // 行は ceil(width/8) バイト。width 以降のビットを立てると ref/ と
        // 出力バイトが食い違う。
        var result = JobAssembly.BuildJobPlanes(
            FillImage(5, 2, 255, 255, 255), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "full" });

        Assert.Equal(new byte[] { 0xF8, 0xF8 }, PlaneOf(result, "mf_ink")!);
    }

    [Fact]
    public void CoverageModes_DoNotTouchNonCoverageInks()
    {
        // coverage を持たないインクを指す項目は無視する(D-048)。black に
        // "full" を頼んでも、通常どおり magic_rgb 一致分(左半分)のまま。
        var result = JobAssembly.BuildJobPlanes(
            ArtworkImage(4, 2), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["black"] = "full" });

        Assert.Equal(4, PopCount(PlaneOf(result, "black")!));
    }

    [Fact]
    public void UnknownCoverageMode_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(
            ArtworkImage(), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "everywhere" }));
        Assert.Contains("unknown coverage mode", ex.Message);
    }

    [Fact]
    public void UnknownCoverageMode_IsNotSilentlyDowngradedToNone()
    {
        // 打ち間違いが黙って「何もしない」になると、リボンを無駄にするまで気づけない。
        Assert.Throws<ArgumentException>(() => JobAssembly.BuildJobPlanes(
            ArtworkImage(), DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "Artwork" }));
    }

    [Fact]
    public void CoverageInks_LandAtTheirPaletteOrder()
    {
        // D-048 決定 5 / 刷り重ねの層の順序の検出器: MF インク(order 5)は
        // 白(10)より前、光沢仕上げ2(95)は黒(90)より後。
        byte[] pixels =
        {
            230, 230, 230,
            0, 0, 0,
            255, 255, 255,
            255, 255, 255,
        };
        var result = JobAssembly.BuildJobPlanes(
            new PpmImage(4, 1, pixels), DefaultPalette(), "spot_only", whiteMode: "magic",
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "full", ["glossy_finish"] = "full" });

        Assert.Equal(
            new[] { "mf_ink", "white", "black", "glossy_finish" },
            result.Select(r => r.Ink.Name).ToArray());
    }

    [Fact]
    public void Artwork_IsNotHalftoned()
    {
        // D-048 決定 4 / ppmtomd man:564-565: オンかオフだけで、網点にしない。
        // 中間調のベタはハーフトーンが掛かれば網点になるので、全画素のビットが
        // 立っていることがそのまま「掛かっていない」証拠になる。
        int width = 16, height = 4;
        var result = JobAssembly.BuildJobPlanes(
            FillImage(width, height, 128, 128, 128), DefaultPalette(), "auto",
            halftone: "coarse_halftone", whiteMode: "none", colourCorrection: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "artwork" });

        Assert.Equal(width * height, PopCount(PlaneOf(result, "glossy_finish")!));

        // 同じジョブの色の側は実際に網点になっている(上の判定が「この画像では
        // ハーフトーンが何もしなかった」で通っているのではないことの確認)。
        bool anyScreened = result.Any(r =>
            r.Ink.Name != "glossy_finish" && PopCount(r.Plane) > 0 && PopCount(r.Plane) < width * height);
        Assert.True(anyScreened, "expected at least one halftoned colour plane");
    }

    [Fact]
    public void Full_IsNotColourCorrected()
    {
        int width = 16, height = 4;
        var result = JobAssembly.BuildJobPlanes(
            FillImage(width, height, 128, 128, 128), DefaultPalette(), "auto",
            whiteMode: "none", colourCorrection: "photo",
            resolution: 600, photoLutPath: Path.Combine(RepoRoot, "colour", "photo_colcor.bin"),
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "full" });

        Assert.Equal(width * height, PopCount(PlaneOf(result, "mf_ink")!));
    }

    // --- ref/ との突き合わせ(D-006)。定数は test_coverage_ink.py と同一 -------

    [Fact]
    public void CrossLanguage_ArtworkPlaneHash()
    {
        var image = PpmImage.Read(Path.Combine(CasesDir, CrossLanguageFixture));
        var result = JobAssembly.BuildJobPlanes(
            image, DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["glossy_finish"] = "artwork" });

        Assert.Equal(CrossLanguageArtworkSha256, Sha256Hex(PlaneOf(result, "glossy_finish")!));
    }

    [Fact]
    public void CrossLanguage_FullPlaneHash()
    {
        var image = PpmImage.Read(Path.Combine(CasesDir, CrossLanguageFixture));
        var result = JobAssembly.BuildJobPlanes(
            image, DefaultPalette(), "spot_only", whiteMode: "none",
            coverageModes: new Dictionary<string, string> { ["mf_ink"] = "full" });

        Assert.Equal(CrossLanguageFullSha256, Sha256Hex(PlaneOf(result, "mf_ink")!));
    }

    private static string Sha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
