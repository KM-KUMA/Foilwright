// Foilwright.Tray.Tests — 「使うインク」の既定値(D-030 / D-048)の検出器。
//
// これが緩むと、**パレットにインクを足した瞬間に「新規インストールでは既定で有効」**
// になる。既存の利用者は settings.json に UsedInks が保存済みなので気づけず、
// 別の環境に入れた人だけが「知らないインクにチェックが入っている」状態を踏む。
// 2026-08-22 に D-048 の 2 色(光沢仕上げ2 / MF インク)で実際に起きかけた。

using Foilwright.Core;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class DefaultUsedInksTests
{
    private static InkDefinition Process(string name, string channel) => new()
    {
        Name = name,
        Label = name,
        PrinterCode = 0x00,
        Order = 10,
        Channel = channel,
    };

    private static InkDefinition Spot(string name, bool autoUndercoat = false) => new()
    {
        Name = name,
        Label = name,
        PrinterCode = 0x04,
        Order = 50,
        MagicRgb = new[] { 1, 2, 3 },
        Tolerance = 8,
        AutoUndercoat = autoUndercoat,
    };

    private static InkDefinition Coverage(string name) => new()
    {
        Name = name,
        Label = name,
        PrinterCode = 0x0E,
        Order = 95,
        Coverage = true,
    };

    [Fact]
    public void DefaultUsedInks_KeepsProcessAndUndercoatInks()
    {
        var palette = new List<InkDefinition>
        {
            Process("black", "K"),
            Process("cyan", "C"),
            Spot("white", autoUndercoat: true),
        };

        Assert.Equal(
            new HashSet<string> { "black", "cyan", "white" },
            TraySettings.DefaultUsedInks(palette));
    }

    [Fact]
    public void DefaultUsedInks_LeavesOutMetallics()
    {
        // D-030: メタリックは特別な用途にしか使わないので既定では外す。
        var palette = new List<InkDefinition> { Process("black", "K"), Spot("metallic_gold") };

        Assert.Equal(new HashSet<string> { "black" }, TraySettings.DefaultUsedInks(palette));
    }

    [Fact]
    public void DefaultUsedInks_LeavesOutCoverageInks()
    {
        // D-048: 塗る範囲で決まるインクも既定では外す。「塗る範囲」を選ばないと
        // そもそもプレーンが作られないため、チェックだけ入った 0 ドットの行になる。
        var palette = new List<InkDefinition> { Process("black", "K"), Coverage("glossy_finish") };

        Assert.Equal(new HashSet<string> { "black" }, TraySettings.DefaultUsedInks(palette));
    }

    /// <summary>実物の palette/default.yaml に対する検出器。**パレットにインクを足したときに
    /// ここが赤くなる**ので、「既定で有効にしてよいインクか」を必ず考えることになる。
    /// 増やしてよい場合はこの一覧を更新する — 更新せずに緑になることはない。</summary>
    [Fact]
    public void DefaultUsedInks_OnTheRealPalette_IsExactlyTheEverydayInks()
    {
        string assetRoot = AssetRoot.ResolveDefault();
        var palette = ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml"));

        Assert.Equal(
            new HashSet<string> { "white", "cyan", "magenta", "yellow", "black" },
            TraySettings.DefaultUsedInks(palette));
    }
}
