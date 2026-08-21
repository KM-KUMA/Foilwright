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
    /// <summary>タイムアウト後、打ち切りが終わるまでに許す時間。
    ///
    /// ここで見たいのは「**無期限にブロックしていないこと**」だけであり、
    /// 打ち切りが何ミリ秒で終わるかは検証の対象ではない。以前は 5,000 ms
    /// だったが、**実測が上限 5,500 ms に対して 5,522 ms と境界に貼り付いており、
    /// 環境しだいで落ちるテストになっていた**(2026-08-21 に 2 つの実行環境で再現)。
    /// 時間を測るテストで余裕を実測値の近くに置くと、**コードが壊れていないのに赤くなる**。
    ///
    /// **打ち切りそのものに約 5 秒かかっている**ことは観測事実として残す —
    /// タイムアウト 500 ms を指定しても、呼び出しが戻るまでは概ね 5.5 秒になる。
    /// **実運用では「止めると決めてから戻るまで 5 秒待たされる」**という意味であり、
    /// 気になるなら別途調べる価値がある(ここでは扱わない)。</summary>
    private const int CancellationGraceMs = 15_000;

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
        Assert.True(stopwatch.ElapsedMilliseconds < timeoutMs + CancellationGraceMs,
            $"took {stopwatch.ElapsedMilliseconds} ms, expected well under {timeoutMs + CancellationGraceMs} ms");

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

        Assert.True(stopwatch.ElapsedMilliseconds < timeoutMs + CancellationGraceMs,
            $"took {stopwatch.ElapsedMilliseconds} ms, expected well under {timeoutMs + CancellationGraceMs} ms");

        Assert.Contains("電源を入れ直して", ex.Message);
    }

    [Fact]
    public void TryReadWithTimeout_NoDataArrives_ReturnsTimedOutWithoutThrowing()
    {
        // ドレインの中核となる読み取りプリミティブの検証。ReadWithTimeout と
        // 違い、タイムアウトは正常な終了条件であって例外にしてはならない
        // (受信パイプに読み残しが無いことを示す)。
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        var (bytesRead, timedOut) = TimedIo.TryReadWithTimeout(
            WrapAsFileHandle(server.SafePipeHandle), new byte[64], 300, "drain read");

        Assert.True(timedOut);
        Assert.Equal(0, bytesRead);
    }

    [Fact]
    public void TryReadWithTimeout_PartialDataAvailable_ReturnsActualByteCountWithoutRequiringExactFill()
    {
        // ReadWithTimeout はちょうど count バイトを要求するが、ドレインでは
        // バッファを埋め切らない部分読み取りも正常な結果として受理する。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        byte[] leftover = { 0x00, 0x00, 0x00, 0x00, 0x00 }; // 実測で観測された読み残し(102 バイトのうちの一部を模擬)
        server.Write(leftover, 0, leftover.Length);
        server.Flush();

        var (bytesRead, timedOut) = TimedIo.TryReadWithTimeout(
            WrapAsFileHandle(client.SafePipeHandle), new byte[64], 2_000, "drain read");

        Assert.False(timedOut);
        Assert.Equal(leftover.Length, bytesRead);
    }

    [Fact]
    public void Drain_LeftoverBytesQueued_DiscardsThemAndStopsOnTimeout()
    {
        // Open() 相当のシナリオ: 受信パイプに前回の読み残しが滞留した状態から
        // Drain() を呼ぶと、それを読み捨てて DrainedByteCount に反映し、
        // それ以上データが来なくなった時点(タイムアウト)で止まること。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        byte[] leftover = new byte[102]; // 実測(状態応答の読み残し)と同じ長さ
        server.Write(leftover, 0, leftover.Length);
        server.Flush();

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(client.SafePipeHandle)), 30_000, 10_000);

        int discarded = transport.Drain(readTimeoutMs: 300);

        Assert.Equal(leftover.Length, discarded);
        Assert.Equal(leftover.Length, transport.DrainedByteCount);
    }

    [Fact]
    public void Drain_NothingQueued_ReturnsZeroWithinBoundedTime()
    {
        // 読み残しが無い正常時は、1 回のタイムアウト待ちだけで即座に戻ること
        // (無期限に待ち続けない)。
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 30_000, 10_000);

        var stopwatch = Stopwatch.StartNew();
        int discarded = transport.Drain(readTimeoutMs: 300);
        stopwatch.Stop();

        Assert.Equal(0, discarded);
        Assert.Equal(0, transport.DrainedByteCount);
        Assert.True(stopwatch.ElapsedMilliseconds < 300 + 5_000,
            $"took {stopwatch.ElapsedMilliseconds} ms");
    }
}
