// Foilwright.Core — L0: ALPS USB バルクプロトコルでのプリンタ送出。
//
// DOMAIN §15(実測で確定した USB 転送プロトコル)の C# 実装。
// 参照実装: tools/alps-send.ps1(PowerShell + インライン C#。動作実証済み)、
// tools/alps_send.py(Linux + libusb 版。同一プロトコル)。
//
// ドライバの差し替えは行わない(D-020)。usbprint.sys が公開する
// デバイスインターフェースを CreateFile で開き、WriteFile/ReadFile で
// 独自パケット層をやり取りする。
//
// 注意(実測で判明・DOMAIN §15.2): 応答を返さないコマンドの直後に
// バルク IN を読んではならない。読むとインターフェースがウェッジし、
// 物理再接続でしか回復しない。本実装はコマンドと応答読み取りを必ず
// 対にして呼ぶ(送信要求→許可読取、データ→受理読取、状態問合せ→状態読取)。

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foilwright.Core.Tests")]

namespace Foilwright.Core;

/// <summary>Transport 層の失敗(デバイスが見つからない、開けない、
/// I/O が失敗した、プリンタが想定外の応答を返した)で送出する。</summary>
public class TransportException : Exception
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>I/O がタイムアウト時間内に完了しなかった場合に送出する
/// (DOMAIN §15.2.2)。プリンタがジョブを受理せず沈黙する状態は実地で
/// 確認済みで、解消にはプリンタ本体の電源再投入が必要だった。
/// メッセージに復旧手順を含める(黙って固まらないための要求)。</summary>
public sealed class TransportTimeoutException : TransportException
{
    public TransportTimeoutException(string message) : base(message) { }
}

/// <summary>ALPS 独自パケット層のバイト列組み立て(DOMAIN §15.2)。
/// I/O を一切行わない純粋関数のみで構成し、実機なしで単体テストできる。</summary>
public static class AlpsProtocol
{
    /// <summary>1 データパケットに載せられる最大ペイロード長(総転送 32768 バイト
    /// からヘッダ 4 バイトを引いた値)。</summary>
    public const int MaxPayload = 32764;

    /// <summary>送信要求(OUT)。この直後に 1 バイトの許可応答(0x06)を読む。</summary>
    public static readonly byte[] SendRequest = { 0x05, 0xFF };

    /// <summary>カセット状態の問い合わせ(OUT)。この直後に 38 バイトの状態応答を読む。</summary>
    public static readonly byte[] StatusRequest = { 0x05, 0x01 };

    /// <summary>許可・受理として期待される 1 バイト応答。</summary>
    public const byte Ack = 0x06;

    /// <summary>状態応答の総バイト数(ヘッダ 5 バイト + 11 レコード x 3 バイト)。</summary>
    public const int StatusResponseLength = 38;

    /// <summary>RGL ストリームを MaxPayload バイトごとの断片に分割する。
    /// 空の入力は 0 個の断片を返す(空ジョブを送る意味が無いため)。</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> SplitPayload(byte[] rgl)
    {
        for (int offset = 0; offset < rgl.Length; offset += MaxPayload)
        {
            int len = Math.Min(MaxPayload, rgl.Length - offset);
            yield return new ReadOnlyMemory<byte>(rgl, offset, len);
        }
    }

    /// <summary>1 断片からデータパケット(`02 01 {len-1 の LE16} {断片}`)を組み立てる
    /// (DOMAIN §15.2)。断片は 1..MaxPayload バイトでなければならない。</summary>
    public static byte[] BuildDataPacket(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length == 0 || chunk.Length > MaxPayload)
        {
            throw new ArgumentException(
                $"chunk length must be in 1..{MaxPayload}, got {chunk.Length}", nameof(chunk));
        }
        int n = chunk.Length - 1;
        byte[] packet = new byte[chunk.Length + 4];
        packet[0] = 0x02;
        packet[1] = 0x01;
        packet[2] = (byte)(n & 0xFF);
        packet[3] = (byte)((n >> 8) & 0xFF);
        chunk.CopyTo(packet.AsSpan(4));
        return packet;
    }
}

/// <summary>カセット状態応答(38 バイト)のパース結果(DOMAIN §11.4 / §15.5)。
/// ヘッダ 5 バイト + 11 レコード x 3 バイト。各レコードの先頭バイトが
/// カセットのバーコード番号、0xff は未装着。9 番目のレコード(index 8)は
/// 現在ヘッドに装着中のカセット。</summary>
public sealed class CassetteStatus
{
    /// <summary>装着なしを表すバーコード値。</summary>
    public const byte NotLoaded = 0xFF;

    /// <summary>現在ヘッドに装着中のカセットのスロット index(0 起点)。</summary>
    public const int HeadSlotIndex = 8;

    public required byte[] Header { get; init; } // 5 バイト。5 バイト目が実行状態(00=待機/09=実行中/01=完了)
    public required IReadOnlyList<byte> SlotBarcodes { get; init; } // 11 バイト

    /// <summary>5 バイト目(状態バイト)。00=送出前 / 09=印刷実行中 / 01=完了(DOMAIN §15.4)。</summary>
    public byte StatusByte => Header[4];

    /// <summary>現在ヘッドに装着中のカセットのバーコード。未装着なら NotLoaded。</summary>
    public byte HeadCassette => SlotBarcodes[HeadSlotIndex];

    public static CassetteStatus Parse(byte[] raw)
    {
        if (raw.Length != AlpsProtocol.StatusResponseLength)
        {
            throw new TransportException(
                $"status response must be {AlpsProtocol.StatusResponseLength} bytes, got {raw.Length}");
        }
        var header = raw[..5];
        var slots = new byte[11];
        for (int i = 0; i < 11; i++)
        {
            slots[i] = raw[5 + i * 3];
        }
        return new CassetteStatus { Header = header, SlotBarcodes = slots };
    }
}

/// <summary>IEEE 1284 デバイス ID の取得結果(GET_DEVICE_ID の前置き。DOMAIN §11.4/§15.2/§15.3)。
/// 取得に失敗しても呼び出し側は送出を続ける — 前置きが不要な個体・状況もあるため。
/// Diagnostic には試した候補と win32 エラーを残し、失敗が追えるようにする。</summary>
public readonly record struct DeviceIdProbeResult(bool Success, string? DeviceId, string Diagnostic);

/// <summary>`IOCTL_USBPRINT_GET_1284_ID`(0x220050)経由で IEEE 1284 デバイス ID を取得する
/// (DOMAIN §11.4「先に GET_DEVICE_ID を打たないと次の IN がタイムアウトする」への対応)。
///
/// DOMAIN §11.4 の実測では同じ呼び出し作法(入力バッファ NULL・出力バッファ確保のみ)で
/// win32=87(引数不正)になっており、原因は文書だけでは特定できていない
/// (Microsoft Learn の IOCTL_USBPRINT_GET_1284_ID 仕様上は「入力バッファ不使用・
/// 出力バッファは 2 バイトの長さプレフィックス+ID+終端 NUL、65535 バイトだと失敗する
/// 個体があるため 4094 バイト以下を推奨」とあるだけで、この呼び出し方自体に誤りは
/// 見当たらない)。実機での原因切り分けができないため、出力バッファ長を複数候補で
/// 順に試す(不明点を推測で単一の答えに決め打ちしない)。</summary>
internal static class DeviceIdProbe
{
    // CTL_CODE(FILE_DEVICE_UNKNOWN=0x22, function=20, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    // = (0x22 << 16) | (20 << 2) = 0x220050。DOMAIN §11.4 の実測値と一致
    // (同じ計算式で導出した IOCTL_USBPRINT_GET_LPT_STATUS=0x22004C は実機で成功済み)。
    private const uint IoctlUsbprintGet1284Id = 0x220050;

    /// <summary>出力バッファ長の候補(バイト)。Microsoft Learn の推奨上限(4094)以下で、
    /// 大きい順に試す。§11.4 で失敗した際の 1024 も候補に含める。</summary>
    private static readonly int[] CandidateOutputBufferSizes = { 1024, 256, 64 };

    public static DeviceIdProbeResult TryGet(SafeFileHandle handle)
    {
        var attempts = new List<string>();
        foreach (int size in CandidateOutputBufferSizes)
        {
            byte[] buffer = new byte[size];
            // 入力バッファは MS Learn 仕様通り未使用(NULL/0)。
            bool ok = NativeMethods.DeviceIoControl(
                handle, IoctlUsbprintGet1284Id, IntPtr.Zero, 0, buffer, size, out int returned, IntPtr.Zero);
            if (ok && returned > 2)
            {
                // 先頭 2 バイトはビッグエンディアンの長さ(DOMAIN §11.4)。
                // 申告長が信頼できない場合に備え、実際の読み取りバイト数を上限にする。
                int declaredLen = (buffer[0] << 8) | buffer[1];
                int available = returned - 2;
                int len = declaredLen > 0 && declaredLen <= available ? declaredLen : available;
                string id = System.Text.Encoding.ASCII.GetString(buffer, 2, len).TrimEnd('\0');
                attempts.Add($"bufsize={size}: OK ({returned} bytes)");
                return new DeviceIdProbeResult(true, id, string.Join("; ", attempts));
            }
            if (ok)
            {
                attempts.Add($"bufsize={size}: returned={returned} (too short)");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                attempts.Add($"bufsize={size}: win32={err}");
            }
        }
        return new DeviceIdProbeResult(false, null, string.Join("; ", attempts));
    }
}

/// <summary>usbprint.sys のデバイスインターフェースを直接開き、DOMAIN §15.2 の
/// パケット層で ALPS プリンタと通信する(D-020)。</summary>
public sealed class AlpsTransport : IDisposable
{
    // usbprint.sys が公開するデバイスインターフェース GUID(DOMAIN §15.6)。
    private const string DeviceInterfaceGuid = "{28d78fad-5a12-11D1-ae5b-0000f803a8c2}";

    /// <summary>データパケット書き込みの既定タイムアウト(ミリ秒)。
    /// 送出データ自体の書き込みは長め(DOMAIN §15.2.2)。</summary>
    public const int DefaultWriteTimeoutMs = 30_000;

    /// <summary>ハンドシェイク応答読み取りの既定タイムアウト(ミリ秒)。
    /// `06` 応答・状態応答の待ちは短め(DOMAIN §15.2.2)。</summary>
    public const int DefaultReadTimeoutMs = 10_000;

    private readonly SafeFileHandleWrapper _handle;
    private readonly int _writeTimeoutMs;
    private readonly int _readTimeoutMs;
    private bool _disposed;

    /// <summary>直近の GET_DEVICE_ID 前置き(IOCTL_USBPRINT_GET_1284_ID)の結果。
    /// SendJob / ReadStatus のたびに更新される。失敗しても送出は継続するため、
    /// 呼び出し側が原因を追いたい場合にここを参照する(DOMAIN §11.4/§15.3)。</summary>
    public DeviceIdProbeResult? LastDeviceIdProbe { get; private set; }

    private AlpsTransport(SafeFileHandleWrapper handle, int writeTimeoutMs, int readTimeoutMs)
    {
        _handle = handle;
        _writeTimeoutMs = writeTimeoutMs;
        _readTimeoutMs = readTimeoutMs;
    }

    /// <summary>レジストリの DeviceClasses からデバイスインターフェースパスを探す。
    /// キー名の先頭の `##?#` だけを `\\?\` に置換し、以降の `#` はそのまま残す
    /// (すべて置換すると開けない。DOMAIN §15.6 / D-020)。</summary>
    public static string FindDevicePath(string vidMatch = "VID_044E")
    {
        string root = $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{DeviceInterfaceGuid}";
        using var key = Registry.LocalMachine.OpenSubKey(root);
        if (key is null)
        {
            throw new TransportException($"registry key not found: HKLM\\{root}");
        }
        foreach (string subKeyName in key.GetSubKeyNames())
        {
            if (subKeyName.Contains(vidMatch, StringComparison.OrdinalIgnoreCase))
            {
                return @"\\?\" + Regex.Replace(subKeyName, "^##\\?#", string.Empty);
            }
        }
        throw new TransportException($"no device found matching '{vidMatch}' under HKLM\\{root}");
    }

    /// <summary>デバイスパスを読み書き両用で開く。
    /// writeTimeoutMs / readTimeoutMs は DOMAIN §15.2.2 に基づく既定値
    /// (書き込み 30 秒・応答読み取り 10 秒)を、呼び出し側で変更できる。</summary>
    public static AlpsTransport Open(
        string devicePath,
        int writeTimeoutMs = DefaultWriteTimeoutMs,
        int readTimeoutMs = DefaultReadTimeoutMs)
    {
        var handle = NativeMethods.CreateFile(
            devicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            throw new TransportException($"CreateFile failed for '{devicePath}' (win32={err})");
        }
        return new AlpsTransport(new SafeFileHandleWrapper(handle), writeTimeoutMs, readTimeoutMs);
    }

    /// <summary>デバイスを探して開く(FindDevicePath + Open の組み合わせ)。</summary>
    public static AlpsTransport OpenDevice(
        string vidMatch = "VID_044E",
        int writeTimeoutMs = DefaultWriteTimeoutMs,
        int readTimeoutMs = DefaultReadTimeoutMs)
    {
        return Open(FindDevicePath(vidMatch), writeTimeoutMs, readTimeoutMs);
    }

    private void WriteExact(ReadOnlySpan<byte> data, string what)
    {
        byte[] buffer = data.ToArray();
        TimedIo.WriteWithTimeout(_handle.Handle, buffer, buffer.Length, _writeTimeoutMs, what);
    }

    private byte[] ReadExact(int count, string what)
    {
        return TimedIo.ReadWithTimeout(_handle.Handle, count, _readTimeoutMs, what);
    }

    /// <summary>バルクのやり取りの前置きとして GET_DEVICE_ID を打つ
    /// (DOMAIN §11.4「先に打たないと次の IN がタイムアウトする」/ §15.2「制御転送は
    /// 列挙と GET_DEVICE_ID にしか使わない」)。失敗しても致命的にしない
    /// — 前置きが不要な個体・状況もあるため、送出/状態問い合わせは必ず試みる。
    /// 結果は LastDeviceIdProbe に残る。</summary>
    private void ProbeDeviceId()
    {
        LastDeviceIdProbe = DeviceIdProbe.TryGet(_handle.Handle);
    }

    /// <summary>カセット状態を問い合わせる(`05 01` → 38 バイト。DOMAIN §15.2 / §11.4)。</summary>
    public CassetteStatus ReadStatus()
    {
        ProbeDeviceId();
        WriteExact(AlpsProtocol.StatusRequest, "status request");
        byte[] raw = ReadExact(AlpsProtocol.StatusResponseLength, "status response");
        return CassetteStatus.Parse(raw);
    }

    /// <summary>RGL ジョブを送出する。progress は各断片の送出完了ごとに
    /// (送出済みバイト数, 総バイト数) で呼ばれる(省略可)。
    /// 断片ごとに `05 ff` → `06` → データパケット → `06` を繰り返す
    /// (DOMAIN §15.2)。</summary>
    public void SendJob(byte[] rgl, Action<int, int>? progress = null)
    {
        ProbeDeviceId();
        int done = 0;
        foreach (var chunk in AlpsProtocol.SplitPayload(rgl))
        {
            WriteExact(AlpsProtocol.SendRequest, "send request");
            byte[] permission = ReadExact(1, "send permission");
            if (permission[0] != AlpsProtocol.Ack)
            {
                throw new TransportException(
                    $"send request rejected: expected 0x{AlpsProtocol.Ack:x2}, got 0x{permission[0]:x2}");
            }

            byte[] packet = AlpsProtocol.BuildDataPacket(chunk.Span);
            WriteExact(packet, "data packet");
            byte[] accept = ReadExact(1, "data acceptance");
            if (accept[0] != AlpsProtocol.Ack)
            {
                throw new TransportException(
                    $"data packet rejected: expected 0x{AlpsProtocol.Ack:x2}, got 0x{accept[0]:x2}");
            }

            done += chunk.Length;
            progress?.Invoke(done, rgl.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _handle.Dispose();
    }
}

/// <summary>CreateFile が返すハンドルの薄いラッパー(SafeHandle 派生を独自に
/// 用意して確実に CloseHandle を呼ぶ)。</summary>
internal sealed class SafeFileHandleWrapper : IDisposable
{
    public Microsoft.Win32.SafeHandles.SafeFileHandle Handle { get; }

    public SafeFileHandleWrapper(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        Handle = handle;
    }

    public void Dispose()
    {
        Handle.Dispose();
    }
}

/// <summary>タイムアウト付き同期 WriteFile/ReadFile(DOMAIN §15.2.2)。
///
/// ハンドルは FILE_FLAG_OVERLAPPED なしで開いているため WriteFile/ReadFile
/// はブロッキング呼び出しになる。そこで実 I/O をバックグラウンドスレッドで
/// 実行し、タイムアウトに達したら `CancelIoEx(handle, IntPtr.Zero)` で
/// そのハンドルに対する保留中の I/O をすべて打ち切る。CancelIoEx は
/// lpOverlapped に NULL を渡すと「発行元スレッドを問わず」そのハンドルの
/// 保留 I/O を取り消す(呼び出しスレッドからの発行に限らない)ため、
/// 監視側スレッド(この関数を呼んでいるスレッド)から安全に打ち切れる。
///
/// overlapped I/O へ作り直す代替案もあったが、既存の CreateFile/WriteFile/
/// ReadFile 呼び出し規約(同期・バッファ直渡し)を変えずに済み、変更範囲を
/// Transport.cs 内に閉じられるため、CancelIoEx 方式を採った。</summary>
internal static class TimedIo
{
    /// <summary>キャンセル後、バックグラウンドスレッドの後始末を待つ上限。
    /// 通常の usbprint.sys はここまで待たずに ERROR_OPERATION_ABORTED で
    /// 戻るはずだが、ドライバがキャンセルに応じない万一の場合に永久停止
    /// させないための安全弁。</summary>
    private const int CancelGraceMs = 5_000;

    public static void WriteWithTimeout(SafeFileHandle handle, byte[] buffer, int length, int timeoutMs, string what)
    {
        bool ok = false;
        int written = 0;
        int win32Error = 0;

        var ioTask = Task.Run(() =>
        {
            ok = NativeMethods.WriteFile(handle, buffer, length, out written, IntPtr.Zero);
            if (!ok)
            {
                win32Error = Marshal.GetLastWin32Error();
            }
        });

        if (!ioTask.Wait(timeoutMs))
        {
            NativeMethods.CancelIoEx(handle, IntPtr.Zero);
            ioTask.Wait(CancelGraceMs);
            throw new TransportTimeoutException(
                $"{what}: プリンタが {timeoutMs} ミリ秒以内に応答しませんでした。" +
                "プリンタが応答しない状態です。プリンタ本体の電源を入れ直してから再試行してください。");
        }

        if (!ok)
        {
            throw new TransportException($"{what}: WriteFile failed (win32={win32Error})");
        }
        if (written != length)
        {
            throw new TransportException($"{what}: WriteFile wrote {written} of {length} bytes");
        }
    }

    public static byte[] ReadWithTimeout(SafeFileHandle handle, int count, int timeoutMs, string what)
    {
        byte[] buffer = new byte[count];
        bool ok = false;
        int read = 0;
        int win32Error = 0;

        var ioTask = Task.Run(() =>
        {
            ok = NativeMethods.ReadFile(handle, buffer, count, out read, IntPtr.Zero);
            if (!ok)
            {
                win32Error = Marshal.GetLastWin32Error();
            }
        });

        if (!ioTask.Wait(timeoutMs))
        {
            NativeMethods.CancelIoEx(handle, IntPtr.Zero);
            ioTask.Wait(CancelGraceMs);
            throw new TransportTimeoutException(
                $"{what}: プリンタが {timeoutMs} ミリ秒以内に応答しませんでした。" +
                "プリンタが応答しない状態です。プリンタ本体の電源を入れ直してから再試行してください。");
        }

        if (!ok)
        {
            throw new TransportException($"{what}: ReadFile failed (win32={win32Error})");
        }
        if (read != count)
        {
            throw new TransportException($"{what}: ReadFile returned {read} of {count} bytes");
        }
        return buffer;
    }
}

internal static class NativeMethods
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, byte[] lpBuffer, int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);
}
