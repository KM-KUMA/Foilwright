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

    // D-029: 色補正(none/plain/photo)。既定は photo(下色除去のみの plain は
    // 写真的なフルカラー原稿で紫・緑・茶を黒一色に潰した実測を受けての決定)。
    public string ColourCorrection { get; set; } = "photo";

    // カール矯正の抑制(DOMAIN §7.1 / §10.10.4)。デカール・フィルム等、
    // 裏面印刷でカール矯正を止めたい用途向け。既定は false(矯正する)。
    public bool NoCurlCorrection { get; set; }

    /// <summary>D-030: 「そのジョブで使うインク」の許可リスト(ink 名の集合)。
    /// D-024 の下層(既定値)であり、プレビューのチェック列(D-028 の UI を
    /// 一般化したもの)がジョブごとの上書きを持つ。
    ///
    /// null と空集合を区別する: null は「利用者が一度も触っていない(または
    /// 旧 settings.json に項目が無い)」を表し、<see cref="ResolveUsedInks"/> が
    /// パレットから既定(メタリック無効・それ以外有効)を都度導出する。
    /// 空集合は「利用者が明示的に全インクを無効にした」状態であり、そのまま
    /// 尊重する(既定へフォールバックしない)。</summary>
    public HashSet<string>? UsedInks { get; set; }

    /// <summary>メタリック系インクかどうかをデータから判定する(DOMAIN §4.5:
    /// インク名をコードに列挙しない)。palette/default.yaml のスキーマでは、
    /// メタリック 4 色だけが「magic_rgb を持ち、かつプロセスインクでも
    /// (channel が null)、白版の下地にもならない(auto_undercoat が false)」
    /// という組み合わせになる — 白は auto_undercoat=true、黒とプロセス
    /// インクは channel が非 null で区別できるため、名前を挙げずに導ける。</summary>
    public static bool IsMetallic(InkDefinition ink) =>
        ink.MagicRgb is not null && ink.Channel is null && !ink.AutoUndercoat;

    /// <summary>UsedInks が null のときの既定値(D-030: メタリックだけ無効)。
    /// パレット全体から動的に導出するため、パレットにインクが増減しても
    /// コード変更なしで追従する。</summary>
    public static HashSet<string> DefaultUsedInks(IReadOnlyList<InkDefinition> palette) =>
        palette.Where(ink => !IsMetallic(ink)).Select(ink => ink.Name).ToHashSet();

    /// <summary>このジョブで実際に使えるインク名の集合を解決する。UsedInks が
    /// 設定済みならそれをそのまま使い(空集合も含めて尊重する)、null なら
    /// パレットから既定値を導出する。</summary>
    public HashSet<string> ResolveUsedInks(IReadOnlyList<InkDefinition> palette) =>
        UsedInks is { } used ? new HashSet<string>(used) : DefaultUsedInks(palette);

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
