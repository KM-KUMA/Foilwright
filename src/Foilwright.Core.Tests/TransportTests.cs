// Foilwright.Core.Tests — L0(Transport)のパケット組み立て検証。
//
// 実機へ送出するコードはここでは一切実行しない(紙とリボンを消費する)。
// AlpsProtocol の純粋関数(パケット組み立て・分割)のみを検証する
// (DOMAIN §15.2)。

using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class TransportTests
{
    // TimedIo 検証(TransportTimeoutTests.cs)と同じ手法: 匿名パイプの生ハンドルを
    // 所有権なしの SafeFileHandle でラップして DeviceIoControl に渡す。
    private static SafeFileHandle WrapAsFileHandle(SafePipeHandle pipeHandle)
    {
        return new SafeFileHandle(pipeHandle.DangerousGetHandle(), ownsHandle: false);
    }

    [Fact]
    public void DeviceIdProbe_TryGet_OnNonUsbprintHandle_FailsGracefullyWithoutThrowing()
    {
        // usbprint.sys 以外のハンドル(匿名パイプ)には IOCTL_USBPRINT_GET_1284_ID が
        // 存在しないため、DeviceIoControl は失敗するはず。ここで検証したいのは値の
        // 正しさではなく、「実機が無くても・IOCTL が失敗しても、例外を投げず、
        // 試した候補と win32 エラーが Diagnostic に残ること」(DOMAIN §11.4)。
        using var server = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);

        var result = DeviceIdProbe.TryGet(WrapAsFileHandle(server.SafePipeHandle));

        Assert.False(result.Success);
        Assert.Null(result.DeviceId);
        Assert.Contains("win32=", result.Diagnostic);
    }

    [Fact]
    public void BuildDataPacket_HeaderEncodesLengthMinusOneLittleEndian()
    {
        byte[] chunk = { 0xAA, 0xBB, 0xCC };
        byte[] packet = AlpsProtocol.BuildDataPacket(chunk);

        // ヘッダ: 02 01 {len-1 LE16}。len=3 -> len-1=2 -> 02 00
        Assert.Equal(new byte[] { 0x02, 0x01, 0x02, 0x00, 0xAA, 0xBB, 0xCC }, packet);
    }

    [Fact]
    public void BuildDataPacket_SingleByteChunk()
    {
        byte[] chunk = { 0x42 };
        byte[] packet = AlpsProtocol.BuildDataPacket(chunk);

        // len=1 -> len-1=0
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0x00, 0x42 }, packet);
    }

    [Fact]
    public void BuildDataPacket_MaxPayloadLengthEncodesCorrectly()
    {
        byte[] chunk = new byte[AlpsProtocol.MaxPayload];
        for (int i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (byte)(i % 256);
        }
        byte[] packet = AlpsProtocol.BuildDataPacket(chunk);

        // len = 32764 -> len-1 = 32763 = 0x7FFB -> LE: FB 7F
        Assert.Equal(0x02, packet[0]);
        Assert.Equal(0x01, packet[1]);
        Assert.Equal(0xFB, packet[2]);
        Assert.Equal(0x7F, packet[3]);
        Assert.Equal(chunk.Length + 4, packet.Length);
        Assert.Equal(chunk, packet[4..]);
    }

    [Fact]
    public void BuildDataPacket_RejectsEmptyChunk()
    {
        Assert.Throws<ArgumentException>(() => AlpsProtocol.BuildDataPacket(Array.Empty<byte>()));
    }

    [Fact]
    public void BuildDataPacket_RejectsOversizedChunk()
    {
        byte[] chunk = new byte[AlpsProtocol.MaxPayload + 1];
        Assert.Throws<ArgumentException>(() => AlpsProtocol.BuildDataPacket(chunk));
    }

    [Fact]
    public void SplitPayload_EmptyInputYieldsNoChunks()
    {
        var chunks = AlpsProtocol.SplitPayload(Array.Empty<byte>()).ToList();
        Assert.Empty(chunks);
    }

    [Fact]
    public void SplitPayload_SmallInputYieldsSingleChunk()
    {
        byte[] rgl = { 1, 2, 3, 4, 5 };
        var chunks = AlpsProtocol.SplitPayload(rgl).ToList();

        Assert.Single(chunks);
        Assert.Equal(rgl, chunks[0].ToArray());
    }

    [Fact]
    public void SplitPayload_ExactlyMaxPayloadYieldsSingleChunk()
    {
        // 境界ちょうどのケース: MaxPayload バイトはちょうど 1 断片に収まる
        // べきで、2 断片目(空)を生んではならない。
        byte[] rgl = new byte[AlpsProtocol.MaxPayload];
        var chunks = AlpsProtocol.SplitPayload(rgl).ToList();

        Assert.Single(chunks);
        Assert.Equal(AlpsProtocol.MaxPayload, chunks[0].Length);
    }

    [Fact]
    public void SplitPayload_OneByteOverMaxPayloadYieldsTwoChunks()
    {
        // 境界ちょうど + 1 バイト: 2 断片目に 1 バイトだけ残るべき。
        byte[] rgl = new byte[AlpsProtocol.MaxPayload + 1];
        for (int i = 0; i < rgl.Length; i++)
        {
            rgl[i] = (byte)(i % 256);
        }
        var chunks = AlpsProtocol.SplitPayload(rgl).ToList();

        Assert.Equal(2, chunks.Count);
        Assert.Equal(AlpsProtocol.MaxPayload, chunks[0].Length);
        Assert.Equal(1, chunks[1].Length);
        Assert.Equal(rgl[AlpsProtocol.MaxPayload], chunks[1].Span[0]);

        // 元データを結合すると一致すること(境界での欠落・重複が無いこと)
        byte[] rejoined = chunks.SelectMany(c => c.ToArray()).ToArray();
        Assert.Equal(rgl, rejoined);
    }

    [Fact]
    public void SplitPayload_TwoFullPayloadsPlusOneByte()
    {
        // 32764 * 2 + 1 バイト -> 3 断片(最後は 1 バイト)。
        byte[] rgl = new byte[AlpsProtocol.MaxPayload * 2 + 1];
        var chunks = AlpsProtocol.SplitPayload(rgl).ToList();

        Assert.Equal(3, chunks.Count);
        Assert.Equal(AlpsProtocol.MaxPayload, chunks[0].Length);
        Assert.Equal(AlpsProtocol.MaxPayload, chunks[1].Length);
        Assert.Equal(1, chunks[2].Length);
    }

    [Fact]
    public void CassetteStatus_Parse_SplitsHeaderAndNineEntriesPlusErrorBytes()
    {
        // STX(1) + パケット種別(1) + ペイロード長 LE16(2) + 状態バイト(1) +
        // 9 エントリ x 3 バイト(27) + エラーバイト(5) + ETX(1) = 38。
        // 一次情報: ppmtomd 付属 getstat.pl の parse_status(URL は DOMAIN §11.4 参照)。
        byte[] raw = new byte[38];
        raw[0] = 0x02; // STX
        raw[1] = 0x80; // パケット種別
        raw[2] = 0x21; // ペイロード長 LE16 下位
        raw[3] = 0x00; // ペイロード長 LE16 上位
        raw[4] = 0x09; // 状態バイト = 実行中
        byte[] expectedStats = { 0xff, 0x03, 0x01, 0x00, 0xff, 0x02, 0xff, 0xff, 0x00 };
        for (int i = 0; i < CassetteStatus.EntryCount; i++)
        {
            raw[5 + i * 3] = expectedStats[i];
            raw[5 + i * 3 + 1] = 0xEE; // low(ダミー)
            raw[5 + i * 3 + 2] = 0xDD; // high(ダミー)
        }
        byte[] expectedErrorBytes = { 0x11, 0x22, 0x33, 0x44, 0x55 };
        for (int i = 0; i < expectedErrorBytes.Length; i++)
        {
            raw[32 + i] = expectedErrorBytes[i];
        }
        raw[37] = 0x03; // ETX

        var status = CassetteStatus.Parse(raw);

        Assert.Equal(0x09, status.StatusByte);
        Assert.Equal(expectedStats, status.SlotBarcodes);
        Assert.Equal(0x00, status.HeadCassette); // index 8 = ヘッドに装着中
        Assert.All(status.EntryLow, b => Assert.Equal(0xEE, b));
        Assert.All(status.EntryHigh, b => Assert.Equal(0xDD, b));
        Assert.Equal(expectedErrorBytes, status.ErrorBytes);
    }

    [Fact]
    public void CassetteStatus_Parse_RejectsWrongLength()
    {
        Assert.Throws<TransportException>(() => CassetteStatus.Parse(new byte[37]));
        Assert.Throws<TransportException>(() => CassetteStatus.Parse(new byte[39]));
    }

    [Fact]
    public void CassetteStatus_Parse_RejectsMissingEtx()
    {
        byte[] raw = new byte[38];
        raw[0] = 0x02;
        raw[1] = 0x80;
        raw[37] = 0x00; // ETX ではない
        var ex = Assert.Throws<TransportException>(() => CassetteStatus.Parse(raw));
        Assert.Contains("ETX", ex.Message);
    }
}
