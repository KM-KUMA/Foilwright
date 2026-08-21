// Foilwright.Core.Tests — CassetteCheck(§7.3 のカセット過不足判定 / D-026)の検証。
//
// 実機を要さない: CassetteStatus は DOMAIN §11.4 の実測 38 バイトをそのまま使う。
// パレットは default.yaml を実際に読み、11 インクすべてに barcode があることも
// あわせて検証する。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class CassetteCheckTests
{
    // DOMAIN §11.4「状態応答の構造を訂正 + エラー時の値」の正常例(完全な 38 バイト)。
    // ホルダ: slot[1]=シアン(0x03) slot[3]=イエロー(0x01) slot[6]=マゼンタ(0x02)。
    // ヘッド(slot[8])は未装着(0xff)。
    private static readonly byte[] NormalResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x03, 0x00, 0x00,
        0x01, 0x00, 0x00,
        0x00, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x02, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x00, 0x00, 0x00,
        0x00, 0x00, 0x03,
    };

    // 上と同じだが、ヘッド(slot[8])にブラック(0x00)が装着中。
    private static readonly byte[] NormalResponseWithHeadLoaded =
    {
        0x02, 0x80, 0x21, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x03, 0x00, 0x00,
        0x01, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x02, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x00, 0x00, 0x00, // slot[8] = head = 0x00 (紙用ブラック)
        0x00, 0x00, 0x00,
        0x00, 0x00, 0x03,
    };

    // DOMAIN §11.4 の給紙エラー例(完全な 38 バイト、状態バイト 0xc9)。
    private static readonly byte[] PrintingErrorResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0xc9,
        0xff, 0x00, 0x00,
        0x03, 0x31, 0x00,
        0x01, 0x8d, 0x02,
        0x00, 0x8d, 0x02,
        0xff, 0x00, 0x00,
        0x02, 0x3f, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x00, 0x10, 0x00,
        0x86, 0x00, 0x03,
    };

    private static InkDefinition MakeInk(string name, int? barcode) =>
        new()
        {
            Name = name,
            Label = name + "ラベル",
            PrinterCode = 0x99,
            Order = 10,
            Barcode = barcode,
        };

    [Fact]
    public void Evaluate_AllRequiredInksLoaded_IsSufficient()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        // 装填済み: 0x03(シアン) 0x01(イエロー) 0x00(ブラック) 0x02(マゼンタ)
        var required = new List<InkDefinition> { MakeInk("cyan", 0x03), MakeInk("yellow", 0x01) };

        var result = CassetteCheck.Evaluate(required, status);

        Assert.Equal(CassetteCheckStatus.Sufficient, result.Status);
        Assert.True(result.IsSufficient);
        Assert.Empty(result.MissingInks);
        Assert.Empty(result.UndeterminableInks);
    }

    [Fact]
    public void Evaluate_HeadCassetteCountsAsLoaded()
    {
        var status = CassetteStatus.Parse(NormalResponseWithHeadLoaded);
        var required = new List<InkDefinition> { MakeInk("black", 0x00) };

        var result = CassetteCheck.Evaluate(required, status);

        Assert.Equal(CassetteCheckStatus.Sufficient, result.Status);
    }

    [Fact]
    public void Evaluate_SomeInksMissing_ReturnsOnlyMissingOnesWithLabel()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        // 装填済み: 0x03(シアン) 0x01(イエロー) 0x00(ブラック) 0x02(マゼンタ)。
        // magenta_metallic(0x08) は未装着 -> 不足。
        var required = new List<InkDefinition>
        {
            MakeInk("cyan", 0x03),
            MakeInk("metallic_gold", 0x08),
        };

        var result = CassetteCheck.Evaluate(required, status);

        Assert.Equal(CassetteCheckStatus.Insufficient, result.Status);
        Assert.False(result.IsSufficient);
        Assert.Single(result.MissingInks);
        Assert.Equal("metallic_gold", result.MissingInks[0].Name);
        Assert.Equal("metallic_goldラベル", result.MissingInks[0].Label);
        Assert.Empty(result.UndeterminableInks);
    }

    [Fact]
    public void Evaluate_ErrorResponse_IsIndeterminate_NotInsufficient()
    {
        var status = CassetteStatus.Parse(PrintingErrorResponse);
        // このジョブに必要なインクが装填されていなくても、エラー中は
        // 「不足」と誤判定してはならず、判定不能を返すこと(D-026 補足)。
        var required = new List<InkDefinition> { MakeInk("cyan", 0x03), MakeInk("metallic_gold", 0x08) };

        var result = CassetteCheck.Evaluate(required, status);

        Assert.Equal(CassetteCheckStatus.Indeterminate, result.Status);
        Assert.False(result.IsSufficient);
        Assert.Empty(result.MissingInks);
        Assert.Empty(result.UndeterminableInks);
    }

    [Fact]
    public void Evaluate_InkWithoutBarcode_IsUndeterminable_NotMissing()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        var required = new List<InkDefinition> { MakeInk("thirdparty_spot", null) };

        var result = CassetteCheck.Evaluate(required, status);

        // barcode が無いインクは「不足」ではなく「判定不能」に区分される。
        Assert.Equal(CassetteCheckStatus.Sufficient, result.Status); // 判定できるインクは全て足りている
        Assert.Empty(result.MissingInks);
        Assert.Single(result.UndeterminableInks);
        Assert.Equal("thirdparty_spot", result.UndeterminableInks[0].Name);
    }

    [Fact]
    public void DefaultPalette_AllInksHaveBarcode()
    {
        string repoRoot = FindRepoRoot();
        var palette = ConfigLoader.LoadPalette(Path.Combine(repoRoot, "palette", "default.yaml"));

        // D-048 で coverage インク 2 色(mf_ink / glossy_finish)が加わって 11 になった。
        Assert.Equal(11, palette.Count);
        foreach (var ink in palette)
        {
            Assert.True(ink.Barcode.HasValue, $"ink '{ink.Name}' is missing 'barcode'");
        }

        // D-026 の対応表どおりの値であること(§13.7.5)。
        var byName = palette.ToDictionary(i => i.Name, i => i.Barcode!.Value);
        Assert.Equal(16, byName["white"]);
        Assert.Equal(8, byName["metallic_gold"]);
        Assert.Equal(9, byName["metallic_magenta"]);
        Assert.Equal(10, byName["metallic_cyan"]);
        Assert.Equal(11, byName["metallic_silver"]);
        Assert.Equal(0, byName["black"]);
        Assert.Equal(1, byName["yellow"]);
        Assert.Equal(2, byName["magenta"]);
        Assert.Equal(3, byName["cyan"]);
        // D-048 / DOMAIN §14.7: vendor/ppmtomd-1.6/mddata.h:75-76 の
        // barVPhotoPrimer = 18 / barFinishII = 19。
        Assert.Equal(18, byName["mf_ink"]);
        Assert.Equal(19, byName["glossy_finish"]);
    }

    // GoldenTests.cs と同じ規則(実行アセンブリの場所からリポジトリ直下を探す)。
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("could not locate repo root (CLAUDE.md not found)");
        }
        return dir.FullName;
    }
}
