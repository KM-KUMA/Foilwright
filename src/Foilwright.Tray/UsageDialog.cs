// Foilwright.Tray — リボン消費の記録(usage.jsonl)を見せる小さな窓(D-046)。
//
// **プレビューから切り出してある。** 消費を確かめたいのは「刷り終わった直後」なのに、
// 正常終了するとプレビューは 3 秒で自動的に閉じる(D-038 5.1)ため、
// **成功したときほどボタンに手が届かない**という食い違いが実機で出た(2026-08-22)。
// そこで窓だけを独立させ、タスクトレイのアイコンからもいつでも開けるようにした。
//
// 呼び出し側は 2 つ:
//   - PreviewForm の「リボン消費を見る」ボタン(親あり。プレビューの中央に出る)
//   - TrayApplicationContext のメニュー(親なし。画面の中央に出る)
//
// インクの表示名は呼び出し側が辞書にして渡す。ここでパレットを読まないのは、
// 親がすでにパレットを持っている場合(プレビュー)と、これから読む場合(トレイ)で
// 事情が違うため。

using System.Drawing;
using System.Windows.Forms;

namespace Foilwright.Tray;

/// <summary>リボン消費の記録(usage.jsonl)をインクごとに集計して見せる小さな窓。
///
/// **「カセットを新品に替えた」を記録する機能はまだ無い。** 履歴が貯まってから
/// どういう形がよいかを決めたいので、まず記録を取り始めることを優先している
/// (UsageLog の冒頭コメント)。そのあいだ手で区切りたい人のために、
/// 記録ファイルの場所をこの窓に出しておく。</summary>
internal static class UsageDialog
{
    /// <summary>記録が 1 件も無いときに出す文言。**無言にしない** —
    /// 何も出ないと「壊れている」のか「まだ刷っていない」のか区別が付かない。</summary>
    internal static string EmptyMessage => "まだ記録がありません。印刷すると貯まります。";

    /// <summary>識別子から表示名を引く。引けなければ識別子をそのまま返す —
    /// 古い記録や、いまのパレットに無いインクを落とさないため(D-046 5)。</summary>
    internal static string ResolveInkLabel(string name, IReadOnlyDictionary<string, string> inkLabels) =>
        inkLabels.TryGetValue(name, out string? label) ? label : name;

    /// <summary>記録を集計して見せる。
    ///
    /// inkLabels: 識別子 → 表示名。引けないものは識別子をそのまま出す
    /// (古い記録や、いまのパレットに無いインクを落とさないため)。</summary>
    public static void Show(IWin32Window? owner, IReadOnlyDictionary<string, string> inkLabels)
    {
        var summaries = UsageLog.Summarise(UsageLog.Load());

        using var dialog = new Form
        {
            Text = "Foilwright — リボン消費の記録",
            // 親が無いときに画面外へ出ないよう、そのときだけ画面中央に出す。
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            ClientSize = new Size(720, 360),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (summaries.Count == 0)
        {
            layout.Controls.Add(
                new Label
                {
                    Dock = DockStyle.Fill,
                    Text = EmptyMessage,
                },
                0,
                0);
        }
        else
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            };
            grid.Columns.Add("Ink", "インク");
            grid.Columns.Add("TotalDots", "累計ドット");
            grid.Columns.Add("TotalPasses", "パス数");
            grid.Columns.Add("Jobs", "ジョブ数");
            grid.Columns.Add("FirstUsed", "最初");
            grid.Columns.Add("LastUsed", "最後");
            foreach (var summary in summaries)
            {
                grid.Rows.Add(
                    ResolveInkLabel(summary.Ink, inkLabels),
                    // 「ドット数」列と同じ桁区切り(例 181,422)で表示する。
                    summary.TotalDots.ToString("N0"),
                    summary.TotalPasses.ToString("N0"),
                    summary.Jobs.ToString("N0"),
                    FormatUsageDate(summary.FirstUsed),
                    FormatUsageDate(summary.LastUsed));
            }
            layout.Controls.Add(grid, 0, 0);
        }

        layout.Controls.Add(
            new TextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = dialog.BackColor,
                // 中身を見たり、カセットを替えたときに手で編集したりできるよう、
                // 場所を出しておく(選択してコピーできるよう TextBox にする)。
                Text = "記録ファイル: " + UsageLog.FilePath,
            },
            0,
            1);

        var closeButtonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var closeButton = new Button { Text = "閉じる", AutoSize = true, Height = 28 };
        closeButton.Click += (_, _) => dialog.Close();
        closeButtonPanel.Controls.Add(closeButton);
        layout.Controls.Add(closeButtonPanel, 0, 2);

        dialog.Controls.Add(layout);
        dialog.AcceptButton = closeButton;
        dialog.CancelButton = closeButton;
        dialog.ShowDialog(owner);
    }

    /// <summary>記録の時刻(UTC)を現地時間の日付にして出す。利用者が見るのは
    /// 「いつごろ使ったか」なので、時分までは出さない。</summary>
    private static string FormatUsageDate(DateTimeOffset? timestamp) =>
        timestamp is null ? string.Empty : timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd");
}
