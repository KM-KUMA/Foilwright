// Foilwright.Core.Tests — StatusDecoder(CassetteStatus → 日本語表示)の検証。
//
// ここで使う 38 バイトの応答はすべて DOMAIN §11.4 に記録された実測値。
// ただし §11.4 の「ペーパーフィード異常」2 例(MD-5500 / MD-5000)は文中の
// 表記が 1 レコード(3 バイト)分短く転記されている(rec[9]/rec[10] の手前で
// 抜けている)。ここでは末尾 2 レコード(rec[9]=80 00 00 / rec[10])と
// ヘッダの状態バイトだけを実測値どおりに保ち、抜けている 1 レコード分は
// 検証に影響しない ff 00 00(未装着)で埋めて 38 バイトに揃えている。
//
// 【訂正(2026-08-08、DOMAIN §11.4)】以下の変数名・コメントは採取時の呼称
// 「ペーパーフィード異常」をそのまま残しているが、これは同一のバイト列の
// 出どころを指す名前に過ぎない。rec[9] 先頭 0x80 が示す実際の意味は
// 「機構エラーのいずれか(8 種類、種別不明)」であり、デコーダの出力は
// 特定の機構名を含まない(Describe_MechanismError_DoesNotNameSpecificMechanism 参照)。

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
    public void Describe_Md5500PaperFeedError_ReportsKnownErrorMessage()
    {
        var status = CassetteStatus.Parse(Md5500PaperFeedErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.False(report.IsPrinting);
        Assert.True(report.CassetteInfoMayBeStale);
        Assert.Equal("機構に異常が発生しました(種別は特定できません)", report.ErrorDetail);
        Assert.Contains("機構に異常が発生しました(種別は特定できません)", report.StatusSummary);
    }

    [Fact]
    public void Describe_Md5000Error_ReportsSameKnownErrorMessage()
    {
        // DOMAIN §11.4: 「エラーの意味づけは機種をまたいで通用するとみられる」。
        var status = CassetteStatus.Parse(Md5000ErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.True(report.IsError);
        Assert.Equal("機構に異常が発生しました(種別は特定できません)", report.ErrorDetail);
    }

    [Fact]
    public void Describe_MechanismError_DoesNotNameSpecificMechanism()
    {
        // DOMAIN §11.4【訂正】(2026-08-08): 「rec[9] 先頭 0x80」は 2026-08-04 に
        // 「ペーパーフィードメカニズム異常」と確定したが、同一機体で純正ドライバが
        // 「キャリッジメカニズムに異常」を表示中に採取した応答が 1 バイトも
        // 違わなかったため撤回した。05 01 の応答は §13.8.3 の 8 種類の機構エラーを
        // 区別しないので、表示文言に特定の機構名を書き込んではならない。
        var md5500 = StatusDecoder.Describe(CassetteStatus.Parse(Md5500PaperFeedErrorResponse));
        var md5000 = StatusDecoder.Describe(CassetteStatus.Parse(Md5000ErrorResponse));

        foreach (var detail in new[] { md5500.ErrorDetail, md5000.ErrorDetail })
        {
            Assert.NotNull(detail);
            Assert.DoesNotContain("ペーパーフィード", detail);
            Assert.DoesNotContain("キャリッジ", detail);
        }
    }

    [Fact]
    public void Describe_0xC9_IsBothErrorAndPrinting()
    {
        var status = CassetteStatus.Parse(PrintingErrorResponse);
        var report = StatusDecoder.Describe(status);

        Assert.Equal((byte)0xc9, status.StatusByte);
        Assert.True(report.IsError);
        Assert.True(report.IsPrinting);
        // rec[9] 先頭は 0x00 なので、確定済みの「ペーパーフィード」(0x80)には
        // 該当しない未知のエラーとして、生の値付きで表示されること。
        Assert.Contains("未知のエラー", report.ErrorDetail);
        Assert.Contains("0xc9", report.ErrorDetail);
        Assert.Contains("0x00", report.ErrorDetail);
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
        string name = CassetteCatalog.GetName(0x99);
        Assert.Contains("不明なカセット", name);
        Assert.Contains("0x99", name);
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
}
