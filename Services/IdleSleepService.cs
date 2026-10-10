using System.Diagnostics;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Services;

public interface IIdleSleepService : IDisposable
{
    bool IsSleeping { get; }
    void ReportActivity();
    bool TryWakeUp();
}

public sealed class IdleSleepService : IIdleSleepService
{
    private readonly ISettingsService _settingsService;
    private readonly IModelMemoryDiagnosticsService _diagnosticsService;
    private readonly IOcrCoordinator? _coordinator;
    private readonly System.Threading.Timer _checkTimer;
    private readonly object _lock = new();
    private DateTime _lastActivityTime = DateTime.UtcNow;
    private bool _isSleeping;
    private bool _disposed;

    public bool IsSleeping
    {
        get
        {
            lock (_lock)
            {
                return _isSleeping;
            }
        }
    }

    public IdleSleepService(
        ISettingsService settingsService,
        IModelMemoryDiagnosticsService diagnosticsService,
        IOcrCoordinator? coordinator = null)
    {
        _settingsService = settingsService;
        _diagnosticsService = diagnosticsService;
        _coordinator = coordinator;

        if (_coordinator != null)
        {
            _coordinator.StateChanged += OnCoordinatorStateChanged;
        }

        // 每 15 秒检查一次闲置状态
        _checkTimer = new System.Threading.Timer(OnCheckTimer, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void ReportActivity()
    {
        lock (_lock)
        {
            _lastActivityTime = DateTime.UtcNow;
            _isSleeping = false;
        }
    }

    public bool TryWakeUp()
    {
        lock (_lock)
        {
            _lastActivityTime = DateTime.UtcNow;
            if (_isSleeping)
            {
                _isSleeping = false;
                return true;
            }
            return false;
        }
    }

    private void OnCoordinatorStateChanged(OcrState state, string? message)
    {
        ReportActivity();
    }

    private void OnCheckTimer(object? state)
    {
        if (_disposed) return;

        var currentSettings = _settingsService.Current;
        if (!currentSettings.EnableIdleSleep || currentSettings.IdleSleepMinutes <= 0)
        {
            return;
        }

        bool shouldSleep = false;
        lock (_lock)
        {
            // 如果正在进行选区或识别中，不进入休眠
            if (_coordinator?.CurrentState is OcrState.Selecting or OcrState.Recognizing)
            {
                _lastActivityTime = DateTime.UtcNow;
                return;
            }

            var idleThreshold = TimeSpan.FromMinutes(currentSettings.IdleSleepMinutes);
            if (!_isSleeping && (DateTime.UtcNow - _lastActivityTime) >= idleThreshold)
            {
                _isSleeping = true;
                shouldSleep = true;
            }
        }

        if (shouldSleep)
        {
            try
            {
                _diagnosticsService.TrimMemory();
                Debug.WriteLine($"[IdleSleepService] 闲置已达 {currentSettings.IdleSleepMinutes} 分钟，已触发自动休眠并深度压缩物理内存");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[IdleSleepService] 内存释放失败: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_coordinator != null)
            {
                _coordinator.StateChanged -= OnCoordinatorStateChanged;
            }
            _checkTimer.Dispose();
        }
    }
}
