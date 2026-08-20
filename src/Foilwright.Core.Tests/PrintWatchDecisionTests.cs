// Foilwright.Core.Tests — D-038: 送出後の見張りループの判定(PrintWatchDecision)の検証。
//
// StatusDecoder のデコード結果(PrinterStatusReport)を経由して判定させる
// (実際の呼び出し経路 — PreviewForm も StatusDecoder.Describe の結果を渡す — と揃える)。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class PrintWatchDecisionTests
{
    /// <summary>状態バイトとエラーバイト 5 個だけを指定し、残りは意味のない
    /// ダミー値(エントリ全て未装着 ff 00 00)で埋めた 38 バイトの応答を組み立てる
    /// (StatusDecoderTests.BuildResponse と同じ組み立て方)。</summary>
    private static PrinterStatusReport MakeReport(byte statusByte, byte[]? errorBytes = null)
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
        Array.Copy(errorBytes ?? new byte[5], 0, raw, 32, 5);
        raw[37] = 0x03; // ETX

        var status = CassetteStatus.Parse(raw);
        return StatusDecoder.Describe(status);
    }

    [Fact]
    public void Evaluate_IdleStatus_NoGrace_IsCompleted()
    {
        // 0x00 = 待機(印刷中ビット無し)。猶予期間外なら完了とみなす。
        var report = MakeReport(0x00);
        Assert.Equal(PrintWatchOutcome.Completed, PrintWatchDecision.Evaluate(report, graceActive: false));
    }

    [Fact]
    public void Evaluate_IdleStatus_DuringGrace_IsContinue()
    {
        // 送出直後の猶予期間中は「まだ刷り始めていないだけ」の可能性があるため、
        // 印刷中ビットが無くても完了と判定しない(D-038)。
        var report = MakeReport(0x00);
        Assert.Equal(PrintWatchOutcome.Continue, PrintWatchDecision.Evaluate(report, graceActive: true));
    }

    [Fact]
    public void Evaluate_PrintingStatus_IsContinue()
    {
        // 0x09 = 印刷実行中ビットのみ。猶予の有無に関わらず継続。
        var report = MakeReport(0x09);
        Assert.Equal(PrintWatchOutcome.Continue, PrintWatchDecision.Evaluate(report, graceActive: false));
        Assert.Equal(PrintWatchOutcome.Continue, PrintWatchDecision.Evaluate(report, graceActive: true));
    }

    [Fact]
    public void Evaluate_ErrorStatus_IsErrorRegardlessOfGrace()
    {
        // 0xC9 = 0xC0(エラー) | 0x09(印刷実行中)。エラーは猶予・印刷中ビットに
        // 関わらず即座に Error(D-038: 見張りを止め、デコーダの文言をそのまま出す)。
        var report = MakeReport(0xC9, new byte[] { 0x80, 0x00, 0x00, 0x00, 0x10 }); // モータエラー(LF)
        Assert.Equal(PrintWatchOutcome.Error, PrintWatchDecision.Evaluate(report, graceActive: false));
        Assert.Equal(PrintWatchOutcome.Error, PrintWatchDecision.Evaluate(report, graceActive: true));
        Assert.Equal("モータエラー(LF)", report.ErrorDetail);
    }

    [Fact]
    public void Evaluate_ErrorButNotPrinting_IsError_NotCompleted()
    {
        // 【2026-08-20】この 1 件だけがエラー判定の「順序」を守らせる。
        //
        // 0xC9(エラー + 印刷中)では、エラー判定を後ろに回しても結果は変わらない
        // (印刷中ビットが立っているので「完了」の枝に入らないため)。
        // 差が出るのは **エラーだが印刷中ではない** 状態 — 同日に実機で観測した
        // 0xC0(モータエラーで機構が止まり、印刷フェーズも落ちている)がこれ。
        //
        // 判定順序を間違えると、この状態を「完了」と誤報する。
        var report = MakeReport(0xC0, new byte[] { 0x80, 0x00, 0x00, 0x00, 0x80 });

        Assert.True(report.IsError);
        Assert.False(report.IsPrinting);
        Assert.Equal(PrintWatchOutcome.Error, PrintWatchDecision.Evaluate(report, graceActive: false));
        Assert.Equal(PrintWatchOutcome.Error, PrintWatchDecision.Evaluate(report, graceActive: true));
    }
}
