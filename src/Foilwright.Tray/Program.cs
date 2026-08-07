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
    /// (--resolution / --media / --halftone / --white-mode)。実機・UI 無しで
    /// 設定項目ごとの挙動を確認するための検証用オプション(DOMAIN §7.1)。</summary>
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
            string mediaName = settings.MediaName;
            string halftone = settings.Halftone;
            string whiteMode = settings.WhiteMode;
            for (int i = 0; i < extraArgs.Length - 1; i++)
            {
                switch (extraArgs[i])
                {
                    case "--resolution": resolutionKey = extraArgs[i + 1]; i++; break;
                    case "--media": mediaName = extraArgs[i + 1]; i++; break;
                    case "--halftone": halftone = extraArgs[i + 1]; i++; break;
                    case "--white-mode": whiteMode = extraArgs[i + 1]; i++; break;
                }
            }

            var result = JobPipeline.BuildPreview(
                psPath, repoRoot, route, settings.InkMode, settings.PaperName, mediaName,
                resolutionKey, halftone, whiteMode);

            result.Preview.Save(outputPngPath, System.Drawing.Imaging.ImageFormat.Png);

            Console.WriteLine($"プレビュー画像: {outputPngPath}");
            Console.WriteLine(
                $"パス数: {result.Inks.Count} / 解像度: {result.Resolution.Key} / メディア: {mediaName} / " +
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
}
