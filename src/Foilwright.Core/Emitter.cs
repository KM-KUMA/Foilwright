// Foilwright.Core — L1: インクプレーン + 機種プロファイル -> MD コマンドの
// バイト列。
//
// tests/golden/ の golden fixture が行使する範囲の ppmtomd 1.6 RGL コマンド
// 生成を再現する:
//
// - 単一ページ・単一パス・単一転送モードグループ(既定は "colourPlane" =
//   0x04; ppmtomd.c:1312-1313。印字色が 4 本を超えると "multiPlane" = 0x08
//   へ自動的に切り替わる; ppmtomd.c:1780-1783)
// - multiPlane が要求するカセット一覧(ESC & l {本数} 00 C + バーコード列;
//   ppmtomd.c:2526-2544、golden g25-g27)
// - LF/印字ヘッド補正なし、光沢仕上げなし、オーバーレイモードなし
//   (golden のコマンド列はどれもこれらを有効化するオプションを使っていない)
// - PackBits 圧縮は ppmtomd.c:2362-2452(packbits())のとおり
// - ppmtomd の「余ったプレーンはスクラッチバッファへ回し、バックフィード
//   コマンドで後から継ぎ足す」挙動(fd のルーティングは ppmtomd.c:2092-2138、
//   バックフィード/スプライスは 2244-2296)— これが、既定(-colours なし)の
//   ジョブでも黒しかインクが無いのに空の Cyan/Magenta/Yellow プレーンが
//   出力される理由。
//
// ppmtomd から意図的に離れる箇所が 1 つだけある(D-052): 1200dpi のとき、
// プロセスインクでないインクのプレーンだけ横 1/2 に縮める。プリンタが特色 /
// 塗る範囲のカセットを 600dpi で走らせるため(DOMAIN §14.7.1)。ppmtomd は
// これをやらないので golden では守れない — 代わりに
// Foilwright.Core.Tests/Shrink1200Tests.cs の不変条件で守る。
//
// 機種による分岐はここに一切書かない(DOMAIN §4.4): 上記はすべて固定の
// プロトコル挙動か、job(呼び出し側)によって決まる。
//
// 参照実装: ref/foilwright_ref/emitter.py。

namespace Foilwright.Core;

/// <summary>1 ジョブで使う 1 インクの選択情報(印刷順に並べる)。</summary>
public sealed class JobInk
{
    public required string Name { get; init; }
    public required int PrinterCode { get; init; }

    /// <summary>カセットのバーコード番号(DOMAIN §6.5)。色選択に使う
    /// PrinterCode とは別体系なので、パレットの InkDefinition.Barcode を
    /// そのまま渡すこと(印字色が 4 本以下なら送らないので null でよい)。
    /// 5 本以上(= multi_plane)では必須で、欠けていれば例外になる。</summary>
    public int? Barcode { get; init; }

    /// <summary>重ね塗り回数(DOMAIN §6.2)。既定 1。ref/ の
    /// job["inks"][i]["passes"] に対応する(config.load_palette の既定値と
    /// 揃えてある — config.py:181)。</summary>
    public int Passes { get; init; } = 1;

    /// <summary>プロセスインク(CMYK)かどうか。パレットの
    /// InkDefinition.Channel が非 null なら true(DOMAIN §4.5 — インク名で
    /// 判定しない)。
    ///
    /// false のインクは 1200dpi のときプレーンが横 1/2 に縮む(D-052)。
    /// **既定は true**、つまり「知らないインクは縮めない」= ppmtomd と
    /// 1 バイトも違わない挙動。逆にするとインク種別を渡し忘れた呼び出し元が
    /// CMYK を半分幅で刷ってしまうので、安全側の既定はこちら。
    /// ref/ の job["inks"][i]["is_process"] に対応する。</summary>
    public bool IsProcess { get; init; } = true;
}

/// <summary>emit_job に渡すジョブ記述。ここには機種プロファイルの参照ロジックを
/// 一切混ぜない(DOMAIN §4.4 — 機種依存の分岐なし)。</summary>
public sealed class PrintJob
{
    public required int Resolution { get; init; }
    public required PaperSpec Paper { get; init; }
    public required MediaSpec Media { get; init; }

    /// <summary>印刷順の全アクティブインク。プレーンが全面ブランクの
    /// エントリでも(ブランクな)選択コマンドは出力される(ppmtomd の既定
    /// -colours 挙動 = 常に C/M/Y/K を駆動する)。</summary>
    public required IReadOnlyList<JobInk> Inks { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    public int XShift { get; init; }
    public int YShift { get; init; }

    /// <summary>カール補正の抑制(ppmtomd の -nocurlcorrection)。
    /// デカール素材はシートを平らに保つ必要があるため抑制する(DOMAIN §10.10.4)。</summary>
    public bool NoCurlCorrection { get; init; }

    /// <summary>転送モード。"colour_plane"(既定、ppmtomd の既定)か
    /// "black_raster"(-black、単一プレーン、色選択コマンドなし)。
    /// "colour_plane" は印字色が Emitter.MaxColourPlaneInks 本を超えると
    /// 自動的に "multi_plane" へ格上げされる(ppmtomd.c:1780-1783)。</summary>
    public string TransferMode { get; init; } = "colour_plane";
}

/// <summary>負のシフト(ラスタのトリミングを要求する)など、未実装の経路が
/// 要求されたときに送出する。</summary>
public sealed class EmitterNotImplementedException : Exception
{
    public EmitterNotImplementedException(string message) : base(message) { }
}

public static class Emitter
{
    private const byte Esc = 0x1B;

    private static readonly Dictionary<int, byte> ResolutionCodes = new()
    {
        [300] = 0x02,
        [600] = 0x03,
        [1200] = 0x04,
    };

    // 転送モード(mddata.h の transferMode)。データ部全体の形を決める
    // (単一プレーンのモードは色選択コマンドを一切持たない)。
    //
    // 以下の 3 つのみ実装する。カセットモードは色選択コマンドの末尾を 'r' では
    // なく 'c' にする(ppmtomd.c:2262-2263)し、ラスタモードはデータレイアウトが
    // 異なる。
    private static readonly Dictionary<string, int> TransferModes = new()
    {
        ["black_raster"] = 0x00, // 単一プレーン、色選択なし(-black)
        ["colour_plane"] = 0x04, // 選択 1 回 + インクごとの行(ppmtomd 既定)
        ["multi_plane"] = 0x08,  // 形は同じで 5〜7 本用(下記)
    };

    /// <summary>colourPlane のジョブが持てる印字色の上限。これを超えると
    /// ppmtomd はジョブ全体を multiPlane へ切り替える(ppmtomd.c:1780-1783)。
    /// データレイアウトが変わるわけではなく — 色選択もラスタも 1 バイト違わない —
    /// モードバイトが変わり、初期化にカセット一覧が 1 つ増えるだけ
    /// (ppmtomd.c:2526-2544)。</summary>
    public const int MaxColourPlaneInks = 4;

    /// <summary>1 パスで印字ヘッドに載るカートリッジの本数。ppmtomd はこれを
    /// 超えると誤った印刷をせずエラーにする(ppmtomd.c:1778
    /// "Too many printing colours")。</summary>
    public const int MaxPrintingColours = 7;

    /// <summary>ppmtomd の packbits()(ppmtomd.c:2362-2452)の移植。
    ///
    /// row は既にビットパック済み(MSB ファースト)でバイト境界に揃った
    /// ラスタ行。戻り値は (n, data): n &gt;= 0 なら data(長さ n)は末尾ゼロを
    /// 切り詰めた行で非圧縮のまま送るべき; n &lt; 0 なら data(長さ -n)は
    /// PackBits 圧縮済みで圧縮して送るべき。n == 0 は行全体が空白で、
    /// その行については何も送らないことを意味する。</summary>
    private static (int N, byte[] Data) PackBits(ReadOnlySpan<byte> row)
    {
        int num = row.Length;
        while (num > 0 && row[num - 1] == 0)
        {
            num -= 1;
        }
        byte[] outu = row[..num].ToArray();
        if (num == 0)
        {
            return (0, Array.Empty<byte>());
        }

        var runcnt = new int[num];
        int start = 0;
        runcnt[0] = 0;
        for (int i = 1; i < num; i++)
        {
            if (outu[i] == outu[i - 1])
            {
                if (runcnt[start] <= 0 && runcnt[start] > -127)
                {
                    runcnt[start] -= 1;
                }
                else
                {
                    start = i;
                    runcnt[start] = 0;
                }
            }
            else
            {
                if (runcnt[start] >= 0 && runcnt[start] < 127)
                {
                    runcnt[start] += 1;
                }
                else
                {
                    start = i;
                    runcnt[start] = 0;
                }
            }
        }

        var outc = new List<byte>();
        int idx = 0;
        while (idx < num)
        {
            int count = runcnt[idx];
            int frm = idx;
            if (count >= 0)
            {
                while (true)
                {
                    int nxt0 = idx + 1 + runcnt[idx];
                    if (nxt0 >= num || runcnt[nxt0] < 0 || count + runcnt[nxt0] + 1 > 127)
                    {
                        break;
                    }
                    count += runcnt[nxt0] + 1;
                    idx = nxt0;
                }
            }
            int nxt = idx + 1 + (runcnt[idx] < 0 ? -runcnt[idx] : runcnt[idx]);
            outc.Add((byte)(count & 0xFF));
            if (count >= 0)
            {
                int j = frm;
                int c = count;
                while (c >= 0)
                {
                    outc.Add(outu[j]);
                    j += 1;
                    c -= 1;
                }
            }
            else
            {
                outc.Add(outu[frm]);
            }
            idx = nxt;
        }

        if (outc.Count < num)
        {
            return (-outc.Count, outc.ToArray());
        }
        return (num, outu);
    }

    /// <summary>1bpp プレーンを横 1/2 に縮める。横に並ぶ 2 ドットの OR を取る
    /// (D-052)。
    ///
    /// プリンタは走査解像度をカセットのバーコードで決めており、特色 /
    /// 塗る範囲のカセットはジョブが 1200 を指定していても 600dpi で走る
    /// (DOMAIN §14.7.1)。開始位置は 1200 として正しく解釈され、ラスタの
    /// ドットだけが 600 ピッチで打たれる = 横 2 倍になる。ここで半分に
    /// しておくとちょうど打ち消し合う。
    ///
    /// 間引き(偶数列だけ採る)ではなく OR にするのは意図的: 特色は下地・
    /// 上掛け・ベタが主用途で、細い線が 600dpi の 1 ドット太るより
    /// 消えるほうが害が大きい(D-052)。
    ///
    /// width が奇数のとき、最後のソースドットには相棒がいない。**単独で
    /// 1 ドットとして残す**(出力幅は ceil(width / 2))。捨てると絵の右端に
    /// 塗り残しの列が出るが、それは OR を選んだ理由そのもの(インクの
    /// 欠落)である。
    ///
    /// 入力も出力も 1bpp・MSB 先頭・行はバイト境界に揃う。出力の行末
    /// パディングビットは 0 のまま。</summary>
    private static byte[] ShrinkPlaneHalfWidth(byte[] plane, int width, int height)
    {
        int srcRowBytes = (width + 7) / 8;
        int dstWidth = (width + 1) / 2;
        int dstRowBytes = (dstWidth + 7) / 8;
        var outPlane = new byte[dstRowBytes * height];
        for (int row = 0; row < height; row++)
        {
            int srcBase = row * srcRowBytes;
            int dstBase = row * dstRowBytes;
            for (int x = 0; x < dstWidth; x++)
            {
                int sx = 2 * x;
                int bit = (plane[srcBase + (sx >> 3)] >> (7 - (sx & 7))) & 1;
                if (bit == 0 && sx + 1 < width)
                {
                    bit = (plane[srcBase + ((sx + 1) >> 3)] >> (7 - ((sx + 1) & 7))) & 1;
                }
                if (bit != 0)
                {
                    outPlane[dstBase + (x >> 3)] |= (byte)(1 << (7 - (x & 7)));
                }
            }
        }
        return outPlane;
    }

    private static byte[] EmitPlaneRows(byte[] plane, int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        var outBytes = new List<byte>();
        int compressionState = -1; // -1 == ppmtomd の未設定センチネル
        int rowsToSkip = 0;

        for (int row = 0; row < height; row++)
        {
            var raw = new ReadOnlySpan<byte>(plane, row * rowBytes, Math.Min(rowBytes, plane.Length - row * rowBytes));
            var (n, data) = PackBits(raw);
            if (n == 0)
            {
                rowsToSkip += 1;
                continue;
            }
            int mode = n >= 0 ? 0 : 1;
            if (compressionState != mode)
            {
                outBytes.AddRange(new byte[] { Esc, 0x2A, 0x62, (byte)(mode != 0 ? 2 : 0), 0, 0x4D });
                compressionState = mode;
            }
            if (rowsToSkip != 0)
            {
                outBytes.AddRange(new byte[] { Esc, 0x2A, 0x62, (byte)(rowsToSkip % 256), (byte)(rowsToSkip / 256), 0x59 });
                rowsToSkip = 0;
            }
            int length = n >= 0 ? n : -n;
            byte vw = row == height - 1 ? (byte)0x56 : (byte)0x57;
            outBytes.AddRange(new byte[] { Esc, 0x2A, 0x62, (byte)(length % 256), (byte)(length / 256), vw });
            outBytes.AddRange(data);
        }
        return outBytes.ToArray();
    }

    /// <summary>1 ページ分の MD コマンドバイト列を組み立てる。
    ///
    /// planes: インク名 -> パック済み 1bit プレーンバイト列(Raster.ToPlanes 等
    /// から)。job は機種プロファイル参照ロジックを一切混ぜない
    /// (DOMAIN §4.4 — 機種依存の分岐なし)、1 ジョブを完全に記述する。</summary>
    public static byte[] EmitJob(IReadOnlyDictionary<string, byte[]> planes, PrintJob job)
    {
        int resolution = job.Resolution;
        byte resCode = ResolutionCodes[resolution];
        int width = job.Width;
        int height = job.Height;

        var paper = job.Paper;
        int pageWidth = paper.Width;
        int pageLength = paper.Length;
        if (resolution == 300)
        {
            pageWidth /= 2;
            pageLength /= 2;
        }
        else if (resolution == 1200)
        {
            pageWidth *= 2;
        }

        var inks = job.Inks;

        // 転送モード。ppmtomd は colourPlane から出発し、印字色が 4 本を超えた
        // 時点でジョブ全体を multiPlane へ格上げする(ppmtomd.c:1780-1783)。
        // 単一プレーンのモードが明示されていればそのまま — ppmtomd と同じ。
        //
        // passes(DOMAIN §6.2)は同じインク = 同じカセットの繰り返しなので
        // 印字色は増えず、ここでは意図的に数えない。数えているのは「利用者に
        // 何本のカセットを載せてもらうか」で、下のカセット一覧が並べるものと
        // 同じ。(ppmtomd に passes は無く、g21/g22 は同じインクを複数
        // コンポーネントに割り当てて同じ形を得ているので、golden はこの点を
        // 決めていない。)
        string modeName = job.TransferMode;
        if (modeName == "colour_plane" && inks.Count > MaxColourPlaneInks)
        {
            modeName = "multi_plane";
        }
        int mode = TransferModes[modeName];
        if (mode != TransferModes["black_raster"] && inks.Count > MaxPrintingColours)
        {
            throw new ArgumentException(
                $"too many printing colours: {inks.Count} inks, the print head holds at most {MaxPrintingColours}");
        }

        // D-052: 1200dpi ではプロセスインク以外のカセットが 600 ピッチで走る
        // ので、そのプレーンだけ横 1/2 で送る。上のページ幅コマンドはジョブに
        // 1 回であり 1200 のまま変えない — CMYK は実際に 1200 で刷られ、
        // 縮んだインクの行の長さだけが変わる。
        //
        // インク名には一切依存しない(DOMAIN §4.5)。種別は呼び出し元が
        // パレットから読んで渡す。
        (byte[] Plane, int Width) PlaneAndWidth(JobInk ink)
        {
            byte[] plane = planes[ink.Name];
            if (resolution == 1200 && !ink.IsProcess)
            {
                return (ShrinkPlaneHalfWidth(plane, width, height), (width + 1) / 2);
            }
            return (plane, width);
        }

        var outBytes = new List<byte>();

        // rgl_init_page (ppmtomd.c:2484-2564)、golden fixture が使うフィールド
        // に絞ったもの。
        outBytes.AddRange(new byte[] { Esc, 0x25, 0x80, 0x41 }); // RGL モード選択
        // ppmtomd の sprintf("\033*t%cR", ...) は 5 バイトだが、out_function に
        // 渡す送出バイト数は 6(ppmtomd.c:2489-2490)。つまり sprintf の文字列
        // 終端 NUL が余計に 1 バイト送られる。これは ppmtomd の実バグだが、
        // golden fixture はどれもこれを織り込んでいる。
        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x74, resCode, 0x52, 0x00 }); // 出力解像度
        var media = job.Media;
        outBytes.AddRange(new byte[] { Esc, 0x26, 0x6C, (byte)media.Byte1, (byte)media.Byte2, 0x4D });
        outBytes.AddRange(new byte[] { Esc, 0x26, 0x6C, (byte)paper.Code, 0, 0x41 });
        outBytes.AddRange(new byte[] { Esc, 0x26, 0x6C, (byte)(pageLength % 256), (byte)(pageLength / 256), 0x50 });
        outBytes.AddRange(new byte[] { Esc, 0x26, 0x61, (byte)(pageWidth % 256), (byte)(pageWidth / 256), 0x4D });

        // カセット一覧。multiPlane のときだけ送る(ppmtomd.c:2526-2544):
        // ESC & l {本数} 00 C のあとに、印刷順でインク 1 本につき 1 バイトの
        // カセットバーコード番号が続く。バーコードの番号体系は色選択バイトとは
        // 別物なので(DOMAIN §6.5)、パレットの barcode をそのまま使い、
        // printer_code から導出しない。
        if (mode == TransferModes["multi_plane"])
        {
            var barcodes = new List<byte>();
            foreach (var ink in inks)
            {
                if (ink.Barcode is null)
                {
                    throw new ArgumentException(
                        $"ink '{ink.Name}': 'barcode' is required with more than {MaxColourPlaneInks} inks (the cassette list command carries it)");
                }
                int barcode = ink.Barcode.Value;
                if (barcode < 0 || barcode > 255)
                {
                    throw new ArgumentException(
                        $"ink '{ink.Name}': 'barcode' must be an integer in 0..255, got {barcode}");
                }
                barcodes.Add((byte)barcode);
            }
            outBytes.AddRange(new byte[] { Esc, 0x26, 0x6C, (byte)barcodes.Count, 0, 0x43 });
            outBytes.AddRange(barcodes);
        }

        // x/y オフセット(出力解像度基準のドット)。ppmtomd はシフトが正の
        // ときだけコマンドを出す(ppmtomd.c:2546-2555)。
        // 負のシフトは「ラスタの途中から始める」ことを意味し、ppmtomd では
        // コマンドではなく画像データのトリミングで実装される(ppmtomd.c:2659)
        // — ここでは未実装なので、誤った位置に印字するのではなく明確に拒否する。
        //
        // 用紙の印字不能マージン(ppmtomd の -autoshift)を差し引くのは
        // 呼び出し側の仕事: この層は最終的なシフト値を受け取るだけにして、
        // マージン値は用紙表側に留める。
        int xShift = job.XShift;
        int yShift = job.YShift;
        if (xShift < 0 || yShift < 0)
        {
            throw new EmitterNotImplementedException(
                $"negative shift (x={xShift}, y={yShift}) trims the raster instead of emitting a command; not implemented");
        }
        if (xShift > 0)
        {
            outBytes.AddRange(new byte[] { Esc, 0x26, 0x61, (byte)(xShift % 256), (byte)(xShift / 256), 0x4C });
        }
        if (yShift > 0)
        {
            outBytes.AddRange(new byte[] { Esc, 0x26, 0x6C, (byte)(yShift % 256), (byte)(yShift / 256), 0x45 });
        }

        // changemode ブロック(ppmtomd.c:2189-2245)。印字モードは既定
        // (byMediaMode)のままなので、最初のインクの前に 1 回だけ発火する。
        //
        // カール補正: 0 なら適用、1 なら抑制(ppmtomd の -nocurlcorrection)。
        // デカール素材はシートを平らに保つ必要があるので抑制する(DOMAIN §10.10.4)。
        byte curl = job.NoCurlCorrection ? (byte)1 : (byte)0;
        outBytes.AddRange(new byte[] { Esc, 0x1A, curl, 0, 0x43 });

        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x72, (byte)mode, 0x55 });
        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x72, 0, 0x41 }); // ラスタグラフィックス開始

        if (mode == TransferModes["black_raster"])
        {
            // 単一プレーンのモードは色選択コマンドを一切持たない: モード
            // そのものがどのリボンを使うか示すので、選ぶものもバックフィード
            // で挟むものもない。ppmtomd -black で検証済み: コマンド列は
            // colourPlane のものから選択 4 回・バックフィード 3 回(35
            // バイト)を除いたのと同一。
            if (inks.Count != 1)
            {
                throw new ArgumentException($"black_raster carries exactly one plane, got {inks.Count}");
            }
            var (singlePlane, singleWidth) = PlaneAndWidth(inks[0]);
            outBytes.AddRange(EmitPlaneRows(singlePlane, singleWidth, height));
        }
        else
        {
            // passes(DOMAIN §6.2): インクの(色選択+ラスタ)を passes 回
            // 繰り返す。省略時は既定 1(ConfigLoader.LoadPalette の既定値に
            // 揃えてある)。この展開は emitter の出力形状レベルだけの話で、
            // プレーン自体はインクごとに 1 枚のまま変わらない。
            //
            // passes >= 2 は ppmtomd の実機 golden で検証済み: g21(passes=2)/
            // g22(passes=3)。2026-08-19、WSL 復旧後に採取(GoldenTests.cs の
            // G21WhiteTwiceMd5000_600 / G22WhiteThriceMd5000_600 参照)。
            var occurrences = new List<JobInk>();
            foreach (var ink in inks)
            {
                if (ink.Passes < 1)
                {
                    throw new ArgumentException($"ink '{ink.Name}': 'passes' must be an integer >= 1, got {ink.Passes}");
                }
                for (int p = 0; p < ink.Passes; p++)
                {
                    occurrences.Add(ink);
                }
            }

            int lastIndex = occurrences.Count - 1;

            byte[] SelectAndRows(int index)
            {
                var ink = occurrences[index];
                byte flag = index == lastIndex ? (byte)0x80 : (byte)0x00;
                var buf = new List<byte> { Esc, 0x1A, (byte)ink.PrinterCode, flag, 0x72 };
                var (inkPlane, inkWidth) = PlaneAndWidth(ink);
                buf.AddRange(EmitPlaneRows(inkPlane, inkWidth, height));
                return buf.ToArray();
            }

            // 最初の(直接の)出現のバイト列はそのままストリームに乗る。
            // それ以降の出現は — 別インクでも同じインクの繰り返しパスでも —
            // ppmtomd によって別バッファに蓄えられ、バックフィードコマンドの
            // 後ろに継ぎ足される(ppmtomd.c:2272-2296)。これは出現が
            // 2 つ以上あるときにのみ起きる。
            outBytes.AddRange(SelectAndRows(0));
            if (occurrences.Count > 1)
            {
                for (int index = 1; index < occurrences.Count; index++)
                {
                    outBytes.AddRange(new byte[] { Esc, 0x1A, 0, 0, 0x0C }); // バックフィード
                    outBytes.AddRange(SelectAndRows(index));
                }
            }
        }

        // ジョブ終端(ppmtomd.c:2332-2345)
        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x72, 0x43 }); // ラスタグラフィックス終了
        outBytes.Add(0x0C); // フォームフィード
        outBytes.AddRange(new byte[] { Esc, 0x25, 0, 0x58 }); // RGL モード終了
        outBytes.AddRange(new byte[] { Esc, 0x65 }); // プリンタリセット

        return outBytes.ToArray();
    }
}
