using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ShawMuOCR.Core.Scheduling;
using Xunit;

namespace ShawMuOCR.Tests;

public class CpuThrottleSchedulerTests
{
    [Fact]
    public void Constructor_ClampsValues_Correctly()
    {
        using var schedulerLow = new CpuThrottleScheduler(0, 5);
        Assert.Equal(1, schedulerLow.MaxCores);
        Assert.Equal(10, schedulerLow.CpuLimitPercent);

        using var schedulerHigh = new CpuThrottleScheduler(9999, 150);
        Assert.Equal(Math.Max(8, Environment.ProcessorCount), schedulerHigh.MaxCores);
        Assert.Equal(100, schedulerHigh.CpuLimitPercent);
    }

    [Theory]
    [InlineData(100, 50, 100)]   // 100ms at 50% => 100ms
    [InlineData(100, 25, 300)]   // 100ms at 25% => 300ms
    [InlineData(100, 80, 25)]    // 100ms at 80% => 25ms
    [InlineData(100, 100, 0)]    // 100ms at 100% => 0ms
    [InlineData(0, 50, 0)]       // 0ms work => 0ms
    public void CalculateDutyCyclePauseMs_CalculatesCorrectly(long workMs, int limitPercent, int expectedPauseMs)
    {
        int pauseMs = CpuThrottleScheduler.CalculateDutyCyclePauseMs(workMs, limitPercent);
        Assert.Equal(expectedPauseMs, pauseMs);
    }

    [Fact]
    public async Task ConcurrencyLimit_EnforcesMaxCores()
    {
        const int maxCores = 2;
        using var scheduler = new CpuThrottleScheduler(maxCores, 100);

        int currentRunning = 0;
        int maxObservedRunning = 0;
        object lockObj = new();

        var tasks = new List<Task>();
        for (int i = 0; i < 6; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                using var lease = await scheduler.AcquireLeaseAsync();
                lock (lockObj)
                {
                    currentRunning++;
                    if (currentRunning > maxObservedRunning)
                    {
                        maxObservedRunning = currentRunning;
                    }
                }

                await Task.Delay(50);

                lock (lockObj)
                {
                    currentRunning--;
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(maxCores, maxObservedRunning);
        Assert.Equal(0, scheduler.ActiveCount);
        Assert.Equal(0, scheduler.QueuedCount);
    }

    [Fact]
    public async Task ExecuteAsync_WrapsAndReturnsResult()
    {
        using var scheduler = new CpuThrottleScheduler(2, 100);

        var result = await scheduler.ExecuteAsync(async ct =>
        {
            await Task.Delay(10, ct);
            return "ok";
        });

        Assert.Equal("ok", result);
        Assert.Equal(0, scheduler.ActiveCount);
    }

    [Fact]
    public async Task UpdateSettings_IncreasesConcurrencyDynamically()
    {
        using var scheduler = new CpuThrottleScheduler(1, 100);

        var holdTcs = new TaskCompletionSource<bool>();
        var task1Started = new TaskCompletionSource<bool>();
        var task2Started = new TaskCompletionSource<bool>();

        // 任务 1 占用插槽
        var task1 = Task.Run(async () =>
        {
            using var lease = await scheduler.AcquireLeaseAsync();
            task1Started.SetResult(true);
            await holdTcs.Task;
        });

        await task1Started.Task;
        Assert.Equal(1, scheduler.ActiveCount);

        // 任务 2 进入等待队列（由于 MaxCores=1）
        var task2 = Task.Run(async () =>
        {
            using var lease = await scheduler.AcquireLeaseAsync();
            task2Started.SetResult(true);
            await holdTcs.Task;
        });

        await Task.Delay(50);
        Assert.False(task2Started.Task.IsCompleted);
        Assert.Equal(1, scheduler.QueuedCount);

        // 动态提升并发核心上限为 2
        scheduler.UpdateSettings(2, 100);

        // 任务 2 应该立即被唤醒获得插槽
        await task2Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, scheduler.ActiveCount);
        Assert.Equal(0, scheduler.QueuedCount);

        holdTcs.SetResult(true);
        await Task.WhenAll(task1, task2);
    }

    [Fact]
    public async Task Cancellation_DequeuesAndCancelsCleanly()
    {
        using var scheduler = new CpuThrottleScheduler(1, 100);
        var holdTcs = new TaskCompletionSource<bool>();

        // 占用唯一插槽
        var blocker = Task.Run(async () =>
        {
            using var lease = await scheduler.AcquireLeaseAsync();
            await holdTcs.Task;
        });

        await Task.Delay(30);

        using var cts = new CancellationTokenSource();
        var waitTask = Task.Run(async () =>
        {
            await scheduler.AcquireLeaseAsync(cts.Token);
        });

        await Task.Delay(30);
        Assert.Equal(1, scheduler.QueuedCount);

        // 取消等待中的任务
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);

        // 释放 blocker
        holdTcs.SetResult(true);
        await blocker;

        Assert.Equal(0, scheduler.ActiveCount);
    }
}
