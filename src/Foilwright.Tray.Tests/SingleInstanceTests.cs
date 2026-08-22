// Foilwright.Tray.Tests — トレイの二重起動防止(名前付きミューテックス)の単体テスト。
//
// 対象は Program から切り出した 2 つの純粋な処理
// (TryAcquireSingleInstance / ReleaseSingleInstance)と、その名前の定数。
// 画面にもパイプにも触らないので、ここで壊れを検出できる
// (BuildMagicRgbWarning / DescribeUserError / BuildInkFilterNotice と同じ形)。
//
// 最も大事なのは「Global\ であること」の検出器 — 守るべき資源である
// \\.\pipe\foilwright は計算機全体で 1 本しか無いため、Local\ にすると
// 別のログオンセッションのトレイを取りこぼす。

using Foilwright.Tray;

namespace Foilwright.Tray.Tests;

public class SingleInstanceTests
{
    /// <summary>テストごとに衝突しない名前を作る。本番の定数と同じく Global\ で始める
    /// (Global\ を作れない環境ならテストも落ちてほしいため、ここで Local\ に逃げない)。</summary>
    private static string UniqueName() => $@"Global\Foilwright.Tray.Tests.{Guid.NewGuid():n}";

    /// <summary>別スレッドから取りに行く。名前付きミューテックスの所有はスレッド単位で、
    /// 同じスレッドから 2 度取ると再入で成功してしまうため、「2 個目のトレイ」を
    /// 再現するには別スレッドから試す必要がある。</summary>
    private static bool TryAcquireOnOtherThread(string name)
    {
        bool acquired = false;
        var thread = new Thread(() =>
        {
            acquired = Program.TryAcquireSingleInstance(name, out Mutex? mutex);
            Program.ReleaseSingleInstance(mutex);
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "取得を試すスレッドが終わらなかった");
        return acquired;
    }

    [Fact]
    public void SingleInstanceMutexName_IsGlobal()
    {
        // Local\ に変えたらここが赤くなる(別セッションのトレイを取りこぼす退行の検出器)。
        Assert.StartsWith(@"Global\", Program.SingleInstanceMutexName, StringComparison.Ordinal);
    }

    [Fact]
    public void TryAcquireSingleInstance_SecondAttemptFailsWhileTheFirstHoldsIt()
    {
        string name = UniqueName();
        Assert.True(Program.TryAcquireSingleInstance(name, out Mutex? first));
        try
        {
            // 2 個目のトレイに相当する。ここが true になると、2 個目が起動して
            // パイプを取り合う状態(延々と失敗を繰り返す)に戻る。
            Assert.False(TryAcquireOnOtherThread(name));
        }
        finally
        {
            // テストの後で必ず解放する(残すと以降のテスト・実際の起動に影響する)。
            Program.ReleaseSingleInstance(first);
        }
    }

    [Fact]
    public void TryAcquireSingleInstance_SucceedsAgainAfterRelease()
    {
        string name = UniqueName();
        Assert.True(Program.TryAcquireSingleInstance(name, out Mutex? first));
        Program.ReleaseSingleInstance(first);

        // 解放したら次が取れること — ここが false になると、一度終了したあと
        // 二度と起動できなくなる。
        Assert.True(TryAcquireOnOtherThread(name));
    }

    [Fact]
    public void ReleaseSingleInstance_IgnoresNull()
    {
        // 取れなかったとき(out が null)にそのまま渡しても落ちないこと。
        Program.ReleaseSingleInstance(null);
    }
}
