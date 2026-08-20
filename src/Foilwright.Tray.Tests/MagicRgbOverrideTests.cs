// Foilwright.Tray.Tests — マジックカラーのジョブごとの上書き(D-042)の単体テスト。
//
// 対象は 2 つ:
//   TraySettings.ApplyMagicRgbOverride — パレットに上書きを適用した「照合用パレット」
//   Program.ParseMagicRgbArg           — `--magic-rgb ink=#RRGGBB,ink=none` の解析
//
// どちらも UI もプリンタも Ghostscript も要らない純粋な処理であり、
// ここで壊れを検出できるようにしておく(写し漏れの検出器も兼ねる)。

using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class MagicRgbOverrideTests
{
    /// <summary>テスト用のパレット。palette/default.yaml の値を写したもの
    /// (白 tolerance=8、金 tolerance=12、シアンは tolerance を持たない)。
    /// ファイルに依存させないため、ここで組み立てる。</summary>
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
            Name = "cyan",
            Label = "紙用シアン (MDC-FLCC)",
            PrinterCode = 0x01,
            Order = 60,
            Channel = "C",
            Barcode = 3,
            Passes = 2,
        },
    };

    private static InkDefinition Find(IEnumerable<InkDefinition> palette, string name) =>
        palette.Single(ink => ink.Name == name);

    [Fact]
    public void NullOverride_ReturnsPaletteUnchanged()
    {
        var palette = BuildPalette();
        var result = TraySettings.ApplyMagicRgbOverride(palette, null);

        Assert.Equal(palette.Count, result.Count);
        for (int i = 0; i < palette.Count; i++)
        {
            // 上書きが無いインクはインスタンスごとそのまま返る。
            Assert.Same(palette[i], result[i]);
        }
    }

    [Fact]
    public void EmptyOverride_ReturnsPaletteUnchanged()
    {
        var palette = BuildPalette();
        var result = TraySettings.ApplyMagicRgbOverride(palette, new Dictionary<string, int[]?>());

        Assert.Equal(palette.Count, result.Count);
        for (int i = 0; i < palette.Count; i++)
        {
            Assert.Same(palette[i], result[i]);
        }
    }

    [Fact]
    public void InstanceMethod_UsesMagicRgbOverrideProperty()
    {
        var palette = BuildPalette();
        var settings = new TraySettings
        {
            MagicRgbOverride = new Dictionary<string, int[]?> { ["white"] = new[] { 0, 0, 0 } },
        };

        var result = settings.ApplyMagicRgbOverride(palette);

        Assert.Equal(new[] { 0, 0, 0 }, Find(result, "white").MagicRgb);
    }

    [Fact]
    public void OverrideColour_ChangesMagicRgbButKeepsTolerance()
    {
        var palette = BuildPalette();
        var overrides = new Dictionary<string, int[]?>
        {
            // D-042: 純正の「単色モード」相当 — 原稿のベタ黒を白インクで刷る。
            ["white"] = new[] { 0, 0, 0 },
            ["metallic_gold"] = new[] { 255, 0, 0 },
        };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);

        var white = Find(result, "white");
        Assert.Equal(new[] { 0, 0, 0 }, white.MagicRgb);
        // D-042 決定 2: 許容誤差は上書きしない。パレットの値(白=8)のまま。
        Assert.Equal(8, white.Tolerance);

        var gold = Find(result, "metallic_gold");
        Assert.Equal(new[] { 255, 0, 0 }, gold.MagicRgb);
        Assert.Equal(12, gold.Tolerance);
    }

    [Fact]
    public void OverrideColour_OnInkWithoutTolerance_UsesDefaultOverrideTolerance()
    {
        var palette = BuildPalette();
        var overrides = new Dictionary<string, int[]?> { ["cyan"] = new[] { 10, 20, 30 } };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);

        var cyan = Find(result, "cyan");
        Assert.Equal(new[] { 10, 20, 30 }, cyan.MagicRgb);
        // D-042 決定 3: プロセスインクにも色を割り当てられる。tolerance を持たない
        // インクには既定値(白・黒と同じ 8)を使う。
        Assert.Equal(TraySettings.DefaultOverrideTolerance, cyan.Tolerance);
        Assert.Equal(8, TraySettings.DefaultOverrideTolerance);
    }

    [Fact]
    public void OverrideWithNull_ClearsMagicRgbAndTolerance()
    {
        var palette = BuildPalette();
        var overrides = new Dictionary<string, int[]?> { ["white"] = null };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);

        var white = Find(result, "white");
        Assert.Null(white.MagicRgb);
        Assert.Null(white.Tolerance);
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]           // 2 要素
    [InlineData(new[] { 0, 0, 256 })]      // 範囲外(上)
    [InlineData(new[] { -1, 0, 0 })]       // 範囲外(下)
    public void InvalidRgb_ThrowsConfigException(int[] rgb)
    {
        var palette = BuildPalette();
        var overrides = new Dictionary<string, int[]?> { ["white"] = rgb };

        var ex = Assert.Throws<ConfigException>(
            () => TraySettings.ApplyMagicRgbOverride(palette, overrides));
        Assert.Contains("white", ex.Message);
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]
    [InlineData(new[] { 0, 0, 256 })]
    [InlineData(new[] { -1, 0, 0 })]
    public void IsValidMagicRgb_RejectsInvalidValues(int[] rgb)
    {
        Assert.False(TraySettings.IsValidMagicRgb(rgb));
    }

    [Fact]
    public void IsValidMagicRgb_AcceptsNullAndBounds()
    {
        // null は「色なし」を表す正当な値。
        Assert.True(TraySettings.IsValidMagicRgb(null));
        Assert.True(TraySettings.IsValidMagicRgb(new[] { 0, 0, 0 }));
        Assert.True(TraySettings.IsValidMagicRgb(new[] { 255, 255, 255 }));
    }

    [Fact]
    public void ApplyMagicRgbOverride_DoesNotMutateOriginalPalette()
    {
        var palette = BuildPalette();
        var whiteBefore = Find(palette, "white");
        var overrides = new Dictionary<string, int[]?>
        {
            ["white"] = new[] { 0, 0, 0 },
            ["cyan"] = null,
        };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);

        // 引数のリストも、その中の InkDefinition も変わっていないこと。
        Assert.Equal(3, palette.Count);
        Assert.Same(whiteBefore, Find(palette, "white"));
        Assert.Equal(new[] { 230, 230, 230 }, Find(palette, "white").MagicRgb);
        Assert.Equal(8, Find(palette, "white").Tolerance);
        Assert.Null(Find(palette, "cyan").MagicRgb);
        // 上書き後は別インスタンスになっている。
        Assert.NotSame(whiteBefore, Find(result, "white"));
    }

    [Fact]
    public void ApplyMagicRgbOverride_MutatingReturnedRgbDoesNotAffectOverrideDictionary()
    {
        var palette = BuildPalette();
        int[] rgb = { 1, 2, 3 };
        var overrides = new Dictionary<string, int[]?> { ["white"] = rgb };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);
        Find(result, "white").MagicRgb![0] = 99;

        // 呼び出し側が渡した配列を共有していないこと(片方を触るともう片方が
        // 黙って変わる、という事故を防ぐ)。
        Assert.Equal(new[] { 1, 2, 3 }, rgb);
    }

    /// <summary>写し漏れの検出器。InkDefinition は sealed + init プロパティのみで
    /// `with` が使えず、上書き時に全プロパティを手で写している。1 つでも写し忘れると
    /// 送出に使う printer_code や過不足判定の barcode が黙って消えるため、
    /// Name 以外のすべてをここで突き合わせる。</summary>
    [Fact]
    public void ApplyMagicRgbOverride_PreservesAllOtherProperties()
    {
        var palette = BuildPalette();
        var overrides = new Dictionary<string, int[]?>
        {
            ["white"] = new[] { 0, 0, 0 },
            ["metallic_gold"] = null,
            ["cyan"] = new[] { 10, 20, 30 },
        };

        var result = TraySettings.ApplyMagicRgbOverride(palette, overrides);

        foreach (var before in palette)
        {
            var after = Find(result, before.Name);
            Assert.Equal(before.Label, after.Label);
            Assert.Equal(before.PrinterCode, after.PrinterCode);
            Assert.Equal(before.Order, after.Order);
            Assert.Equal(before.Channel, after.Channel);
            Assert.Equal(before.Barcode, after.Barcode);
            Assert.Equal(before.AutoUndercoat, after.AutoUndercoat);
            Assert.Equal(before.Passes, after.Passes);
        }
    }

    [Fact]
    public void UnknownInkName_IsIgnoredButStillValidated()
    {
        var palette = BuildPalette();

        // パレットに無いインク名の項目は結果に現れない(増やさない)。
        var result = TraySettings.ApplyMagicRgbOverride(
            palette, new Dictionary<string, int[]?> { ["no_such_ink"] = new[] { 1, 2, 3 } });
        Assert.Equal(3, result.Count);

        // ただし値の妥当性は名前に関わらず確認する(綴り間違いを見逃さない)。
        Assert.Throws<ConfigException>(() => TraySettings.ApplyMagicRgbOverride(
            palette, new Dictionary<string, int[]?> { ["no_such_ink"] = new[] { 1, 2 } }));
    }

    // --- Program.ParseMagicRgbArg -------------------------------------------

    [Fact]
    public void ParseMagicRgbArg_ParsesColourAndNone()
    {
        var result = Program.ParseMagicRgbArg("white=#000000,metallic_gold=none");

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { 0, 0, 0 }, result["white"]);
        Assert.True(result.ContainsKey("metallic_gold"));
        Assert.Null(result["metallic_gold"]);
    }

    [Fact]
    public void ParseMagicRgbArg_AcceptsHexWithoutHashAndIsCaseInsensitive()
    {
        var result = Program.ParseMagicRgbArg("white=E1A000,cyan=NONE");

        Assert.Equal(new[] { 0xE1, 0xA0, 0x00 }, result["white"]);
        Assert.Null(result["cyan"]);
    }

    [Fact]
    public void ParseMagicRgbArg_EmptyArgumentGivesEmptyDictionary()
    {
        Assert.Empty(Program.ParseMagicRgbArg(string.Empty));
    }

    [Theory]
    [InlineData("white")]            // = が無い
    [InlineData("=#000000")]         // インク名が無い
    [InlineData("white=#00000")]     // 桁が足りない
    [InlineData("white=#0000000")]   // 桁が多い
    [InlineData("white=#00zz00")]    // 16 進でない
    [InlineData("white=black")]      // 色名は受け付けない
    public void ParseMagicRgbArg_RejectsInvalidFormats(string arg)
    {
        Assert.Throws<ConfigException>(() => Program.ParseMagicRgbArg(arg));
    }

    // --- Program.RejectUnknownInkNames ---------------------------------------
    //
    // 綴り間違いは黙って無視されると「指定したのに何も変わらない」という
    // 追いにくい形になる(実際に `--magic-rgb whte=#000000` が無反応で通った)。
    // ここが検出器になる。

    [Fact]
    public void RejectUnknownInkNames_AcceptsNamesPresentInPalette()
    {
        var overrides = new Dictionary<string, int[]?> { ["white"] = new[] { 0, 0, 0 }, ["cyan"] = null };
        Program.RejectUnknownInkNames(overrides, BuildPalette());
    }

    [Fact]
    public void RejectUnknownInkNames_ThrowsOnMisspelledName()
    {
        var overrides = new Dictionary<string, int[]?> { ["whte"] = new[] { 0, 0, 0 } };
        var ex = Assert.Throws<ConfigException>(() => Program.RejectUnknownInkNames(overrides, BuildPalette()));
        // 打ち間違えた名前と、使える名前の一覧の両方が出ること(直せるようにするため)。
        Assert.Contains("whte", ex.Message);
        Assert.Contains("white", ex.Message);
    }

    [Fact]
    public void RejectUnknownInkNames_IsCaseSensitive()
    {
        // インク名はパレットの表記のまま照合する(大文字小文字を吸収しない)。
        var overrides = new Dictionary<string, int[]?> { ["White"] = new[] { 0, 0, 0 } };
        Assert.Throws<ConfigException>(() => Program.RejectUnknownInkNames(overrides, BuildPalette()));
    }

    [Fact]
    public void RejectUnknownInkNames_EmptyOverridesIsAccepted()
    {
        Program.RejectUnknownInkNames(new Dictionary<string, int[]?>(), BuildPalette());
    }
}
