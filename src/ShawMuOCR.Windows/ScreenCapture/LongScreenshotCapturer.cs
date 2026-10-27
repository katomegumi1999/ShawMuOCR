using System.Diagnostics;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using SkiaSharp;

namespace ShawMuOCR.Windows.ScreenCapture;

/// <summary>长截图采集结果。</summary>
/// <param name="Image">拼接好的长图，失败时为 null。调用方负责释放。</param>
/// <param name="FrameCount">实际参与拼接的帧数。</param>
/// <param name="Height">最终高度（像素）。</param>
/// <param name="Status">面向用户的结束原因描述。</param>
public sealed record LongScreenshotResult(SKBitmap? Image, int FrameCount, int Height, string Status);

/// <summary>
/// 采集节奏与退出阈值的可调参数。默认值即生产使用值；
/// 测试注入更短的节奏即可在毫秒级跑完整个循环，不需要真的等 12 秒。
/// </summary>
internal sealed record LongScreenshotTimings(
    TimeSpan FirstScrollTimeout,
    TimeSpan IdleStopDelay,
    TimeSpan StartupDelay,
    int MaxConsecutiveFailures,
    int MaxFrames,
    TimeSpan TargetFrameInterval)
{
    /// <summary>生产默认值：不设静止自动收尾（完全由用户点击完成或快捷键控制）、不设首滚超时、启动缓冲 120ms、连续 8 帧失败放弃、最多 1500 帧、帧间隔上限 12ms。</summary>
    public static LongScreenshotTimings Default { get; } = new(
        FirstScrollTimeout: TimeSpan.Zero,
        IdleStopDelay: TimeSpan.Zero,
        StartupDelay: TimeSpan.FromMilliseconds(120),
        MaxConsecutiveFailures: 8,
        MaxFrames: 1500,
        TargetFrameInterval: TimeSpan.FromMilliseconds(12));
}

/// <summary>
/// 「跟随用户手动滚动」的长截图采集器。
/// <para>
/// 工作方式：先由调用方框选一块区域，之后本类以尽可能高的帧率连续抓这块区域，
/// 每帧与上一帧比对算出内容上移了多少像素，把新露出的那几行接到拼接器末尾。
/// 用户停止滚动一段时间就自动收尾。
/// </para>
/// <para>
/// 不控速、不注入滚动事件、不要求用户按固定节奏——完全让用户按自己的习惯滚。
/// 代价是必须容忍三类现实问题，都已在循环里处理：
/// 单帧位移估计不可信（重复纹理）、连续多帧估计失败（窗口被切走）、以及用户完全不滚（空结果）。
/// </para>
/// </summary>
public sealed class LongScreenshotCapturer
{
    private readonly IScreenCaptureService _captureService;
    private readonly LongScreenshotTimings _timings;
    private volatile bool _stopRequested;
    private volatile bool _cancelRequested;

    public LongScreenshotCapturer(IScreenCaptureService captureService)
        : this(captureService, LongScreenshotTimings.Default)
    {
    }

    internal LongScreenshotCapturer(IScreenCaptureService captureService, LongScreenshotTimings timings)
    {
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _timings = timings ?? throw new ArgumentNullException(nameof(timings));
    }

    /// <summary>采集是否正在进行。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>请求提前结束（例如用户再次按下长截图热键或点击完成）。</summary>
    public void RequestStop() => _stopRequested = true;

    /// <summary>请求取消长截图（放弃所有拼接内容并立即退出）。</summary>
    public void RequestCancel()
    {
        _cancelRequested = true;
        _stopRequested = true;
    }

    /// <summary>
    /// 对指定区域执行一次长截图采集。
    /// </summary>
    /// <param name="region">要采集的屏幕区域（虚拟屏坐标）。</param>
    /// <param name="onStatus">状态回调，可能从后台线程调用。</param>
    public async Task<LongScreenshotResult> RunAsync(ScreenRect region, Action<string>? onStatus = null)
    {
        if (IsRunning)
        {
            return new LongScreenshotResult(null, 0, 0, "已有长截图任务在进行中");
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            return new LongScreenshotResult(null, 0, 0, "选区尺寸无效");
        }

        IsRunning = true;
        _stopRequested = false;
        _cancelRequested = false;

        try
        {
            if (_timings.StartupDelay > TimeSpan.Zero)
            {
                await Task.Delay(_timings.StartupDelay).ConfigureAwait(false);
            }

            using var stitcher = new LongScreenshotStitcher(region.Width, region.Height);
            var clock = Stopwatch.StartNew();
            var frameClock = Stopwatch.StartNew();
            var lastMovement = TimeSpan.Zero;
            bool hasMovement = false;
            int consecutiveFailures = 0;
            int frameIndex = 0;
            string lastRejectReason = string.Empty;

            // previous 始终持有「上一帧」的所有权，退出循环时由最外层 finally 释放。
            OcrImage previous = _captureService.CaptureRegion(region);
            try
            {
                if (!stitcher.AppendFirst(previous.Bitmap))
                {
                    return new LongScreenshotResult(null, 0, 0, "首帧采集失败");
                }

                onStatus?.Invoke("长截屏进行中，请滚动目标窗口；完成后点击【✓ 完成】或按快捷键结束");

                while (!_stopRequested)
                {
                    if (frameIndex >= _timings.MaxFrames)
                    {
                        break;
                    }

                    // 限制最高帧率以免空转吃满一个核，但捕获本身已经慢于目标间隔时不再额外等待
                    // （Windows 的 Task.Delay 最小粒度约 15ms，无条件 Delay(1) 会把循环硬压到 ~30fps）
                    int frameElapsedMs = (int)frameClock.ElapsedMilliseconds;
                    int waitMs = (int)_timings.TargetFrameInterval.TotalMilliseconds - frameElapsedMs;
                    if (waitMs > 0)
                    {
                        await Task.Delay(waitMs).ConfigureAwait(false);
                    }
                    frameClock.Restart();

                    OcrImage current = _captureService.CaptureRegion(region);
                    try
                    {
                        var estimate = VerticalShiftEstimator.Estimate(previous.Bitmap, current.Bitmap);

                        if (!estimate.IsConfident)
                        {
                            consecutiveFailures++;
                            lastRejectReason = estimate.Reason;
                            if (consecutiveFailures >= _timings.MaxConsecutiveFailures)
                            {
                                break;
                            }
                        }
                        else
                        {
                            consecutiveFailures = 0;

                            if (estimate.ShiftPixels > 0)
                            {
                                stitcher.TryAppendScrolled(current.Bitmap, estimate.ShiftPixels, out _);
                                hasMovement = true;
                                lastMovement = clock.Elapsed;
                                onStatus?.Invoke($"已拼接 {stitcher.FrameCount} 帧 · {stitcher.RenderedHeight} 像素（完成后请点击【完成】）");
                            }
                            else if (estimate.ShiftPixels < 0)
                            {
                                // 用户往回滚：这块画面已经在长图里了，拼接器会记一笔并拒绝，
                                // 但仍要算作「用户还在操作」，否则会被空闲判定提前收尾。
                                stitcher.TryAppendScrolled(current.Bitmap, estimate.ShiftPixels, out _);
                                lastMovement = clock.Elapsed;
                            }
                        }

                        if (stitcher.IsFull)
                        {
                            break;
                        }

                        // 退出判定：若配置了超时阈值（如单元测试）则按超时退出；默认完全由用户主动控制（_stopRequested / _cancelRequested）
                        if (_timings.FirstScrollTimeout > TimeSpan.Zero && !hasMovement)
                        {
                            if (clock.Elapsed > _timings.FirstScrollTimeout)
                            {
                                return new LongScreenshotResult(null, 0, 0, "等待滚动超时，没有检测到画面滚动");
                            }
                        }
                        else if (_timings.IdleStopDelay > TimeSpan.Zero && hasMovement)
                        {
                            var idle = clock.Elapsed - lastMovement;
                            if (idle > _timings.IdleStopDelay)
                            {
                                break;
                            }
                        }
                    }
                    finally
                    {
                        // 先把所有权交给 previous，再释放上一帧，避免异常路径泄漏当前帧。
                        OcrImage stale = previous;
                        previous = current;
                        stale.Dispose();
                        frameIndex++;
                    }
                }
            }
            finally
            {
                previous.Dispose();
            }

            if (_cancelRequested)
            {
                return new LongScreenshotResult(null, 0, 0, "长截图已取消");
            }

            if (!hasMovement)
            {
                string reason = string.IsNullOrEmpty(lastRejectReason)
                    ? "没有检测到画面滚动"
                    : $"没有检测到有效滚动（{lastRejectReason}）";
                return new LongScreenshotResult(null, 0, 0, reason);
            }

            if (stitcher.RenderedHeight <= stitcher.FrameHeight)
            {
                return new LongScreenshotResult(null, 0, 0, "滚动距离太短，长图没有比单屏更长");
            }

            SKBitmap image = stitcher.BuildResult();
            string status = stitcher.IsFull
                ? $"已拼接 {stitcher.RenderedHeight} 像素（达到高度上限，后续内容未收入）"
                : $"已拼接 {stitcher.RenderedHeight} 像素";

            return new LongScreenshotResult(image, stitcher.FrameCount, stitcher.RenderedHeight, status);
        }
        catch (Exception ex)
        {
            return new LongScreenshotResult(null, 0, 0, $"长截图失败：{ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }
}
