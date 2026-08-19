// Foilwright.Tray — ジョブの変換(Ghostscript → 切り出し → プレーン分解)と
// 送出をまとめる層。PreviewForm から呼ばれる。
//
// Foilwright.Cli.Program の HandleJob と同じ流れを踏襲しているが、Cli の
// private メンバーは参照できない(別エージェントが編集中のため無変更)ので
// ここに複製してある。パス数・使用インク・順序を UI に渡す点、送出を
// 独立したメソッドに分けてある点が Cli との違い(プレビューと送出を
// 分離するため)。

using Foilwright.Core;

namespace Foilwright.Tray;

public sealed class JobConfig
{
    public required ProfileSpec Profile { get; init; }
    public required PaperSpec Paper { get; init; }
    public required MediaSpec Media { get; init; }
    public required List<InkDefinition> Palette { get; init; }
}

/// <summary>プレビュー用のインク 1 件分の要約(§7.2「ジョブ内容の表示」)。</summary>
public sealed class InkPreviewInfo
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required int Order { get; init; }
    public required int Passes { get; init; }
    public required int PrinterCode { get; init; }
    public required System.Drawing.Color Color { get; init; }
}

public sealed class PreviewResult : IDisposable
{
    public required System.Drawing.Bitmap Preview { get; init; }
    public required IReadOnlyList<InkPreviewInfo> Inks { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>このプレビューを作った際に解決した解像度。Emitter.PrintJob.Resolution
    /// に渡す値(ResolutionEntry.DpiX)はこれを使う — Print() を呼ぶ側が
    /// 再度プロファイルを読んで解決し直す必要をなくす。</summary>
    public required ResolutionEntry Resolution { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せるプレーン。</summary>
    public required Dictionary<string, byte[]> Planes { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せる印刷順のインク一覧。</summary>
    public required List<JobInk> JobInks { get; init; }

    /// <summary>ジョブが実際に使うインクの定義一覧(Barcode を含む)。
    /// カセットの過不足判定(§7.3 / D-026 / CassetteCheck)に渡す。</summary>
    public required IReadOnlyList<InkDefinition> RequiredInks { get; init; }

    /// <summary>Ghostscript で変換し、用紙寸法で切り出し済みの画像。D-028 補足:
    /// インク除外の切り替えでは Ghostscript を再実行せず、この画像を保持した
    /// まま <see cref="JobPipeline.RebuildFromImage"/> でジョブ組み立てだけを
    /// やり直す。</summary>
    public required PpmImage Image { get; set; }

    /// <summary>このプレビューを組み立てた際のジョブ設定(パレット・用紙・
    /// メディア・プロファイル)。RebuildFromImage の再呼び出しに必要。</summary>
    public required JobConfig Config { get; set; }

    /// <summary>Bitmap(GDI ハンドル)と、切り出し済み画像・プレーン(管理ヒープ上の
    /// 大きなバイト配列。A4/600dpi で約 68MB、1200x600 で約 137MB)を解放する。
    /// 古いプレビューを差し替える際は必ずこれを呼ぶこと(DOMAIN §7.2 補足)。</summary>
    public void Dispose()
    {
        Preview.Dispose();
        Planes.Clear();
        Image = null!;
        Config = null!;
    }
}

public static class JobPipeline
{
    // Foilwright.Cli.Program の既定値と同じ(D-024 のトレイアプリ設定既定値)。
    public const string DefaultResolutionKey = "600";
    public const string DefaultPaperName = "a4";
    public const string DefaultMediaName = "plain_paper";
    public const string DefaultHalftone = "none";
    public const string DefaultWhiteMode = "auto";

    // D-029: 色補正の既定は photo。photo_colcor テーブルはリポジトリ直下
    // colour/photo_colcor.bin に同梱してある(D-029 §3)。
    public const string DefaultColourCorrection = "photo";

    private const int PreviewMaxWidth = 900;

    /// <summary>実行アセンブリの場所からリポジトリ直下を探す。
    /// Foilwright.Cli.Program.FindRepoRoot と同じ規則(bin/Debug/net10.0-windows
    /// から 5 階層上がる)。</summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir.Parent is null)
            {
                break;
            }
            dir = dir.Parent;
        }
        return dir.FullName;
    }

    public static JobConfig LoadJobConfig(string repoRoot, MachineRoute route, string paperName, string mediaName)
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(repoRoot, "profiles", route.ProfileFileName));
        var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(repoRoot, "papers"));
        if (!paperTable.TryGetValue(paperName, out var paper))
        {
            throw new ConfigException($"paper '{paperName}' not found in paper table '{profile.PaperTable}'");
        }
        var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(repoRoot, "media.yaml"));
        if (!mediaTable.TryGetValue(mediaName, out var media))
        {
            throw new ConfigException($"media '{mediaName}' not found in media.yaml");
        }
        var palette = ConfigLoader.LoadPalette(Path.Combine(repoRoot, "palette", "default.yaml"));
        return new JobConfig { Profile = profile, Paper = paper, Media = media, Palette = palette };
    }

    /// <summary>PostScript ファイルを変換し、プレビュー用のビットマップと
    /// ジョブ情報を組み立てる。送出は一切行わない(UI 側が確認するまで印刷は
    /// 始まってはならない。DOMAIN §7.2)。
    ///
    /// resolutionKey: ResolutionEntry.Key の形式(例: "600" / "1200x600")。
    /// プロファイルの resolutions から解決する(DOMAIN §4.5: コードに埋め込まない)。</summary>
    public static PreviewResult BuildPreview(
        string psPath, string repoRoot, MachineRoute route, string inkMode,
        string paperName, string mediaName, string resolutionKey, string halftone, string whiteMode,
        IReadOnlySet<string>? excludedInks = null, string colourCorrection = DefaultColourCorrection)
    {
        var config = LoadJobConfig(repoRoot, route, paperName, mediaName);
        var resolutionEntry = config.Profile.ResolveResolutionByKey(resolutionKey);
        string ppmPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ppm");
        try
        {
            Ghostscript.ConvertToPpm(psPath, ppmPath, resolutionEntry.DpiX, resolutionEntry.DpiY);
            var fullImage = PpmImage.Read(ppmPath);
            // 用紙表は 600dpi 基準のため、選んだ解像度へ換算してから切り出す
            // (DOMAIN §7.1: 1200x600 は幅方向だけ 2 倍)。
            var scaledPaper = config.Paper.ScaleToResolution(resolutionEntry.DpiX, resolutionEntry.DpiY);
            var image = fullImage.Crop(scaledPaper.LeftMargin, scaledPaper.TopMargin, scaledPaper.Width, scaledPaper.Length);

            return BuildPreviewCore(image, config, resolutionEntry, inkMode, halftone, whiteMode, excludedInks, colourCorrection);
        }
        finally
        {
            TryDelete(ppmPath);
        }
    }

    /// <summary>切り出し済みの画像を保持したまま、ジョブ組み立て(インク割り当て・
    /// プレーン分解・プレビュー描画)だけをやり直す。Ghostscript は再実行しない
    /// (D-028 補足)。プレビュー画面でインクの除外(チェック)を切り替えたときに使う。
    ///
    /// excludedInks: D-028 の「除外 = そのジョブのパレットからそのインクを外す」を
    /// 実現する集合。ここに含まれるインクは <paramref name="config"/>.Palette から
    /// 除いたうえで組み立てるため、`auto` では該当画素がそのまま CMYK 分解へ回る
    /// (プレーンを作ってから捨てるのではない)。</summary>
    public static PreviewResult RebuildFromImage(
        PpmImage image, JobConfig config, ResolutionEntry resolution,
        string inkMode, string halftone, string whiteMode,
        IReadOnlySet<string>? excludedInks, string colourCorrection = DefaultColourCorrection)
    {
        return BuildPreviewCore(image, config, resolution, inkMode, halftone, whiteMode, excludedInks, colourCorrection);
    }

    /// <summary>BuildPreview と RebuildFromImage の共通処理(インク割り当て以降)。
    /// D-028: excludedInks に含まれるインクはパレットから除いてから
    /// JobAssembly.BuildJobPlanes に渡す — プレーンを作ってから捨てるのではない。
    /// D-029: colourCorrection == "photo" のとき、photo_colcor テーブル
    /// (colour/photo_colcor.bin、リポジトリ直下から解決)と選択中の解像度を
    /// JobAssembly.BuildJobPlanes へ渡す。ガンマの既定値が解像度で変わるため
    /// (600 は 0.8、1200 は -0.9)、解像度を渡し忘れると色がずれる。</summary>
    private static PreviewResult BuildPreviewCore(
        PpmImage image, JobConfig config, ResolutionEntry resolutionEntry,
        string inkMode, string halftone, string whiteMode, IReadOnlySet<string>? excludedInks,
        string colourCorrection)
    {
        var palette = excludedInks is { Count: > 0 }
            ? config.Palette.Where(ink => !excludedInks.Contains(ink.Name)).ToList()
            : config.Palette;

        string repoRoot = FindRepoRoot();
        string photoLutPath = Path.Combine(repoRoot, "colour", "photo_colcor.bin");

        var jobPlanes = JobAssembly.BuildJobPlanes(
            image, palette, inkMode, halftone, whiteMode, colourCorrection, resolutionEntry.DpiX, photoLutPath);

        var planes = jobPlanes.ToDictionary(jp => jp.Ink.Name, jp => jp.Plane);
        var jobInks = jobPlanes
            .Select(jp => new JobInk { Name = jp.Ink.Name, PrinterCode = jp.Ink.PrinterCode })
            .ToList();
        var inkInfos = jobPlanes
            .Select(jp => new InkPreviewInfo
            {
                Name = jp.Ink.Name,
                Label = jp.Ink.Label,
                Order = jp.Ink.Order,
                Passes = jp.Ink.Passes,
                PrinterCode = jp.Ink.PrinterCode,
                Color = PreviewRenderer.ResolveDisplayColor(jp.Ink),
            })
            .ToList();

        var bitmap = PreviewRenderer.Render(image.Width, image.Height, jobPlanes, PreviewMaxWidth);

        return new PreviewResult
        {
            Preview = bitmap,
            Inks = inkInfos,
            Width = image.Width,
            Height = image.Height,
            Planes = planes,
            JobInks = jobInks,
            RequiredInks = jobPlanes.Select(jp => jp.Ink).ToList(),
            Resolution = resolutionEntry,
            Image = image,
            Config = config,
        };
    }

    /// <summary>RGL を組み立てるだけで送出しない。実機を消費せずに
    /// バイト列を検査するための経路(§9.5: バイト列の検証と実機の刷り上がり
    /// 確認は到達範囲が異なる)。</summary>
    public static byte[] BuildRgl(Dictionary<string, byte[]> planes, PrintJob job)
    {
        return Emitter.EmitJob(planes, job);
    }

    /// <summary>実機へ送出する。DOMAIN §15.2.1: トレイアプリが送出を排他的に
    /// 所有する — この呼び出しの間は状態問い合わせ(ReadRawStatus)を挟んでは
    /// ならない(呼び出し側 UI が busy フラグで担保する)。</summary>
    public static void Print(
        Dictionary<string, byte[]> planes, PrintJob job, MachineRoute route, string vid, Action<int, int>? progress)
    {
        byte[] rgl = Emitter.EmitJob(planes, job);
        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        transport.SendJob(rgl, progress);
    }

    /// <summary>プリンタ状態を生の値のまま読む(§7.2 の 7「プリンタ状態表示」)。
    /// カセットの過不足判定(§7.3)のデコードは別エージェントが実装中のため、
    /// ここでは Foilwright.Core.CassetteStatus をそのまま返すに留める。</summary>
    public static CassetteStatus ReadRawStatus(MachineRoute route, string vid)
    {
        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        return transport.ReadStatus();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はジョブの成否に影響しないため無視する。
        }
    }
}
