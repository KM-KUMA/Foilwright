// Foilwright.Core — D-038: 送出後にプリンタを見張るループが「続けるか /
// 完了とみなすか / エラーとして止めるか」を決める純粋な判定ロジック。
//
// このクラス自体は 4 秒おきのポーリングや UI 更新を一切知らない。
// PreviewForm(Foilwright.Tray)が状態を読み取り、StatusDecoder.Describe で
// 得た PrinterStatusReport をここに渡して、次の一手を決める。

namespace Foilwright.Core;

/// <summary>D-038: 見張りループが次に何をすべきかの判定結果。</summary>
public enum PrintWatchOutcome
{
    /// <summary>まだ印刷中とみなし、見張りを続ける。</summary>
    Continue,

    /// <summary>印刷が完了したとみなす。</summary>
    Completed,

    /// <summary>エラーが発生した。見張りを止め、エラー内容を表示する。</summary>
    Error,
}

/// <summary>D-038 の判定ロジック本体。</summary>
public static class PrintWatchDecision
{
    /// <param name="report">直近の状態応答をデコードしたもの(StatusDecoder.Describe)。</param>
    /// <param name="graceActive">送出直後の猶予期間中なら true。送出直後はまだ
    /// 印刷が始まっておらず、状態バイトが「待機」(印刷中ビット=0)のことがある
    /// (D-038)。この猶予期間中は「印刷中でない」を理由に完了と判定しない
    /// — 猶予の長さ自体は呼び出し側(PreviewForm)が持つ。</param>
    ///
    /// 判定順序:
    ///   1. エラービットが立っていれば、印刷中ビットや猶予の状態に関わらず
    ///      即座に Error(§11.4 のデコーダの文言をそのまま利用者に見せる)。
    ///   2. 印刷中ビットが立っていない場合、猶予期間中でなければ Completed。
    ///      猶予期間中は「まだ刷り始めていないだけ」の可能性があるため Continue。
    ///   3. それ以外(印刷中ビットが立っている)は Continue。
    public static PrintWatchOutcome Evaluate(PrinterStatusReport report, bool graceActive)
    {
        if (report.IsError)
        {
            return PrintWatchOutcome.Error;
        }
        if (!report.IsPrinting && !graceActive)
        {
            return PrintWatchOutcome.Completed;
        }
        return PrintWatchOutcome.Continue;
    }
}
