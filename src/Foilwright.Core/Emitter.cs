// Foilwright.Core — L1: インクプレーン + 機種プロファイル -> MD コマンドの
// バイト列。
//
// tests/golden/ の golden fixture が行使する範囲の ppmtomd 1.6 RGL コマンド
// 生成を再現する:
//
// - 単一ページ・単一パス・単一転送モードグループ(転送モードは常に
//   "colourPlane" = 0x04; ppmtomd.c:1312-1313)
// - カール補正なし、LF/印字ヘッド補正なし、光沢仕上げなし、カセットの
//   バーコード一覧なし、x/y シフトなし(golden のコマンド列はどれもこれらを
//   有効化するオプションを使っていない)
// - PackBits 圧縮は ppmtomd.c:2362-2452(packbits())のとおり
// - ppmtomd の「余ったプレーンはスクラッチバッファへ回し、バックフィード
//   コマンドで後から継ぎ足す」挙動(fd のルーティングは ppmtomd.c:2092-2138、
//   バックフィード/スプライスは 2244-2296)— これが、既定(-colours なし)の
//   ジョブでも黒しかインクが無いのに空の Cyan/Magenta/Yellow プレーンが
//   出力される理由。
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
    /// "black_raster"(-black、単一プレーン、色選択コマンドなし)。</summary>
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
    // 以下の 2 つのみ実装する。カセットモードはここでは扱わないカートリッジ
    // バーコードを送る(DOMAIN §6.5)し、ラスタモードはデータレイアウトが
    // 異なる。
    private static readonly Dictionary<string, int> TransferModes = new()
    {
        ["black_raster"] = 0x00, // 単一プレーン、色選択なし(-black)
        ["colour_plane"] = 0x04, // 選択 1 回 + インクごとの行(ppmtomd 既定)
    };

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

        int mode = TransferModes[job.TransferMode];
        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x72, (byte)mode, 0x55 });
        outBytes.AddRange(new byte[] { Esc, 0x2A, 0x72, 0, 0x41 }); // ラスタグラフィックス開始

        var inks = job.Inks;

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
            outBytes.AddRange(EmitPlaneRows(planes[inks[0].Name], width, height));
        }
        else
        {
            int lastIndex = inks.Count - 1;

            byte[] SelectAndRows(int index)
            {
                var ink = inks[index];
                byte flag = index == lastIndex ? (byte)0x80 : (byte)0x00;
                var buf = new List<byte> { Esc, 0x1A, (byte)ink.PrinterCode, flag, 0x72 };
                buf.AddRange(EmitPlaneRows(planes[ink.Name], width, height));
                return buf.ToArray();
            }

            // 最初の(直接の)インクのバイト列はそのままストリームに乗る。
            // それ以降の各インクは ppmtomd によって別バッファに蓄えられ、
            // バックフィードコマンドの後ろに継ぎ足される(ppmtomd.c:2272-2296)
            // — これはアクティブなインクが 2 つ以上あるときにのみ起きる。
            outBytes.AddRange(SelectAndRows(0));
            if (inks.Count > 1)
            {
                for (int index = 1; index < inks.Count; index++)
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
