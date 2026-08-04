// Foilwright.Cli — トレイアプリの前身となるコンソールアプリ(Phase 2)。
//
// サブコマンド 3 つ:
//   status  プリンタの状態を読んで、装填カセットを人が読める形で表示する
//   print   既に組み立て済みの RGL バイト列ファイルを送出する
//   listen  名前付きパイプで PostScript を受け取り、変換して印刷する
//
// 設定ファイル(profiles/ papers/ palette/ media.yaml)はすべてリポジトリ
// 直下から読む。値をここにハードコードしない(DOMAIN §4.5)。
// 既定値: MD-5500 / 600dpi / A4 / 普通紙。

using System.IO.Pipes;
using Foilwright.Core;

namespace Foilwright.Cli;

internal static class Program
{
    // 既定ジョブ設定(D-024: トレイアプリの設定既定値。ここでは Phase 2 の
    // 固定値として置く。将来のトレイアプリ UI 化で設定画面に移す)。
    private const string DefaultModel = "md-5500";
    private const int DefaultResolution = 600;
    private const string DefaultPaperName = "a4";
    private const string DefaultMediaName = "plain_paper";
    private const string PipeName = "foilwright";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        try
        {
            switch (args[0])
            {
                case "status":
                    return RunStatus();
                case "print":
                    if (args.Length < 2)
                    {
                        Console.Error.WriteLine("使い方: Foilwright.Cli print <ジョブのバイト列ファイル>");
                        return 1;
                    }
                    return RunPrint(args[1]);
                case "listen":
                    return RunListen();
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex) when (ex is TransportException or GhostscriptException or ConfigException or PpmFormatException)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("使い方: Foilwright.Cli <status|print|listen> [引数]");
        Console.Error.WriteLine("  status              プリンタの状態(装填カセット)を表示する");
        Console.Error.WriteLine("  print <file>        RGL バイト列ファイルを送出する");
        Console.Error.WriteLine("  listen              名前付きパイプで PostScript を受け取り印刷する");
    }

    // --- リポジトリ内の設定ファイルの場所 --------------------------------------

    private static string FindRepoRoot()
    {
        // src/Foilwright.Cli/bin/Debug/net10.0 から 5 階層上がる
        // (Foilwright.Core.Tests/GoldenTests.cs と同じ規則)。
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

    private sealed class JobConfig
    {
        public required ProfileSpec Profile { get; init; }
        public required PaperSpec Paper { get; init; }
        public required MediaSpec Media { get; init; }
        public required List<InkDefinition> Palette { get; init; }
    }

    private static JobConfig LoadDefaultJobConfig(string repoRoot)
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(repoRoot, "profiles", DefaultModel + ".yaml"));
        var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(repoRoot, "papers"));
        if (!paperTable.TryGetValue(DefaultPaperName, out var paper))
        {
            throw new ConfigException($"paper '{DefaultPaperName}' not found in paper table '{profile.PaperTable}'");
        }
        var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(repoRoot, "media.yaml"));
        if (!mediaTable.TryGetValue(DefaultMediaName, out var media))
        {
            throw new ConfigException($"media '{DefaultMediaName}' not found in media.yaml");
        }
        var palette = ConfigLoader.LoadPalette(Path.Combine(repoRoot, "palette", "default.yaml"));
        return new JobConfig { Profile = profile, Paper = paper, Media = media, Palette = palette };
    }

    // --- status ------------------------------------------------------------------

    private static int RunStatus()
    {
        using var transport = AlpsTransport.OpenDevice();
        PrintDrainResult(transport);
        var status = transport.ReadStatus();
        PrintDeviceIdProbe(transport);

        Console.WriteLine($"ヘッダ: {Convert.ToHexString(status.Header)}");
        Console.WriteLine($"状態バイト(5 バイト目): 0x{status.StatusByte:x2}" +
            (status.StatusByte == 0x00 ? " (待機)" : status.StatusByte == 0x09 ? " (実行中)" : status.StatusByte == 0x01 ? " (完了/待機)" : " (未知)"));
        Console.WriteLine("カセットスロット(11 スロット、先頭バイトがバーコード番号、0xff = 未装着):");
        for (int i = 0; i < status.SlotBarcodes.Count; i++)
        {
            byte barcode = status.SlotBarcodes[i];
            string marker = i == CassetteStatus.HeadSlotIndex ? "  <- ヘッドに装着中" : string.Empty;
            string value = barcode == CassetteStatus.NotLoaded ? "未装着" : $"0x{barcode:x2}";
            Console.WriteLine($"  slot[{i,2}] = {value}{marker}");
        }
        return 0;
    }

    // --- print ---------------------------------------------------------------

    private static int RunPrint(string jobPath)
    {
        if (!File.Exists(jobPath))
        {
            Console.Error.WriteLine($"ファイルなし: {jobPath}");
            return 1;
        }
        byte[] rgl = File.ReadAllBytes(jobPath);
        Console.WriteLine($"ジョブ: {jobPath} ({rgl.Length} バイト)");

        using var transport = AlpsTransport.OpenDevice();
        PrintDrainResult(transport);
        var before = transport.ReadStatus();
        PrintDeviceIdProbe(transport);
        Console.WriteLine($"送出前状態バイト: 0x{before.StatusByte:x2}");

        transport.SendJob(rgl, (done, total) => Console.WriteLine($"  {done}/{total} バイト"));
        Console.WriteLine("送出完了");
        return 0;
    }

    /// <summary>デバイスを開いた直後のドレイン結果を表示する。0 バイトなら
    /// 何も表示しない(正常時は無言でよい)。0 でなければ、前回の会話の
    /// 読み残しが受信パイプに滞留していたことを意味する(実測で確認済みの
    /// 不具合の症状)。</summary>
    private static void PrintDrainResult(AlpsTransport transport)
    {
        if (transport.DrainedByteCount > 0)
        {
            Console.WriteLine($"受信パイプに {transport.DrainedByteCount} バイトの読み残しがあったため破棄しました");
        }
    }

    /// <summary>バルクの前置き GET_DEVICE_ID(DOMAIN §11.4/§15.3)の結果を表示する。
    /// 失敗しても送出自体は続くが、原因追跡のため必ず可視化する。</summary>
    private static void PrintDeviceIdProbe(AlpsTransport transport)
    {
        var probe = transport.LastDeviceIdProbe;
        if (probe is null)
        {
            return;
        }
        if (probe.Value.Success)
        {
            Console.WriteLine($"GET_DEVICE_ID: {probe.Value.DeviceId}");
        }
        else
        {
            Console.WriteLine($"GET_DEVICE_ID 失敗(送出は続行します): {probe.Value.Diagnostic}");
        }
    }

    // --- listen ----------------------------------------------------------------

    private static int RunListen()
    {
        string repoRoot = FindRepoRoot();
        var config = LoadDefaultJobConfig(repoRoot);

        Console.WriteLine($"名前付きパイプ \\\\.\\pipe\\{PipeName} で待ち受け中(Ctrl+C で終了)...");

        while (true)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
            Console.WriteLine("接続を待機中...");
            pipe.WaitForConnection();
            Console.WriteLine("接続を受理。PostScript を受信中...");

            try
            {
                HandleJob(pipe, config);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ジョブの処理に失敗しました: {ex.Message}");
            }
        }
    }

    private static void HandleJob(NamedPipeServerStream pipe, JobConfig config)
    {
        string psPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ps");
        string ppmPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ppm");

        try
        {
            using (var fileStream = File.Create(psPath))
            {
                pipe.CopyTo(fileStream);
            }
            Console.WriteLine($"PostScript 受信完了: {new FileInfo(psPath).Length} バイト");

            Console.WriteLine("Ghostscript で PPM へ変換中...");
            Ghostscript.ConvertToPpm(psPath, ppmPath, DefaultResolution);

            var fullImage = PpmImage.Read(ppmPath);
            Console.WriteLine($"PPM(用紙全面): {fullImage.Width}x{fullImage.Height}");

            // Ghostscript は用紙全面を描くが、プリンタが刷れるのは印字可能領域
            // だけ(papers/5000-series.yaml の left_margin/top_margin/width/length)。
            // ラスタの原点は印字可能領域の原点に対応する(-autoshift と整合)。
            var image = fullImage.Crop(config.Paper.LeftMargin, config.Paper.TopMargin, config.Paper.Width, config.Paper.Length);
            Console.WriteLine($"PPM(印字可能領域に切り出し後): {image.Width}x{image.Height}");

            var planes = Raster.ToPlanesMagic(image, config.Palette);
            var inks = config.Palette
                .Where(ink => ink.MagicRgb is not null)
                .Select(ink => new JobInk { Name = ink.Name, PrinterCode = ink.PrinterCode })
                .ToList();

            var job = new PrintJob
            {
                Resolution = DefaultResolution,
                Paper = config.Paper,
                Media = config.Media,
                Inks = inks,
                Width = image.Width,
                Height = image.Height,
            };

            byte[] rgl = Emitter.EmitJob(planes, job);
            Console.WriteLine($"RGL 組み立て完了: {rgl.Length} バイト。送出中...");

            using var transport = AlpsTransport.OpenDevice();
            PrintDrainResult(transport);
            transport.SendJob(rgl, (done, total) => Console.WriteLine($"  {done}/{total} バイト"));
            PrintDeviceIdProbe(transport);
            Console.WriteLine("送出完了");
        }
        finally
        {
            TryDelete(psPath);
            TryDelete(ppmPath);
        }
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
