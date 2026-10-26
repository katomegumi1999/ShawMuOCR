using Avalonia.Threading;
using MuRecorder.Interop;

namespace MuRecorder.Services;

/// <summary>
/// 统计拉取器：DispatcherTimer 200ms 调 PollStats 更新绑定属性。
/// v1 保持单向拉取（不用回调），简单可调试；同时承担异常停止检测——
/// 拉取发现 LastError != 0 而服务仍认为在录制，即判定为内部异常停止，
/// 经 <see cref="RecordingService.Stopped"/> 事件统一通知 UI（单一通知路径）。
/// </summary>
public sealed class RecordingPoller : IDisposable
{
    private readonly RecordingService _service;
    private readonly Action<RecorderStats> _onStats;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public RecordingPoller(RecordingService service, Action<RecorderStats> onStats)
    {
        _service = service;
        _onStats = onStats;
        _timer = new DispatcherTimer
        {
            // 200ms：与 Rust 侧 stats 采样周期一致，UI 刷新足够跟手又不浪费。
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var stats = _service.PollStats();
        if (stats is null)
        {
            // 句柄已释放（正常 StopAsync 路径）：无需处理。
            return;
        }

        _onStats(stats.Value);

        if (stats.Value.LastError != 0 && _service.IsRecording)
        {
            _service.RaiseStopped(
                (RecorderResult)stats.Value.LastError,
                "录制意外停止",
                null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer.Stop();
    }
}
