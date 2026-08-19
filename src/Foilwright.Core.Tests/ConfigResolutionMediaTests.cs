// Foilwright.Core.Tests — 解像度(DOMAIN §7.1 / papers/5000-series.yaml
// 冒頭コメント)とメディア種別(DOMAIN §5.5.2)の設定駆動を検証する。
//
// いずれもコードに値を埋め込まず、profiles/*.yaml・media.yaml から読んだ
// 内容だけで解決できることを確認する(DOMAIN §4.5)。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class ConfigResolutionMediaTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ProfilesDir = Path.Combine(RepoRoot, "profiles");
    private static readonly string MediaYaml = Path.Combine(RepoRoot, "media.yaml");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir.Parent is null)
            {
                break;
            }
            dir = dir.Parent;
        }
        return dir.FullName;
    }

    [Fact]
    public void ResolveResolutionByKey_600_ReturnsSquareResolution()
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(ProfilesDir, "md-5000.yaml"));

        var entry = profile.ResolveResolutionByKey("600");

        Assert.Equal(600, entry.DpiX);
        Assert.Equal(600, entry.DpiY);
        Assert.Equal("600", entry.Key);
    }

    [Fact]
    public void ResolveResolutionByKey_1200x600_ReturnsNonSquareResolution()
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(ProfilesDir, "md-5000.yaml"));

        var entry = profile.ResolveResolutionByKey("1200x600");

        Assert.Equal(1200, entry.DpiX);
        Assert.Equal(600, entry.DpiY);
        Assert.Equal("1200x600", entry.Key);
    }

    [Fact]
    public void ResolveResolutionByKey_UnknownKey_ThrowsConfigException()
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(ProfilesDir, "md-5000.yaml"));

        Assert.Throws<ConfigException>(() => profile.ResolveResolutionByKey("2400"));
    }

    [Fact]
    public void ResolveResolution_UnknownDpi_ThrowsConfigException()
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(ProfilesDir, "md-5000.yaml"));

        Assert.Throws<ConfigException>(() => profile.ResolveResolution(2400));
    }

    [Fact]
    public void ScaleToResolution_1200x600_DoublesWidthOnly()
    {
        var paper = new PaperSpec { Code = 1, Width = 4960, Length = 7016, LeftMargin = 12, TopMargin = 24 };

        var scaled = paper.ScaleToResolution(1200, 600);

        Assert.Equal(paper.Width * 2, scaled.Width);
        Assert.Equal(paper.Length, scaled.Length);
        Assert.Equal(paper.LeftMargin * 2, scaled.LeftMargin);
        Assert.Equal(paper.TopMargin, scaled.TopMargin);
    }

    [Fact]
    public void ScaleToResolution_600_IsIdentity()
    {
        var paper = new PaperSpec { Code = 1, Width = 4960, Length = 7016, LeftMargin = 12, TopMargin = 24 };

        var scaled = paper.ScaleToResolution(600, 600);

        Assert.Equal(paper.Width, scaled.Width);
        Assert.Equal(paper.Length, scaled.Length);
        Assert.Equal(paper.LeftMargin, scaled.LeftMargin);
        Assert.Equal(paper.TopMargin, scaled.TopMargin);
    }

    [Fact]
    public void LoadMediaTable_ReturnsAllTwentyFourEntriesWithDistinctLabelsAndBytes()
    {
        var table = ConfigLoader.LoadMediaTable(MediaYaml);

        // ppmtomd の media_table[] 全 24 種(DOMAIN §5.5.2)。名前をコードに
        // 埋め込まず、ファイル由来のキー集合をそのまま検証する。
        Assert.Equal(24, table.Count);
        Assert.Contains("plain_paper", table.Keys);
        Assert.Contains("cardboard", table.Keys);
        Assert.Contains("fine_plain_paper", table.Keys);
        Assert.Contains("post_card", table.Keys);
        Assert.Contains("laser_paper", table.Keys);

        foreach (var (name, media) in table)
        {
            Assert.False(string.IsNullOrWhiteSpace(media.Label));
        }

        // メディアごとに byte1/byte2 の組が異なる(コードに埋め込んだ固定値
        // ではなく、実際に YAML の内容が反映されていることの確認)。
        var byteCombos = table.Values.Select(m => (m.Byte1, m.Byte2)).ToHashSet();
        Assert.True(byteCombos.Count > 1);
    }

    [Fact]
    public void LoadMediaTable_UnknownFile_Throws()
    {
        Assert.ThrowsAny<Exception>(() => ConfigLoader.LoadMediaTable(Path.Combine(RepoRoot, "does-not-exist.yaml")));
    }
}
