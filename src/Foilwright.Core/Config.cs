// Foilwright.Core — 設定読み込み: 機種プロファイル(profiles/*.yaml)、
// インクパレット(palette/*.yaml)、用紙寸法表(papers/*.yaml)、メディア種別表。
//
// スキーマは docs/DOMAIN.md §5.1(機種プロファイル)、§5.5(用紙寸法表)、
// §6.1/§6.2(パレット)が正。本ファイルはそれらの「形」だけを知っており、
// どの機種・インク・用紙が存在するかを一切ハードコードしない(DOMAIN §4.4 / §4.5)
// — その情報は常に呼び出し側が渡す YAML から来る。
//
// 参照実装: ref/foilwright_ref/config.py。
//
// 実装メモ: YamlDotNet の Deserialize<object> は本プロジェクトのバージョンでは
// スカラー値をすべて string として返す(0x0B のような 16 進数リテラルも
// 文字列のまま、bool/null 以外は型解決されない)。そのため PyYAML のように
// int/bool へ自動変換された木は得られず、本ファイルの ParseInt/ParseBool が
// その変換を肩代わりする。

using YamlDotNet.Serialization;

namespace Foilwright.Core;

/// <summary>
/// プロファイル・パレット・用紙表の検証に失敗したとき、または呼び出し側が
/// null(未計測)の値を要求したときに送出する(DOMAIN §5.2)。
/// </summary>
public sealed class ConfigException : Exception
{
    public ConfigException(string message) : base(message) { }
}

/// <summary>機種プロファイルの解像度エントリ(DOMAIN §5.1)。</summary>
public sealed class ResolutionEntry
{
    public required int DpiX { get; init; }
    public required int DpiY { get; init; }
    public bool IsDefault { get; init; }

    /// <summary>設定・CLI 引数で使う表示名。dpi_x == dpi_y なら "600" のように
    /// 単一値、異なるなら "1200x600" のように連結する。コードに解像度の
    /// 値そのものを埋め込まないための、プロファイルから導出した識別子
    /// (DOMAIN §4.5)。</summary>
    public string Key => DpiX == DpiY ? DpiX.ToString() : $"{DpiX}x{DpiY}";
}

/// <summary>機種プロファイル(DOMAIN §5.1)。</summary>
public sealed class ProfileSpec
{
    public required string Model { get; init; }
    public required string PaperTable { get; init; }
    public required IReadOnlyList<ResolutionEntry> Resolutions { get; init; }

    /// <summary>パス間の紙送り誤差の補正値。機体固有・実測値(DOMAIN §10.2)。
    /// 未計測の間は null(DOMAIN §5.2) — 推測値で埋めない。</summary>
    public int? LfCorrection { get; init; }

    /// <summary>実測待ちの最大印字幅(ドット)。未計測の間は null(DOMAIN §5.2)。</summary>
    public int? MaxWidthDots { get; init; }

    /// <summary>dpiX(例: 600 / 1200)から、このプロファイルが提供する解像度
    /// エントリを引く。存在しなければ ConfigException(不正な設定値)。
    /// 解像度の選択肢をコードに埋め込まないため、常に Resolutions(YAML 由来)
    /// から探す(DOMAIN §4.5 / §7.1)。</summary>
    public ResolutionEntry ResolveResolution(int dpiX)
    {
        var match = Resolutions.FirstOrDefault(r => r.DpiX == dpiX);
        if (match is null)
        {
            throw new ConfigException(
                $"resolution {dpiX}dpi is not offered by profile '{Model}'; available: " +
                string.Join(", ", Resolutions.Select(r => r.Key)));
        }
        return match;
    }

    /// <summary>解像度キー(ResolutionEntry.Key の形式、例: "600" / "1200x600")
    /// から解像度エントリを引く。CLI / トレイアプリの --resolution 引数向け。</summary>
    public ResolutionEntry ResolveResolutionByKey(string key)
    {
        var match = Resolutions.FirstOrDefault(r => r.Key == key);
        if (match is null)
        {
            throw new ConfigException(
                $"resolution '{key}' is not offered by profile '{Model}'; available: " +
                string.Join(", ", Resolutions.Select(r => r.Key)));
        }
        return match;
    }
}

/// <summary>パレット中の 1 インク(DOMAIN §6.1 / D-019)。</summary>
public sealed class InkDefinition
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required int PrinterCode { get; init; }
    public required int Order { get; init; }

    /// <summary>特色インクの目標 RGB。特色でなければ null(D-019)。</summary>
    public int[]? MagicRgb { get; init; }

    /// <summary>特色マッチングの許容誤差。MagicRgb が null なら常に null。</summary>
    public int? Tolerance { get; init; }

    /// <summary>プロセスインクが受け持つ CMYK チャンネル("C"/"M"/"Y"/"K")。
    /// プロセスインクでなければ null(D-019)。</summary>
    public string? Channel { get; init; }

    /// <summary>カセットの物理的なバーコード番号(0〜255)。状態応答(`05 01`)の
    /// スロットの先頭バイトと突き合わせて過不足を判定するために使う(D-026 /
    /// DOMAIN §7.3 / §13.7.5)。`printer_code` とは別の番号体系であり混同しない
    /// こと。省略可能 — 値が無いインクは過不足判定の対象にできず「判定不能」
    /// として扱う(CassetteCheck)。</summary>
    public int? Barcode { get; init; }

    public bool AutoUndercoat { get; init; }
    public int Passes { get; init; } = 1;
}

/// <summary>用紙寸法表の 1 エントリ(DOMAIN §5.5)。すべて 600dpi 基準のドット。</summary>
public sealed class PaperSpec
{
    public required int Code { get; init; }
    public required int Width { get; init; }
    public required int Length { get; init; }
    public required int LeftMargin { get; init; }
    public required int TopMargin { get; init; }

    /// <summary>600dpi 基準の値を実際の出力解像度へ換算する
    /// (papers/5000-series.yaml 冒頭コメント: 300dpi → 半分、1200dpi → width
    /// のみ 2 倍)。Width/LeftMargin は dpiX/600 の比、Length/TopMargin は
    /// dpiY/600 の比で換算する — Emitter.EmitJob の内部換算(ref/emitter.py と
    /// 同じ規則)と揃え、Ghostscript のラスタライズ結果を印字可能領域の
    /// 実ドット数で切り出せるようにする。</summary>
    public PaperSpec ScaleToResolution(int dpiX, int dpiY) => new()
    {
        Code = Code,
        Width = Width * dpiX / 600,
        Length = Length * dpiY / 600,
        LeftMargin = LeftMargin * dpiX / 600,
        TopMargin = TopMargin * dpiY / 600,
    };
}

/// <summary>メディア種別表の 1 エントリ(DOMAIN §5.5.2)。</summary>
public sealed class MediaSpec
{
    public required string Label { get; init; }
    public required int Byte1 { get; init; }
    public required int Byte2 { get; init; }
}

public static class ConfigLoader
{
    private static readonly System.Text.RegularExpressions.Regex NameRe =
        new(@"^[a-z_]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] PaletteRequiredFields = { "name", "label", "printer_code", "order" };
    private static readonly string[] PaletteChannels = { "C", "M", "Y", "K" };
    private static readonly string[] PaperRequiredFields =
        { "name", "code", "width", "length", "left_margin", "top_margin" };

    // --- YAML 木のロードと最小限のアクセサ ------------------------------------

    private static object? LoadYamlRoot(string path)
    {
        string text = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder().Build();
        return deserializer.Deserialize<object>(text);
    }

    private static Dictionary<string, object?> AsMap(object? node, string context)
    {
        if (node is not Dictionary<object, object> raw)
        {
            throw new ConfigException($"{context}: must be a YAML mapping");
        }
        var result = new Dictionary<string, object?>();
        foreach (var kv in raw)
        {
            result[kv.Key.ToString() ?? string.Empty] = kv.Value;
        }
        return result;
    }

    private static List<object?> AsList(object? node, string context)
    {
        if (node is not List<object> raw)
        {
            throw new ConfigException($"{context}: must be a YAML list");
        }
        return raw.Cast<object?>().ToList();
    }

    private static string AsString(object? node, string context)
    {
        if (node is null)
        {
            throw new ConfigException($"{context}: must not be null");
        }
        return node.ToString() ?? string.Empty;
    }

    /// <summary>10 進または(PyYAML 互換の)0x/0X 16 進リテラルを整数に変換する。
    /// bool の混入(order: "50" の類の間違い)は YamlDotNet が既に文字列化
    /// しているため型では検出できず、パース失敗として弾く。</summary>
    private static int ParseInt(object? node, string context, int low, int? high = null)
    {
        if (node is null)
        {
            throw new ConfigException($"{context}: must be an integer >= {low}, got null");
        }
        string s = node.ToString()!.Trim();
        bool ok;
        int value;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("-0x", StringComparison.OrdinalIgnoreCase))
        {
            bool negative = s.StartsWith("-");
            string digits = negative ? s.Substring(3) : s.Substring(2);
            ok = int.TryParse(digits, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
            if (ok && negative)
            {
                value = -value;
            }
        }
        else
        {
            ok = int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        if (!ok)
        {
            throw new ConfigException($"{context}: must be an integer, got '{s}'");
        }
        if (value < low || (high is not null && value > high))
        {
            string range = high is null ? $">= {low}" : $"in {low}..{high}";
            throw new ConfigException($"{context}: must be an integer {range}, got {value}");
        }
        return value;
    }

    private static bool ParseBool(object? node, string context)
    {
        if (node is null)
        {
            throw new ConfigException($"{context}: must be true or false, got null");
        }
        string s = node.ToString()!.Trim().ToLowerInvariant();
        return s switch
        {
            "true" => true,
            "false" => false,
            _ => throw new ConfigException($"{context}: must be true or false, got '{s}'"),
        };
    }

    // --- 機種プロファイル ------------------------------------------------------

    /// <summary>機種プロファイルを読む(DOMAIN §5.1)。
    /// lf_correction / max_width_dots は YAML 上 null なら null のまま保持する
    /// (DOMAIN §5.2) — ここで推測値を補わない。</summary>
    public static ProfileSpec LoadProfile(string path)
    {
        var root = AsMap(LoadYamlRoot(path), path);

        if (!root.TryGetValue("model", out var modelRaw) || modelRaw is null ||
            string.IsNullOrEmpty(modelRaw.ToString()))
        {
            throw new ConfigException($"{path}: profile is missing required field 'model'");
        }

        if (!root.TryGetValue("resolutions", out var resolutionsRaw) || resolutionsRaw is not List<object>)
        {
            throw new ConfigException($"{path}: 'resolutions' must be a non-empty list");
        }
        var resolutionsList = AsList(resolutionsRaw, $"{path}: resolutions");
        if (resolutionsList.Count == 0)
        {
            throw new ConfigException($"{path}: 'resolutions' must be a non-empty list");
        }

        var resolutions = new List<ResolutionEntry>();
        for (int i = 0; i < resolutionsList.Count; i++)
        {
            if (resolutionsList[i] is not Dictionary<object, object> entryRaw)
            {
                throw new ConfigException(
                    $"{path}: resolutions[{i}] must be a mapping with 'dpi_x' and 'dpi_y'");
            }
            var entry = AsMap(entryRaw, $"{path}: resolutions[{i}]");
            if (!entry.ContainsKey("dpi_x") || !entry.ContainsKey("dpi_y"))
            {
                throw new ConfigException(
                    $"{path}: resolutions[{i}] must be a mapping with 'dpi_x' and 'dpi_y'");
            }
            resolutions.Add(new ResolutionEntry
            {
                DpiX = ParseInt(entry["dpi_x"], $"{path}: resolutions[{i}].dpi_x", 0),
                DpiY = ParseInt(entry["dpi_y"], $"{path}: resolutions[{i}].dpi_y", 0),
                IsDefault = entry.TryGetValue("default", out var def) && def is not null && ParseBool(def, $"{path}: resolutions[{i}].default"),
            });
        }

        if (!root.TryGetValue("paper_table", out var paperTableRaw) || paperTableRaw is null ||
            string.IsNullOrEmpty(paperTableRaw.ToString()))
        {
            throw new ConfigException($"{path}: profile is missing required field 'paper_table'");
        }

        int? lfCorrection = root.TryGetValue("lf_correction", out var lfRaw) && lfRaw is not null
            ? ParseInt(lfRaw, $"{path}: lf_correction", int.MinValue)
            : null;
        int? maxWidthDots = root.TryGetValue("max_width_dots", out var mwRaw) && mwRaw is not null
            ? ParseInt(mwRaw, $"{path}: max_width_dots", 0)
            : null;

        return new ProfileSpec
        {
            Model = modelRaw.ToString()!,
            PaperTable = paperTableRaw.ToString()!,
            Resolutions = resolutions,
            LfCorrection = lfCorrection,
            MaxWidthDots = maxWidthDots,
        };
    }

    /// <summary>null(未計測)の値を要求する場面向け。値があればそれを返し、
    /// null なら「実機で測定してから」という ConfigException を送出する
    /// (DOMAIN §5.2)。</summary>
    public static int RequireValue(int? value, string key)
    {
        if (value is null)
        {
            throw new ConfigException(
                $"profile field '{key}' is required here but is unset (null); " +
                "it must be measured on real hardware before this operation " +
                "can proceed (see DOMAIN.md §5.2)");
        }
        return value.Value;
    }

    // --- パレット ---------------------------------------------------------------

    private static InkDefinition ValidateInk(Dictionary<string, object?> raw, int index, string path)
    {
        var missing = PaletteRequiredFields.Where(f => !raw.ContainsKey(f)).ToList();
        if (missing.Count > 0)
        {
            throw new ConfigException($"palette ink #{index}: missing required field(s) [{string.Join(", ", missing)}]");
        }

        string name = AsString(raw["name"], $"palette ink #{index}.name");
        if (!NameRe.IsMatch(name))
        {
            throw new ConfigException(
                $"palette ink #{index} ('{name}'): 'name' must contain only ASCII lowercase letters and underscores");
        }

        bool hasMagicRgb = raw.ContainsKey("magic_rgb") && raw["magic_rgb"] is not null;
        bool hasChannel = raw.ContainsKey("channel") && raw["channel"] is not null;
        if (!hasMagicRgb && !hasChannel)
        {
            throw new ConfigException(
                $"palette ink '{name}': must have 'magic_rgb' (spot ink), 'channel' (process ink), or both");
        }

        bool hasTolerance = raw.ContainsKey("tolerance") && raw["tolerance"] is not null;
        if (hasMagicRgb != hasTolerance)
        {
            throw new ConfigException(
                $"palette ink '{name}': 'magic_rgb' and 'tolerance' must be given together " +
                "(both present, for a spot ink) or both absent (process-only ink)");
        }

        int[]? magicRgb = null;
        if (hasMagicRgb)
        {
            var list = AsList(raw["magic_rgb"], $"palette ink '{name}'.magic_rgb");
            if (list.Count != 3)
            {
                throw new ConfigException($"palette ink '{name}': 'magic_rgb' must be 3 integers in 0..255");
            }
            magicRgb = list.Select((v, i) => ParseInt(v, $"palette ink '{name}'.magic_rgb[{i}]", 0, 255)).ToArray();
        }

        string? channel = null;
        if (hasChannel)
        {
            channel = AsString(raw["channel"], $"palette ink '{name}'.channel");
            if (!PaletteChannels.Contains(channel))
            {
                throw new ConfigException(
                    $"palette ink '{name}': 'channel' must be one of C, M, Y, K, got '{channel}'");
            }
        }

        int order = ParseInt(raw["order"], $"palette ink '{name}'.order", 0);
        int printerCode = ParseInt(raw["printer_code"], $"palette ink '{name}'.printer_code", 0, 255);
        int? tolerance = hasMagicRgb
            ? ParseInt(raw["tolerance"], $"palette ink '{name}'.tolerance", 0, 255)
            : null;

        int passes = raw.TryGetValue("passes", out var passesRaw) && passesRaw is not null
            ? ParseInt(passesRaw, $"palette ink '{name}'.passes", 1)
            : 1;

        bool autoUndercoat = raw.TryGetValue("auto_undercoat", out var auRaw) && auRaw is not null
            && ParseBool(auRaw, $"palette ink '{name}'.auto_undercoat");

        int? barcode = raw.TryGetValue("barcode", out var barcodeRaw) && barcodeRaw is not null
            ? ParseInt(barcodeRaw, $"palette ink '{name}'.barcode", 0, 255)
            : null;

        string label = AsString(raw["label"], $"palette ink '{name}'.label");

        return new InkDefinition
        {
            Name = name,
            Label = label,
            PrinterCode = printerCode,
            Order = order,
            MagicRgb = magicRgb,
            Tolerance = tolerance,
            Channel = channel,
            Barcode = barcode,
            AutoUndercoat = autoUndercoat,
            Passes = passes,
        };
    }

    /// <summary>パレットを読み、実行順(order 昇順、同値は記述順)にソートして返す
    /// (DOMAIN §6.1 / §4.3)。安定ソートで tie-break する(DOMAIN §4.9) —
    /// C# の List&lt;T&gt;.Sort は不安定なので OrderBy(安定ソート)を使う。</summary>
    public static List<InkDefinition> LoadPalette(string path)
    {
        var root = AsMap(LoadYamlRoot(path), path);
        if (!root.TryGetValue("inks", out var inksRaw) || inksRaw is not List<object>)
        {
            throw new ConfigException($"{path}: palette must be a YAML mapping with an 'inks' list");
        }
        var rawInks = AsList(inksRaw, $"{path}: inks");
        if (rawInks.Count == 0)
        {
            throw new ConfigException($"{path}: 'inks' must be a non-empty list");
        }

        var inks = new List<InkDefinition>();
        for (int i = 0; i < rawInks.Count; i++)
        {
            if (rawInks[i] is not Dictionary<object, object>)
            {
                throw new ConfigException($"palette ink #{i}: entry must be a mapping");
            }
            inks.Add(ValidateInk(AsMap(rawInks[i], $"palette ink #{i}"), i, path));
        }

        var seenNames = new HashSet<string>();
        var seenChannels = new HashSet<string>();
        for (int i = 0; i < inks.Count; i++)
        {
            var ink = inks[i];
            if (!seenNames.Add(ink.Name))
            {
                throw new ConfigException($"palette has duplicate ink name '{ink.Name}'");
            }
            if (ink.Channel is not null && !seenChannels.Add(ink.Channel))
            {
                throw new ConfigException($"palette has duplicate channel '{ink.Channel}'");
            }
        }

        return inks.OrderBy(ink => ink.Order).ToList();
    }

    // --- 用紙寸法表 ---------------------------------------------------------------

    private static PaperSpec ValidatePaper(Dictionary<string, object?> raw, int index, string path)
    {
        var missing = PaperRequiredFields.Where(f => !raw.ContainsKey(f)).ToList();
        if (missing.Count > 0)
        {
            throw new ConfigException($"paper table entry #{index}: missing required field(s) [{string.Join(", ", missing)}]");
        }
        string name = AsString(raw["name"], $"paper table entry #{index}.name");
        if (string.IsNullOrEmpty(name))
        {
            throw new ConfigException($"paper table entry #{index}: 'name' must be a non-empty string");
        }
        return new PaperSpec
        {
            Code = ParseInt(raw["code"], $"paper '{name}'.code", 0, 255),
            Width = ParseInt(raw["width"], $"paper '{name}'.width", 0),
            Length = ParseInt(raw["length"], $"paper '{name}'.length", 0),
            LeftMargin = ParseInt(raw["left_margin"], $"paper '{name}'.left_margin", 0),
            TopMargin = ParseInt(raw["top_margin"], $"paper '{name}'.top_margin", 0),
        };
    }

    /// <summary>用紙寸法表を読む(DOMAIN §5.5)。名前 → 寸法 のマップを返す。</summary>
    public static Dictionary<string, PaperSpec> LoadPaperTable(string path)
    {
        var root = AsMap(LoadYamlRoot(path), path);
        if (!root.TryGetValue("papers", out var papersRaw) || papersRaw is not List<object>)
        {
            throw new ConfigException($"{path}: paper table must be a YAML mapping with a 'papers' list");
        }
        var rawPapers = AsList(papersRaw, $"{path}: papers");
        if (rawPapers.Count == 0)
        {
            throw new ConfigException($"{path}: 'papers' must be a non-empty list");
        }

        var table = new Dictionary<string, PaperSpec>();
        for (int i = 0; i < rawPapers.Count; i++)
        {
            if (rawPapers[i] is not Dictionary<object, object>)
            {
                throw new ConfigException($"paper table entry #{i}: entry must be a mapping");
            }
            var map = AsMap(rawPapers[i], $"paper table entry #{i}");
            var paper = ValidatePaper(map, i, path);
            string name = AsString(map["name"], $"paper table entry #{i}.name");
            if (!table.TryAdd(name, paper))
            {
                throw new ConfigException($"{path}: duplicate paper name '{name}'");
            }
        }
        return table;
    }

    /// <summary>プロファイルの paper_table 参照を papersDir 配下の
    /// {name}.yaml に解決して読む(DOMAIN §5.1 / §5.5)。</summary>
    public static Dictionary<string, PaperSpec> ResolvePaperTable(ProfileSpec profile, string papersDir)
    {
        string path = Path.Combine(papersDir, profile.PaperTable + ".yaml");
        if (!File.Exists(path))
        {
            throw new ConfigException($"paper table '{profile.PaperTable}' not found: {path} does not exist");
        }
        return LoadPaperTable(path);
    }

    // --- メディア種別表 -------------------------------------------------------------

    /// <summary>メディア種別表を読む(DOMAIN §5.5.2)。機種による分岐はなく単一のフラットファイル。</summary>
    public static Dictionary<string, MediaSpec> LoadMediaTable(string path)
    {
        var root = AsMap(LoadYamlRoot(path), path);
        if (!root.TryGetValue("media", out var mediaRaw) || mediaRaw is not List<object>)
        {
            throw new ConfigException($"{path}: media table must be a YAML mapping with a 'media' list");
        }
        var rawMedia = AsList(mediaRaw, $"{path}: media");
        if (rawMedia.Count == 0)
        {
            throw new ConfigException($"{path}: 'media' must be a non-empty list");
        }

        var table = new Dictionary<string, MediaSpec>();
        for (int i = 0; i < rawMedia.Count; i++)
        {
            if (rawMedia[i] is not Dictionary<object, object>)
            {
                throw new ConfigException($"media entry #{i}: entry must be a mapping");
            }
            var map = AsMap(rawMedia[i], $"media entry #{i}");
            var missing = new[] { "name", "label", "byte1", "byte2" }.Where(f => !map.ContainsKey(f)).ToList();
            if (missing.Count > 0)
            {
                throw new ConfigException($"media entry #{i}: missing required field(s) [{string.Join(", ", missing)}]");
            }
            string name = AsString(map["name"], $"media entry #{i}.name");
            if (!NameRe.IsMatch(name))
            {
                throw new ConfigException(
                    $"media entry #{i} ('{name}'): 'name' must contain only ASCII lowercase letters and underscores");
            }
            var spec = new MediaSpec
            {
                Label = AsString(map["label"], $"media '{name}'.label"),
                Byte1 = ParseInt(map["byte1"], $"media '{name}'.byte1", 0, 255),
                Byte2 = ParseInt(map["byte2"], $"media '{name}'.byte2", 0, 255),
            };
            if (!table.TryAdd(name, spec))
            {
                throw new ConfigException($"{path}: duplicate media name '{name}'");
            }
        }
        return table;
    }
}
