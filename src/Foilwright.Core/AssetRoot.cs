// Foilwright.Core — 設定ファイル一式(profiles/*.yaml, papers/, media.yaml,
// palette/default.yaml, colour/photo_colcor.bin)の置き場所を決める(D-040)。
//
// 開発でも配布でも同じコードで動く形にするため、探す順番を 3 段にする。
// 「配布版にはリポジトリが無く、実行ファイルから 5 階層上がる規則では
// 何も見つからない」という問題への対応(D-039 の着手順 1)。

namespace Foilwright.Core;

/// <summary>設定ファイル一式の置き場所を決める(D-040)。</summary>
public static class AssetRoot
{
    /// <summary>目印。ここにこれがあれば「設定一式の置き場所」とみなす。
    /// ディレクトリの有無だけで判定すると、空のディレクトリを掴んで
    /// 後段で分かりにくく失敗する(D-040 補足)。</summary>
    public const string MarkerRelativePath = "palette/default.yaml";

    /// <summary>探す順番を適用する。環境変数の値、実行ファイルの位置、
    /// 「そこに設定があるか」の判定を引数で受け取る純粋な形にしてあり、
    /// 環境変数を書き換えずにテストできる。
    ///
    /// 順番(見つかった最初のものを使う):
    /// 1. envHome が設定されていて、そこに設定があれば、そこ
    /// 2. baseDirectory に設定があれば、そこ(配布版)
    /// 3. baseDirectory から 5 階層上に設定があれば、そこ(開発時。現行の挙動)
    ///
    /// どこにも無ければ ConfigException。メッセージには探した場所を
    /// 全部列挙する。</summary>
    public static string Resolve(string? envHome, string baseDirectory, Func<string, bool> hasAssets)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(envHome))
        {
            candidates.Add(envHome);
        }

        candidates.Add(baseDirectory);
        candidates.Add(FindAncestor(baseDirectory, levels: 5));

        foreach (var candidate in candidates)
        {
            if (hasAssets(candidate))
            {
                return candidate;
            }
        }

        var searched = string.Join(", ", candidates.Select(c => $"'{c}'"));
        throw new ConfigException(
            $"could not locate Foilwright config assets (looked for '{MarkerRelativePath}' in: {searched}). " +
            $"set the FOILWRIGHT_HOME environment variable, or place the assets next to the executable, " +
            $"or run from within the repository.");
    }

    /// <summary>実環境向け。環境変数 FOILWRIGHT_HOME と AppContext.BaseDirectory と
    /// 実ファイルの存在確認を使って Resolve を呼ぶ。</summary>
    public static string ResolveDefault()
    {
        string? envHome = Environment.GetEnvironmentVariable("FOILWRIGHT_HOME");
        string baseDirectory = AppContext.BaseDirectory;
        return Resolve(envHome, baseDirectory, HasAssets);
    }

    private static bool HasAssets(string dir)
    {
        return File.Exists(Path.Combine(dir, MarkerRelativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string FindAncestor(string baseDirectory, int levels)
    {
        var dir = new DirectoryInfo(baseDirectory);
        for (int i = 0; i < levels; i++)
        {
            if (dir.Parent is null)
            {
                break;
            }
            dir = dir.Parent;
        }
        return dir.FullName;
    }
}
