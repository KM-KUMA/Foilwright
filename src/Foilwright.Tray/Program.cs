// Foilwright.Tray — トレイアプリのエントリポイント(D-024)。
//
// 通常起動: タスクトレイに常駐し、名前付きパイプ \\.\pipe\foilwright で
// ジョブを待つ(TrayApplicationContext)。
//
// デバッグ起動その 1: --debug-ps <PostScript ファイル> を渡すと、パイプも
// トレイも使わずプレビュー画面だけを直接開く。実機・スプーラが無くても
// プレビューの動作を確認するための隠しコマンド(タスク仕様の検証手順)。
// 例: Foilwright.Tray.exe --debug-ps ..\..\dumps\spool.ps
//
// デバッグ起動その 2: --debug-preview-png <PostScript ファイル> <出力 PNG> を
// 渡すと、ウィンドウを一切開かずプレビュー画像だけを PNG として保存して
// 終了する。対話的デスクトップが無い環境(自動検証など)でも、実際に
// 生成されるプレビュー画像を確認できるようにするための経路。

using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Foilwright.Core;

// 引数解析(ParseMagicRgbArg など)は UI もプリンタも要らない純粋な処理であり、
// 単体テストの対象にする。テストアセンブリにだけ internal を見せる。
[assembly: InternalsVisibleTo("Foilwright.Tray.Tests")]

namespace Foilwright.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        int pngIndex = Array.IndexOf(args, "--debug-preview-png");
        if (pngIndex >= 0 && pngIndex + 2 < args.Length)
        {
            RunDebugPreviewPng(args[pngIndex + 1], args[pngIndex + 2], args.Skip(pngIndex + 3).ToArray());
            return;
        }

        int rglIndex = Array.IndexOf(args, "--debug-rgl");
        if (rglIndex >= 0 && rglIndex + 2 < args.Length)
        {
            RunDebugRgl(args[rglIndex + 1], args[rglIndex + 2], args.Skip(rglIndex + 3).ToArray());
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        int debugIndex = Array.IndexOf(args, "--debug-ps");
        if (debugIndex >= 0 && debugIndex + 1 < args.Length)
        {
            RunDebugPreview(args[debugIndex + 1]);
            return;
        }

        Application.Run(new TrayApplicationContext());
    }

    /// <summary>実機・パイプ無しでプレビューだけを確認するための経路。
    /// 送出(Print)ボタンを押した場合は通常どおり実機へ送出しようとするため、
    /// 検証時は Print ボタンを押さないこと(呼び出し元の運用で担保する)。</summary>
    private static void RunDebugPreview(string psPath)
    {
        if (!File.Exists(psPath))
        {
            MessageBox.Show(
                $"ファイルが見つかりません: {psPath}",
                "Foilwright デバッグ起動",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var settings = TraySettings.Load();
        using var form = new PreviewForm(psPath, settings);
        Application.Run(form);
    }

    /// <summary>ウィンドウを開かずプレビュー画像を PNG として保存する。
    /// 対話的デスクトップの有無に依存しない検証経路(実機・送出は一切行わない)。
    /// 終了コード: 0=成功、1=失敗(標準エラーに理由を出す)。
    ///
    /// extraArgs: 保存済み設定(TraySettings)を上書きする任意のオプション
    /// (--resolution / --paper / --media / --halftone / --white-mode)。実機・UI 無しで
    /// 設定項目ごとの挙動を確認するための検証用オプション(DOMAIN §7.1)。</summary>
    /// <summary>PostScript から RGL を組み立ててファイルへ書き出す。
    /// **送出はしない。** 実機を消費せずにバイト列を検査するための経路で、
    /// 送るかどうかは書き出した内容を確認してから別途判断する(§9.5)。</summary>
    private static void RunDebugRgl(string psPath, string outputRglPath, string[] extraArgs)
    {
        try
        {
            if (!File.Exists(psPath))
            {
                Console.Error.WriteLine($"ファイルが見つかりません: {psPath}");
                Environment.ExitCode = 1;
                return;
            }

            string assetRoot = AssetRoot.ResolveDefault();
            var settings = TraySettings.Load();
            var route = MachineRoute.Resolve(settings.Machine);

            string resolutionKey = settings.ResolutionKey;
            string paperName = settings.PaperName;
            string mediaName = settings.MediaName;
            string halftone = settings.Halftone;
            string whiteMode = settings.WhiteMode;
            string colourCorrection = settings.ColourCorrection;
            bool noCurlCorrection = settings.NoCurlCorrection;
            string machine = settings.Machine;
            // D-030: UI 無しで許可リストの効果を検証するための隠しオプション
            // (カンマ区切りのインク名。src/Foilwright.Tray/PreviewForm.cs のチェック列と同義)。
            // --use-inks は許可リストそのものを指定し、--exclude-inks はそこから
            // 引く(両方指定時は --use-inks を先に適用してから --exclude-inks を引く)。
            HashSet<string>? useInksArg = null;
            HashSet<string>? excludeInksArg = null;
            // D-031: UI 無しでパス数の上書きを検証するための隠しオプション
            // (カンマ区切りの ink=n。src/Foilwright.Tray/PreviewForm.cs の「パス数」列と同義)。
            Dictionary<string, int>? passesArg = null;
            // D-042: UI 無しでマジックカラーの上書きを検証するための隠しオプション
            // (カンマ区切りの ink=#RRGGBB / ink=none。PreviewForm の「色」列と同義)。
            Dictionary<string, int[]?>? magicRgbArg = null;
            // D-048: UI 無しで塗る範囲を検証するための隠しオプション
            // (カンマ区切りの ink=none|artwork|full。PreviewForm の「塗る範囲」列と同義)。
            // **実機での確認に使う経路**であり、RGL 側とプレビュー PNG 側の両方で
            // 同じように解決する(片方だけ直すと動作がずれる)。
            Dictionary<string, string>? coverageArg = null;
            for (int i = 0; i < extraArgs.Length - 1; i++)
            {
                switch (extraArgs[i])
                {
                    case "--resolution": resolutionKey = extraArgs[i + 1]; i++; break;
                    case "--paper": paperName = extraArgs[i + 1]; i++; break;
                    case "--media": mediaName = extraArgs[i + 1]; i++; break;
                    case "--halftone": halftone = extraArgs[i + 1]; i++; break;
                    case "--white-mode": whiteMode = extraArgs[i + 1]; i++; break;
                    case "--colour-correction": colourCorrection = extraArgs[i + 1]; i++; break;
                    case "--machine": machine = extraArgs[i + 1]; i++; break;
                    case "--magic-rgb":
                        magicRgbArg = ParseMagicRgbArg(extraArgs[i + 1]);
                        i++;
                        break;
                    case "--use-inks":
                        useInksArg = extraArgs[i + 1]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet();
                        i++;
                        break;
                    case "--exclude-inks":
                        excludeInksArg = extraArgs[i + 1]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet();
                        i++;
                        break;
                    case "--passes":
                        passesArg = ParsePassesArg(extraArgs[i + 1]);
                        i++;
                        break;
                    case "--coverage":
                        coverageArg = ParseCoverageArg(extraArgs[i + 1]);
                        i++;
                        break;
                }
            }
            if (Array.IndexOf(extraArgs, "--no-curl-correction") >= 0)
            {
                noCurlCorrection = true;
            }
            route = MachineRoute.Resolve(machine);

            var config = JobPipeline.LoadJobConfig(assetRoot, route, paperName, mediaName);

            // D-030: --use-inks が指定されていればそれを許可リストの起点にし、
            // 無ければ TraySettings の既定(旧設定はメタリック無効)を使う。
            // --exclude-inks は最後に必ず適用する(D-028 の隠しオプションを維持)。
            var usedInks = useInksArg ?? settings.ResolveUsedInks(config.Palette);
            if (excludeInksArg is { Count: > 0 })
            {
                usedInks = new HashSet<string>(usedInks.Where(name => !excludeInksArg.Contains(name)));
            }

            // D-031: --passes が指定されていればそれを上書きの起点にし、
            // 無ければ TraySettings の既定(保存済みの上書き)を使う。
            var passesOverride = passesArg ?? settings.PassesOverride ?? new Dictionary<string, int>();

            // D-042: --magic-rgb が指定されていればそれを上書きの起点にし、
            // 無ければ TraySettings の既定(保存済みの上書き)を使う。
            var magicRgbOverride = magicRgbArg ?? settings.MagicRgbOverride ?? new Dictionary<string, int[]?>();
            // D-042: 打ち間違いはその場で止める。
            if (magicRgbArg is not null) { RejectUnknownInkNames(magicRgbArg, config.Palette); }

            // D-048: --coverage が指定されていればそれを使い、無ければ TraySettings の
            // 既定(保存済みの指定)を使う。どちらも無ければ空 = coverage インクは刷られない。
            var coverageModes = coverageArg ?? settings.CoverageModes ?? new Dictionary<string, string>();
            // D-048: 打ち間違い(綴り・coverage でないインク)はその場で止める。
            if (coverageArg is not null)
            {
                RejectUnknownInkNames(coverageArg, config.Palette, "--coverage", "D-048");
                RejectNonCoverageInks(coverageArg, config.Palette);
            }

            Console.WriteLine($"使うインク(D-030): {string.Join(", ", usedInks.OrderBy(n => n, StringComparer.Ordinal))}");
            Console.WriteLine($"色補正(D-029): {colourCorrection}");
            Console.WriteLine(
                $"パス数の上書き(D-031): {(passesOverride.Count == 0 ? "(なし。パレットの既定値を使用)" : string.Join(", ", passesOverride.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))}");
            Console.WriteLine($"マジックカラーの上書き(D-042): {FormatMagicRgbOverride(magicRgbOverride)}");
            Console.WriteLine($"塗る範囲(D-048): {FormatCoverageModes(coverageModes)}");

            var result = JobPipeline.BuildPreview(
                psPath, assetRoot, route, settings.InkMode, paperName, mediaName,
                resolutionKey, halftone, whiteMode, usedInks, passesOverride, colourCorrection, magicRgbOverride,
                coverageModes);
            var job = new PrintJob
            {
                // Emitter.EmitJob は Paper を常に 600dpi 基準で受け取り、
                // Resolution に応じた換算を内部で行う(未換算のまま渡す)。
                Resolution = result.Resolution.DpiX,
                Paper = config.Paper,
                Media = config.Media,
                Inks = result.JobInks,
                Width = result.Width,
                Height = result.Height,
                NoCurlCorrection = noCurlCorrection,
            };
            byte[] rgl = JobPipeline.BuildRgl(result.Planes, job);
            File.WriteAllBytes(outputRglPath, rgl);

            Console.WriteLine($"RGL: {outputRglPath} ({rgl.Length} バイト)");
            Console.WriteLine(
                $"機種: {machine} / パス数: {result.Inks.Count} / 解像度: {result.Resolution.Key} / " +
                // 用紙名とサイズ(= 印字可能領域。result.Width/Height は用紙表の
                // left_margin/top_margin/width/length で切り出し済み。§3.6.1)を
                // ログへ出す(用紙の取り違え検出。§15.10.2)。
                $"用紙: {paperName} / メディア: {mediaName} / 白版モード: {whiteMode} / サイズ: {result.Width}x{result.Height}");
            foreach (var ink in result.Inks)
            {
                Console.WriteLine($"  order={ink.Order} label={ink.Label} passes={ink.Passes}");
            }
        }
        catch (Exception ex) when (ex is GhostscriptException or ConfigException or PpmFormatException or MachineRouteException)
        {
            Console.Error.WriteLine($"エラー: {DescribeUserError(ex)}");
            Environment.ExitCode = 1;
        }
    }

    private static void RunDebugPreviewPng(string psPath, string outputPngPath, string[] extraArgs)
    {
        try
        {
            if (!File.Exists(psPath))
            {
                Console.Error.WriteLine($"ファイルが見つかりません: {psPath}");
                Environment.ExitCode = 1;
                return;
            }

            string assetRoot = AssetRoot.ResolveDefault();
            var settings = TraySettings.Load();
            var route = MachineRoute.Resolve(settings.Machine);

            string resolutionKey = settings.ResolutionKey;
            string paperName = settings.PaperName;
            string mediaName = settings.MediaName;
            string halftone = settings.Halftone;
            string whiteMode = settings.WhiteMode;
            string colourCorrection = settings.ColourCorrection;
            // D-030: UI 無しで許可リストの効果を検証するための隠しオプション。
            // --use-inks は許可リストそのものを指定し、--exclude-inks はそこから
            // 引く(両方指定時は --use-inks を先に適用してから --exclude-inks を引く)。
            HashSet<string>? useInksArg = null;
            HashSet<string>? excludeInksArg = null;
            Dictionary<string, int>? passesArg = null;
            // D-042: RunDebugRgl と同じ隠しオプション(ink=#RRGGBB / ink=none)。
            Dictionary<string, int[]?>? magicRgbArg = null;
            // D-048: UI 無しで塗る範囲を検証するための隠しオプション
            // (カンマ区切りの ink=none|artwork|full。PreviewForm の「塗る範囲」列と同義)。
            // **実機での確認に使う経路**であり、RGL 側とプレビュー PNG 側の両方で
            // 同じように解決する(片方だけ直すと動作がずれる)。
            Dictionary<string, string>? coverageArg = null;
            for (int i = 0; i < extraArgs.Length - 1; i++)
            {
                switch (extraArgs[i])
                {
                    case "--resolution": resolutionKey = extraArgs[i + 1]; i++; break;
                    case "--paper": paperName = extraArgs[i + 1]; i++; break;
                    case "--media": mediaName = extraArgs[i + 1]; i++; break;
                    case "--halftone": halftone = extraArgs[i + 1]; i++; break;
                    case "--white-mode": whiteMode = extraArgs[i + 1]; i++; break;
                    case "--colour-correction": colourCorrection = extraArgs[i + 1]; i++; break;
                    case "--magic-rgb":
                        magicRgbArg = ParseMagicRgbArg(extraArgs[i + 1]);
                        i++;
                        break;
                    case "--use-inks":
                        useInksArg = extraArgs[i + 1]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet();
                        i++;
                        break;
                    case "--exclude-inks":
                        excludeInksArg = extraArgs[i + 1]
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .ToHashSet();
                        i++;
                        break;
                    case "--passes":
                        passesArg = ParsePassesArg(extraArgs[i + 1]);
                        i++;
                        break;
                    case "--coverage":
                        coverageArg = ParseCoverageArg(extraArgs[i + 1]);
                        i++;
                        break;
                }
            }

            var config = JobPipeline.LoadJobConfig(assetRoot, route, paperName, mediaName);
            var usedInks = useInksArg ?? settings.ResolveUsedInks(config.Palette);
            if (excludeInksArg is { Count: > 0 })
            {
                usedInks = new HashSet<string>(usedInks.Where(name => !excludeInksArg.Contains(name)));
            }
            var passesOverride = passesArg ?? settings.PassesOverride ?? new Dictionary<string, int>();
            // D-042: RunDebugRgl と同じ解決順(コマンドライン → 保存済み設定 → 上書き無し)。
            var magicRgbOverride = magicRgbArg ?? settings.MagicRgbOverride ?? new Dictionary<string, int[]?>();
            // D-042: 打ち間違いはその場で止める。
            if (magicRgbArg is not null) { RejectUnknownInkNames(magicRgbArg, config.Palette); }

            // D-048: --coverage が指定されていればそれを使い、無ければ TraySettings の
            // 既定(保存済みの指定)を使う。どちらも無ければ空 = coverage インクは刷られない。
            var coverageModes = coverageArg ?? settings.CoverageModes ?? new Dictionary<string, string>();
            // D-048: 打ち間違い(綴り・coverage でないインク)はその場で止める。
            if (coverageArg is not null)
            {
                RejectUnknownInkNames(coverageArg, config.Palette, "--coverage", "D-048");
                RejectNonCoverageInks(coverageArg, config.Palette);
            }

            Console.WriteLine($"使うインク(D-030): {string.Join(", ", usedInks.OrderBy(n => n, StringComparer.Ordinal))}");
            Console.WriteLine($"色補正(D-029): {colourCorrection}");
            Console.WriteLine(
                $"パス数の上書き(D-031): {(passesOverride.Count == 0 ? "(なし。パレットの既定値を使用)" : string.Join(", ", passesOverride.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))}");
            Console.WriteLine($"マジックカラーの上書き(D-042): {FormatMagicRgbOverride(magicRgbOverride)}");
            Console.WriteLine($"塗る範囲(D-048): {FormatCoverageModes(coverageModes)}");

            var result = JobPipeline.BuildPreview(
                psPath, assetRoot, route, settings.InkMode, paperName, mediaName,
                resolutionKey, halftone, whiteMode, usedInks, passesOverride, colourCorrection, magicRgbOverride,
                coverageModes);

            result.Preview.Save(outputPngPath, System.Drawing.Imaging.ImageFormat.Png);

            Console.WriteLine($"プレビュー画像: {outputPngPath}");
            Console.WriteLine(
                $"パス数: {result.Inks.Count} / 解像度: {result.Resolution.Key} / " +
                // 用紙名とサイズ(= 印字可能領域。§3.6.1 / §15.10.2)をログへ出す
                // (用紙の取り違え検出)。
                $"用紙: {paperName} / メディア: {mediaName} / " +
                $"ハーフトーン: {halftone} / 白版モード: {whiteMode} / サイズ: {result.Width}x{result.Height}");
            foreach (var ink in result.Inks)
            {
                Console.WriteLine(
                    $"  order={ink.Order} label={ink.Label} passes={ink.Passes} color=#{ink.Color.R:x2}{ink.Color.G:x2}{ink.Color.B:x2}");
            }
        }
        catch (Exception ex) when (ex is GhostscriptException or ConfigException or PpmFormatException or MachineRouteException)
        {
            Console.Error.WriteLine($"エラー: {DescribeUserError(ex)}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>例外を利用者向けの文言にする。PPM が複数ページだったとき
    /// (PpmFormatException.IsMultiPage)だけ日本語の補足を添える。判定は
    /// 必ず IsMultiPage で行う — 文言の一致で判定すると、文言を変えた
    /// 途端に黙って補足が出なくなるため。
    ///
    /// PpmFormatException を捕まえる箇所は Program と PreviewForm の両方に
    /// あり(合計 6 箇所)、片方だけ直すと表示がずれる。共通化してここ 1 箇所
    /// に集約する。</summary>
    internal static string DescribeUserError(Exception ex)
    {
        if (ex is PpmFormatException { IsMultiPage: true })
        {
            // 日本語を先に、英語の原文は「詳細:」として後ろへ回す。英語が先頭だと
            // 利用者は自分に関係のある行(何をすればよいか)にたどり着く前に読むのを
            // やめてしまう。ページ数は英語側の文言が持っているので、詳細を残すことで
            // 「何ページ検出したか」も読める — 数を日本語側へ写すには文言を解析する
            // ことになり、IsMultiPage を目印にした意味が無くなる。
            return "複数ページの原稿には対応していません。1 ページずつ印刷してください。" + Environment.NewLine
                + "(印刷ダイアログの「部数」を 2 以上にすると、ドライバが複数ページの原稿を作るためこうなります)"
                + Environment.NewLine + Environment.NewLine
                + $"詳細: {ex.Message}";
        }
        return ex.Message;
    }

    /// <summary>D-031: `--passes white=4,black=2` の形式を解析する。範囲外
    /// (1〜8。TraySettings.MinPasses/MaxPasses)や非整数は黙って丸めず、その場で
    /// 拒否する(打ち間違いで生産終了品のリボンを失わないため。PreviewForm の
    /// CellValidating と同じ方針)。呼び出し元の catch (ConfigException) に
    /// 拾わせることで、UI 版と同様に「その場で拒否する」を CLI でも再現する。</summary>
    private static Dictionary<string, int> ParsePassesArg(string arg)
    {
        var result = new Dictionary<string, int>();
        foreach (string entry in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                throw new ConfigException($"--passes の形式が不正です(ink=n の形式にしてください): '{entry}'");
            }
            string inkName = parts[0];
            if (!int.TryParse(parts[1], out int passes)
                || passes < TraySettings.MinPasses || passes > TraySettings.MaxPasses)
            {
                throw new ConfigException(
                    $"--passes '{inkName}' の値が不正です。整数で {TraySettings.MinPasses}〜{TraySettings.MaxPasses} の範囲で指定してください(D-031): '{parts[1]}'");
            }
            result[inkName] = passes;
        }
        return result;
    }

    /// <summary>D-042: `--magic-rgb white=#000000,metallic_gold=none` の形式を解析する。
    /// 色は `#RRGGBB`(先頭の `#` は省略可)、`none` はそのインクの色を明示的に外す
    /// (= マジック判定に参加させない)ことを表し、辞書の値 null になる。
    /// 不正な書式は黙って無視せずその場で拒否する(ParsePassesArg と同じ方針)。</summary>
    internal static Dictionary<string, int[]?> ParseMagicRgbArg(string arg)
    {
        var result = new Dictionary<string, int[]?>();
        foreach (string entry in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                throw new ConfigException(
                    $"--magic-rgb の形式が不正です(ink=#RRGGBB または ink=none の形式にしてください): '{entry}'");
            }
            string inkName = parts[0];
            string value = parts[1];
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            {
                result[inkName] = null;
                continue;
            }
            string hex = value.StartsWith('#') ? value[1..] : value;
            if (hex.Length != 6
                || !int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out int r)
                || !int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g)
                || !int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
            {
                throw new ConfigException(
                    $"--magic-rgb '{inkName}' の値が不正です。#RRGGBB(16 進 6 桁)または none で指定してください(D-042): '{value}'");
            }
            result[inkName] = new[] { r, g, b };
        }
        return result;
    }

    /// <summary>D-042: --magic-rgb にパレットへ無いインク名が混じっていたら、その場で
    /// 止める。黙って無視すると「指定したのに何も変わらない」という追いにくい形に
    /// なるため(実際に `whte=#000000` が無反応で通ってしまった)。
    ///
    /// 判定するのはコマンドラインで渡された分だけで、settings.json に保存された
    /// 上書きは対象にしない — パレットからインクが消えた古い設定が残っていても、
    /// 印刷そのものを止めてしまわないようにするため(該当項目は無視される)。
    ///
    /// D-048 で --coverage からも使うため、値の型を問わない形(ジェネリック)に
    /// してある。option の既定値は "--magic-rgb" で、既存の呼び出しと文言は変わらない。</summary>
    internal static void RejectUnknownInkNames<TValue>(
        IReadOnlyDictionary<string, TValue> overrides, IReadOnlyList<InkDefinition> palette,
        string option = "--magic-rgb", string decision = "D-042")
    {
        var known = palette.Select(ink => ink.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = overrides.Keys
            .Where(name => !known.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (unknown.Count > 0)
        {
            throw new ConfigException(
                $"{option} にパレットへ無いインク名があります({decision}): {string.Join(", ", unknown)} / " +
                $"使えるインク名: {string.Join(", ", known.OrderBy(name => name, StringComparer.Ordinal))}");
        }
    }

    /// <summary>D-048: `--coverage glossy_finish=artwork,mf_ink=full` の形式を解析する。
    /// 値は none / artwork / full(TraySettings.CoverageModeValues)。知らないモードは
    /// 黙って既定へ落とさずその場で拒否する(ParsePassesArg / ParseMagicRgbArg と同じ方針)。
    ///
    /// **インク名がパレットにあるか、そのインクが coverage かはここでは見ない** —
    /// パレットを読む前でも解析できるようにするため。名前の検証は
    /// <see cref="RejectUnknownInkNames{TValue}"/>、coverage かどうかの検証は
    /// <see cref="RejectNonCoverageInks"/> が担う(呼び出し側が両方を通す)。</summary>
    internal static Dictionary<string, string> ParseCoverageArg(string arg)
    {
        var result = new Dictionary<string, string>();
        foreach (string entry in arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split('=', 2);
            if (parts.Length != 2 || parts[0].Length == 0)
            {
                throw new ConfigException(
                    $"--coverage の形式が不正です(ink=none|artwork|full の形式にしてください): '{entry}'");
            }
            string inkName = parts[0];
            string mode = parts[1];
            if (!TraySettings.CoverageModeValues.Contains(mode, StringComparer.Ordinal))
            {
                throw new ConfigException(
                    $"--coverage '{inkName}' の値が不正です。{string.Join(" / ", TraySettings.CoverageModeValues)} " +
                    $"のいずれかで指定してください(D-048): '{mode}'");
            }
            result[inkName] = mode;
        }
        return result;
    }

    /// <summary>D-048: --coverage に coverage でないインク(パレットで coverage: true が
    /// 付いていないインク)が混じっていたら、その場で止める。JobAssembly は
    /// coverage でないインクの指定を黙って無視するため、放置すると
    /// 「指定したのに何も出ない」という追いにくい形になる(--magic-rgb の綴り間違いと
    /// まったく同じ罠)。
    ///
    /// 名前がパレットに無い場合はここでは何も言わない — それは
    /// <see cref="RejectUnknownInkNames{TValue}"/> の担当であり、先に呼ぶ。</summary>
    internal static void RejectNonCoverageInks(
        IReadOnlyDictionary<string, string> coverageModes, IReadOnlyList<InkDefinition> palette)
    {
        var coverageInks = palette.Where(ink => ink.Coverage).Select(ink => ink.Name).ToHashSet(StringComparer.Ordinal);
        var known = palette.Select(ink => ink.Name).ToHashSet(StringComparer.Ordinal);
        var wrong = coverageModes.Keys
            .Where(name => known.Contains(name) && !coverageInks.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (wrong.Count > 0)
        {
            throw new ConfigException(
                $"--coverage は「塗る範囲で決まるインク」にしか指定できません(D-048): {string.Join(", ", wrong)} / " +
                $"指定できるインク名: {string.Join(", ", coverageInks.OrderBy(name => name, StringComparer.Ordinal))}");
        }
    }

    /// <summary>D-048: 塗る範囲をログ 1 行にまとめる(--passes と同じ調子)。</summary>
    private static string FormatCoverageModes(IReadOnlyDictionary<string, string> coverageModes) =>
        coverageModes.Count == 0
            ? "(なし。coverage インクは刷られない)"
            : string.Join(", ", coverageModes
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>D-042: マジックカラーの上書きをログ 1 行にまとめる(--passes と同じ調子)。</summary>
    private static string FormatMagicRgbOverride(IReadOnlyDictionary<string, int[]?> overrides) =>
        overrides.Count == 0
            ? "(なし。パレットの既定値を使用)"
            : string.Join(", ", overrides
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Value is { } rgb
                    ? $"{kv.Key}=#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}"
                    : $"{kv.Key}=none"));
}
