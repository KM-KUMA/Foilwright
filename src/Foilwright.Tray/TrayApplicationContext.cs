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

    private void ShowPreview(string psPath)
    {
        var settings = TraySettings.Load();
        using var form = new PreviewForm(psPath, settings);
        form.ShowDialog();
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
