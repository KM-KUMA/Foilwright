// Foilwright.Tray — 設定の既定値(DOMAIN §7.1)。
//
// 設定は二層: 既定値(このクラスが表す)と、ジョブごとの上書き
// (PreviewForm のコントロールが保持し、Save() を呼ばない限りここへは
// 反映しない)。
//
// 永続化先: %AppData%\Foilwright\settings.json
// (= Environment.SpecialFolder.ApplicationData、通常
// C:\Users\<user>\AppData\Roaming\Foilwright\settings.json)。
// 利用者ごとの設定であり、リポジトリには含めない。JSON によるファイル
// 保存は簡易実装(タスク仕様上「今回は簡易でよい」とされている)。

using System.Text.Json;
using Foilwright.Core;

namespace Foilwright.Tray;

public sealed class TraySettings
{
    public string Machine { get; set; } = MachineRoute.DefaultMachine;

    // D-016: 既定は 'auto'。トレイアプリでは per_page は選ばせない
    // (単一ページの PostScript しか受け取らないため。Foilwright.Cli.Program の
    // listen と同じ制約)。
    public string InkMode { get; set; } = "auto";

    // D-027: DOMAIN §7.1 の残りの設定項目。
    public string ResolutionKey { get; set; } = JobPipeline.DefaultResolutionKey;
    public string PaperName { get; set; } = JobPipeline.DefaultPaperName;
    public string MediaName { get; set; } = JobPipeline.DefaultMediaName;
    public string Halftone { get; set; } = JobPipeline.DefaultHalftone;
    public string WhiteMode { get; set; } = JobPipeline.DefaultWhiteMode;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Foilwright",
        "settings.json");

    /// <summary>保存済みの既定値を読む。ファイルが無い、または壊れている場合は
    /// 組み込みの既定値にフォールバックする(黙って落とさない代わりに、
    /// トレイアプリの起動自体は止めない)。</summary>
    public static TraySettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<TraySettings>(json);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 破損した設定ファイルは既定値へフォールバックする。
        }
        return new TraySettings();
    }

    public void Save()
    {
        string? dir = Path.GetDirectoryName(SettingsPath);
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
