// Foilwright.Tray — リボン消費の記録(刷ったドット数を自分で数えて書き溜める)。
//
// **プリンタに残量を尋ねる経路は使えない。** `05 01` の応答の値バイト
// (low / high)は意味が未解明で、実測でも「新品の白 92 に対し使い込んだ黒 112」
// と残量ではないことが分かっている(DOMAIN §11.4.3)。
//
// そこで**プリンタに聞くのをやめて、自分で数える**。Foilwright は刷ったドット数を
// インクごとに知っている(プレビューの「ドット数」列)ので、刷るたびにそれを
// 書き溜めればインクごとの累計消費が分かる。リボンは生産終了品であり、残量が
// この機械を使ううえで最大の制約なので、**まず記録を取り始めることを優先する**。
//
// **「カセットを新品に替えた」を記録する機能は今回作らない。** 履歴が貯まってから
// どういう形(交換の印を打つ / 本ごとに区切る / 1 本あたりの実測値を出す)がよいかを
// 決めたいため、いまは記録の採取だけに絞る。手で区切りたい人は usage.jsonl を
// 直接編集できる(だからこそ 1 行 1 レコードにしてある)。

using System.Text.Json;

namespace Foilwright.Tray;

/// <summary>1 回のパス(= インク 1 色を刷ったこと)の記録。</summary>
public sealed class UsageRecord
{
    /// <summary>刷った時刻(UTC。文字列では ISO 8601 の "o" 形式)。</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>インクの識別子(パレットの name。label ではない)。**表示用の名前ではなく
    /// 識別子を持つ** — label は利用者が編集しうるが、集計は同じインクを同じものとして
    /// まとめ続ける必要があるため。</summary>
    public required string Ink { get; set; }

    /// <summary>そのプレーンで実際に打った点の数。</summary>
    public long Dots { get; set; }

    /// <summary>そのジョブでこのインクを重ねた回数(D-031)。</summary>
    public int Passes { get; set; }

    /// <summary>何部目か / 全何部か(D-044)。1 部だけなら 1 / 1。</summary>
    public int Copy { get; set; }

    public int Copies { get; set; }

    public required string Paper { get; set; }

    public required string Media { get; set; }

    public required string Resolution { get; set; }

    /// <summary>"completed"(見張りが完了と判定)/ "failed"(エラー・打ち切り・上限時間切れ)。
    /// **失敗した回も記録する** — 途中で止まってもリボンは減っているため。</summary>
    public required string Outcome { get; set; }
}

/// <summary>インク 1 色ぶんの集計。</summary>
public sealed class UsageSummary
{
    public required string Ink { get; init; }

    /// <summary>累計の点の数。**Dots × Passes** で数える(重ね塗りした回数ぶん消費するため)。</summary>
    public long TotalDots { get; init; }

    /// <summary>そのインクを刷ったパスの延べ回数(Passes の合計)。</summary>
    public int TotalPasses { get; init; }

    /// <summary>記録があるジョブの件数(レコード数)。</summary>
    public int Jobs { get; init; }

    public DateTimeOffset? FirstUsed { get; init; }

    public DateTimeOffset? LastUsed { get; init; }
}

/// <summary>リボン消費の記録の読み書きと集計。
///
/// **1 行 1 レコードの JSON Lines(.jsonl)にしてある。** 追記だけで済み、途中で
/// 壊れてもその行を飛ばせば残りが読めるため — 印刷のたびに書くファイルなので、
/// 壊れて全履歴を失うのは困る(settings.json / presets.json のように丸ごと
/// 書き直す形にすると、書いている最中に落ちた場合に全部を失う)。
///
/// **記録は設定ではない。** 保存先は同じフォルダだが settings.json とは別ファイルに
/// する(D-045 が既定値とプリセットを分けたのと同じ理屈。寿命も意味も違う)。
///
/// <see cref="Summarise"/> は**画面に触らない純粋な処理**として置いてある
/// (PresetStore.Upsert / PreviewForm.BuildMagicRgbWarning と同じ形。ここが検出器になる)。</summary>
public static class UsageLog
{
    /// <summary>見張りが「刷り終わった」と判定した回。</summary>
    internal const string OutcomeCompleted = "completed";

    /// <summary>エラー・打ち切り・上限時間切れで終わった回。**これも記録する** —
    /// 途中で止まってもリボンは減っている。</summary>
    internal const string OutcomeFailed = "failed";

    /// <summary>記録ファイルの場所(%APPDATA%\Foilwright\usage.jsonl)。
    /// フォルダは <see cref="TraySettings.ConfigFolder"/> と共有する(パスを二重に書かない)。</summary>
    internal static string FilePath => Path.Combine(TraySettings.ConfigFolder, "usage.jsonl");

    /// <summary>1 行 1 レコードにするため、整形はしない(WriteIndented = false が既定)。</summary>
    private static readonly JsonSerializerOptions LineOptions = new();

    /// <summary>1 ジョブ(1 部)ぶんの記録を追記する。書き込みに失敗しても例外を投げない
    /// (記録が取れないことより、印刷が止まることのほうが困る)。</summary>
    public static void Append(IReadOnlyList<UsageRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(TraySettings.ConfigFolder);
            File.AppendAllLines(FilePath, records.Select(r => JsonSerializer.Serialize(r, LineOptions)));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException
                or System.Security.SecurityException or JsonException)
        {
            // 握りつぶす。ここで例外を投げると印刷そのものが止まる。
        }
    }

    /// <summary>記録を全部読む。**壊れた行は飛ばして残りを返す**。ファイルが無ければ空。</summary>
    public static List<UsageRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return ParseLines(File.ReadAllLines(FilePath));
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException
                or System.Security.SecurityException)
        {
            // 読めないときは「記録無し」として扱う(TraySettings.Load と同じ流儀)。
        }
        return new List<UsageRecord>();
    }

    /// <summary>行の並びをレコードに直す。**壊れた行(JSON として読めない・必須項目が
    /// 欠けている)は飛ばす**。ファイルを開かずに済むので、ここが壊れ耐性の検出器になる。</summary>
    internal static List<UsageRecord> ParseLines(IEnumerable<string> lines)
    {
        var result = new List<UsageRecord>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }
            try
            {
                var record = JsonSerializer.Deserialize<UsageRecord>(line, LineOptions);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
                // この行だけ捨てて次へ。1 行壊れても残りは読める形にしてある。
            }
        }
        return result;
    }

    /// <summary>インクごとの集計。**画面に触らない純粋な処理**なので、ここが検出器になる。
    ///
    /// <list type="bullet">
    /// <item><b>TotalDots は Dots × Passes の合計</b> — 重ね塗りした回数ぶん消費する。</item>
    /// <item><b>Outcome が "failed" の記録も数に入れる</b> — 途中で止まってもリボンは減っている。</item>
    /// <item>並びは TotalDots の多い順、同数ならインク名の順(Ordinal)。</item>
    /// </list></summary>
    internal static List<UsageSummary> Summarise(IReadOnlyList<UsageRecord> records) =>
        records
            .GroupBy(r => r.Ink, StringComparer.Ordinal)
            .Select(g => new UsageSummary
            {
                Ink = g.Key,
                TotalDots = g.Sum(r => r.Dots * r.Passes),
                TotalPasses = g.Sum(r => r.Passes),
                Jobs = g.Count(),
                FirstUsed = g.Min(r => r.Timestamp),
                LastUsed = g.Max(r => r.Timestamp),
            })
            .OrderByDescending(s => s.TotalDots)
            .ThenBy(s => s.Ink, StringComparer.Ordinal)
            .ToList();
}
