// Foilwright.Core.Tests — 設定ファイル一式の置き場所を決める AssetRoot.Resolve
// の単体テスト(D-040)。環境変数は書き換えず、すべて引数で渡して検証する。

namespace Foilwright.Core.Tests;

public class AssetRootTests
{
    private static bool HasAssetsAt(string root, string presentRoot)
    {
        return root == presentRoot;
    }

    [Fact]
    public void Resolve_EnvHomeHasAssets_UsesEnvHome()
    {
        string envHome = @"C:\somewhere\envhome";
        string baseDirectory = @"C:\somewhere\else\bin";

        string result = AssetRoot.Resolve(envHome, baseDirectory, root => HasAssetsAt(root, envHome));

        Assert.Equal(envHome, result);
    }

    [Fact]
    public void Resolve_NoEnvHome_BaseDirectoryHasAssets_UsesBaseDirectory()
    {
        string baseDirectory = @"C:\install\Foilwright";

        string result = AssetRoot.Resolve(null, baseDirectory, root => HasAssetsAt(root, baseDirectory));

        Assert.Equal(baseDirectory, result);
    }

    [Fact]
    public void Resolve_NoEnvHome_NoBaseDirectoryAssets_FiveLevelsUpHasAssets_UsesAncestor()
    {
        // src/Foilwright.Cli/bin/Debug/net10.0 から 5 階層上がるとリポジトリ直下になる、
        // という現行の開発時の挙動と同じ規則。
        string repoRoot = @"C:\repo";
        string baseDirectory = Path.Combine(repoRoot, "src", "Foilwright.Cli", "bin", "Debug", "net10.0");

        string result = AssetRoot.Resolve(null, baseDirectory, root => HasAssetsAt(root, repoRoot));

        Assert.Equal(repoRoot, result);
    }

    [Fact]
    public void Resolve_NothingFound_ThrowsWithAllSearchedLocationsListed()
    {
        string envHome = @"C:\somewhere\envhome";
        string baseDirectory = @"C:\install\Foilwright";

        var ex = Assert.Throws<ConfigException>(
            () => AssetRoot.Resolve(envHome, baseDirectory, _ => false));

        Assert.Contains(envHome, ex.Message);
        Assert.Contains(baseDirectory, ex.Message);
    }

    [Fact]
    public void Resolve_NoEnvHome_NothingFound_ThrowsWithoutEnvHomeInMessage()
    {
        string baseDirectory = @"C:\install\Foilwright";

        var ex = Assert.Throws<ConfigException>(
            () => AssetRoot.Resolve(null, baseDirectory, _ => false));

        Assert.Contains(baseDirectory, ex.Message);
    }
    // ここから 2 件が「優先順」を守らせる。上の 5 件はどれも「設定が 1 箇所にしか
    // 無い」形なので、**探す順番を入れ替えても全部通ってしまう**(2026-08-20 に
    // 実際に確認した — 環境変数を最後に回しても 158 件が緑のままだった)。
    // 効くのは「2 箇所に設定がある」場合で、環境変数で上書きしたい場面がまさにそれ。

    [Fact]
    public void Resolve_EnvHomeAndBaseDirectoryBothHaveAssets_EnvHomeWins()
    {
        string envHome = Path.Combine(Path.GetTempPath(), "fw-env");
        string baseDirectory = Path.Combine(Path.GetTempPath(), "fw-base");

        string result = AssetRoot.Resolve(
            envHome, baseDirectory, root => root == envHome || root == baseDirectory);

        Assert.Equal(envHome, result);
    }

    [Fact]
    public void Resolve_BaseDirectoryAndAncestorBothHaveAssets_BaseDirectoryWins()
    {
        // 配布版(実行ファイルの隣に設定)を、たまたま 5 階層上にも設定がある場所へ
        // 置いた場合。隣が勝たないと、意図しない設定を読む。
        string baseDirectory = Path.Combine(
            Path.GetTempPath(), "a", "b", "c", "d", "e", "fw-base");
        string ancestor = Path.Combine(Path.GetTempPath(), "a");

        string result = AssetRoot.Resolve(
            null, baseDirectory, root => root == baseDirectory || root == ancestor);

        Assert.Equal(baseDirectory, result);
    }
}
