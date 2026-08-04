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

namespace Foilwright.Core;

/// <summary>Transport 層の失敗(デバイスが見つからない、開けない、
/// I/O が失敗した、プリンタが想定外の応答を返した)で送出する。</summary>
public sealed class TransportException : Exception
{
    public TransportException(string message) : base(message) { }
    public TransportException(string message, Exception innerException) : base(message, innerException) { }
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

/// <summary>usbprint.sys のデバイスインターフェースを直接開き、DOMAIN §15.2 の
/// パケット層で ALPS プリンタと通信する(D-020)。</summary>
public sealed class AlpsTransport : IDisposable
{
    // usbprint.sys が公開するデバイスインターフェース GUID(DOMAIN §15.6)。
    private const string DeviceInterfaceGuid = "{28d78fad-5a12-11D1-ae5b-0000f803a8c2}";

    private readonly SafeFileHandleWrapper _handle;
    private bool _disposed;

    private AlpsTransport(SafeFileHandleWrapper handle)
    {
        _handle = handle;
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

    /// <summary>デバイスパスを読み書き両用で開く。</summary>
    public static AlpsTransport Open(string devicePath)
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
        return new AlpsTransport(new SafeFileHandleWrapper(handle));
    }

    /// <summary>デバイスを探して開く(FindDevicePath + Open の組み合わせ)。</summary>
    public static AlpsTransport OpenDevice(string vidMatch = "VID_044E")
    {
        return Open(FindDevicePath(vidMatch));
    }

    private void WriteExact(ReadOnlySpan<byte> data, string what)
    {
        if (!NativeMethods.WriteFile(_handle.Handle, data.ToArray(), data.Length, out int written, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new TransportException($"{what}: WriteFile failed (win32={err})");
        }
        if (written != data.Length)
        {
            throw new TransportException($"{what}: WriteFile wrote {written} of {data.Length} bytes");
        }
    }

    private byte[] ReadExact(int count, string what)
    {
        byte[] buffer = new byte[count];
        if (!NativeMethods.ReadFile(_handle.Handle, buffer, count, out int read, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            throw new TransportException($"{what}: ReadFile failed (win32={err})");
        }
        if (read != count)
        {
            throw new TransportException($"{what}: ReadFile returned {read} of {count} bytes");
        }
        return buffer;
    }

    /// <summary>カセット状態を問い合わせる(`05 01` → 38 バイト。DOMAIN §15.2 / §11.4)。</summary>
    public CassetteStatus ReadStatus()
    {
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
}
