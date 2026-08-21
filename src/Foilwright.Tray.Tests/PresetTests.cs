// Foilwright.Tray.Tests — 設定のプリセット(名前を付けて保存し、呼び出す)の単体テスト。
//
// 対象は 2 つ:
//   PresetStore.IsValidPresetName / Upsert / Remove — 名前と一覧の扱い(純粋な処理)
//   SettingsPreset の JSON 往復                     — 写し漏れの検出器
//
// 往復のテストが検出器になる理由: UsedInks / PassesOverride / MagicRgbOverride /
// CoverageModes は **null と空を区別する**ことに意味がある(D-030 / D-031 / D-042 /
// D-048)。プロパティを
// 足したのに写し忘れる・区別を潰す、といった壊れ方はここで赤くなる。
//
// UI もプリンタも Ghostscript も要らない。

using System.Text.Json;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class PresetTests
{
    private static SettingsPreset Preset(string name) => new() { Name = name };

    // --- IsValidPresetName ---------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void InvalidNamesAreRejected(string? name)
    {
        Assert.False(PresetStore.IsValidPresetName(name));
    }

    [Fact]
    public void OrdinaryNameIsAccepted()
    {
        Assert.True(PresetStore.IsValidPresetName("目デカール(フィルム)"));
    }

    /// <summary>ちょうど上限は可、1 文字超えたら不可(境界を両側から押さえる)。</summary>
    [Fact]
    public void NameLengthLimitIsEnforcedAtTheBoundary()
    {
        string atLimit = new('あ', PresetStore.MaxPresetNameLength);
        string overLimit = new('あ', PresetStore.MaxPresetNameLength + 1);

        Assert.True(PresetStore.IsValidPresetName(atLimit));
        Assert.False(PresetStore.IsValidPresetName(overLimit));
    }

    // --- Upsert --------------------------------------------------------------

    [Fact]
    public void UpsertAddsANewPreset()
    {
        var result = PresetStore.Upsert(new List<SettingsPreset>(), Preset("はがきテスト"));

        Assert.Equal(new[] { "はがきテスト" }, result.Select(p => p.Name));
    }

    /// <summary>同じ名前は 1 つに潰す(2 件にならない)。</summary>
    [Fact]
    public void UpsertReplacesTheSameName()
    {
        var original = new List<SettingsPreset> { Preset("はがきテスト"), Preset("目デカール") };
        var replacement = new SettingsPreset { Name = "はがきテスト", Halftone = "coarse" };

        var result = PresetStore.Upsert(original, replacement);

        Assert.Equal(2, result.Count);
        var hagaki = Assert.Single(result, p => p.Name == "はがきテスト");
        Assert.Equal("coarse", hagaki.Halftone);
    }

    /// <summary>利用者から見て「Decal」と「decal」は同じもの。大文字小文字で
    /// 2 件に増えてはならない。</summary>
    [Fact]
    public void UpsertComparesNamesCaseInsensitively()
    {
        var original = new List<SettingsPreset> { Preset("Decal") };

        var result = PresetStore.Upsert(original, new SettingsPreset { Name = "decal", Halftone = "coarse" });

        var only = Assert.Single(result);
        Assert.Equal("decal", only.Name);
        Assert.Equal("coarse", only.Halftone);
    }

    /// <summary>引数の不変性。元のリストは変えない(呼び出し側は保存に成功して
    /// から新しい一覧へ差し替える作りになっている)。</summary>
    [Fact]
    public void UpsertDoesNotMutateTheInputList()
    {
        var original = new List<SettingsPreset> { Preset("はがきテスト") };

        PresetStore.Upsert(original, Preset("目デカール"));
        PresetStore.Upsert(original, new SettingsPreset { Name = "はがきテスト", Halftone = "coarse" });

        var only = Assert.Single(original);
        Assert.Equal("はがきテスト", only.Name);
        Assert.Equal(Preset("参照用").Halftone, only.Halftone);
    }

    [Fact]
    public void UpsertReturnsPresetsSortedByName()
    {
        var original = new List<SettingsPreset> { Preset("cc"), Preset("aa") };

        var result = PresetStore.Upsert(original, Preset("bb"));

        Assert.Equal(new[] { "aa", "bb", "cc" }, result.Select(p => p.Name));
    }

    // --- Remove --------------------------------------------------------------

    [Fact]
    public void RemoveTakesOutOnlyTheNamedPreset()
    {
        var original = new List<SettingsPreset> { Preset("aa"), Preset("bb"), Preset("cc") };

        var result = PresetStore.Remove(original, "bb");

        Assert.Equal(new[] { "aa", "cc" }, result.Select(p => p.Name));
        Assert.Equal(3, original.Count);
    }

    [Fact]
    public void RemoveIsCaseInsensitive()
    {
        var original = new List<SettingsPreset> { Preset("Decal") };

        Assert.Empty(PresetStore.Remove(original, "decal"));
    }

    /// <summary>無い名前を消しても落ちない(内容は同じまま)。</summary>
    [Fact]
    public void RemoveOfAnUnknownNameKeepsEverything()
    {
        var original = new List<SettingsPreset> { Preset("aa"), Preset("bb") };

        var result = PresetStore.Remove(original, "存在しない名前");

        Assert.Equal(new[] { "aa", "bb" }, result.Select(p => p.Name));
    }

    // --- JSON の往復 ---------------------------------------------------------

    /// <summary>すべてのプロパティが JSON を通っても保たれること。
    /// **null と空を区別したまま**往復することを、4 つの上書き項目それぞれで確かめる
    /// (D-030 / D-031 / D-042 / D-048。ここが写し漏れの検出器)。</summary>
    [Fact]
    public void PresetSurvivesAJsonRoundTrip()
    {
        var original = new SettingsPreset
        {
            Name = "目デカール(フィルム)",
            Machine = "md-5500",
            InkMode = "spot_only",
            ResolutionKey = "1200x600",
            PaperName = "hagaki",
            MediaName = "film",
            Halftone = "coarse",
            WhiteMode = "opaque",
            ColourCorrection = "plain",
            NoCurlCorrection = true,
            UsedInks = new HashSet<string> { "white", "black" },
            PassesOverride = new Dictionary<string, int> { ["white"] = 4 },
            MagicRgbOverride = new Dictionary<string, int[]?>
            {
                ["white"] = new[] { 0, 0, 0 },
                // D-042: 値の null は「そのインクの色を明示的に外す」。
                ["gold"] = null,
            },
            // D-048: 塗る範囲。プリセットに入っていないと「光沢仕上げ付きデカール」を
            // 保存できない(用途ごとの設定一式にならない)。
            CoverageModes = new Dictionary<string, string>
            {
                ["glossy_finish"] = "artwork",
                ["mf_ink"] = "full",
            },
        };

        var restored = RoundTrip(original);

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Machine, restored.Machine);
        Assert.Equal(original.InkMode, restored.InkMode);
        Assert.Equal(original.ResolutionKey, restored.ResolutionKey);
        Assert.Equal(original.PaperName, restored.PaperName);
        Assert.Equal(original.MediaName, restored.MediaName);
        Assert.Equal(original.Halftone, restored.Halftone);
        Assert.Equal(original.WhiteMode, restored.WhiteMode);
        Assert.Equal(original.ColourCorrection, restored.ColourCorrection);
        Assert.True(restored.NoCurlCorrection);
        Assert.Equal(new[] { "black", "white" }, restored.UsedInks!.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(4, restored.PassesOverride!["white"]);
        Assert.Equal(new[] { 0, 0, 0 }, restored.MagicRgbOverride!["white"]);
        Assert.True(restored.MagicRgbOverride.ContainsKey("gold"));
        Assert.Null(restored.MagicRgbOverride["gold"]);
        Assert.Equal("artwork", restored.CoverageModes!["glossy_finish"]);
        Assert.Equal("full", restored.CoverageModes["mf_ink"]);
    }

    /// <summary>null(一度も触っていない)は往復しても null のまま。
    /// 空に化けると「全インクを無効にした」「上書き無しを明示した」に意味が
    /// すり替わる(D-030 / D-031 / D-042 / D-048)。</summary>
    [Fact]
    public void NullOverridesStayNullThroughJson()
    {
        var restored = RoundTrip(new SettingsPreset { Name = "既定のまま" });

        Assert.Null(restored.UsedInks);
        Assert.Null(restored.PassesOverride);
        Assert.Null(restored.MagicRgbOverride);
        Assert.Null(restored.CoverageModes);
    }

    /// <summary>空(明示的に空にした)は往復しても空のまま。null に化けないこと。</summary>
    [Fact]
    public void EmptyOverridesStayEmptyThroughJson()
    {
        var restored = RoundTrip(new SettingsPreset
        {
            Name = "全部外した",
            UsedInks = new HashSet<string>(),
            PassesOverride = new Dictionary<string, int>(),
            MagicRgbOverride = new Dictionary<string, int[]?>(),
            CoverageModes = new Dictionary<string, string>(),
        });

        Assert.NotNull(restored.UsedInks);
        Assert.Empty(restored.UsedInks!);
        Assert.NotNull(restored.PassesOverride);
        Assert.Empty(restored.PassesOverride!);
        Assert.NotNull(restored.MagicRgbOverride);
        Assert.Empty(restored.MagicRgbOverride!);
        Assert.NotNull(restored.CoverageModes);
        Assert.Empty(restored.CoverageModes!);
    }

    /// <summary>一覧まるごと(PresetStore.Save が書く形)でも往復すること。</summary>
    [Fact]
    public void PresetListSurvivesAJsonRoundTrip()
    {
        var presets = new List<SettingsPreset>
        {
            new() { Name = "aa", UsedInks = new HashSet<string> { "white" } },
            new() { Name = "bb" },
        };

        string json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
        var restored = JsonSerializer.Deserialize<List<SettingsPreset>>(json)!;

        Assert.Equal(new[] { "aa", "bb" }, restored.Select(p => p.Name));
        Assert.Equal(new[] { "white" }, restored[0].UsedInks!);
        Assert.Null(restored[1].UsedInks);
    }

    private static SettingsPreset RoundTrip(SettingsPreset preset)
    {
        string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
        return JsonSerializer.Deserialize<SettingsPreset>(json)!;
    }

    /// <summary>既定値が TraySettings と同じものを指していること(同じ文字列を
    /// 2 箇所に書いていない、の確認)。片方だけ変えたら赤くなる。</summary>
    [Fact]
    public void PresetDefaultsMatchTraySettingsDefaults()
    {
        var preset = new SettingsPreset { Name = "既定" };
        var settings = new TraySettings();

        Assert.Equal(settings.Machine, preset.Machine);
        Assert.Equal(settings.InkMode, preset.InkMode);
        Assert.Equal(settings.ResolutionKey, preset.ResolutionKey);
        Assert.Equal(settings.PaperName, preset.PaperName);
        Assert.Equal(settings.MediaName, preset.MediaName);
        Assert.Equal(settings.Halftone, preset.Halftone);
        Assert.Equal(settings.WhiteMode, preset.WhiteMode);
        Assert.Equal(settings.ColourCorrection, preset.ColourCorrection);
        Assert.Equal(settings.NoCurlCorrection, preset.NoCurlCorrection);
    }
}
