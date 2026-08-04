// Foilwright.Core — PostScript を PPM(P6)へラスタライズする(D-021)。
//
// Ghostscript は同梱しない。利用者が別途インストールする外部依存
// (DOMAIN §12.2 の既定方針、D-021)。実行ファイルが見つからない場合は
// 導入を促す明確な例外を投げる。
//
// A4/600dpi の出力は約 99.5MB になる(DOMAIN §3.6 / D-021 補足)。
// ファイル経由で受け渡し、呼び出し側が使い終わったら消す。

using System.Diagnostics;

namespace Foilwright.Core;

/// <summary>Ghostscript 実行ファイルが見つからない、または変換に失敗したときに
/// 送出する。</summary>
public sealed class GhostscriptException : Exception
{
    public GhostscriptException(string message) : base(message) { }
}

public static class Ghostscript
{
    private const string ExecutableName = "gswin64c.exe";

    /// <summary>Ghostscript 実行ファイルを PATH と既定インストール先
    /// (`C:\Program Files\gs\*\bin\gswin64c.exe`)から探す。
    /// 見つからない場合は導入を促す GhostscriptException を送出する。</summary>
    public static string FindExecutable()
    {
        string? fromPath = FindOnPath();
        if (fromPath is not null)
        {
            return fromPath;
        }

        string? fromDefaultInstall = FindInDefaultInstallLocation();
        if (fromDefaultInstall is not null)
        {
            return fromDefaultInstall;
        }

        throw new GhostscriptException(
            $"Ghostscript ({ExecutableName}) が見つかりません。" +
            "https://www.ghostscript.com/ から Ghostscript をインストールしてください " +
            "(このアプリには同梱されていません。DOMAIN.md §12.2 / D-021)。");
    }

    private static string? FindOnPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }
        foreach (string dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir))
            {
                continue;
            }
            string candidate;
            try
            {
                candidate = Path.Combine(dir, ExecutableName);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FindInDefaultInstallLocation()
    {
        const string gsRoot = @"C:\Program Files\gs";
        if (!Directory.Exists(gsRoot))
        {
            return null;
        }
        // C:\Program Files\gs\gsX.Y.Z\bin\gswin64c.exe。複数バージョンが
        // あれば最も新しいディレクトリ名(文字列降順)を選ぶ。
        return Directory.GetDirectories(gsRoot)
            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(d => Path.Combine(d, "bin", ExecutableName))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>PostScript ファイルを解像度 dpi の PPM(P6)へ変換する。
    /// 出力先 outputPpmPath は呼び出し側が指定し、変換後の後始末(削除)も
    /// 呼び出し側の責任(DOMAIN §3.6 の 99.5MB 級ファイルをメモリに載せない
    /// 方針に合わせ、常にファイル経由で受け渡す)。</summary>
    public static void ConvertToPpm(string inputPostScriptPath, string outputPpmPath, int dpi)
    {
        string gs = FindExecutable();

        var psi = new ProcessStartInfo
        {
            FileName = gs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-dNOPAUSE");
        psi.ArgumentList.Add("-dBATCH");
        psi.ArgumentList.Add("-dSAFER");
        psi.ArgumentList.Add("-sDEVICE=ppmraw");
        psi.ArgumentList.Add($"-r{dpi}");
        psi.ArgumentList.Add($"-sOutputFile={outputPpmPath}");
        psi.ArgumentList.Add(inputPostScriptPath);

        using var process = Process.Start(psi)
            ?? throw new GhostscriptException($"failed to start Ghostscript process ({gs})");
        string stderr = process.StandardError.ReadToEnd();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new GhostscriptException(
                $"Ghostscript exited with code {process.ExitCode}.\nstdout: {stdout}\nstderr: {stderr}");
        }
        if (!File.Exists(outputPpmPath))
        {
            throw new GhostscriptException(
                $"Ghostscript reported success but did not produce '{outputPpmPath}'.\nstderr: {stderr}");
        }
    }
}
