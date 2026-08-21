// Foilwright.Tray.Tests — 下端のボタン列が窓の中に収まっていることの検出器(D-038 5.1)。
//
// 過去に「設定項目を足したら印刷開始ボタンが画面外へ押し出された」実績がある。
// D-044 で部数の欄をボタン列に足したので、同じことが起きていないかをここで見張る。
//
// 窓は開かない(Show しない)。Load でしか走らないプレビュー生成
// (Ghostscript 呼び出し)には触れないため、プリンタも Ghostscript も要らない。
// レイアウトの計算だけを行い、「印刷開始」ボタンの矩形が窓の内側にあるかを見る。

using System.Drawing;
using System.Windows.Forms;
using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class PreviewFormLayoutTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>MinimumSize と同じ 900x600(いちばん小さくできる窓)まで縮めても、
    /// 「印刷開始」「取り消し」と部数の欄が窓の内側に収まっていること。</summary>
    [Fact]
    public void PrintButtonStaysInsideTheWindowAtTheMinimumSize()
    {
        var failures = new List<string>();
        var report = new List<string>();

        // WinForms は STA スレッドを前提にしている。xunit の既定は STA ではないため、
        // 専用のスレッドを立てて、その中だけで組み立てる。
        var thread = new Thread(() =>
        {
            using var form = new PreviewForm("dummy.ps", new TraySettings());
            form.CreateControl();
            // MinimumSize と同じ、いちばん小さくできる窓。CreateControl の後に
            // 設定して、そのサイズでレイアウトが走り切るようにする。
            form.Size = new Size(900, 600);
            form.PerformLayout();

            var client = form.ClientRectangle;
            report.Add($"ClientRectangle = {client}");
            foreach (string caption in new[] { "印刷開始", "取り消し", "部数:" })
            {
                var control = FindByText(form, caption);
                if (control is null)
                {
                    failures.Add($"'{caption}' が見つかりません");
                    continue;
                }
                var bounds = ToFormClient(form, control);
                report.Add($"'{caption}' bounds(client) = {bounds}");
                if (!client.Contains(bounds))
                {
                    failures.Add($"'{caption}' が窓の外にはみ出しています: {bounds} not in {client}");
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        // 座標は成功時も残す(この確認の根拠になる)。
        Assert.True(failures.Count == 0, string.Join("\n", failures.Concat(report)));
        output.WriteLine(string.Join("\n", report));
    }

    /// <summary>コントロールの矩形を、窓のクライアント座標へ直す。窓を Show していないと
    /// RectangleToScreen 系の変換が当てにならない(ハンドルの位置が実際の表示位置と
    /// 一致しない)ため、親をたどって Location を足し込む形で自前に計算する。
    /// スクロール中の親(AutoScroll)は AutoScrollPosition のぶんもずれるので加味する。</summary>
    private static Rectangle ToFormClient(Form form, Control control)
    {
        var offset = Point.Empty;
        for (var parent = control.Parent; parent is not null && parent != form; parent = parent.Parent)
        {
            offset.Offset(parent.Left, parent.Top);
            if (parent is ScrollableControl { AutoScroll: true } scrollable)
            {
                offset.Offset(scrollable.AutoScrollPosition.X, scrollable.AutoScrollPosition.Y);
            }
        }
        return new Rectangle(
            control.Left + offset.X, control.Top + offset.Y, control.Width, control.Height);
    }

    private static Control? FindByText(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Text == text && child is Button or Label)
            {
                return child;
            }
            var found = FindByText(child, text);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }
}
