// Foilwright.Cli — トレイアプリの前身となるコンソールアプリ(Phase 2)。
//
// サブコマンド:
//   status     プリンタの状態を読んで、装填カセットを人が読める形で表示する
//   print      既に組み立て済みの RGL バイト列ファイルを送出する
//   listen     名前付きパイプで PostScript を受け取り、変換して印刷する
//   build-rgl  【開発用】PPM を直接受け取り、実機に触れずに RGL バイト列を
//              ファイルへ書き出す(D-033: ref/ の job.py との突き合わせテスト
//              専用の決定的な入口。--debug-rgl は Ghostscript を経由するため
//              ラスタライザの差が混入し比較に使えない)。
//
// 設定ファイル(profiles/ papers/ palette/ media.yaml)はすべてリポジトリ
// 直下から読む。値をここにハードコードしない(DOMAIN §4.5)。
// 既定値: MD-5000(D-025) / 600dpi / A4 / 普通紙。
//
// 機種 → (プロファイル・送出方式・VID) の対応は Foilwright.Core.MachineRoute
// に集約してある(D-025)。status/print/listen は共通で --machine / --vid を
// 受け付ける(build-rgl は実機に触れないため --vid は受け付けない)。

using System.IO.Pipes;
using System.Linq;
using Foilwright.Core;

namespace Foilwright.Cli;

internal static class Program
{
    // 既定ジョブ設定(D-024: トレイアプリの設定既定値。ここでは Phase 2 の
    // 固定値として置く。将来のトレイアプリ UI 化で設定画面に移す)。
    // 機種の既定は D-025 で MD-5000 に戻った(MachineRoute.DefaultMachine)。
    private const string DefaultResolutionKey = "600";
    private const string DefaultPaperName = "a4";
    private const string DefaultMediaName = "plain_paper";
    private const string PipeName = "foilwright";

    // D-016: インク指定方式の既定は 'auto'。将来はトレイアプリの設定 UI から
    // 渡される(D-024)。当面はコマンドライン引数 --ink-mode で選ぶ。
    private const string DefaultInkMode = "auto";

    // DOMAIN §7.1 / D-027: ハーフトーン・白版モードの既定。
    private const string DefaultHalftone = "none";
    private const string DefaultWhiteMode = "auto";

    // DOMAIN §7.1 / D-029: 色補正の既定。
    private const string DefaultColourCorrection = "photo";

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
                    return RunStatus(args.Skip(1).ToArray());
                case "send-control":
                    return RunSendControl(args.Skip(1).ToArray());
                case "print":
                    return RunPrint(args.Skip(1).ToArray());
                case "listen":
                    return RunListen(args.Skip(1).ToArray());
                case "build-rgl":
                    return RunBuildRgl(args.Skip(1).ToArray());
                case "decode-png":
                    return RunDecodePng(args.Skip(1).ToArray());
                default:
                    PrintUsage();
                    return 1;
            }
        }
        catch (Exception ex) when (ex is TransportException or GhostscriptException or ConfigException
            or PpmFormatException or MachineRouteException or PngFormatException)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("使い方: Foilwright.Cli <status|print|listen> [引数]");
        Console.Error.WriteLine("  status [--machine md-5000|md-5500] [--vid XXXX] [--raw]");
        Console.Error.WriteLine("                      プリンタの状態(装填カセット)を表示する");
        Console.Error.WriteLine("                      --raw を付けると 05 01 応答 38 バイトを解釈を加えずそのまま表示する(DOMAIN §7.2/§11.4)");
        Console.Error.WriteLine("  print <file> [--machine md-5000|md-5500] [--vid XXXX]");
        Console.Error.WriteLine("                      RGL バイト列ファイルを送出する");
        Console.Error.WriteLine("  listen [--ink-mode auto|per_page|spot_only] [--resolution 600|1200x600]");
        Console.Error.WriteLine("         [--paper <名前>] [--media <名前>] [--halftone none|halftone|coarse_halftone]");
        Console.Error.WriteLine("         [--white-mode none|auto|magic|opaque|silhouette|alpha] [--colour-correction none|plain|photo]");
        Console.Error.WriteLine("         [--no-curl-correction] [--machine md-5000|md-5500] [--vid XXXX]");
        Console.Error.WriteLine("                      名前付きパイプで PostScript を受け取り印刷する");
        Console.Error.WriteLine("                      --ink-mode 省略時は 'auto'(DOMAIN §6.6 / D-016)");
        Console.Error.WriteLine($"                      --resolution 省略時は '{DefaultResolutionKey}'。選べる値はプロファイルの resolutions による(DOMAIN §7.1)");
        Console.Error.WriteLine($"                      --paper 省略時は '{DefaultPaperName}'。選べる値は用紙表(papers/{{系列}}.yaml)による(DOMAIN §5.5 / §15.10.2)");
        Console.Error.WriteLine($"                      --media 省略時は '{DefaultMediaName}'。選べる値は media.yaml による(DOMAIN §5.5.2)");
        Console.Error.WriteLine($"                      --halftone 省略時は '{DefaultHalftone}'(DOMAIN §4.2.1)");
        Console.Error.WriteLine($"                      --white-mode 省略時は '{DefaultWhiteMode}'(DOMAIN §7.1 / D-027、opaque は D-032、silhouette は D-034、alpha は D-037)");
        Console.Error.WriteLine("                      --white-mode alpha を選ぶと、色(ppmraw)の変換に加えて Ghostscript を pngalpha でもう 1 回走らせる(D-037。他のモードでは走らせない)");
        Console.Error.WriteLine($"                      --colour-correction 省略時は '{DefaultColourCorrection}'(DOMAIN §7.1 / D-029)");
        Console.Error.WriteLine("                      --no-curl-correction を指定するとカール矯正を止める(デカール・フィルム用。DOMAIN §10.10.4)");
        Console.Error.WriteLine("  build-rgl <入力.ppm> <出力.bin> [--machine md-5000|md-5500] [--paper <名前>] [--media <名前>]");
        Console.Error.WriteLine("            [--resolution 600|1200x600] [--ink-mode auto|per_page|spot_only]");
        Console.Error.WriteLine("            [--halftone none|halftone|coarse_halftone] [--white-mode none|auto|magic|opaque|silhouette|alpha]");
        Console.Error.WriteLine("            [--colour-correction none|plain|photo] [--alpha-png <入力.png>]");
        Console.Error.WriteLine("                      【開発用】PPM を直接受け取り RGL バイト列をファイルへ書き出す。実機には触れない");
        Console.Error.WriteLine("                      (D-033: ref/ の job.py との突き合わせテスト専用の決定的な入口。listen と違い Ghostscript を経由しない)");
        Console.Error.WriteLine("                      --white-mode alpha のときは --alpha-png で PNG(pngalpha 出力)を直接渡す(D-037。Ghostscript は呼ばない。突き合わせテスト用の決定的な入口)");
        Console.Error.WriteLine("  decode-png <入力.png> <出力.raw>");
        Console.Error.WriteLine("                      【開発用】PNG(RGBA)を読み、幅・高さを標準出力へ、RGBA の生バイト列を出力.raw へ書き出す");
        Console.Error.WriteLine("                      (D-036: ref/ の png.py との突き合わせテスト専用の決定的な入口)");
        Console.Error.WriteLine($"  --machine 省略時は '{MachineRoute.DefaultMachine}'(D-025)。選べるのは: {MachineRoute.KnownMachinesDescription}");
        Console.Error.WriteLine("  --vid は機種既定の VID(変換ケーブル等の個体差)を上書きする。例: --vid 056E");
    }

    /// <summary>3 サブコマンド共通の --machine / --vid を args から取り出す。
    /// 消費した要素は戻り値の positional に残らない(サブコマンド固有の引数
    /// だけが positional に残る)。D-025: 機種を選ぶとプロファイル・送出方式・VID
    /// がまとめて決まる(MachineRoute)。VID だけは個体差がありうるため上書き
    /// できる。</summary>
    private static (MachineRoute Route, string DeviceVid, List<string> Positional) ParseMachineArgs(string[] args)
    {
        string machine = MachineRoute.DefaultMachine;
        string? vidOverride = null;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--machine")
            {
                if (i + 1 >= args.Length)
                {
                    throw new MachineRouteException("使い方: --machine md-5000|md-5500");
                }
                machine = args[i + 1];
                i++;
            }
            else if (args[i] == "--vid")
            {
                if (i + 1 >= args.Length)
                {
                    throw new MachineRouteException("使い方: --vid XXXX(例: --vid 056E)");
                }
                vidOverride = args[i + 1];
                i++;
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        var route = MachineRoute.Resolve(machine);
        string vid = NormalizeVid(vidOverride) ?? route.Vid;
        return (route, vid, positional);
    }

    /// <summary>--vid に渡された値を AlpsTransport.FindDevicePath が期待する
    /// "VID_XXXX" の形に揃える。既にその形なら変えない(利用者が
    /// "--vid VID_056E" と書いても壊れないようにする)。</summary>
    private static string? NormalizeVid(string? vid)
    {
        if (vid is null)
        {
            return null;
        }
        return vid.StartsWith("VID_", StringComparison.OrdinalIgnoreCase) ? vid : $"VID_{vid}";
    }

    private sealed class JobConfig
    {
        public required ProfileSpec Profile { get; init; }
        public required PaperSpec Paper { get; init; }
        public required MediaSpec Media { get; init; }
        public required List<InkDefinition> Palette { get; init; }
    }

    private static JobConfig LoadDefaultJobConfig(string assetRoot, MachineRoute route, string paperName, string mediaName)
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(assetRoot, "profiles", route.ProfileFileName));
        var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(assetRoot, "papers"));
        if (!paperTable.TryGetValue(paperName, out var paper))
        {
            throw new ConfigException($"paper '{paperName}' not found in paper table '{profile.PaperTable}'");
        }
        var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(assetRoot, "media.yaml"));
        if (!mediaTable.TryGetValue(mediaName, out var media))
        {
            throw new ConfigException($"media '{mediaName}' not found in media.yaml");
        }
        var palette = ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml"));
        return new JobConfig { Profile = profile, Paper = paper, Media = media, Palette = palette };
    }

    // --- status ------------------------------------------------------------------

    /// <summary>【開発用・未検証】制御パケットを 16 進で指定して送る。
    ///
    /// ppmtomd 付属 getstat.pl の中断コマンド(`@RCL3`)を実機で確かめるための入口。
    /// **未知のバイト列はインターフェースをウェッジさせうる**(DOMAIN §11.1.1)。
    /// 誤って実行しないよう `--yes` を必須にしている。</summary>
    private static int RunSendControl(string[] args)
    {
        var (route, vid, remaining) = ParseMachineArgs(args);

        bool confirmed = false;
        var positional = new List<string>();
        foreach (string arg in remaining)
        {
            if (arg == "--yes")
            {
                confirmed = true;
                continue;
            }
            positional.Add(arg);
        }

        if (positional.Count != 1)
        {
            Console.Error.WriteLine("使い方: Foilwright.Cli send-control <16進バイト列> --yes");
            Console.Error.WriteLine("  例(getstat.pl の中断コマンド): 020206004052434C3301 03");
            return 1;
        }
        if (!confirmed)
        {
            Console.Error.WriteLine("--yes が要る。未知のバイト列はインターフェースをウェッジさせうる(DOMAIN §11.1.1)");
            return 1;
        }

        byte[] packet;
        try
        {
            packet = Convert.FromHexString(positional[0].Replace(" ", "").Replace("-", ""));
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"16 進として読めない: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"送るバイト列 ({packet.Length} バイト): {Convert.ToHexString(packet)}");
        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        byte[] response = transport.SendControl(packet);
        Console.WriteLine(response.Length == 0
            ? "応答: 無し(タイムアウト)"
            : $"応答 ({response.Length} バイト): {Convert.ToHexString(response)}");
        return 0;
    }

    private static int RunStatus(string[] args)
    {
        var (route, vid, remaining) = ParseMachineArgs(args);

        bool raw = false;
        var positional = new List<string>();
        foreach (string arg in remaining)
        {
            if (arg == "--raw")
            {
                raw = true;
                continue;
            }
            positional.Add(arg);
        }

        if (positional.Count > 0)
        {
            Console.Error.WriteLine($"不明な引数: {positional[0]}");
            return 1;
        }

        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        PrintDrainResult(transport);
        var status = transport.ReadStatus();
        PrintDeviceIdProbe(transport);

        var report = StatusDecoder.Describe(status);

        Console.WriteLine($"ヘッダ: {Convert.ToHexString(status.Header)}");
        Console.WriteLine($"状態: {report.StatusSummary}");
        Console.WriteLine($"装着中のカセット: {report.HeadCassetteName}");
        Console.WriteLine("ホルダ: " + string.Join("  ",
            report.HolderSlots.Select(s => $"[{s.SlotNumber}] {s.Name}")));
        if (report.CassetteInfoMayBeStale)
        {
            Console.WriteLine("注意: エラー中はカセット情報が更新されないため、現物と一致しない可能性があります");
        }
        if (raw)
        {
            PrintRawStatus(status);
        }
        return 0;
    }

    /// <summary>05 01 応答 38 バイトの構造どおりに表示する(DOMAIN §11.4。
    /// 一次情報: ppmtomd 付属 getstat.pl の parse_status。中身はリポジトリに
    /// コピーしない。URL は DOMAIN §11.4 参照)。
    /// 構造: STX(1) + パケット種別(1) + ペイロード長 LE16(2) + 状態バイト(1) +
    /// 9 エントリ x 3 バイト(27) + エラーバイト(5) + ETX(1) = 38。</summary>
    private static void PrintRawStatus(CassetteStatus status)
    {
        var raw = status.RawResponse;
        Console.WriteLine();
        Console.WriteLine("--- --raw: 05 01 応答 38 バイト(DOMAIN §11.4)---");

        Console.Write("16 進ダンプ:");
        for (int i = 0; i < raw.Count; i++)
        {
            if (i % 16 == 0)
            {
                Console.WriteLine();
                Console.Write($"  {i,2:d2}: ");
            }
            Console.Write($"{raw[i]:x2} ");
        }
        Console.WriteLine();

        Console.WriteLine($"STX=0x{raw[0]:x2} パケット種別=0x{raw[1]:x2} " +
            $"ペイロード長=0x{(raw[2] | (raw[3] << 8)):x4} 状態バイト=0x{raw[4]:x2}");

        Console.WriteLine("エントリ 9 個(1 upper/2 upper/3 upper/4 upper/1 lower/2 lower/3 lower/4 lower/carriage。" +
            "各 [stat, low, high]。stat 上位2bit=状態(0=正常/1=リボン逆装着/2=リボン終端/3=カセット無し)、" +
            "下位6bit=バーコード):");
        for (int i = 0; i < CassetteStatus.EntryCount; i++)
        {
            byte stat = status.SlotBarcodes[i];
            byte low = status.EntryLow[i];
            byte high = status.EntryHigh[i];
            string head = i == CassetteStatus.HeadSlotIndex ? "  <- ヘッドに装着中" : string.Empty;
            Console.WriteLine($"  [{i}] stat=0x{stat:x2} low=0x{low:x2} high=0x{high:x2}  " +
                $"{CassetteCatalog.GetName(stat)}{head}");
        }

        Console.WriteLine($"エラーバイト e[0..4]: {Convert.ToHexString(status.ErrorBytes.ToArray())}");
        Console.WriteLine($"ETX=0x{raw[37]:x2}");
    }

    // --- print ---------------------------------------------------------------

    private static int RunPrint(string[] args)
    {
        var (route, vid, positional) = ParseMachineArgs(args);
        if (positional.Count != 1)
        {
            Console.Error.WriteLine("使い方: Foilwright.Cli print <ジョブのバイト列ファイル> [--machine md-5000|md-5500] [--vid XXXX]");
            return 1;
        }
        string jobPath = positional[0];

        if (!File.Exists(jobPath))
        {
            Console.Error.WriteLine($"ファイルなし: {jobPath}");
            return 1;
        }
        byte[] rgl = File.ReadAllBytes(jobPath);
        Console.WriteLine($"ジョブ: {jobPath} ({rgl.Length} バイト)");

        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
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

    /// <summary>listen が受け付けるジョブごとの設定(DOMAIN §7.1)。</summary>
    private sealed class JobOptions
    {
        public string InkMode { get; init; } = DefaultInkMode;
        public string ResolutionKey { get; init; } = DefaultResolutionKey;
        public string MediaName { get; init; } = DefaultMediaName;
        public string Halftone { get; init; } = DefaultHalftone;
        public string WhiteMode { get; init; } = DefaultWhiteMode;

        // D-029: 色補正(none/plain/photo)。既定は photo。
        public string ColourCorrection { get; init; } = DefaultColourCorrection;

        // カール矯正の抑制(DOMAIN §7.1 / §10.10.4)。デカール・フィルム用に
        // 裏面印刷でカール矯正を止めたい場合に立てる。既定は false(矯正する)。
        public bool NoCurlCorrection { get; init; }
    }

    private static int RunListen(string[] args)
    {
        var (route, vid, remaining) = ParseMachineArgs(args);

        string inkMode = DefaultInkMode;
        string resolutionKey = DefaultResolutionKey;
        string paperName = DefaultPaperName;
        string mediaName = DefaultMediaName;
        string halftone = DefaultHalftone;
        string whiteMode = DefaultWhiteMode;
        string colourCorrection = DefaultColourCorrection;
        bool noCurlCorrection = false;

        for (int i = 0; i < remaining.Count; i++)
        {
            string opt = remaining[i];
            if (opt == "--no-curl-correction")
            {
                noCurlCorrection = true;
                continue;
            }
            if (opt is "--ink-mode" or "--resolution" or "--paper" or "--media" or "--halftone" or "--white-mode" or "--colour-correction")
            {
                if (i + 1 >= remaining.Count)
                {
                    Console.Error.WriteLine($"使い方: listen {opt} <値>");
                    return 1;
                }
                string value = remaining[i + 1];
                i++;
                switch (opt)
                {
                    case "--ink-mode": inkMode = value; break;
                    case "--resolution": resolutionKey = value; break;
                    case "--paper": paperName = value; break;
                    case "--media": mediaName = value; break;
                    case "--halftone": halftone = value; break;
                    case "--white-mode": whiteMode = value; break;
                    case "--colour-correction": colourCorrection = value; break;
                }
            }
            else
            {
                Console.Error.WriteLine($"不明な引数: {opt}");
                return 1;
            }
        }

        if (!JobAssembly.ValidInkModes.Contains(inkMode))
        {
            Console.Error.WriteLine(
                $"不明なインク指定方式 '{inkMode}'。次のいずれかを指定してください: {string.Join(", ", JobAssembly.ValidInkModes)}");
            return 1;
        }

        // per_page は 1 ページ = 1 インクであり複数ページ入力を要する
        // (DOMAIN §6.6)。名前付きパイプで受け取る listen は単一ページの
        // PostScript しか扱えないため、ここで明確に拒否する(黙って別方式に
        // すり替えない)。
        if (inkMode == "per_page")
        {
            Console.Error.WriteLine(
                "インク指定方式 'per_page' は現状の listen(単一ページの PostScript のみ受信)では選べません。" +
                "1 ページ = 1 インクの複数ページ入力が必要です。");
            return 1;
        }

        if (!JobAssembly.ValidHalftones.Contains(halftone))
        {
            Console.Error.WriteLine(
                $"不明なハーフトーン '{halftone}'。次のいずれかを指定してください: {string.Join(", ", JobAssembly.ValidHalftones)}");
            return 1;
        }

        if (!JobAssembly.ValidWhiteModes.Contains(whiteMode))
        {
            Console.Error.WriteLine(
                $"不明な白版モード '{whiteMode}'。次のいずれかを指定してください: {string.Join(", ", JobAssembly.ValidWhiteModes)}");
            return 1;
        }

        if (!Colour.ValidColourCorrections.Contains(colourCorrection))
        {
            Console.Error.WriteLine(
                $"不明な色補正 '{colourCorrection}'。次のいずれかを指定してください: {string.Join(", ", Colour.ValidColourCorrections)}");
            return 1;
        }

        string assetRoot = AssetRoot.ResolveDefault();
        // 不明な用紙名は LoadDefaultJobConfig 内で ConfigException を投げる
        // (Main の catch がまとめて拾い、エラー文言を表示して終了コード 1 で
        // 終わる。既定値へ黙って落とさない)。
        var config = LoadDefaultJobConfig(assetRoot, route, paperName, mediaName);

        // --resolution はプロファイルの resolutions から探す。プロファイル読み込み後
        // でなければ選べる値を検証できないため、機種ごとの config ロード後にここで検証する。
        ResolutionEntry resolutionEntry;
        try
        {
            resolutionEntry = config.Profile.ResolveResolutionByKey(resolutionKey);
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }

        var options = new JobOptions
        {
            InkMode = inkMode,
            ResolutionKey = resolutionKey,
            MediaName = mediaName,
            Halftone = halftone,
            WhiteMode = whiteMode,
            ColourCorrection = colourCorrection,
            NoCurlCorrection = noCurlCorrection,
        };

        Console.WriteLine($"機種: {route.Machine}(送出方式: {route.Mode}、VID: {vid})");
        Console.WriteLine(
            $"名前付きパイプ \\\\.\\pipe\\{PipeName} で待ち受け中(Ctrl+C で終了)... " +
            $"インク指定方式: {inkMode} / 解像度: {resolutionEntry.Key} / 用紙: {paperName} / メディア: {mediaName} / " +
            $"ハーフトーン: {halftone} / 白版モード: {whiteMode} / 色補正: {colourCorrection} / " +
            $"カール矯正を止める: {noCurlCorrection}");

        while (true)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
            Console.WriteLine("接続を待機中...");
            pipe.WaitForConnection();
            Console.WriteLine("接続を受理。PostScript を受信中...");

            try
            {
                HandleJob(pipe, config, options, resolutionEntry, route, vid);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ジョブの処理に失敗しました: {ex.Message}");
            }
        }
    }

    private static void HandleJob(
        NamedPipeServerStream pipe, JobConfig config, JobOptions options, ResolutionEntry resolutionEntry,
        MachineRoute route, string vid)
    {
        string psPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ps");
        string ppmPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ppm");
        // 白版モードが "alpha" のときだけ使う(D-037)。他のモードでは
        // 一切参照せず、pngPath も作らない -- Ghostscript を pngalpha で
        // 走らせるのは alpha を選んだときだけという制約(D-037)をここで守る。
        string? pngPath = options.WhiteMode == "alpha"
            ? Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.png")
            : null;

        try
        {
            using (var fileStream = File.Create(psPath))
            {
                pipe.CopyTo(fileStream);
            }
            Console.WriteLine($"PostScript 受信完了: {new FileInfo(psPath).Length} バイト");

            Console.WriteLine("Ghostscript で PPM へ変換中...");
            Ghostscript.ConvertToPpm(psPath, ppmPath, resolutionEntry.DpiX, resolutionEntry.DpiY);

            PngImage? fullAlphaImage = null;
            if (pngPath is not null)
            {
                // D-037: 白版モード alpha のときだけ、色(ppmraw)の変換に加えて
                // pngalpha でもう 1 回変換する。他のモードではこの分岐に入らない。
                Console.WriteLine("Ghostscript で PNG(pngalpha)へ変換中...");
                Ghostscript.ConvertToPngAlpha(psPath, pngPath, resolutionEntry.DpiX, resolutionEntry.DpiY);
                fullAlphaImage = PngImage.Read(pngPath);
            }

            var fullImage = PpmImage.Read(ppmPath);
            Console.WriteLine($"PPM(用紙全面): {fullImage.Width}x{fullImage.Height}");

            // Ghostscript は用紙全面を描くが、プリンタが刷れるのは印字可能領域
            // だけ(papers/5000-series.yaml の left_margin/top_margin/width/length)。
            // ラスタの原点は印字可能領域の原点に対応する(-autoshift と整合)。
            // 用紙表は 600dpi 基準のため、選んだ解像度へ換算してから切り出す
            // (DOMAIN §7.1: 1200x600 は幅方向だけ 2 倍)。
            var scaledPaper = config.Paper.ScaleToResolution(resolutionEntry.DpiX, resolutionEntry.DpiY);
            var image = fullImage.Crop(scaledPaper.LeftMargin, scaledPaper.TopMargin, scaledPaper.Width, scaledPaper.Length);
            Console.WriteLine($"PPM(印字可能領域に切り出し後): {image.Width}x{image.Height}");

            // D-037: 色(image)と同じ切り出しをアルファにも適用する
            // (制約: 切り出しを色とアルファで食い違わせない)。
            PngImage? alphaImage = fullAlphaImage is null
                ? null
                : CropAlpha(fullAlphaImage, scaledPaper.LeftMargin, scaledPaper.TopMargin, scaledPaper.Width, scaledPaper.Length);

            // D-029: colourCorrection == "photo" のときだけ photoLutPath /
            // resolutionEntry.DpiX が参照される。ガンマの既定値は解像度で
            // 変わる(600 は 0.8、1200 は -0.9)ため、解像度を渡し忘れると色がずれる。
            string photoLutPath = Path.Combine(AssetRoot.ResolveDefault(), "colour", "photo_colcor.bin");
            var jobPlanes = JobAssembly.BuildJobPlanes(
                image, config.Palette, options.InkMode, options.Halftone, options.WhiteMode,
                options.ColourCorrection, resolutionEntry.DpiX, photoLutPath, alphaImage);
            if (jobPlanes.Count == 0)
            {
                Console.WriteLine("印刷する内容がありません");
                return;
            }

            var planes = jobPlanes.ToDictionary(jp => jp.Ink.Name, jp => jp.Plane);
            var inks = jobPlanes
                .Select(jp => new JobInk
                {
                    Name = jp.Ink.Name,
                    PrinterCode = jp.Ink.PrinterCode,
                    // 印字色 5 本以上ではカセット一覧に載る(DOMAIN §14.8)。
                    // 4 本以下では使われないので null のままでも構わない。
                    Barcode = jp.Ink.Barcode,
                    Passes = jp.Ink.Passes,
                })
                .ToList();

            var job = new PrintJob
            {
                // Emitter.EmitJob は Paper を常に 600dpi 基準の値として受け取り、
                // Resolution に応じた換算を内部で行う(ScaleToResolution とは別処理
                // なので、ここは config.Paper(未換算)をそのまま渡す)。
                Resolution = resolutionEntry.DpiX,
                Paper = config.Paper,
                Media = config.Media,
                Inks = inks,
                Width = image.Width,
                Height = image.Height,
                NoCurlCorrection = options.NoCurlCorrection,
            };

            byte[] rgl = Emitter.EmitJob(planes, job);
            Console.WriteLine($"RGL 組み立て完了: {rgl.Length} バイト。送出中...");

            using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
            PrintDrainResult(transport);
            transport.SendJob(rgl, (done, total) => Console.WriteLine($"  {done}/{total} バイト"));
            PrintDeviceIdProbe(transport);
            Console.WriteLine("送出完了");
        }
        finally
        {
            TryDelete(psPath);
            TryDelete(ppmPath);
            if (pngPath is not null)
            {
                TryDelete(pngPath);
            }
        }
    }

    /// <summary>PngImage(RGBA)を、色の切り出し(PpmImage.Crop)と同じ規則で
    /// 切り出す(D-037: 切り出しを色とアルファで食い違わせない)。PngImage
    /// 自体には Crop を持たせない(D-036 の対象外。Ghostscript の pngalpha
    /// 出力を読むだけの最小デコーダに留める)。</summary>
    private static PngImage CropAlpha(PngImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0)
        {
            throw new ArgumentException($"crop origin must be non-negative, got ({x}, {y})");
        }
        if (width < 0 || height < 0)
        {
            throw new ArgumentException($"crop size must be non-negative, got ({width}, {height})");
        }

        int availableWidth = Math.Max(0, image.Width - x);
        int availableHeight = Math.Max(0, image.Height - y);
        int outWidth = Math.Min(width, availableWidth);
        int outHeight = Math.Min(height, availableHeight);

        byte[] outPixels = new byte[outWidth * outHeight * 4];
        int srcRowBytes = image.Width * 4;
        int dstRowBytes = outWidth * 4;
        for (int row = 0; row < outHeight; row++)
        {
            int srcOffset = (y + row) * srcRowBytes + x * 4;
            int dstOffset = row * dstRowBytes;
            Array.Copy(image.Pixels, srcOffset, outPixels, dstOffset, dstRowBytes);
        }

        return new PngImage(outWidth, outHeight, outPixels);
    }

    // --- build-rgl(開発用。D-033)---------------------------------------------

    /// <summary>【開発用】PPM を直接受け取り、実機に触れずに RGL バイト列を
    /// ファイルへ書き出す(D-033)。listen の HandleJob と同じ組み立て手順
    /// (JobAssembly.BuildJobPlanes → Emitter.EmitJob)を踏むが、Ghostscript も
    /// 印字可能領域への切り出しも行わない — PPM のピクセル寸法をそのまま
    /// image の width/height として扱う。ref/ の job.build_job_planes /
    /// emitter.emit_job との突き合わせテスト(ref/tests/test_cross_language_match.py)
    /// がラスタライザの差に汚染されないための決定的な入口。</summary>
    private static int RunBuildRgl(string[] args)
    {
        var (route, _, positional) = ParseMachineArgs(args);

        string inkMode = DefaultInkMode;
        string resolutionKey = DefaultResolutionKey;
        string paperName = DefaultPaperName;
        string mediaName = DefaultMediaName;
        string halftone = DefaultHalftone;
        string whiteMode = DefaultWhiteMode;
        string colourCorrection = DefaultColourCorrection;
        string? alphaPngPath = null;

        var freePositional = new List<string>();
        for (int i = 0; i < positional.Count; i++)
        {
            string opt = positional[i];
            if (opt is "--ink-mode" or "--resolution" or "--paper" or "--media" or "--halftone" or "--white-mode" or "--colour-correction" or "--alpha-png")
            {
                if (i + 1 >= positional.Count)
                {
                    Console.Error.WriteLine($"使い方: build-rgl {opt} <値>");
                    return 1;
                }
                string value = positional[i + 1];
                i++;
                switch (opt)
                {
                    case "--ink-mode": inkMode = value; break;
                    case "--resolution": resolutionKey = value; break;
                    case "--paper": paperName = value; break;
                    case "--media": mediaName = value; break;
                    case "--halftone": halftone = value; break;
                    case "--white-mode": whiteMode = value; break;
                    case "--colour-correction": colourCorrection = value; break;
                    case "--alpha-png": alphaPngPath = value; break;
                }
            }
            else
            {
                freePositional.Add(opt);
            }
        }

        if (freePositional.Count != 2)
        {
            Console.Error.WriteLine("使い方: Foilwright.Cli build-rgl <入力.ppm> <出力.bin> [オプション]");
            return 1;
        }
        string inputPath = freePositional[0];
        string outputPath = freePositional[1];

        if (!JobAssembly.ValidInkModes.Contains(inkMode) || inkMode == "per_page")
        {
            Console.Error.WriteLine(
                $"不明なインク指定方式、または build-rgl(単一 PPM 入力)では選べない方式です: '{inkMode}'。" +
                $"次のいずれかを指定してください: auto, spot_only");
            return 1;
        }
        if (!JobAssembly.ValidHalftones.Contains(halftone))
        {
            Console.Error.WriteLine(
                $"不明なハーフトーン '{halftone}'。次のいずれかを指定してください: {string.Join(", ", JobAssembly.ValidHalftones)}");
            return 1;
        }
        if (!JobAssembly.ValidWhiteModes.Contains(whiteMode))
        {
            Console.Error.WriteLine(
                $"不明な白版モード '{whiteMode}'。次のいずれかを指定してください: {string.Join(", ", JobAssembly.ValidWhiteModes)}");
            return 1;
        }
        if (!Colour.ValidColourCorrections.Contains(colourCorrection))
        {
            Console.Error.WriteLine(
                $"不明な色補正 '{colourCorrection}'。次のいずれかを指定してください: {string.Join(", ", Colour.ValidColourCorrections)}");
            return 1;
        }
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"ファイルなし: {inputPath}");
            return 1;
        }
        if (whiteMode == "alpha" && alphaPngPath is null)
        {
            Console.Error.WriteLine("白版モード 'alpha' には --alpha-png <PNG ファイル> の指定が必要です(D-037)。");
            return 1;
        }
        if (alphaPngPath is not null && !File.Exists(alphaPngPath))
        {
            Console.Error.WriteLine($"ファイルなし: {alphaPngPath}");
            return 1;
        }

        string assetRoot = AssetRoot.ResolveDefault();
        var config = LoadDefaultJobConfig(assetRoot, route, paperName, mediaName);

        ResolutionEntry resolutionEntry;
        try
        {
            resolutionEntry = config.Profile.ResolveResolutionByKey(resolutionKey);
        }
        catch (ConfigException ex)
        {
            Console.Error.WriteLine($"エラー: {ex.Message}");
            return 1;
        }

        var image = PpmImage.Read(inputPath);
        // build-rgl は listen と違い Ghostscript を一切呼ばない(D-033)。
        // alpha 用の PNG も --alpha-png で受け取った既存ファイルをそのまま
        // 読むだけで、pngalpha を走らせない(D-037: 突き合わせテスト用の
        // 決定的な入口。切り出しも行わない -- image と同じく寸法をそのまま扱う)。
        PngImage? alphaImage = alphaPngPath is null ? null : PngImage.Read(alphaPngPath);

        string photoLutPath = Path.Combine(assetRoot, "colour", "photo_colcor.bin");
        var jobPlanes = JobAssembly.BuildJobPlanes(
            image, config.Palette, inkMode, halftone, whiteMode,
            colourCorrection, resolutionEntry.DpiX, photoLutPath, alphaImage);

        var planes = jobPlanes.ToDictionary(jp => jp.Ink.Name, jp => jp.Plane);
        var inks = jobPlanes
            .Select(jp => new JobInk
            {
                Name = jp.Ink.Name,
                PrinterCode = jp.Ink.PrinterCode,
                // 印字色 5 本以上ではカセット一覧に載る(DOMAIN §14.8)。
                // 4 本以下では使われないので null のままでも構わない。
                Barcode = jp.Ink.Barcode,
                Passes = jp.Ink.Passes,
            })
            .ToList();

        var job = new PrintJob
        {
            Resolution = resolutionEntry.DpiX,
            Paper = config.Paper,
            Media = config.Media,
            Inks = inks,
            Width = image.Width,
            Height = image.Height,
        };

        byte[] rgl = Emitter.EmitJob(planes, job);
        File.WriteAllBytes(outputPath, rgl);
        Console.WriteLine($"RGL 組み立て完了: {outputPath} ({rgl.Length} バイト)");
        return 0;
    }

    // --- decode-png(開発用。D-036)---------------------------------------------

    /// <summary>【開発用】PNG(RGBA)を読み、幅・高さを標準出力へ、RGBA の生
    /// バイト列を出力ファイルへ書き出す(D-036)。ref/ の png.read_png_rgba
    /// との突き合わせテスト(ref/tests/test_png_cross_language.py)専用の
    /// 決定的な入口。</summary>
    private static int RunDecodePng(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("使い方: Foilwright.Cli decode-png <入力.png> <出力.raw>");
            return 1;
        }
        string inputPath = args[0];
        string outputPath = args[1];

        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"ファイルなし: {inputPath}");
            return 1;
        }

        var image = PngImage.Read(inputPath);
        File.WriteAllBytes(outputPath, image.Pixels);
        Console.WriteLine($"{image.Width} {image.Height}");
        return 0;
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
