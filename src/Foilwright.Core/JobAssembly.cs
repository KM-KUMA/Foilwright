// Foilwright.Core — L2/L1 境界: インク指定方式(DOMAIN §6.6 / D-016)を選び、
// パレットから導出した情報だけでインク別プレーンを組み立て、ジョブに
// 含めるインクを決める。
//
// ここでの責務は 3 つ:
//   1. auto 方式で cmyk_map をパレットの channel フィールドから導出し
//      (D-019)、magic_rgb と channel の両方を持つ二役インク(黒)の
//      プレーンを 1 つに合成する(D-019 補足の後続課題)。
//   2. 中身の無い(1 ドットも立っていない)プレーンのインクを除外する。
//      パスは時間とリボンを消費し、装填していないカセットを要求すると
//      失敗するため(空パスを組む理由が無い)。
//   3. 白版モード(DOMAIN §7.1 / D-027)を適用する。設定がパレットの
//      auto_undercoat を上書きする — パレット側のフラグはあくまで既定値。
//      「白」がどのインクかは、パレットで auto_undercoat: true になっている
//      インクとして判別する(名前を決め打ちしない)。
//
// Raster.cs の既存関数(ToPlanes* 系)のシグネチャ・挙動は一切変更しない
// (golden 検証の対象のため)。本ファイルはそれらを呼び出す側。

namespace Foilwright.Core;

public static class JobAssembly
{
    /// <summary>サポートするインク指定方式の内部識別子(DOMAIN §6.6)。</summary>
    public static readonly IReadOnlyList<string> ValidInkModes = new[] { "auto", "per_page", "spot_only" };

    /// <summary>サポートする白版モードの内部識別子(DOMAIN §7.1 / D-027)。既定は "auto"。</summary>
    public static readonly IReadOnlyList<string> ValidWhiteModes = new[] { "none", "auto", "magic" };

    /// <summary>サポートするハーフトーンの内部識別子(DOMAIN §4.2.1)。既定は "none"。
    /// Raster.cs 内部の同名リストと値を揃えてある(呼び出し側の入力検証・
    /// エラーメッセージ用。Raster.cs 自体は変更しない)。</summary>
    public static readonly IReadOnlyList<string> ValidHalftones = new[] { "none", "halftone", "coarse_halftone" };

    /// <summary>画像とパレットから、実際にジョブへ含めるインクとその
    /// プレーンを、パレットの実行順(order 昇順、同値は記述順 — DOMAIN §4.3 /
    /// §4.9。ConfigLoader.LoadPalette が既にこの順で返す)で返す。
    ///
    /// inkMode: "auto" または "spot_only"。"per_page" は複数ページ入力を
    ///     要するためここでは扱わない(呼び出し側が事前に弾く)。
    ///
    /// whiteMode: "none"(白のプレーンを作らない)/ "auto"(既定。他インクが
    ///     乗る画素の和集合を白にする = auto_undercoat 相当)/ "magic"
    ///     (白の magic_rgb に一致した画素だけを白にする)のいずれか
    ///     (DOMAIN §7.1 / D-027)。パレットの auto_undercoat フラグを上書きする。
    ///
    /// 中身の無いプレーン(1 ドットも立っていない)のインクは戻り値から
    /// 除外する。全インクが空なら空リストを返す(呼び出し側はこれを見て
    /// 送出をスキップする)。
    ///
    /// colourCorrection: "none"/"plain"/"photo"(Raster.ToPlanes / ToPlanesAuto
    ///     と同じ)。既定は "photo"(D-029: 実物のフルカラー原稿で colcorPlain
    ///     の完全な下色除去が紫・緑・茶を黒一色に潰した実測を受けての決定)。
    /// resolution / photoLutPath: colourCorrection == "photo" のときだけ参照
    ///     する。Raster.ToPlanes / ToPlanesAuto にそのまま渡す。</summary>
    public static List<(InkDefinition Ink, byte[] Plane)> BuildJobPlanes(
        PpmImage image, IReadOnlyList<InkDefinition> palette, string inkMode, string halftone = "none", string whiteMode = "auto",
        string colourCorrection = "photo", int resolution = 600, string? photoLutPath = null)
    {
        if (!ValidWhiteModes.Contains(whiteMode))
        {
            throw new ArgumentException($"unknown white mode '{whiteMode}'; expected one of {string.Join(", ", ValidWhiteModes)}");
        }

        var adjustedPalette = ApplyWhiteMode(palette, whiteMode);

        Dictionary<string, byte[]> planes = inkMode switch
        {
            "auto" => BuildAutoPlanes(image, adjustedPalette, halftone, colourCorrection, resolution, photoLutPath),
            "spot_only" => Raster.ToPlanesMagic(image, adjustedPalette),
            "per_page" => throw new ArgumentException(
                "ink mode 'per_page' needs multiple page inputs; the caller must reject it before calling BuildJobPlanes"),
            _ => throw new ArgumentException($"unknown ink mode '{inkMode}'; expected one of {string.Join(", ", ValidInkModes)}"),
        };

        // 結果の一覧は常に元のパレット(白版モードで除外する前)を走査する。
        // "none" で除外したインクは adjustedPalette 側になく、planes 辞書にも
        // キーが無いため、下の TryGetValue が自然に false を返して除外される
        // (白版モード専用の特別扱いを増やさない)。
        var result = new List<(InkDefinition, byte[])>();
        foreach (var ink in palette)
        {
            if (!planes.TryGetValue(ink.Name, out var plane))
            {
                continue;
            }
            if (!PlaneHasContent(plane))
            {
                continue;
            }
            result.Add((ink, plane));
        }
        return result;
    }

    /// <summary>白版モード(DOMAIN §7.1 / D-027)を反映したパレットを作る。
    ///
    /// 「白」はパレットで auto_undercoat: true になっているインクとして
    /// 判別する(名前を決め打ちしない)。該当インクが 0 個または 2 個以上
    /// (2 個以上は Raster.ToPlanesMagic/ToPlanesAuto が例外にする構成)の
    /// 場合は、白版モードの適用対象が定まらないためパレットをそのまま返す。
    ///
    ///   - "none": 白インクをパレットから完全に除外する。マジックカラーの
    ///     直接一致も含め、一切プレーンを作らせない。
    ///   - "auto": 白インクの auto_undercoat を true に強制する(パレット側の
    ///     値が false でも、設定が上書きする)。
    ///   - "magic": 白インクの auto_undercoat を false に強制する。マジック
    ///     カラーへの直接一致分のみが白になる。</summary>
    private static List<InkDefinition> ApplyWhiteMode(IReadOnlyList<InkDefinition> palette, string whiteMode)
    {
        var whiteInks = palette.Where(ink => ink.AutoUndercoat).ToList();
        if (whiteInks.Count != 1)
        {
            return palette.ToList();
        }
        var whiteInk = whiteInks[0];

        return whiteMode switch
        {
            "none" => palette.Where(ink => !ReferenceEquals(ink, whiteInk)).ToList(),
            "auto" => palette.Select(ink => ReferenceEquals(ink, whiteInk) ? WithAutoUndercoat(ink, true) : ink).ToList(),
            "magic" => palette.Select(ink => ReferenceEquals(ink, whiteInk) ? WithAutoUndercoat(ink, false) : ink).ToList(),
            _ => throw new ArgumentException($"unknown white mode '{whiteMode}'; expected one of {string.Join(", ", ValidWhiteModes)}"),
        };
    }

    /// <summary>InkDefinition は record ではないため with 式が使えない。
    /// AutoUndercoat のみを差し替えた複製を作る。</summary>
    private static InkDefinition WithAutoUndercoat(InkDefinition ink, bool autoUndercoat) => new()
    {
        Name = ink.Name,
        Label = ink.Label,
        PrinterCode = ink.PrinterCode,
        Order = ink.Order,
        MagicRgb = ink.MagicRgb,
        Tolerance = ink.Tolerance,
        Channel = ink.Channel,
        Barcode = ink.Barcode,
        AutoUndercoat = autoUndercoat,
        Passes = ink.Passes,
    };

    /// <summary>"auto" 方式のプレーンを組み立てる。cmyk_map はコードに
    /// 埋め込まず、パレットの channel フィールドから導出する(D-019 / DOMAIN
    /// §4.5)。magic_rgb と channel の両方を持つ二役インク(既定パレットの
    /// black)は、Raster.ToPlanesAuto の内部では特色側のプレーンが CMYK 側で
    /// 上書きされてしまう(D-019 補足)ため、内部専用の一時キーで CMYK 側を
    /// 受け取ってから OR で合成する。</summary>
    private static Dictionary<string, byte[]> BuildAutoPlanes(
        PpmImage image, IReadOnlyList<InkDefinition> palette, string halftone,
        string colourCorrection, int resolution, string? photoLutPath)
    {
        var cmykMap = new Dictionary<string, string>();
        var twoRoleTempNames = new Dictionary<string, string>(); // 一時キー -> 実際のインク名

        foreach (var ink in palette)
        {
            if (ink.Channel is null)
            {
                continue;
            }
            if (ink.MagicRgb is not null)
            {
                // 二役インク: CMYK 側を一時キーで受け、後で特色側のプレーンへ OR 合成する。
                string tempName = ink.Name + "\0__cmyk_dup";
                cmykMap[ink.Channel] = tempName;
                twoRoleTempNames[tempName] = ink.Name;
            }
            else
            {
                cmykMap[ink.Channel] = ink.Name;
            }
        }

        var raw = Raster.ToPlanesAuto(image, palette, cmykMap, halftone, colourCorrection, resolution, photoLutPath);

        var merged = new Dictionary<string, byte[]>();
        foreach (var (name, buf) in raw)
        {
            if (twoRoleTempNames.ContainsKey(name))
            {
                continue;
            }
            merged[name] = buf;
        }

        foreach (var (tempName, actualName) in twoRoleTempNames)
        {
            byte[] cmykBuf = raw[tempName];
            // spotInks は常に自分自身のキーでゼロ初期化済みプレーンを持つため
            // (Raster.ToPlanesAuto)、merged[actualName] は必ず存在する。
            byte[] spotBuf = merged[actualName];
            for (int i = 0; i < spotBuf.Length; i++)
            {
                spotBuf[i] |= cmykBuf[i];
            }
        }

        return merged;
    }

    /// <summary>プレーンに 1 ドットでも立っているかを調べる。</summary>
    public static bool PlaneHasContent(byte[] plane)
    {
        foreach (byte b in plane)
        {
            if (b != 0)
            {
                return true;
            }
        }
        return false;
    }
}
