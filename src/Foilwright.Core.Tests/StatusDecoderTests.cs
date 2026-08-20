// Foilwright.Core.Tests — StatusDecoder(CassetteStatus → 日本語表示)の検証。
//
// ここで使う 38 バイトの応答はすべて DOMAIN §11.4 に記録された実測値。
// ただし §11.4 の「ペーパーフィード異常」2 例(MD-5500 / MD-5000)は文中の
// 表記が 1 エントリ(3 バイト)分短く転記されている(offset 29-31 の手前で
// 抜けている)。ここでは末尾のエラーバイト(offset 32-36)とヘッダの状態バイト
// だけを実測値どおりに保ち、抜けている 1 エントリ分は検証に影響しない
// ff 00 00(未装着)で埋めて 38 バイトに揃えている。
//
// 【2026-08-20】パケット構造を一次情報(ppmtomd 付属 getstat.pl の
// parse_status。URL は DOMAIN §11.4 参照)に基づいて訂正した。旧実装は
// 「3 バイト x 11 レコード」と読んでおり、実際のエラーバイト(offset 32-36)を
// レコードとして扱っていた。正しくは 状態 1 + 9 エントリ x 3 + エラー 5 + ETX。
//
// **エラーバイト e[4] にモータ種別の欄が実在することが分かった**(0x10=LF /
// 0x40=CR など)。ただし **これを「故障した機構の種別」として断定してはいけない**:
//
//   - 同じ「ペーパーフィード異常」の表示に対し、MD-5500 は e[4]=0x10(LF)、
//     MD-5000 は e[4]=0x40(CR)と、機種で違う値を返していた
//   - 同一機体(MD-5000)で純正が「ペーパーフィード異常」と「キャリッジ異常」を
//     表示した 2 回の応答は **1 バイトも違わなかった**(DOMAIN §11.4、2026-08-08)。
//     **バイト列が同一である以上、オフセットの読み方を変えても区別はつかない。**
//
// したがって §13.8.3 の「種別は特定できません」という慎重な扱いは有効なまま。
// **バイトが言っていることは表示してよいが、それが実際の故障箇所だと断定しない。**
using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class StatusDecoderTests
{
    // DOMAIN §11.4「状態応答の構造を訂正 + エラー時の値」の正常例(完全な 38 バイト)。
    private static readonly byte[] NormalResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x03, 0x00, 0x00,
        0x01, 0x00, 0x00,
        0x00, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x02, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x00, 0x00, 0x00,
        0x00, 0x00, 0x03,
    };

    // DOMAIN §11.4「状態応答の構造を訂正 + エラー時の値」の給紙エラー例(完全な 38 バイト、
    // 状態バイト 0xc9 = 0xc0(エラー)|0x09(印刷実行中)。rec[9] 先頭は 0x00 で
    // 「ペーパーフィード異常」(0x80)には該当しない未知のエラーコード)。
    private static readonly byte[] PrintingErrorResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0xc9,
        0xff, 0x00, 0x00,
        0x03, 0x31, 0x00,
        0x01, 0x8d, 0x02,
        0x00, 0x8d, 0x02,
        0xff, 0x00, 0x00,
        0x02, 0x3f, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0x00, 0x10, 0x00,
        0x86, 0x00, 0x03,
    };

    // DOMAIN §11.4「確定した対応」— MD-5500 のペーパーフィード異常。
    // 状態バイト 0xc0、rec[9] 先頭 0x80。ヘッド(rec[8])は文中で省略されているため
    // ff 00 00(未装着)で埋めた(冒頭コメント参照)。
    private static readonly byte[] Md5500PaperFeedErrorResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0xc0,
        0x03, 0x31, 0x00,
        0x02, 0x3d, 0x00,
        0x01, 0x4f, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00, // 補完: 文中で省略されているヘッド(rec[8])スロット
        0x80, 0x00, 0x00,
        0x00, 0x10, 0x03,
    };

    // DOMAIN §11.4「MD-5000(パラレル + 変換ケーブル)での実測」。
    // 状態バイト 0xc0、rec[9] 先頭 0x80(同一のペーパーフィード異常が機種をまたいで観測)。
    // ヘッド(rec[8])は文中で省略されているため ff 00 00 で埋めた(冒頭コメント参照)。
    private static readonly byte[] Md5000ErrorResponse =
    {
        0x02, 0x80, 0x21, 0x00, 0xc0,
        0x00, 0x72, 0x00,
        0x03, 0x32, 0x00,
        0x02, 0x3f, 0x00,
        0x01, 0x31, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00,
        0xff, 0x00, 0x00, // 補完: 文中で省略されているヘッド(rec[8])スロット
        0x80, 0x00, 0x00,
        0x00, 0x40, 0x03,
    };

    [Fact]
    public void Describe_NormalResponse_IsIdleAndNotError()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        var report = StatusDecoder.Describe(status);

        Assert.False(report.IsError);
        Assert.False(report.IsPrinting);
        Assert.False(report.CassetteInfoMayBeStale);
        Assert.Equal("待機", report.StatusSummary);
        Assert.Null(report.ErrorDetail);
        Assert.Equal("紙用シアン", CassetteCatalog.GetName(status.SlotBarcodes[1]));
    }

    [Fact]
    public void Describe_Md5500PaperFeedError_ReportsMotorErrorWithLfDetail()
    {
        // errorBytes(offset 32-36) = 80 00 00 00 10 → e[0]&0x80(モータエラー)、
        // e[4]=0x10(LF)。DOMAIN §11.4 / 一次情報 getstat.pl 参照。
        var status = CassetteStatus.Parse(Md5500PaperFeedErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.False(report.IsPrinting);
        Assert.True(report.CassetteInfoMayBeStale);
        Assert.Equal("モータエラー(LF)", report.ErrorDetail);
        Assert.Contains("モータエラー(LF)", report.StatusSummary);
    }

    [Fact]
    public void Describe_Md5000Error_ReportsMotorErrorWithCrDetail()
    {
        // errorBytes(offset 32-36) = 80 00 00 00 40 → e[0]&0x80(モータエラー)、
        // e[4]=0x40(CR)。
        //
        // 注意: この応答は、純正が「ペーパーフィード異常」を表示していたときのもの。
        // 同一機体で「キャリッジ異常」を表示していたときの応答も 1 バイトも違わなかった
        // (DOMAIN §11.4、2026-08-08)。**e[4] が言う CR は、故障箇所の断定に使えない。**
        var status = CassetteStatus.Parse(Md5000ErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.Equal("モータエラー(CR)", report.ErrorDetail);
    }

    [Fact]
    public void Describe_MotorError_ErrorByte4_DiffersBetweenMachines_ForTheSameReportedFault()
    {
        // 同じ「ペーパーフィード異常」に対し、MD-5500 は e[4]=0x10(LF)、
        // MD-5000 は e[4]=0x40(CR)を返していた。**機種で値が違う。**
        // これは「e[4] が故障機構を一意に指す」わけではないことの証拠であり、
        // 区別できること自体を利点として扱わない(§13.8.3 の慎重な扱いは有効)。
        var md5500 = StatusDecoder.Describe(CassetteStatus.Parse(Md5500PaperFeedErrorResponse));
        var md5000 = StatusDecoder.Describe(CassetteStatus.Parse(Md5000ErrorResponse));

        Assert.NotEqual(md5500.ErrorDetail, md5000.ErrorDetail);
        Assert.Contains("LF", md5500.ErrorDetail);
        Assert.Contains("CR", md5000.ErrorDetail);
    }

    [Fact]
    public void Describe_0xC9_IsBothErrorAndPrinting()
    {
        // errorBytes(offset 32-36) = 00 10 00 86 00 → e[3]&0x07=0x06,
        // >>1 = 3 →「リボン不一致(Ribbon Mismatch)」(getstat.pl の e[3] 論理)。
        var status = CassetteStatus.Parse(PrintingErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.Equal((byte)0xc9, status.StatusByte);
        Assert.True(report.IsError);
        Assert.True(report.IsPrinting);
        Assert.Contains("リボン不一致", report.ErrorDetail);
        Assert.Contains("Ribbon Mismatch", report.ErrorDetail);
    }

    [Fact]
    public void Describe_Idle_NoErrorBytes_ReportsNoError()
    {
        // 実測との対応(依頼メッセージの表): 待機。状態=0x00、e[0..4]=00 00 00 00 00。
        var status = CassetteStatus.Parse(BuildResponse(0x00, new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }));
        var report = StatusDecoder.Describe(status);

        Assert.False(report.IsError);
        Assert.Null(report.ErrorDetail);
        Assert.Equal("待機", report.StatusSummary);
    }

    [Fact]
    public void Describe_RealPrintFailure_ReportsOutOfPaperAndJam()
    {
        // 実測との対応: 実原稿の印刷で失敗。状態=0xC9、e[0..4]=00 c0 08 00 00。
        // 期待する解読: 用紙なし(トレイ)+紙詰まり(排紙エラー・トレイ)。
        var status = CassetteStatus.Parse(BuildResponse(0xC9, new byte[] { 0x00, 0xC0, 0x08, 0x00, 0x00 }));
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.Equal("用紙なし(トレイ)+紙詰まり(排紙エラー・トレイ)", report.ErrorDetail);
    }

    [Fact]
    public void Describe_MarginProbeFailure_ReportsPaperSizeMismatch()
    {
        // 実測との対応: 余白プローブで失敗。状態=0xC9、e[0..4]=00 80 80 00 00。
        // 期待する解読: 用紙サイズ違い(トレイ)(紙詰まりビットは立っていない)。
        var status = CassetteStatus.Parse(BuildResponse(0xC9, new byte[] { 0x00, 0x80, 0x80, 0x00, 0x00 }));
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.Equal("用紙サイズ違い(トレイ)", report.ErrorDetail);
    }

    /// <summary>状態バイトとエラーバイト 5 個だけを指定し、残りは意味のない
    /// ダミー値(エントリ全て未装着 ff 00 00)で埋めた 38 バイトの応答を組み立てる。</summary>
    private static byte[] BuildResponse(byte statusByte, byte[] errorBytes)
    {
        byte[] raw = new byte[38];
        raw[0] = 0x02; // STX
        raw[1] = 0x80; // パケット種別
        raw[2] = 0x21; // ペイロード長 LE16 下位(33)
        raw[3] = 0x00; // ペイロード長 LE16 上位
        raw[4] = statusByte;
        for (int i = 0; i < CassetteStatus.EntryCount; i++)
        {
            int offset = 5 + i * 3;
            raw[offset] = 0xFF; // 未装着
            raw[offset + 1] = 0x00;
            raw[offset + 2] = 0x00;
        }
        Array.Copy(errorBytes, 0, raw, 32, 5);
        raw[37] = 0x03; // ETX
        return raw;
    }

    [Fact]
    public void GetFlags_DecomposesKnownStatusBytesCorrectly()
    {
        Assert.Equal(PrinterStatusFlags.None, StatusDecoder.GetFlags(0x00));
        Assert.Equal(PrinterStatusFlags.None, StatusDecoder.GetFlags(0x01));
        Assert.Equal(PrinterStatusFlags.Printing, StatusDecoder.GetFlags(0x09));
        Assert.Equal(PrinterStatusFlags.None, StatusDecoder.GetFlags(0x10));
        Assert.Equal(PrinterStatusFlags.Error, StatusDecoder.GetFlags(0xc0));
        Assert.Equal(PrinterStatusFlags.Error | PrinterStatusFlags.Printing, StatusDecoder.GetFlags(0xc9));
    }

    [Fact]
    public void Describe_UnknownStatusByte_ShowsRawValue()
    {
        byte[] raw = (byte[])NormalResponse.Clone();
        raw[4] = 0x42; // 既知のどの値・ビットにも該当しない状態バイト
        var status = CassetteStatus.Parse(raw);
        var report = StatusDecoder.Describe(status);

        Assert.False(report.IsError);
        Assert.False(report.IsPrinting);
        Assert.Contains("未知の状態", report.StatusSummary);
        Assert.Contains("0x42", report.StatusSummary);
    }

    [Fact]
    public void CassetteCatalog_UnknownBarcode_ShowsRawValue()
    {
        // stat バイト上位 2 ビット=00(正常)、下位 6 ビット=0x3e(対応表に無い値)。
        string name = CassetteCatalog.GetName(0x3e);
        Assert.Contains("不明なカセット", name);
        Assert.Contains("0x3e", name);
    }

    [Fact]
    public void CassetteCatalog_RibbonEnd_NamesTheInkWithEndSuffix()
    {
        // 2026-08-19 実測: シアンのリボンが終端まで来た機体が 0x83 を返し、
        // 利用者が現物でシアン切れを確認した。0x83 = stat 上位 2 ビット 2(リボン終端)
        // + 下位 6 ビット 0x03(紙用シアン)(一次情報: getstat.pl の parse_status)。
        string name = CassetteCatalog.GetName(0x83);
        Assert.Contains("シアン", name);
        Assert.Contains("リボン終端", name);
        Assert.DoesNotContain("不明なカセット", name);
    }

    [Fact]
    public void CassetteCatalog_RibbonReversed_NamesTheInkWithReversedSuffix()
    {
        // stat 上位 2 ビット 1(リボン逆装着)+ 下位 6 ビット 0x00(紙用ブラック)。
        string name = CassetteCatalog.GetName(0x40);
        Assert.Contains("ブラック", name);
        Assert.Contains("リボン逆装着", name);
    }

    [Fact]
    public void CassetteCatalog_NoCassette_ReturnsUnloadedLabel()
    {
        // stat 上位 2 ビット 3(カセット無し)。下位 6 ビットは無意味。
        string name = CassetteCatalog.GetName(0xC0);
        Assert.Equal("未装着", name);
    }

    [Fact]
    public void CassetteCatalog_UnusableFlag_DoesNotAffectKnownBarcodes()
    {
        // 上位ビットが立っていない既知の値は、今までどおり素の名前を返す。
        Assert.Equal("紙用シアン", CassetteCatalog.GetName(0x03));
        Assert.DoesNotContain("使用不可", CassetteCatalog.GetName(0x03));
    }

    [Fact]
    public void CassetteCatalog_NotLoaded_ReturnsUnloadedLabel()
    {
        Assert.Equal("未装着", CassetteCatalog.GetName(CassetteStatus.NotLoaded));
    }

    [Fact]
    public void Describe_HeadCassette_ReflectsSlotIndex8()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        var report = StatusDecoder.Describe(status);

        // NormalResponse の rec[8](head)は ff → 未装着。
        Assert.Equal("未装着", report.HeadCassetteName);
        Assert.Equal(8, CassetteStatus.HeadSlotIndex);
    }

    [Fact]
    public void Describe_HolderSlots_HasEightEntriesWithSlotNumbersOneToEight()
    {
        var status = CassetteStatus.Parse(NormalResponse);
        var report = StatusDecoder.Describe(status);

        Assert.Equal(8, report.HolderSlots.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i + 1, report.HolderSlots[i].SlotNumber);
        }
    }
    [Fact]
    public void Describe_CoverOpen_IsNamed()
    {
        // 2026-08-20 実機実測: カバーを開けると 0xC0 / e[0]=0x40 になり、
        // 閉じると 0x00 / e = 00 00 00 00 00 に戻った。
        // 当初この分岐を実装し忘れており「未知のエラー」と表示していた。
        var raw = new byte[38];
        raw[0] = 0x02; raw[1] = 0x80; raw[2] = 0x21; raw[3] = 0x00;
        raw[4] = 0xC0;
        for (int i = 0; i < 9; i++)
        {
            raw[5 + i * 3] = 0xFF;
        }
        raw[32] = 0x40;
        raw[37] = 0x03;

        var report = StatusDecoder.Describe(CassetteStatus.Parse(raw));

        Assert.True(report.IsError);
        Assert.Equal("カバーが開いています", report.ErrorDetail);
    }
}
