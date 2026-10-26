using SkiaSharp;

namespace ShawMuOCR.Core.Capture;

/// <summary>
/// 长截图拼接器：按「每帧新露出多少行」把连续帧累加到一张高图上。
/// <para>
/// 只做一件事——把 <paramref name="shiftPixels"/> 行新内容追加到画布底部。
/// 位移怎么算（见 <see cref="VerticalShiftEstimator"/>）和帧从哪里来都不归它管，
/// 因此可以完全脱离屏幕与消息循环做单元测试。
/// </para>
/// <para>
/// 画布采用「按需扩容」而不是一次预分配：长截图最终高度事先不可知，
/// 预分配要么浪费几十兆，要么半途不够用；按 1.5 倍扩容的总拷贝量仍摊销为线性。
/// </para>
/// </summary>
public sealed class LongScreenshotStitcher : IDisposable
{
    /// <summary>单张长截图的默认高度上限。再高就没法粘进大多数应用了，内存也吃不消。</summary>
    public const int DefaultMaxHeight = 16_000;

    /// <summary>扩容时已有内容与新容量的最小倍数关系。</summary>
    private const double GrowthFactor = 1.5;

    private readonly int _maxHeight;
    private SKBitmap? _canvas;
    private SKCanvas? _canvasHandle;
    private bool _disposed;

    public LongScreenshotStitcher(int width, int height, int maxHeight = DefaultMaxHeight)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        FrameHeight = height;
        _maxHeight = Math.Max(height, maxHeight);
        _canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvasHandle = new SKCanvas(_canvas);
    }

    /// <summary>画布宽度（等于视口宽度）。</summary>
    public int Width { get; }

    /// <summary>视口高度，即单帧的最大可追加行数。</summary>
    public int FrameHeight { get; }

    /// <summary>已拼接的总高度（含首帧）。</summary>
    public int RenderedHeight { get; private set; }

    /// <summary>累计接收的帧数。</summary>
    public int FrameCount { get; private set; }

    /// <summary>累计被忽略的回滚次数（用户往上滚产生的负位移）。</summary>
    public int IgnoredScrollBackCount { get; private set; }

    /// <summary>是否已经因为达到高度上限而停止接收。</summary>
    public bool IsFull { get; private set; }

    /// <summary>追加首帧：整帧铺到画布顶部。</summary>
    public bool AppendFirst(SKBitmap frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (FrameCount > 0)
        {
            throw new InvalidOperationException("首帧已经追加过了，后续请用 TryAppendScrolled。");
        }

        if (frame.Width != Width)
        {
            throw new ArgumentException($"帧宽度 {frame.Width} 与画布宽度 {Width} 不一致。", nameof(frame));
        }

        // 画面比视口矮时只铺实际高度，不强行拉伸
        int rows = Math.Min(frame.Height, FrameHeight);
        if (rows > _maxHeight)
        {
            rows = _maxHeight;
        }

        EnsureCapacity(rows);
        DrawFrameRows(frame, sourceTop: 0, rows, destinationTop: 0);

        RenderedHeight = rows;
        FrameCount = 1;
        return true;
    }

    /// <summary>
    /// 追加一帧滚动后的画面：只把底部 <paramref name="shiftPixels"/> 行（新露出的部分）接上去。
    /// </summary>
    /// <param name="frame">当前帧。</param>
    /// <param name="shiftPixels">相对上一帧内容向上移动的像素数。</param>
    /// <param name="reason">失败原因。</param>
    public bool TryAppendScrolled(SKBitmap frame, int shiftPixels, out string? reason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        reason = null;

        if (FrameCount == 0)
        {
            reason = "尚未追加首帧";
            return false;
        }

        if (IsFull)
        {
            reason = $"已达到高度上限 {_maxHeight} 像素";
            return false;
        }

        if (frame.Width != Width)
        {
            reason = $"帧宽度 {frame.Width} 与画布宽度 {Width} 不一致";
            return false;
        }

        if (shiftPixels <= 0)
        {
            // 用户回滚或没动：画面已经在画布里了，什么都不做，但要把回滚次数记下来供调用方判断
            if (shiftPixels < 0)
            {
                IgnoredScrollBackCount++;
            }

            FrameCount++;
            reason = shiftPixels == 0 ? "本帧没有滚动" : $"忽略回滚 {shiftPixels} 像素";
            return false;
        }

        int rows = Math.Min(shiftPixels, Math.Min(frame.Height, FrameHeight));
        int remaining = _maxHeight - RenderedHeight;
        if (remaining <= 0)
        {
            IsFull = true;
            reason = $"已达到高度上限 {_maxHeight} 像素";
            return false;
        }

        if (rows > remaining)
        {
            rows = remaining;
            IsFull = true;
        }

        EnsureCapacity(RenderedHeight + rows);

        // 新露出的内容在帧的底部：源起点 = 帧高 - rows
        int sourceTop = Math.Max(0, frame.Height - shiftPixels);
        DrawFrameRows(frame, sourceTop, rows, RenderedHeight);

        RenderedHeight += rows;
        FrameCount++;

        if (IsFull)
        {
            reason = $"已达到高度上限 {_maxHeight} 像素";
        }

        return true;
    }

    /// <summary>
    /// 产出最终长图：裁掉画布下方尚未填充的部分。
    /// 返回的位图由调用方释放；拼接器自身仍可继续使用。
    /// </summary>
    public SKBitmap BuildResult()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_canvas is null || RenderedHeight <= 0)
        {
            throw new InvalidOperationException("还没有任何拼接内容。");
        }

        var result = new SKBitmap(new SKImageInfo(Width, RenderedHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(result))
        {
            canvas.DrawBitmap(
                _canvas,
                new SKRect(0, 0, Width, RenderedHeight),
                new SKRect(0, 0, Width, RenderedHeight),
                SKSamplingOptions.Default);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _canvasHandle?.Dispose();
        _canvasHandle = null;
        _canvas?.Dispose();
        _canvas = null;
        _disposed = true;
    }

    /// <summary>把帧的指定行段画到画布指定位置。</summary>
    private void DrawFrameRows(SKBitmap frame, int sourceTop, int rows, int destinationTop)
    {
        if (_canvasHandle is null || rows <= 0)
        {
            return;
        }

        _canvasHandle.DrawBitmap(
            frame,
            new SKRect(0, sourceTop, Width, sourceTop + rows),
            new SKRect(0, destinationTop, Width, destinationTop + rows),
            SKSamplingOptions.Default);
        _canvasHandle.Flush();
    }

    /// <summary>保证画布至少有 <paramref name="height"/> 高；不够时按倍率扩容并搬移已有内容。</summary>
    private void EnsureCapacity(int height)
    {
        if (_canvas is null)
        {
            return;
        }

        if (height <= _canvas.Height)
        {
            return;
        }

        int target = Math.Max(height, (int)(_canvas.Height * GrowthFactor));
        if (target > _maxHeight)
        {
            target = Math.Max(height, _maxHeight);
        }

        var expanded = new SKBitmap(new SKImageInfo(Width, target, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(expanded))
        {
            canvas.DrawBitmap(_canvas, 0, 0, SKSamplingOptions.Default);
            canvas.Flush();
        }

        _canvasHandle?.Dispose();
        _canvasHandle = null;
        _canvas.Dispose();

        _canvas = expanded;
        _canvasHandle = new SKCanvas(_canvas);
    }
}
