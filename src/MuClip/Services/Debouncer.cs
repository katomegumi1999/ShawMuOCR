using System;
using System.Threading;

namespace MuClip.Services;

/// <summary>
/// 高频事件的合并器：连续触发只执行最后一次。
///
/// 为什么要自己写而不是用 Avalonia 的 <c>DispatcherTimer</c>：
/// ViewModel 可能在后线程构造（剪贴板页是首帧后装配的），而 <c>DispatcherTimer</c>
/// 绑定的是构造时所在线程的 dispatcher。绑定错了 <c>Tick</c> 永远不会来，
/// 表现为「输入了搜索词但列表不刷新」——一个不会抛异常、只会静默失效的坑。
/// 这里用线程池计时器 + 代次判重，没有隐含的线程前提，也不依赖 UI 框架。
///
/// 代次（generation）的作用：计时器到期时如果期间又有新的触发，
/// 旧的那次必须丢弃，否则一次慢查询会把更新鲜的结果覆盖回旧状态。
/// </summary>
internal sealed class Debouncer : IDisposable
{
    private readonly Action _action;
    private readonly int _delayMs;
    private readonly Timer _timer;

    /// <summary>最后一次触发对应的代次。</summary>
    private int _pending;

    /// <summary>已经执行过的代次。</summary>
    private int _executed;

    private bool _disposed;

    public Debouncer(Action action, int delayMs)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _delayMs = Math.Max(1, delayMs);
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>记录一次触发并重置倒计时。</summary>
    public void Trigger()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _pending);
        try
        {
            _timer.Change(_delayMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // 与 Dispose 竞争：直接忽略，不抛给调用方
        }
    }

    /// <summary>取消尚未执行的那一次（例如输入被清空，改为立即执行别的路径）。</summary>
    public void Cancel()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _executed);
        try
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Fire()
    {
        if (_disposed) return;

        int pending = Volatile.Read(ref _pending);
        if (pending == Volatile.Read(ref _executed))
        {
            // 这一代已经被执行过（或已被 Cancel），丢弃
            return;
        }
        Volatile.Write(ref _executed, pending);

        try
        {
            _action();
        }
        catch
        {
            // 计时器回调里抛异常会杀进程（线程池），这里必须兜住
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
