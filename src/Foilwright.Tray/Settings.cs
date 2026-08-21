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
    public string InkMode { get; set; } = DefaultInkMode;

    /// <summary>インク指定方式の既定値(D-016)。SettingsPreset の既定値も
    /// これを参照する — 同じ既定値が 2 箇所に散らばると片方だけ変わる。</summary>
    public const string DefaultInkMode = "auto";

    /// <summary>色補正の既定値(D-029)。DefaultInkMode と同じ理由で定数にしてある。</summary>
    public const string DefaultColourCorrection = "photo";

    // D-027: DOMAIN §7.1 の残りの設定項目。
    public string ResolutionKey { get; set; } = JobPipeline.DefaultResolutionKey;
    public string PaperName { get; set; } = JobPipeline.DefaultPaperName;
    public string MediaName { get; set; } = JobPipeline.DefaultMediaName;
    public string Halftone { get; set; } = JobPipeline.DefaultHalftone;
    public string WhiteMode { get; set; } = JobPipeline.DefaultWhiteMode;

    // D-029: 色補正(none/plain/photo)。既定は photo(下色除去のみの plain は
    // 写真的なフルカラー原稿で紫・緑・茶を黒一色に潰した実測を受けての決定)。
    public string ColourCorrection { get; set; } = DefaultColourCorrection;

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
        palette.Where(ink => !IsOffByDefault(ink)).Select(ink => ink.Name).ToHashSet();

    /// <summary>既定で無効にするインクか。**カセットが刺さっていないのが普通のインク**を
    /// 外すのが狙いで、2 種類ある:
    ///
    ///   メタリック 4 色(D-030)— 特別な用途にしか使わない。
    ///   塗る範囲で決まるインク(D-048: 光沢仕上げ2 / MF インク)— 同上。加えて、
    ///     **これらは「塗る範囲」を選ばないとそもそもプレーンが作られない**ので、
    ///     チェックだけ入った 0 ドットの行が並んでも意味が無い。
    ///
    /// **この判定を忘れると、新しいインクをパレットに足した瞬間に
    /// 「新規インストールでは既定で有効」になる**(既存の利用者は settings.json に
    /// UsedInks が保存済みなので気づけない)。2026-08-22 に D-048 の 2 色で実際に起きた。</summary>
    private static bool IsOffByDefault(InkDefinition ink) => IsMetallic(ink) || ink.Coverage;

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

    /// <summary>D-048: 塗る範囲(ink 名 → "none" / "artwork" / "full")。パレットで
    /// coverage: true になっているインク(紙用光沢仕上げ2 / 紙用 MF インク)だけに効く。
    /// D-031 のパス数と同じ形の下層(既定値)であり、プレビューの「塗る範囲」列が
    /// ジョブごとの上書きを持つ。
    ///
    /// null と空辞書を区別する: null は「利用者が一度も触っていない(または旧
    /// settings.json に項目が無い)」を表し、空辞書は「利用者が一度は編集したが、
    /// 結局どのインクにも塗る範囲を指定しなかった」状態である。どちらの場合も
    /// JobAssembly.BuildJobPlanes には空(または null)が渡り、coverage インクの
    /// プレーンは作られない — **既定は none であり、何もしなければ D-048 以前と
    /// 出力バイトが完全に一致する**(D-048 決定 3)。
    ///
    /// 値は <see cref="CoverageModeValues"/> のいずれかでなければならない。範囲外の
    /// 値を入れてはならない — 検証は呼び出し側(PreviewForm の列と Program の
    /// --coverage 解析)が担い、JobAssembly も受け取った値を再検証する。</summary>
    public Dictionary<string, string>? CoverageModes { get; set; }

    /// <summary>D-048: 塗る範囲として受け付ける値。JobAssembly 側と必ず同じ並びにする。</summary>
    public static readonly string[] CoverageModeValues = { "none", "artwork", "full" };

    /// <summary>D-048: 塗る範囲の既定値。「なし」= プレーンを作らない。</summary>
    public const string DefaultCoverageMode = "none";

    /// <summary>指定したインクについて、このジョブで実際に使う塗る範囲を解決する。
    /// CoverageModes にそのインクの指定があればそれを使い、無ければ
    /// <see cref="DefaultCoverageMode"/>(none)を返す(<see cref="ResolvePasses"/> と
    /// 同じ流儀)。</summary>
    public string ResolveCoverageMode(InkDefinition ink) =>
        CoverageModes is { } modes && modes.TryGetValue(ink.Name, out string? mode) ? mode : DefaultCoverageMode;

    /// <summary>D-042: マジックカラー(magic_rgb)のジョブごとの上書き(ink 名 → RGB 3 値)。
    /// D-030(使うインク)・D-031(パス数)と同じ形の下層(既定値)であり、プレビューの
    /// 「色」列がジョブごとの上書きを持つ。
    ///
    /// null と空辞書を区別する: null は「利用者が一度も触っていない(または旧
    /// settings.json に項目が無い)」を表し、パレット(palette/default.yaml)の
    /// magic_rgb をそのまま使う。空辞書は「利用者が一度は編集したが、結局どの
    /// インクも上書きしなかった」状態であり、そのまま尊重する(結果としてどちらも
    /// パレットの値になるが、意味としては明示的な「上書き無し」)。
    ///
    /// 値の意味は 2 通りある:
    ///   int[3] — そのインクのマジックカラーを差し替える(0〜255 の RGB)。
    ///   null   — そのインクの色を明示的に外す(マジック判定に参加させない)。
    ///
    /// D-042 決定 2: 上書きできるのは色だけ。tolerance(許容誤差)と order(順序)は
    /// パレットの値のままにする — 許容誤差まで出すと「なぜか別のインクで刷られる」
    /// という分かりにくい事故が増えるため。</summary>
    public Dictionary<string, int[]?>? MagicRgbOverride { get; set; }

    /// <summary>D-042: パレットに tolerance を持たないインク(プロセスインク)へ
    /// 色を割り当てたときに使う許容誤差。既定パレットの白・黒と同じ 8。</summary>
    public const int DefaultOverrideTolerance = 8;

    /// <summary>D-042: RGB 3 値として妥当か(3 要素・各 0..255)。
    /// null は「色なし」(マジック判定に参加させない)を表す正当な値のため true。</summary>
    public static bool IsValidMagicRgb(int[]? rgb) =>
        rgb is null || (rgb.Length == 3 && rgb.All(v => v is >= 0 and <= 255));

    /// <summary>D-042: パレットに <see cref="MagicRgbOverride"/> を適用した
    /// 「照合用パレット」を返す。元のリスト・元の InkDefinition は変更しない。</summary>
    public List<InkDefinition> ApplyMagicRgbOverride(IReadOnlyList<InkDefinition> palette) =>
        ApplyMagicRgbOverride(palette, MagicRgbOverride);

    /// <summary>D-042: パレットにマジックカラーの上書きを適用した「照合用パレット」を
    /// 返す(辞書を受け取る静的版。JobPipeline は TraySettings のインスタンスを
    /// 持たないためこちらを使う)。
    ///
    /// 規則:
    ///   該当インクの項目が無い → そのインクはそのまま(同じインスタンスを返す)。
    ///   値が int[3]           → MagicRgb をその値にする。Tolerance は元のインクが
    ///                           持っていればその値を維持し、持っていなければ
    ///                           <see cref="DefaultOverrideTolerance"/> を使う
    ///                           (D-042 決定 2: 許容誤差は上書きしない)。
    ///   値が null             → MagicRgb / Tolerance をともに null にする(色を外す)。
    ///
    /// 妥当でない値(3 要素でない・0..255 の範囲外)は黙って無視せず
    /// <see cref="ConfigException"/> を投げる — 打ち間違いのまま刷ると
    /// 生産終了品のリボンと用紙を失うため(D-031 の CellValidating と同じ方針)。
    /// 検証はパレットに無いインク名の項目に対しても行う(綴り間違いを見逃さない)。</summary>
    public static List<InkDefinition> ApplyMagicRgbOverride(
        IReadOnlyList<InkDefinition> palette, IReadOnlyDictionary<string, int[]?>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return palette.ToList();
        }

        foreach (var (inkName, rgb) in overrides)
        {
            if (!IsValidMagicRgb(rgb))
            {
                throw new ConfigException(
                    $"マジックカラーの上書き '{inkName}' の値が不正です。RGB 3 値(各 0〜255)で指定してください(D-042): " +
                    $"[{string.Join(", ", rgb!)}]");
            }
        }

        var result = new List<InkDefinition>(palette.Count);
        foreach (var ink in palette)
        {
            if (!overrides.TryGetValue(ink.Name, out int[]? rgb))
            {
                // 上書きが無いインクはそのまま(インスタンスを作り直さない)。
                result.Add(ink);
                continue;
            }
            // InkDefinition は sealed + init プロパティのみで `with` が使えないため、
            // 全プロパティを写した新しいインスタンスを作る。写し漏れは静かなバグに
            // なる(送出に使う printer_code や過不足判定の barcode が消える)ので、
            // プロパティを足したときは必ずここも足すこと。
            result.Add(new InkDefinition
            {
                Name = ink.Name,
                Label = ink.Label,
                PrinterCode = ink.PrinterCode,
                Order = ink.Order,
                MagicRgb = rgb is null ? null : new[] { rgb[0], rgb[1], rgb[2] },
                Tolerance = rgb is null ? null : ink.Tolerance ?? DefaultOverrideTolerance,
                Channel = ink.Channel,
                Barcode = ink.Barcode,
                AutoUndercoat = ink.AutoUndercoat,
                Passes = ink.Passes,
            });
        }
        return result;
    }

    /// <summary>利用者ごとの設定を置くフォルダ(%AppData%\Foilwright)。
    /// settings.json と presets.json が同じ場所に並ぶよう、両者でここを共有する
    /// (パスの文字列を 2 箇所に書かない)。</summary>
    internal static string ConfigFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Foilwright");

    private static string SettingsPath => Path.Combine(ConfigFolder, "settings.json");

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

/// <summary>設定のプリセット 1 件。名前を付けて保存した「設定一式」。
///
/// **既定値(TraySettings)とは別物として併存する。** 既定値は「何も選ばなかった
/// ときの初期値」であり 1 組しか持てないが、プリセットは「目デカール(フィルム)」
/// 「はがきテスト」のように**用途ごとに何組でも**持てる。用途を行き来しても
/// 上書きにならないのがプリセット側の役目。
///
/// 既定値は必ず <see cref="TraySettings"/> と同じものを参照する — 同じ文字列を
/// 2 箇所に書くと、片方だけ変えたときに黙ってずれる。
///
/// **意図的に入れていないもの:**
///   部数 — D-044 決定 3。「そのジョブ限りの量」であり、保存すると次のジョブも
///          同じ部数だけ刷る事故になる。
///   「1 部ずつ確認する」— 紙の入れ方(物理的な段取り)であって原稿の設定ではない。
///          その日の給紙のしかたで決まるものを原稿の設定と一緒に保存しない。</summary>
public sealed class SettingsPreset
{
    /// <summary>利用者が付けた名前。コンボに出る文字そのもの。</summary>
    public required string Name { get; set; }

    public string Machine { get; set; } = MachineRoute.DefaultMachine;
    public string InkMode { get; set; } = TraySettings.DefaultInkMode;
    public string ResolutionKey { get; set; } = JobPipeline.DefaultResolutionKey;
    public string PaperName { get; set; } = JobPipeline.DefaultPaperName;
    public string MediaName { get; set; } = JobPipeline.DefaultMediaName;
    public string Halftone { get; set; } = JobPipeline.DefaultHalftone;
    public string WhiteMode { get; set; } = JobPipeline.DefaultWhiteMode;
    public string ColourCorrection { get; set; } = TraySettings.DefaultColourCorrection;
    public bool NoCurlCorrection { get; set; }

    /// <summary>D-030: 使うインクの許可リスト。**null と空集合を区別する** —
    /// null は「このプリセットは触っていない(パレットから既定を導出する)」、
    /// 空集合は「全インクを明示的に無効にした」。TraySettings と同じ約束。</summary>
    public HashSet<string>? UsedInks { get; set; }

    /// <summary>D-031: パス数の上書き。null と空辞書を区別する(TraySettings と同じ)。</summary>
    public Dictionary<string, int>? PassesOverride { get; set; }

    /// <summary>D-042: マジックカラーの上書き。null と空辞書を区別し、値の null は
    /// 「そのインクの色を明示的に外す」を表す(TraySettings と同じ)。</summary>
    public Dictionary<string, int[]?>? MagicRgbOverride { get; set; }

    /// <summary>D-048: 塗る範囲。null と空辞書を区別する(TraySettings と同じ)。
    /// **プリセットに入れる理由:** プリセットは「用途ごとの設定一式」であり、
    /// 「光沢仕上げ付きデカール」を保存できないとプリセットの意味がない。</summary>
    public Dictionary<string, string>? CoverageModes { get; set; }
}

/// <summary>プリセットの保存と読み出し(%AppData%\Foilwright\presets.json)。
///
/// **settings.json とは別ファイルにする。** 既定値は「何も選ばなかったときの初期値」で
/// 常に 1 組、プリセットは用途ごとに増える一覧であり、寿命も意味も違う。
///
/// 名前の検証と一覧の操作(<see cref="IsValidPresetName"/> /
/// <see cref="Upsert"/> / <see cref="Remove"/>)は**画面に触らない純粋な処理**として
/// ここに置いてある(PreviewForm.BuildMagicRgbWarning / Program.DescribeUserError と
/// 同じ形。ここが検出器になる)。</summary>
public static class PresetStore
{
    /// <summary>プリセット名の長さの上限。コンボに収まらない名前を防ぐだけの実務的な値。</summary>
    internal const int MaxPresetNameLength = 40;

    /// <summary>名前の比較・並べ替えに使う比較子。利用者から見て「目デカール」と
    /// 「目デカール」は同じものなので、大文字小文字は区別しない。</summary>
    internal static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private static string PresetsPath => Path.Combine(TraySettings.ConfigFolder, "presets.json");

    /// <summary>プリセット名として受け付けるか。空白のみは不可(前後の空白は
    /// 呼び出し側で Trim した上で渡す)。長すぎる名前も不可
    /// (<see cref="MaxPresetNameLength"/>)。</summary>
    internal static bool IsValidPresetName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= MaxPresetNameLength;

    /// <summary>名前の重複を「同じ名前は 1 つ」に潰して、指定のプリセットを追加または
    /// 差し替えた新しい一覧を返す(名前順)。**元のリストは変更しない。**
    /// 比較は大文字小文字を区別しない(<see cref="NameComparer"/>)。</summary>
    internal static List<SettingsPreset> Upsert(IReadOnlyList<SettingsPreset> presets, SettingsPreset preset)
    {
        var result = presets.Where(p => !NameComparer.Equals(p.Name, preset.Name)).ToList();
        result.Add(preset);
        return Sort(result);
    }

    /// <summary>指定の名前を取り除いた新しい一覧を返す(見つからなければ内容は同じ)。
    /// 元のリストは変更しない。</summary>
    internal static List<SettingsPreset> Remove(IReadOnlyList<SettingsPreset> presets, string name) =>
        Sort(presets.Where(p => !NameComparer.Equals(p.Name, name)).ToList());

    private static List<SettingsPreset> Sort(IEnumerable<SettingsPreset> presets) =>
        presets.OrderBy(p => p.Name, NameComparer).ToList();

    /// <summary>保存済みのプリセットを名前順で返す。ファイルが無い・壊れているときは
    /// 空を返す(TraySettings.Load と同じ流儀。**例外を投げて印刷そのものを止めない**)。
    /// 手で編集して名前が壊れた項目(空・長すぎ)は落とす — 名前で選ぶ仕組みなので、
    /// 選べない項目を一覧に残しても操作できない。</summary>
    public static List<SettingsPreset> Load()
    {
        try
        {
            if (File.Exists(PresetsPath))
            {
                string json = File.ReadAllText(PresetsPath);
                var loaded = JsonSerializer.Deserialize<List<SettingsPreset>>(json);
                if (loaded is not null)
                {
                    return Sort(loaded.Where(p => IsValidPresetName(p.Name)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 破損したプリセットファイルは「プリセット無し」として扱う。
        }
        return new List<SettingsPreset>();
    }

    /// <summary>プリセット一覧を丸ごと書き出す(TraySettings.Save と同じ流儀)。</summary>
    public static void Save(IReadOnlyList<SettingsPreset> presets)
    {
        Directory.CreateDirectory(TraySettings.ConfigFolder);
        string json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PresetsPath, json);
    }
}
