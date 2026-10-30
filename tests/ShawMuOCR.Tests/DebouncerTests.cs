using System;
using System.Threading;
using MuClip.Services;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 搜索防抖的逻辑验证。
///
/// 之所以抽成独立类来测：ViewModel 里的防抖如果直接依赖 Avalonia 的 DispatcherTimer，
/// 一旦它绑到了错误的 dispatcher，表现是「输入后列表永不刷新」——
/// 既没有异常也没有日志，只能在真实界面上肉眼发现。这里用确定性断言把它钉住。
/// </summary>
public class DebouncerTests
{
    [Fact]
    public void ConsecutiveTriggersRunOnlyOnce()
    {
        int runs = 0;
        using var debouncer = new Debouncer(() => Interlocked.Increment(ref runs), 60);

        for (int i = 0; i < 5; i++)
        {
            debouncer.Trigger();
            Thread.Sleep(10);
        }

        // 5 次输入在 60ms 窗口内，只应落地一次检索
        Thread.Sleep(200);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void TriggerAfterQuietPeriodRunsAgain()
    {
        int runs = 0;
        using var debouncer = new Debouncer(() => Interlocked.Increment(ref runs), 50);

        debouncer.Trigger();
        Thread.Sleep(150);
        Assert.Equal(1, runs);

        // 停顿之后再输入，是一次新的检索
        debouncer.Trigger();
        Thread.Sleep(150);
        Assert.Equal(2, runs);
    }

    [Fact]
    public void CancelPreventsPendingRun()
    {
        int runs = 0;
        using var debouncer = new Debouncer(() => Interlocked.Increment(ref runs), 80);

        debouncer.Trigger();
        debouncer.Cancel(); // 例如用户把搜索框清空，改走「立即刷新」路径

        Thread.Sleep(200);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void DisposedDebouncerNeverRuns()
    {
        int runs = 0;
        var debouncer = new Debouncer(() => Interlocked.Increment(ref runs), 50);

        debouncer.Trigger();
        debouncer.Dispose();
        Thread.Sleep(150);

        Assert.Equal(0, runs);
        // 释放后再触发不应抛异常（界面销毁过程的竞态）
        debouncer.Trigger();
        debouncer.Cancel();
    }

    [Fact]
    public void ActionExceptionsDoNotEscapeTheTimerThread()
    {
        using var debouncer = new Debouncer(() => throw new InvalidOperationException("boom"), 30);

        debouncer.Trigger();
        Thread.Sleep(150);

        // 计时器回调抛异常会杀掉线程池线程（进而终止进程），这里验证被兜住
        Assert.True(true);
    }
}
