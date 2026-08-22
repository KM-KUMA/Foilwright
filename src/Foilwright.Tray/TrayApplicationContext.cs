// Foilwright.Tray — タスクトレイ常駐とパイプサーバ(D-023 / D-024 / §7.2)。
//
// 名前付きパイプ \\.\pipe\foilwright でスプーラからの PostScript を受け、
// ジョブごとに PreviewForm をモーダルで開く。ジョブは 1 件ずつ処理する
// (Foilwright.Cli.Program.RunListen の while ループと同じ考え方) —
// プレビュー表示中に次のジョブを裏で変換・送出することはしない
// (§15.2.1 の排他所有をプロセス全体でも守るための単純化)。

using System.IO.Pipes;
using System.Windows.Forms;
using Foilwright.Core;

namespace Foilwright.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string PipeName = "foilwright";

    private readonly NotifyIcon _notifyIcon;
    private readonly Form _uiMarshal; // 非表示。バックグラウンドスレッドから UI スレッドへ処理を渡すためだけに使う
    private readonly Thread _pipeThread;
    private volatile bool _stopping;

    public TrayApplicationContext()
    {
        _uiMarshal = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            Opacity = 0,
            Size = new System.Drawing.Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point(-2000, -2000),
        };
        _uiMarshal.Load += (_, _) => _uiMarshal.Hide();
        _uiMarshal.Show();
        _uiMarshal.Hide();

        var menu = new ContextMenuStrip();
        // リボン消費を確かめたいのは「刷り終わった直後」だが、正常終了すると
        // プレビューは 3 秒で自動的に閉じる(D-038 5.1)ため、成功したときほど
        // プレビューの「リボン消費を見る」ボタンに手が届かない。ここから開けるようにする。
        menu.Items.Add("リボン消費を見る", null, (_, _) => ShowUsage());
        // 状態も同じ理由でここから読めるようにする(刷り終わった直後に確かめたいのに、
        // 正常終了するとプレビューが 3 秒で閉じて手が届かない)。
        menu.Items.Add("プリンタの状態を読む", null, (_, _) => ShowStatus());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitThread());
        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "Foilwright — \\\\.\\pipe\\foilwright で待機中",
            ContextMenuStrip = menu,
        };

        _pipeThread = new Thread(PipeLoop) { IsBackground = true, Name = "Foilwright.Tray.PipeLoop" };
        _pipeThread.Start();
    }

    private void PipeLoop()
    {
        while (!_stopping)
        {
            string? psPath = null;
            try
            {
                using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);
                pipe.WaitForConnection();

                psPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ps");
                using (var fileStream = File.Create(psPath))
                {
                    pipe.CopyTo(fileStream);
                }
            }
            catch (Exception ex) when (!_stopping)
            {
                ShowBalloon($"ジョブの受信に失敗しました: {ex.Message}", ToolTipIcon.Error);
                continue;
            }

            if (_stopping || psPath is null)
            {
                break;
            }

            string capturedPsPath = psPath;
            try
            {
                // ShowDialog はモーダルなので、この呼び出しはプレビュー画面が
                // 閉じるまで戻らない = 次のジョブの受信はそれまで始まらない。
                _uiMarshal.Invoke(() => ShowPreview(capturedPsPath));
            }
            catch (Exception ex) when (!_stopping)
            {
                ShowBalloon($"ジョブの処理に失敗しました: {ex.Message}", ToolTipIcon.Error);
            }
            finally
            {
                TryDelete(capturedPsPath);
            }
        }
    }

    /// <summary>プレビューを開いているあいだ true。トレイのメニューから
    /// 状態を読ませないための目印(§15.2.1: 送出中に状態問い合わせを挟まない)。
    /// プレビューが出ているあいだは、そのジョブがインターフェースを所有している。</summary>
    private volatile bool _previewOpen;

    private void ShowPreview(string psPath)
    {
        var settings = TraySettings.Load();
        _previewOpen = true;
        try
        {
            using var form = new PreviewForm(psPath, settings);
            form.ShowDialog();
        }
        finally
        {
            _previewOpen = false;
        }
    }

    /// <summary>プリンタの状態を読んで見せる。リボン消費と同じ理由でここに置く —
    /// 確かめたいのは「刷り終わった直後」なのに、正常終了するとプレビューは
    /// 3 秒で閉じてしまい、画面の「状態を読む」に手が届かない
    /// (2026-08-22 に 1200dpi の切り分け中、実際に押せなかった)。
    ///
    /// **プレビューが開いているあいだは読まない。** §15.2.1 のとおり、
    /// 送出中に状態問い合わせを挟んではならない。プレビューが出ている =
    /// そのジョブがインターフェースを所有している、とみなして断る。</summary>
    private void ShowStatus()
    {
        if (_previewOpen)
        {
            ShowBalloon(
                "印刷の確認中は読めません(送出とぶつかるため)。" +
                "プレビューの画面にある「状態を読む(05 01)」を使ってください。",
                ToolTipIcon.Info);
            return;
        }

        string text;
        try
        {
            var route = MachineRoute.Resolve(TraySettings.Load().Machine);
            var status = JobPipeline.ReadRawStatus(route, route.Vid);
            var report = StatusDecoder.Describe(status);
            // 画面の枠と同じ材料を出す。エラーの中身は、あるときだけ足す。
            text =
                $"状態: {report.StatusSummary}\r\n" +
                (string.IsNullOrEmpty(report.ErrorDetail)
                    ? string.Empty
                    : $"エラー: {report.ErrorDetail}\r\n") +
                $"\r\n状態バイト: 0x{status.StatusByte:x2}\r\n" +
                "スロット(先頭バイトがバーコード番号、0xff = 未装着):\r\n" +
                string.Join(
                    "\r\n",
                    status.SlotBarcodes.Select((b, i) =>
                        $"  slot[{i,2}] = 0x{b:x2}"
                        + (i == CassetteStatus.HeadSlotIndex ? "  <- ヘッドに装着中" : string.Empty)));
        }
        catch (Exception ex) when (ex is TransportException or ConfigException or MachineRouteException)
        {
            text = $"状態を読めませんでした: {ex.Message}";
        }

        try
        {
            _uiMarshal.Invoke(() => MessageBox.Show(
                text, "Foilwright — プリンタ状態", MessageBoxButtons.OK, MessageBoxIcon.Information));
        }
        catch (ObjectDisposedException)
        {
            // 終了処理中に押された場合は何もしない(ShowUsage と同じ扱い)。
        }
    }

    /// <summary>リボン消費の記録の窓を開く。**PipeLoop は止まらない** —
    /// パイプの待ち受けは別スレッド(_pipeThread)にあり、この窓の ShowDialog が
    /// 回す入れ子のメッセージループが _uiMarshal.Invoke を捌くため、開いている
    /// あいだにジョブが来てもプレビューは通常どおり開く。</summary>
    private void ShowUsage()
    {
        var labels = LoadInkLabels();
        try
        {
            _uiMarshal.Invoke(() => UsageDialog.Show(null, labels));
        }
        catch (ObjectDisposedException)
        {
            // 終了処理中に押された場合は何もしない(ShowBalloon と同じ扱い)。
        }
    }

    /// <summary>インクの識別子 → 表示名。パレット(D-040 の置き場所)から作る。
    ///
    /// **読めなくても落ちない。** 空の辞書を返し、窓は識別子のまま表示する —
    /// 記録を見るだけの操作が、設定ファイルの不備で使えなくなってはいけない。</summary>
    private static Dictionary<string, string> LoadInkLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string assetRoot = AssetRoot.ResolveDefault();
            foreach (var ink in ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml")))
            {
                labels[ink.Name] = ink.Label;
            }
        }
        catch (Exception)
        {
            // 例外の種類を絞らないのは意図的。置き場所の解決・ファイル読み込み・
            // YAML の解釈のどこでも失敗しうり、投げられる型は開いている。
            // ここで落とすくらいなら識別子のまま見せるほうがよい。
            labels.Clear();
        }
        return labels;
    }

    private void ShowBalloon(string text, ToolTipIcon icon)
    {
        try
        {
            _uiMarshal.Invoke(() =>
            {
                _notifyIcon.BalloonTipTitle = "Foilwright";
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(5000);
            });
        }
        catch (ObjectDisposedException)
        {
            // 終了処理中に届いた通知は無視する。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はジョブの成否に影響しないため無視する。
        }
    }

    protected override void ExitThreadCore()
    {
        _stopping = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _uiMarshal.Close();
        _uiMarshal.Dispose();
        base.ExitThreadCore();
    }
}
