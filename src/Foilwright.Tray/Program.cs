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

using System.Windows.Forms;
using Foilwright.Core;

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

            string repoRoot = JobPipeline.FindRepoRoot();
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
                }
            }
            if (Array.IndexOf(extraArgs, "--no-curl-correction") >= 0)
            {
                noCurlCorrection = true;
            }
            route = MachineRoute.Resolve(machine);

            var config = JobPipeline.LoadJobConfig(repoRoot, route, paperName, mediaName);

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

            Console.WriteLine($"使うインク(D-030): {string.Join(", ", usedInks.OrderBy(n => n, StringComparer.Ordinal))}");
            Console.WriteLine($"色補正(D-029): {colourCorrection}");
            Console.WriteLine(
                $"パス数の上書き(D-031): {(passesOverride.Count == 0 ? "(なし。パレットの既定値を使用)" : string.Join(", ", passesOverride.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))}");

            var result = JobPipeline.BuildPreview(
                psPath, repoRoot, route, settings.InkMode, paperName, mediaName,
                resolutionKey, halftone, whiteMode, usedInks, passesOverride, colourCorrection);
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
            Console.Error.WriteLine($"エラー: {ex.Message}");
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

            string repoRoot = JobPipeline.FindRepoRoot();
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
                }
            }

            var config = JobPipeline.LoadJobConfig(repoRoot, route, paperName, mediaName);
            var usedInks = useInksArg ?? settings.ResolveUsedInks(config.Palette);
            if (excludeInksArg is { Count: > 0 })
            {
                usedInks = new HashSet<string>(usedInks.Where(name => !excludeInksArg.Contains(name)));
            }
            var passesOverride = passesArg ?? settings.PassesOverride ?? new Dictionary<string, int>();

            Console.WriteLine($"使うインク(D-030): {string.Join(", ", usedInks.OrderBy(n => n, StringComparer.Ordinal))}");
            Console.WriteLine($"色補正(D-029): {colourCorrection}");
            Console.WriteLine(
                $"パス数の上書き(D-031): {(passesOverride.Count == 0 ? "(なし。パレットの既定値を使用)" : string.Join(", ", passesOverride.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}")))}");

            var result = JobPipeline.BuildPreview(
                psPath, repoRoot, route, settings.InkMode, paperName, mediaName,
                resolutionKey, halftone, whiteMode, usedInks, passesOverride, colourCorrection);

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
            Console.Error.WriteLine($"エラー: {ex.Message}");
            Environment.ExitCode = 1;
        }
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
}
