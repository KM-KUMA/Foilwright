// Foilwright.Core.Tests — L0(Transport)のタイムアウト機構の検証。
//
// 実機は一切使わない(DOMAIN §15.2.2 は実機で踏んだ欠陥だが、検証は
// 名前付きなし匿名パイプ(Win32 ハンドル)を使い、応答しない相手を
// 模擬する)。ReadFile/WriteFile が対象のハンドルへ届く限り、
// 検証対象の TimedIo は usbprint.sys のハンドルと同じ Win32 API 経路を
// 通るため、パイプでの検証は実装の妥当性を裏付ける。

using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class TransportTimeoutTests
{
    // TimedIo は SafeFileHandle を要求するが、匿名パイプは SafePipeHandle
    // (別の SafeHandle 派生)を返す。生ハンドル値は同じ Win32 HANDLE なので、
    // 所有権を持たない SafeFileHandle でラップして渡す(Dispose は元の
    // SafePipeHandle 側に任せる)。
    private static SafeFileHandle WrapAsFileHandle(SafePipeHandle pipeHandle)
    {
        return new SafeFileHandle(pipeHandle.DangerousGetHandle(), ownsHandle: false);
    }

    [Fact]
    public void ReadWithTimeout_NoDataArrives_ThrowsTransportTimeoutExceptionWithinBoundedTime()
    {
        // サーバー側(読み取り専用)を開くだけでクライアント側には誰も書き込まない。
        // ReadFile はブロックし続けるはずで、これがプリンタが 06 を返さない
        // 状況(DOMAIN §15.2.2)の模擬になる。
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        int timeoutMs = 500;
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.Throws<TransportTimeoutException>(
            () => TimedIo.ReadWithTimeout(WrapAsFileHandle(server.SafePipeHandle), 8, timeoutMs, "test read"));

        stopwatch.Stop();

        // タイムアウト+キャンセルの猶予を大きく超えて待たされていないこと
        // (無期限ブロックしていないことの直接的な証明)。
        Assert.True(stopwatch.ElapsedMilliseconds < timeoutMs + 5_000,
            $"took {stopwatch.ElapsedMilliseconds} ms, expected well under {timeoutMs + 5_000} ms");

        Assert.Contains("電源を入れ直して", ex.Message);
    }

    [Fact]
    public void WriteWithTimeout_NoReaderDrainsPipe_ThrowsTransportTimeoutExceptionWithinBoundedTime()
    {
        // クライアント側を誰も読まないまま、サーバー側(書き込み専用)の
        // パイプバッファを使い切るまで書き続けると、以降の WriteFile は
        // ブロックする。プリンタがデータパケットを受理しない状況
        // (DOMAIN §15.2.2)の模擬になる。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        int timeoutMs = 500;

        // まずパイプバッファを埋める(クライアントは読まないので必ず埋まる)。
        // 十分大きいチャンクを、詰まって書けなくなるまで書き込む。
        byte[] filler = new byte[64 * 1024];
        bool blocked = false;
        var fillStopwatch = Stopwatch.StartNew();
        while (fillStopwatch.ElapsedMilliseconds < 5_000)
        {
            try
            {
                TimedIo.WriteWithTimeout(WrapAsFileHandle(server.SafePipeHandle), filler, filler.Length, 200, "fill");
            }
            catch (TransportTimeoutException)
            {
                blocked = true;
                break;
            }
        }

        Assert.True(blocked, "pipe did not fill within 5s; cannot exercise write timeout path");

        // ここからが本検証: バッファが詰まった状態でのタイムアウト計測。
        var stopwatch = Stopwatch.StartNew();

        var ex = Assert.Throws<TransportTimeoutException>(
            () => TimedIo.WriteWithTimeout(WrapAsFileHandle(server.SafePipeHandle), filler, filler.Length, timeoutMs, "test write"));

        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < timeoutMs + 5_000,
            $"took {stopwatch.ElapsedMilliseconds} ms, expected well under {timeoutMs + 5_000} ms");

        Assert.Contains("電源を入れ直して", ex.Message);
    }
}
