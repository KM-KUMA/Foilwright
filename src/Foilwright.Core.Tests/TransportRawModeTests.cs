// Foilwright.Core.Tests — L0(Transport)の生送出モード(TransportMode.Raw)の検証。
//
// D-025: MD-5000 + USB-パラレル変換ケーブル経由の経路には ALPS 独自パケット層が
// 無いとみられるため、RGL を包まずそのまま書く。実機は一切使わない(既存テストと
// 同じく匿名パイプの生ハンドルで代用する)。
//
// 特に重要な確認事項(DOMAIN §15.2.1): 生送出は応答を返さない操作なので、
// 書き込み後にバルク IN を読んではならない。以下のテストの多くは
// PipeDirection.Out(書き込み専用)のハンドルを AlpsTransport に渡すことで、
// もし実装が誤って読み取りを行えば ReadFile が即座に失敗して例外になる
// (アクセス権が無いため無期限にブロックはしない)ことを利用し、
// 「読み取りが一切発生しない」ことを検証している。

using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class TransportRawModeTests
{
    // TransportTests.cs / TransportTimeoutTests.cs と同じ手法: 匿名パイプの
    // 生ハンドルを所有権なしの SafeFileHandle でラップして渡す。
    private static SafeFileHandle WrapAsFileHandle(SafePipeHandle pipeHandle)
    {
        return new SafeFileHandle(pipeHandle.DangerousGetHandle(), ownsHandle: false);
    }

    [Fact]
    public void SendJob_RawMode_WritesRglUnwrapped_NoPacketHeaderNoHandshakeBytes()
    {
        // server(Out)= プリンタ側の受信口を模擬。AlpsTransport はここへ書き込む。
        // client(In) = 実際に届いたバイト列を検証する側。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 5_000, 300, TransportMode.Raw);

        // 末尾に 05 FF(パケット層の送信要求と同じバイト列)を混ぜてあるが、
        // 生送出はプロトコル的な意味づけを一切せずそのまま流すはずなので、
        // これも変換されずに届くことを確認する。
        byte[] rgl = { 0x1B, 0x65, 0xAA, 0xBB, 0xCC, 0x05, 0xFF };

        transport.SendJob(rgl);

        byte[] received = new byte[rgl.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            int r = client.Read(received, totalRead, received.Length - totalRead);
            Assert.True(r > 0, "client did not receive the expected bytes");
            totalRead += r;
        }

        // 生送出であること: `02 01 {len-1}` のパケットヘッダも `05 ff` の
        // 送信要求も付加されず、RGL バイト列がそのまま届く。
        Assert.Equal(rgl, received);
    }

    [Fact]
    public void SendJob_RawMode_DoesNotReadFromDevice()
    {
        // server は PipeDirection.Out で開いているため GENERIC_WRITE のみを持つ。
        // 生送出の実装がもし誤ってバルク IN を読もうとすれば、ReadFile は
        // アクセス権不足で即座に失敗し例外になる(無期限ブロックではない)。
        // 例外が起きずに完了することが「読み取りを一切行っていない」ことの証明になる。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 5_000, 300, TransportMode.Raw);

        byte[] rgl = { 0x01, 0x02, 0x03 };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var exception = Record.Exception(() => transport.SendJob(rgl));
        stopwatch.Stop();

        Assert.Null(exception);
        // 読み取りタイムアウト(300ms)を待つ理由が無いので、それよりずっと
        // 短時間で完了するはず(応答を待っていないことの傍証)。
        Assert.True(stopwatch.ElapsedMilliseconds < 300,
            $"took {stopwatch.ElapsedMilliseconds} ms; a read attempt would have blocked or timed out");

        // 送出したデータは読み捨てず検証する(書き込み自体は行われている)。
        byte[] received = new byte[rgl.Length];
        int totalRead = 0;
        while (totalRead < received.Length)
        {
            totalRead += client.Read(received, totalRead, received.Length - totalRead);
        }
        Assert.Equal(rgl, received);
    }

    [Fact]
    public void SendJob_RawMode_PayloadLargerThanPacketMaxPayload_HasNoFramingAtBoundary()
    {
        // パケット層の 32764 バイト上限(AlpsProtocol.MaxPayload)はデータパケットの
        // ヘッダに由来する制約であり、生送出には同じ上限を適用する根拠が無い。
        // ここでは境界をまたぐ長さでも分割・ヘッダ挿入が起きないことを確認する。
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        int length = AlpsProtocol.MaxPayload + 100;
        byte[] rgl = new byte[length];
        for (int i = 0; i < length; i++)
        {
            rgl[i] = (byte)(i % 256);
        }

        byte[] received = new byte[length];
        var readerTask = Task.Run(() =>
        {
            int total = 0;
            while (total < length)
            {
                int r = client.Read(received, total, length - total);
                if (r <= 0)
                {
                    break;
                }
                total += r;
            }
            return total;
        });

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 10_000, 300, TransportMode.Raw);

        transport.SendJob(rgl);

        int totalRead = readerTask.GetAwaiter().GetResult();

        Assert.Equal(length, totalRead);
        // 32764 バイト境界(AlpsProtocol.MaxPayload)にパケットヘッダ(02 01 ..)が
        // 挿入されていれば、この一致は成立しない。
        Assert.Equal(rgl, received);
    }

    [Fact]
    public void SendJob_RawMode_InvokesProgressOnceWithFullLength()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 5_000, 300, TransportMode.Raw);

        byte[] rgl = { 1, 2, 3, 4 };
        var calls = new List<(int done, int total)>();

        transport.SendJob(rgl, (done, total) => calls.Add((done, total)));

        byte[] buffer = new byte[rgl.Length];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            totalRead += client.Read(buffer, totalRead, buffer.Length - totalRead);
        }

        Assert.Single(calls);
        Assert.Equal((rgl.Length, rgl.Length), calls[0]);
    }

    [Fact]
    public void SendJob_RawMode_EmptyPayload_WritesNothingAndReportsZeroProgress()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(server.SafePipeHandle)), 5_000, 300, TransportMode.Raw);

        var calls = new List<(int done, int total)>();
        transport.SendJob(Array.Empty<byte>(), (done, total) => calls.Add((done, total)));

        Assert.Single(calls);
        Assert.Equal((0, 0), calls[0]);
    }

    [Fact]
    public void SendJob_PacketMode_StillFramesRequestsAndReadsAcks_NoRegression()
    {
        // TransportMode.Packet(既定)は今回のリファクタで挙動を変えていない
        // ことの回帰確認。プリンタ役を模擬するには読み書き両方向が要るため、
        // 単方向の匿名パイプではなく双方向の名前付きパイプを使う(実機は使わない)。
        string pipeName = "foilwright-test-" + Guid.NewGuid();
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);

        var connectTask = Task.Run(() => server.WaitForConnection());
        client.Connect(2_000);
        connectTask.Wait(2_000);

        byte[] rgl = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        byte[]? capturedSendRequest = null;
        byte[]? capturedDataPacket = null;

        // 疑似プリンタ役: server 側で読み取り、許可/受理(06)を返す(DOMAIN §15.2)。
        var deviceTask = Task.Run(() =>
        {
            byte[] req = new byte[2];
            ReadFully(server, req);
            capturedSendRequest = req;
            server.WriteByte(0x06);
            server.Flush();

            byte[] header = new byte[4];
            ReadFully(server, header);
            int lenMinusOne = header[2] | (header[3] << 8);
            int payloadLen = lenMinusOne + 1;
            byte[] payload = new byte[payloadLen];
            ReadFully(server, payload);
            capturedDataPacket = header.Concat(payload).ToArray();
            server.WriteByte(0x06);
            server.Flush();
        });

        using var transport = new AlpsTransport(
            new SafeFileHandleWrapper(WrapAsFileHandle(client.SafePipeHandle)), 5_000, 5_000, TransportMode.Packet);

        transport.SendJob(rgl);

        Assert.True(deviceTask.Wait(5_000), "simulated device did not finish reading the job");

        Assert.Equal(new byte[] { 0x05, 0xFF }, capturedSendRequest);
        Assert.NotNull(capturedDataPacket);
        Assert.Equal(0x02, capturedDataPacket![0]);
        Assert.Equal(0x01, capturedDataPacket[1]);
        Assert.Equal(rgl, capturedDataPacket[4..]);
    }

    private static void ReadFully(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = stream.Read(buffer, total, buffer.Length - total);
            if (r <= 0)
            {
                throw new IOException("stream closed before expected bytes arrived");
            }
            total += r;
        }
    }
}
