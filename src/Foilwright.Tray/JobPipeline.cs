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

public sealed class PreviewResult
{
    public required System.Drawing.Bitmap Preview { get; init; }
    public required IReadOnlyList<InkPreviewInfo> Inks { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せるプレーン。</summary>
    public required Dictionary<string, byte[]> Planes { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せる印刷順のインク一覧。</summary>
    public required List<JobInk> JobInks { get; init; }
}

public static class JobPipeline
{
    // Foilwright.Cli.Program の既定値と同じ(D-024 のトレイアプリ設定既定値)。
    public const int DefaultResolution = 600;
    public const string DefaultPaperName = "a4";
    public const string DefaultMediaName = "plain_paper";

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
    /// 始まってはならない。DOMAIN §7.2)。</summary>
    public static PreviewResult BuildPreview(
        string psPath, string repoRoot, MachineRoute route, string inkMode,
        string paperName, string mediaName, int resolution)
    {
        var config = LoadJobConfig(repoRoot, route, paperName, mediaName);
        string ppmPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ppm");
        try
        {
            Ghostscript.ConvertToPpm(psPath, ppmPath, resolution);
            var fullImage = PpmImage.Read(ppmPath);
            var image = fullImage.Crop(config.Paper.LeftMargin, config.Paper.TopMargin, config.Paper.Width, config.Paper.Length);

            var jobPlanes = JobAssembly.BuildJobPlanes(image, config.Palette, inkMode);

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
            };
        }
        finally
        {
            TryDelete(ppmPath);
        }
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
