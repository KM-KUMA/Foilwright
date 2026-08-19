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

    /// <summary>D-031: 重ね塗り回数(パス数)のジョブごとの上書き(ink 名 → 回数)。
    /// D-024 の下層(既定値)であり、プレビューの「パス数」列(D-030 のチェック列と
    /// 同じ形)がジョブごとの上書きを持つ。
    ///
    /// null と空辞書を区別する: null は「利用者が一度も触っていない(または旧
    /// settings.json に項目が無い)」を表し、<see cref="ResolvePasses"/> がパレットの
    /// `passes`(インクと媒体の組み合わせに対する妥当な初期値。DOMAIN §6.2)を
    /// そのまま使う。空辞書は「利用者が一度は編集したが、結局どのインクも上書き
    /// しなかった」状態であり、そのまま尊重する(全インクがパレットの値に戻る点は
    /// 結果として null と同じだが、意味としては明示的な「上書き無し」)。
    ///
    /// 範囲は 1〜8(D-031)。この辞書に範囲外の値を入れてはならない — 検証は
    /// 呼び出し側(PreviewForm の CellValidating)が担う。</summary>
    public Dictionary<string, int>? PassesOverride { get; set; }

    /// <summary>D-031: パス数として受け付ける範囲(下限)。範囲外は打ち間違いとみなし
    /// その場で拒否する — 生産終了品のリボンを黙って消費させないため。</summary>
    public const int MinPasses = 1;

    /// <summary>D-031: パス数として受け付ける範囲(上限)。§10.7 の実運用値は 4 で、
    /// 8 はそれを超える余裕を持たせた値(それを超える指定はほぼ打ち間違い)。</summary>
    public const int MaxPasses = 8;

    /// <summary>指定したインクについて、このジョブで実際に使うパス数を解決する。
    /// PassesOverride にそのインクの上書きがあればそれを使い、無ければ
    /// パレットの <see cref="InkDefinition.Passes"/>(既定値)をそのまま使う。</summary>
    public int ResolvePasses(InkDefinition ink) =>
        PassesOverride is { } overrides && overrides.TryGetValue(ink.Name, out int passes) ? passes : ink.Passes;

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
