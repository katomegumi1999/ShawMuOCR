using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShawMuOCR.Core.Scheduling;

/// <summary>
/// 纯托管多核并发与 CPU 占空比限流调度器
/// 支持动态调节并发核心上限、基于 Duty Cycle 占空比的 CPU 利用率限流与异步租约（Lease）模型
/// </summary>
public sealed class CpuThrottleScheduler : IDisposable
{
    private readonly object _lock = new();
    private readonly Queue<TaskCompletionSource<CpuScheduleLease>> _waitQueue = new();
    private int _maxCores;
    private int _cpuLimitPercent;
    private int _activeSlots;
    private bool _disposed;

    /// <summary>
    /// 初始化 CPU 限流调度器
    /// </summary>
    /// <param name="maxCores">最大并发核心数（自动约束在 1 到系统逻辑核心数之间）</param>
    /// <param name="cpuLimitPercent">目标 CPU 利用率上限百分比（10 ~ 100）</param>
    public CpuThrottleScheduler(int maxCores, int cpuLimitPercent)
    {
        _maxCores = Math.Clamp(maxCores, 1, Environment.ProcessorCount);
        _cpuLimitPercent = Math.Clamp(cpuLimitPercent, 10, 100);
    }

    /// <summary>
    /// 当前允许的最大并发核心数
    /// </summary>
    public int MaxCores
    {
        get { lock (_lock) return _maxCores; }
    }

    /// <summary>
    /// 当前 CPU 利用率上限百分比（10 ~ 100）
    /// </summary>
    public int CpuLimitPercent
    {
        get { lock (_lock) return _cpuLimitPercent; }
    }

    /// <summary>
    /// 当前正在执行的任务插槽数量
    /// </summary>
    public int ActiveCount
    {
        get { lock (_lock) return _activeSlots; }
    }

    /// <summary>
    /// 当前排队等待执行的任务数量
    /// </summary>
    public int QueuedCount
    {
        get { lock (_lock) return _waitQueue.Count; }
    }

    /// <summary>
    /// 动态更新并发核心数与 CPU 利用率上限配置
    /// </summary>
    public void UpdateSettings(int maxCores, int cpuLimitPercent)
    {
        lock (_lock)
        {
            _maxCores = Math.Clamp(maxCores, 1, Environment.ProcessorCount);
            _cpuLimitPercent = Math.Clamp(cpuLimitPercent, 10, 100);

            // 若并发核心配额调大，立即唤醒排队等待的任务
            DispatchWaitersUnderLock();
        }
    }

    /// <summary>
    /// 申请调度租约（异步等待空闲核心配额）
    /// 获得租约后，调用方享有该核心插槽的使用权，并在租约 Dispose 时释放插槽
    /// </summary>
    public async ValueTask<CpuScheduleLease> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<CpuScheduleLease>? tcs;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_activeSlots < _maxCores)
            {
                _activeSlots++;
                return new CpuScheduleLease(this);
            }

            tcs = new TaskCompletionSource<CpuScheduleLease>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waitQueue.Enqueue(tcs);
        }

        CancellationTokenRegistration reg = default;
        if (cancellationToken.CanBeCanceled)
        {
            reg = cancellationToken.Register(() =>
            {
                lock (_lock)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
            });
        }

        try
        {
            var lease = await tcs.Task.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                lease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return lease;
        }
        finally
        {
            reg.Dispose();
        }
    }

    /// <summary>
    /// 执行包装的工作单元并自动执行耗时统计、占空比限流与插槽归还
    /// </summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            try
            {
                await lease.ThrottleAsync(sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 执行包装的工作单元并自动执行耗时统计、占空比限流与插槽归还（无返回值重载）
    /// </summary>
    public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var lease = await AcquireLeaseAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            try
            {
                await lease.ThrottleAsync(sw.ElapsedMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// 占空比计算：根据任务工作耗时与目标 CPU 占用率计算需要补偿休眠的毫秒数
    /// 公式：工作时间 / (工作时间 + 休眠时间) = CPU目标占用率%
    /// => 休眠时间 = 工作时间 * (100 - CPU目标占用率) / CPU目标占用率
    /// </summary>
    public static int CalculateDutyCyclePauseMs(long workMs, int cpuLimitPercent)
    {
        if (cpuLimitPercent >= 100 || workMs <= 0) return 0;
        cpuLimitPercent = Math.Clamp(cpuLimitPercent, 10, 100);
        double pauseMs = workMs * (100.0 - cpuLimitPercent) / cpuLimitPercent;
        return (int)Math.Clamp(pauseMs, 1, 60000);
    }

    /// <summary>
    /// 应用 CPU 占空比节流休眠
    /// </summary>
    public static async Task ApplyDutyCycleThrottleAsync(long workMs, int cpuLimitPercent, CancellationToken cancellationToken = default)
    {
        int pauseMs = CalculateDutyCyclePauseMs(workMs, cpuLimitPercent);
        if (pauseMs > 0)
        {
            await Task.Delay(pauseMs, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void ReleaseLease()
    {
        lock (_lock)
        {
            _activeSlots--;
            DispatchWaitersUnderLock();
        }
    }

    private void DispatchWaitersUnderLock()
    {
        while (_activeSlots < _maxCores && _waitQueue.Count > 0)
        {
            var nextTcs = _waitQueue.Dequeue();
            if (nextTcs.Task.IsCompleted)
            {
                // 已取消或已被处理
                continue;
            }

            _activeSlots++;
            nextTcs.TrySetResult(new CpuScheduleLease(this));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            while (_waitQueue.Count > 0)
            {
                var tcs = _waitQueue.Dequeue();
                tcs.TrySetException(new ObjectDisposedException(nameof(CpuThrottleScheduler), "调度器已被释放"));
            }
        }
    }
}

/// <summary>
/// CPU 调度租约，表示已被授予一个多核并发插槽
/// </summary>
public sealed class CpuScheduleLease : IDisposable
{
    private CpuThrottleScheduler? _scheduler;

    internal CpuScheduleLease(CpuThrottleScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    /// <summary>
    /// 在持有当前插槽的前提下，根据任务工作耗时应用 CPU 占空比节流休眠
    /// </summary>
    public async Task ThrottleAsync(long workMs, CancellationToken cancellationToken = default)
    {
        var scheduler = _scheduler;
        if (scheduler != null)
        {
            await CpuThrottleScheduler.ApplyDutyCycleThrottleAsync(workMs, scheduler.CpuLimitPercent, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 释放当前插槽并归还给调度器
    /// </summary>
    public void Dispose()
    {
        var scheduler = Interlocked.Exchange(ref _scheduler, null);
        scheduler?.ReleaseLease();
    }
}
