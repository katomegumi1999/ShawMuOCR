using System.Collections.ObjectModel;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MuRecorder.Audio;
using MuRecorder.Interop;
using MuRecorder.Services;
using MuRecorder.Windows;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.Settings;

namespace MuRecorder.ViewModels;

/// <summary>
/// 录制目标：整屏（主显示器）/ 指定显示器 / 指定窗口 / 自定义区域。
/// 区域一律为物理像素（虚拟桌面坐标系），由 Rust 侧校验必须落在单一显示器内。
/// </summary>
public enum RecordingSource
{
    /// <summary>主显示器全屏。</summary>
    Screen = 0,
    /// <summary>指定显示器全屏。</summary>
    Monitor = 1,
    /// <summary>指定顶层窗口矩形。</summary>
    Window = 2,
    /// <summary>用户框选的自定义区域。</summary>
    Region = 3,
}

/// <summary>带中文标签的 ComboBox 选项。</summary>
public sealed record NamedOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>录屏完成事件参数。</summary>
public sealed class RecordingSavedEventArgs : EventArgs
{
    public string FilePath { get; }
    public TimeSpan Duration { get; }

    public RecordingSavedEventArgs(string filePath, TimeSpan duration)
    {
        FilePath = filePath;
        Duration = duration;
    }
}

/// <summary>
/// 录屏页面 ViewModel。职责边界（设计文档）：
/// - 只做来源枚举、区域选择（复用原生遮罩）、配置组装与状态展示；
/// - 捕获 / 编码 / 封装 / 线程全部在 Rust core 内闭环，本类不经手一帧像素。
/// 线程规则：start/stop 走 RecordingService 的 Task.Run；统计由 RecordingPoller 在
/// DispatcherTimer（200ms）里拉取，属性更新天然落在 UI 线程。
/// </summary>
public partial class RecordingViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RecordingService _service;

    /// <summary>
    /// 显示器 / 窗口 / 编码器 / 音频设备列表是否已真正枚举过一次。
    /// 构造阶段不刷新（这些枚举合计约 140ms，首屏用不到），因此开录前必须检查。
    /// </summary>
    private bool _sourcesLoaded;
    private readonly RecordingPoller _poller;
    private readonly IScreenCaptureService _capture;
    private readonly IScreenSelectionService _selection;
    private readonly ILoggerService? _logger;
    private readonly ISettingsService? _settingsService;
    private string _lastSavedFile = string.Empty;
    private DateTime _recordingStartTime;
    private bool _disposed;

    /// <summary>recorder_core.dll 是否可用（缺失时录屏入口整体禁用，UI 显示降级提示）。</summary>
    public static bool IsAvailable => RecordingService.IsAvailable;

    /// <summary>实例级可用性探测（编译绑定走实例属性，避开静态属性绑定限制）。</summary>
    public bool IsCoreAvailable => RecordingService.IsAvailable;

    /// <summary>核心缺失时的降级提示开关。</summary>
    public bool IsCoreUnavailable => !IsCoreAvailable;

    /// <summary>录制中锁定所有配置控件。</summary>
    public bool CanConfigure => !IsRecording;

    // ---------- 来源单选（RadioButton 绑定） ----------

    public bool IsSourceScreen
    {
        get => Source == RecordingSource.Screen;
        set
        {
            if (value)
            {
                Source = RecordingSource.Screen;
            }
        }
    }

    public bool IsSourceMonitor
    {
        get => Source == RecordingSource.Monitor;
        set
        {
            if (value)
            {
                Source = RecordingSource.Monitor;
            }
        }
    }

    public bool IsSourceWindow
    {
        get => Source == RecordingSource.Window;
        set
        {
            if (value)
            {
                Source = RecordingSource.Window;
            }
        }
    }

    public bool IsSourceRegion
    {
        get => Source == RecordingSource.Region;
        set
        {
            if (value)
            {
                Source = RecordingSource.Region;
            }
        }
    }

    partial void OnSourceChanged(RecordingSource value)
    {
        OnPropertyChanged(nameof(IsSourceScreen));
        OnPropertyChanged(nameof(IsSourceMonitor));
        OnPropertyChanged(nameof(IsSourceWindow));
        OnPropertyChanged(nameof(IsSourceRegion));
        NotifyTargetBoundsChanged();
    }

    public RecordingViewModel(
        IScreenCaptureService capture,
        IScreenSelectionService selection,
        ILoggerService? logger = null,
        ISettingsService? settingsService = null)
    {
        _capture = capture;
        _selection = selection;
        _logger = logger;
        _settingsService = settingsService;
        _service = new RecordingService();
        _poller = new RecordingPoller(_service, OnStats);
        _service.Stopped += OnServiceStopped;

        if (_settingsService != null)
        {
            _showRegionBorder = _settingsService.Current.RecordingShowRegionBorder;
            _filePrefix = _settingsService.Current.RecordingFilePrefix;
            _captureCursor = _settingsService.Current.RecordingCaptureCursor;
            _cursorClickEffect = _settingsService.Current.RecordingCursorClickEffect;
            _cursorTrail = _settingsService.Current.RecordingCursorTrail;
            _cursorTrailMs = _settingsService.Current.RecordingCursorTrailMs;
            _excludeFloatingBarFromCapture = _settingsService.Current.RecordingExcludeFloatingBarFromCapture;
            _isFloatingBarEnabled = _settingsService.Current.RecordingShowFloatingBar;
            _minimizeOnStart = _settingsService.Current.RecordingMinimizeOnStart;
            _liveCursorEffect = _settingsService.Current.RecordingLiveCursorEffect;
        }

        OutputPath = BuildDefaultOutputPath();
        // 硬件探测在后台跑（100–430 ms）。完成前探针只能写「待确认」，
        // 这里订阅一次，预热结束后把它刷新成真实结论。
        RecordingService.HardwareProbeCompleted += OnHardwareProbeCompleted;
        // 构造阶段刻意不刷来源：显示器枚举、窗口枚举、硬件编码器探测（要初始化 D3D/MF）
        // 与音频设备枚举合计约 140ms，而首屏根本看不到录屏页。
        // 这些统一由进入录屏页时的 RefreshSources 补齐（见 MainViewModel.Navigate）。
    }

    private void OnHardwareProbeCompleted()
    {
        Dispatcher.UIThread.Post(RefreshEncoderProbe);
    }

    // ---------- 可配置状态 ----------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfigure))]
    private bool _isRecording;

    [ObservableProperty]
    private RecordingSource _source = RecordingSource.Screen;

    [ObservableProperty]
    private MonitorInfo? _selectedMonitor;

    [ObservableProperty]
    private string _selectedWindowsSummary = "未选择窗口";

    [ObservableProperty]
    private bool _hasSelectedWindows;

    private ScreenRect _windowUnionBounds;

    public WindowInfo? SelectedWindow => Windows.FirstOrDefault(w => w.IsSelected)?.Window;

    [ObservableProperty]
    private NamedOption<uint>? _selectedFps;

    [ObservableProperty]
    private NamedOption<uint>? _selectedBitrate;

    [ObservableProperty]
    private NamedOption<EncoderPreference>? _selectedEncoder;

    /// <summary>
    /// 编码器探针主行：当前选择预计会用哪个编码格式。
    /// 选「自动检测」时显示的是 Auto 的实际首选（按 Rust 候选表推断），不是"自动"两个字。
    /// </summary>
    [ObservableProperty]
    private string _encoderProbeTitle = "编码器探针：等待硬件探测…";

    /// <summary>探针副行：降级链、待确认原因或回退警告。</summary>
    [ObservableProperty]
    private string _encoderProbeHint = string.Empty;

    /// <summary>探针是否处于告警态（例如指定的硬编本机不可用、将回退软编）。</summary>
    [ObservableProperty]
    private bool _encoderProbeIsWarning;

    [ObservableProperty]
    private NamedOption<AudioPreset>? _selectedAudioPreset;

    [ObservableProperty]
    private string _audioSelectedSummary = "未勾选设备 (静音录制)";

    [ObservableProperty]
    private bool _hasAudioDevices;

    [ObservableProperty]
    private NamedOption<AudioSourceType>? _selectedAudioSource;

    [ObservableProperty]
    private NamedOption<uint>? _selectedAudioBitrate;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private bool _isAudioDeviceSelectorVisible;

    /// <summary>是否正在做「系统声音 + 麦克风」叠加（勾了输入设备且同时有输出设备）。</summary>
    [ObservableProperty]
    private bool _isMicOverlayActive;

    /// <summary>
    /// 麦克风叠加增益（百分比）。100 = 原样，上限 300（约 +9.5 dB）。
    /// 仅在 <see cref="IsMicOverlayActive"/> 为真时参与录制。
    /// </summary>
    [ObservableProperty]
    private double _micGainPercent = 100;

    partial void OnMicGainPercentChanged(double value)
    {
        // 滑块拖到 0 会让 Rust 侧当成「不叠加」——那会静默关掉麦克风，
        // 与滑块「音量」的语义不符，所以这里把下限钉在 10%。
        var clamped = Math.Clamp(value, 10d, 300d);
        if (Math.Abs(clamped - value) > 0.01)
        {
            MicGainPercent = clamped;
            return; // 递归回到这里时再通知标签，避免重复通知
        }
        OnPropertyChanged(nameof(MicGainLabel));
    }

    /// <summary>麦克风增益的可读文本（百分比取整）。</summary>
    public string MicGainLabel => $"{Math.Round(MicGainPercent):0}%";

    [ObservableProperty]
    private string _audioDeviceLabel = "音频设备";

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    private string _filePrefix = "MuRecorder";

    [ObservableProperty]
    private ScreenRect _region;

    [ObservableProperty]
    private bool _showRegionBorder = true;

    [ObservableProperty]
    private bool _captureCursor = true;

    [ObservableProperty]
    private bool _cursorClickEffect;

    [ObservableProperty]
    private bool _cursorTrail;

    [ObservableProperty]
    private uint _cursorTrailMs = 200;

    [ObservableProperty]
    private bool _excludeFloatingBarFromCapture = true;

    [ObservableProperty]
    private bool _isFloatingBarEnabled = false;

    /// <summary>
    /// 开录后是否自动最小化主窗口。用户可关（默认开），与「显示悬浮控制条」互不影响。
    /// </summary>
    [ObservableProperty]
    private bool _minimizeOnStart = true;

    [ObservableProperty]
    private bool _liveCursorEffect;

    [ObservableProperty]
    private string _regionText = "未选择";

    /// <summary>当前录制或选定的目标矩形区域（物理像素）。</summary>
    public ScreenRect CurrentTargetBounds => ResolveTargetBounds();

    /// <summary>当前录制画面的宽高比（宽 / 高），用于小窗预览自适应比例保持。</summary>
    public double TargetAspectRatio
    {
        get
        {
            var bounds = CurrentTargetBounds;
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                return (double)bounds.Width / bounds.Height;
            }
            return 16.0 / 9.0;
        }
    }

    /// <summary>当前录制目标分辨率文字描述。</summary>
    public string StatResolution
    {
        get
        {
            var bounds = CurrentTargetBounds;
            return (bounds.Width > 0 && bounds.Height > 0)
                ? $"{bounds.Width} × {bounds.Height}"
                : "--";
        }
    }

    public void NotifyTargetBoundsChanged()
    {
        OnPropertyChanged(nameof(CurrentTargetBounds));
        OnPropertyChanged(nameof(TargetAspectRatio));
        OnPropertyChanged(nameof(StatResolution));
    }

    /// <summary>请求区域边界呼吸灯高亮闪烁提示。</summary>
    public event EventHandler? RegionBorderPulseRequested;

    /// <summary>请求显示录屏桌面实时鼠标波纹特效图层。</summary>
    public event EventHandler? LiveCursorEffectShowRequested;

    /// <summary>请求隐藏录屏桌面实时鼠标波纹特效图层。</summary>
    public event EventHandler? LiveCursorEffectHideRequested;

    /// <summary>请求显示录屏悬浮工具栏。</summary>
    public event EventHandler? FloatingBarShowRequested;

    /// <summary>请求隐藏录屏悬浮工具栏。</summary>
    public event EventHandler? FloatingBarHideRequested;

    /// <summary>请求切换（显示/隐藏）录屏悬浮工具栏。</summary>
    public event EventHandler? FloatingBarToggleRequested;

    /// <summary>
    /// 请求宿主最小化主窗口。录屏开始后主窗口留在桌面上会挡住录制内容，
    /// 但窗口操作属于 UI 层，ViewModel 不直接持有 Window，故以事件形式向上请求。
    /// </summary>
    public event EventHandler? HostWindowMinimizeRequested;

    /// <summary>录屏正常结束或中断且文件成功落盘时触发。</summary>
    public event EventHandler<RecordingSavedEventArgs>? RecordingCompleted;

    /// <summary>设置实时录制硬件预览宿主 HWND（传 IntPtr.Zero 暂停预览）。</summary>
    public void SetPreviewHwnd(IntPtr hwnd)
    {
        _service.SetPreviewHwnd(hwnd);
    }

    public void RequestRegionBorderPulse()
    {
        RegionBorderPulseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>在桌面呼出并置顶录屏悬浮控制栏（单次点击动作呼出）。</summary>
    [RelayCommand]
    public void SummonFloatingBar()
    {
        FloatingBarShowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换桌面录屏悬浮工具栏。</summary>
    [RelayCommand]
    public void ToggleFloatingBar()
    {
        FloatingBarToggleRequested?.Invoke(this, EventArgs.Empty);
    }


    // ---------- 屏幕实时标注事件与状态 ----------

    /// <summary>请求切换屏幕实时标注覆盖层 (参数为是否启用)。</summary>
    public event EventHandler<bool>? AnnotationToggleRequested;

    /// <summary>请求变更屏幕实时标注工具 (Pen/Arrow/Rectangle/Ellipse/Laser/Text)。</summary>
    public event EventHandler<string>? AnnotationToolChangeRequested;

    /// <summary>请求变更屏幕实时标注字号大小 (像素)。</summary>
    public event EventHandler<double>? AnnotationFontSizeChangeRequested;

    /// <summary>请求变更屏幕标注颜色 (HEX 字符串)。</summary>
    public event EventHandler<string>? AnnotationColorChangeRequested;

    /// <summary>请求切换穿透模式。</summary>
    public event EventHandler<bool>? AnnotationClickThroughChangeRequested;

    /// <summary>请求撤销上一笔标注。</summary>
    public event EventHandler? AnnotationUndoRequested;

    /// <summary>请求清空所有屏幕标注。</summary>
    public event EventHandler? AnnotationClearRequested;

    /// <summary>
    /// 标注/涂鸦图层窗口句柄提供委托（供 UI 层注入，使窗口录制模式能把标注图层作为顶层 AlphaBlend 源录入）。
    /// </summary>
    public Func<ScreenRect, IntPtr>? AnnotationOverlayHwndProvider { get; set; }

    [ObservableProperty]
    private bool _isAnnotationActive;

    [ObservableProperty]
    private bool _isAnnotationClickThrough;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnnotationTextToolSelected))]
    [NotifyPropertyChangedFor(nameof(IsAnnotationLaserSelected))]
    private int _selectedAnnotationToolIndex; // 0: Pen, 1: Arrow, 2: Rect, 3: Ellipse, 4: Laser, 5: Text

    public bool IsAnnotationTextToolSelected => SelectedAnnotationToolIndex == 5;
    public bool IsAnnotationLaserSelected => SelectedAnnotationToolIndex == 4;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnnotationFontSizeText))]
    private double _annotationFontSize = 40.0;

    public string AnnotationFontSizeText => $"{(int)Math.Round(AnnotationFontSize)}px";

    partial void OnAnnotationFontSizeChanged(double value)
    {
        AnnotationFontSizeChangeRequested?.Invoke(this, value);
    }

    [ObservableProperty]
    private string _selectedAnnotationColor = "#FF4D4F";

    /// <summary>切换屏幕实时标注模式。</summary>
    [RelayCommand]
    public void ToggleAnnotation()
    {
        IsAnnotationActive = !IsAnnotationActive;
        if (!IsAnnotationActive)
        {
            IsAnnotationClickThrough = false;
        }
        AnnotationToggleRequested?.Invoke(this, IsAnnotationActive);
        if (IsAnnotationActive)
        {
            StatusMessage = "✏️ 屏幕实时标注已开启（可在屏幕上涂鸦、箭头与框选）";
        }
        else
        {
            StatusMessage = "已退出屏幕标注模式";
        }
    }

    /// <summary>主动关闭屏幕实时标注模式。</summary>
    [RelayCommand]
    public void CloseAnnotation()
    {
        if (IsAnnotationActive)
        {
            IsAnnotationActive = false;
            IsAnnotationClickThrough = false;
            AnnotationToggleRequested?.Invoke(this, false);
            StatusMessage = "已退出屏幕标注模式";
        }
    }

    /// <summary>设置标注工具类型。</summary>
    [RelayCommand]
    public void SetAnnotationTool(string toolName)
    {
        // 如果当前已经是激光笔，并且用户再次点击了激光笔，则切回普通画笔（一键启闭）
        if (toolName == "Laser" && SelectedAnnotationToolIndex == 4)
        {
            toolName = "Pen";
        }
        else if (toolName == "Text" && SelectedAnnotationToolIndex == 5)
        {
            toolName = "Pen";
        }

        SelectedAnnotationToolIndex = toolName switch
        {
            "Pen" => 0,
            "Arrow" => 1,
            "Rectangle" => 2,
            "Ellipse" => 3,
            "Laser" => 4,
            "Text" => 5,
            _ => 0
        };
        IsAnnotationClickThrough = false;
        AnnotationToolChangeRequested?.Invoke(this, toolName);
    }

    /// <summary>设置标注颜色。</summary>
    [RelayCommand]
    public void SetAnnotationColor(string hex)
    {
        SelectedAnnotationColor = hex;
        AnnotationColorChangeRequested?.Invoke(this, hex);
    }

    /// <summary>切换鼠标穿透模式。</summary>
    [RelayCommand]
    public void ToggleAnnotationClickThrough()
    {
        IsAnnotationClickThrough = !IsAnnotationClickThrough;
        AnnotationClickThroughChangeRequested?.Invoke(this, IsAnnotationClickThrough);
        StatusMessage = IsAnnotationClickThrough ? "🖱️ 标注穿透已开启（可直接点击底层窗口）" : "✏️ 标注绘制模式已激活";
    }

    /// <summary>撤销上一步标注。</summary>
    [RelayCommand]
    public void AnnotationUndo()
    {
        AnnotationUndoRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空所有屏幕标注。</summary>
    [RelayCommand]
    public void AnnotationClear()
    {
        AnnotationClearRequested?.Invoke(this, EventArgs.Empty);
    }

    partial void OnSelectedMonitorChanged(MonitorInfo? value)
    {
        NotifyTargetBoundsChanged();
    }

    partial void OnRegionChanged(ScreenRect value)
    {
        UpdateRegionText();
        NotifyTargetBoundsChanged();
    }

    partial void OnShowRegionBorderChanged(bool value)
    {
        NotifyTargetBoundsChanged();
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingShowRegionBorder = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnFilePrefixChanged(string value)
    {
        var sanitized = SanitizeFilePrefix(value);
        if (_settingsService != null && _settingsService.Current.RecordingFilePrefix != value)
        {
            _settingsService.Current.RecordingFilePrefix = value;
            _settingsService.Save(_settingsService.Current);
        }

        if (!IsRecording)
        {
            var dir = !string.IsNullOrWhiteSpace(OutputPath) ? Path.GetDirectoryName(OutputPath) : null;
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = EffectiveOutputDirectory;
            }
            OutputPath = Path.Combine(dir, $"{sanitized}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }
    }

    partial void OnSelectedEncoderChanged(NamedOption<EncoderPreference>? value)
    {
        if (value != null && _settingsService != null)
        {
            _settingsService.Current.RecordingEncoder = (int)value.Value;
            _settingsService.Save(_settingsService.Current);
        }

        RefreshEncoderProbe();
    }

    /// <summary>
    /// 重算编码器探针。
    /// 只读 <see cref="RecordingService.TryGetHardwareEncoder"/> 的缓存，绝不在这里现场枚举
    /// （单个编码器的 MFTEnumEx 要 100–430 ms，压在切下拉框上就是明显的卡顿）。
    /// </summary>
    public void RefreshEncoderProbe()
    {
        var selected = SelectedEncoder?.Value ?? EncoderPreference.Auto;

        var probe = EncoderProbe.Describe(
            selected,
            RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuAv1),
            RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuHevc),
            RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuH264));

        EncoderProbeTitle = probe.Title;
        EncoderProbeHint = probe.Hint;
        EncoderProbeIsWarning = probe.IsWarning;
    }

    partial void OnSelectedFpsChanged(NamedOption<uint>? value)
    {
        if (value != null && _settingsService != null)
        {
            _settingsService.Current.RecordingFps = value.Value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnSelectedBitrateChanged(NamedOption<uint>? value)
    {
        if (value != null && _settingsService != null)
        {
            _settingsService.Current.RecordingBitrateKbps = value.Value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnSelectedAudioBitrateChanged(NamedOption<uint>? value)
    {
        if (value != null && _settingsService != null)
        {
            _settingsService.Current.RecordingAudioBitrateKbps = value.Value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnCaptureCursorChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingCaptureCursor = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnCursorClickEffectChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingCursorClickEffect = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnCursorTrailChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingCursorTrail = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnCursorTrailMsChanged(uint value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingCursorTrailMs = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnExcludeFloatingBarFromCaptureChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingExcludeFloatingBarFromCapture = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnIsFloatingBarEnabledChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingShowFloatingBar = value;
            _settingsService.Save(_settingsService.Current);
        }
        if (value)
        {
            FloatingBarShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FloatingBarHideRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnMinimizeOnStartChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingMinimizeOnStart = value;
            _settingsService.Save(_settingsService.Current);
        }
    }

    partial void OnLiveCursorEffectChanged(bool value)
    {
        if (_settingsService != null)
        {
            _settingsService.Current.RecordingLiveCursorEffect = value;
            _settingsService.Save(_settingsService.Current);
        }
        if (IsRecording)
        {
            if (value)
            {
                LiveCursorEffectShowRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                LiveCursorEffectHideRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool _isUpdatingAudioPreset;

    partial void OnSelectedAudioPresetChanged(NamedOption<AudioPreset>? value)
    {
        if (_isUpdatingAudioPreset || value == null) return;
        ApplyAudioPreset(value.Value);
    }

    /// <summary>应用音频预设，自动勾选/取消勾选对应默认设备。</summary>
    public void ApplyAudioPreset(AudioPreset preset)
    {
        _isUpdatingAudioPreset = true;
        try
        {
            switch (preset)
            {
                case AudioPreset.None:
                    foreach (var dev in AudioDeviceList)
                    {
                        dev.IsSelected = false;
                    }
                    break;

                case AudioPreset.System:
                    var defaultRender = AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Render && d.IsDefault)
                        ?? AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Render);

                    foreach (var dev in AudioDeviceList)
                    {
                        dev.IsSelected = dev == defaultRender;
                    }
                    break;

                case AudioPreset.Microphone:
                    var defaultCapture = AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Capture && d.IsDefault)
                        ?? AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Capture);

                    foreach (var dev in AudioDeviceList)
                    {
                        dev.IsSelected = dev == defaultCapture;
                    }
                    break;

                case AudioPreset.SystemAndMic:
                    var defRender = AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Render && d.IsDefault)
                        ?? AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Render);
                    var defCapture = AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Capture && d.IsDefault)
                        ?? AudioDeviceList.FirstOrDefault(d => d.Flow == AudioDeviceFlow.Capture);

                    foreach (var dev in AudioDeviceList)
                    {
                        dev.IsSelected = dev == defRender || dev == defCapture;
                    }
                    break;

                case AudioPreset.Custom:
                    // 保持当前勾选不变
                    break;
            }
        }
        finally
        {
            _isUpdatingAudioPreset = false;
        }

        UpdateAudioSummaryAndPresetFromList();
    }

    /// <summary>当设备勾选状态变动时触发汇总更新与预设推断。</summary>
    private void OnAudioDeviceSelectionChanged()
    {
        if (_isUpdatingAudioPreset) return;
        UpdateAudioSummaryAndPresetFromList();
    }

    private void UpdateAudioSummaryAndPresetFromList()
    {
        var selected = AudioDeviceList.Where(d => d.IsSelected).ToList();
        var selectedRenders = selected.Where(d => d.Flow == AudioDeviceFlow.Render).ToList();
        var selectedCaptures = selected.Where(d => d.Flow == AudioDeviceFlow.Capture).ToList();

        if (selected.Count == 0)
        {
            AudioSelectedSummary = "未勾选设备 (静音录制)";
        }
        else if (selectedRenders.Count > 0 && selectedCaptures.Count > 0)
        {
            AudioSelectedSummary = $"系统声音 + 麦克风 (输出 {selectedRenders.Count}，输入 {selectedCaptures.Count})";
        }
        else
        {
            AudioSelectedSummary = $"已勾选 {selected.Count} 个设备 (输出 {selectedRenders.Count}，输入 {selectedCaptures.Count})";
        }

        // 「系统声音 + 麦克风」才显示增益滑块：只勾输入设备时主音源本身就是麦克风，
        // 叠加层不存在，滑到哪儿都没用。
        IsMicOverlayActive = selectedRenders.Count > 0 && selectedCaptures.Count > 0;

        // 同步兼容历史字段
        if (selectedRenders.Count > 0)
        {
            SelectedAudioDevice = new AudioDeviceInfo(selectedRenders[0].Id, selectedRenders[0].Name, AudioDeviceFlow.Render, selectedRenders[0].IsDefault);
            SelectedAudioSource = selectedRenders[0].IsDefault
                ? AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.System)
                : AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.Device);
        }
        else if (selectedCaptures.Count > 0)
        {
            SelectedAudioDevice = new AudioDeviceInfo(selectedCaptures[0].Id, selectedCaptures[0].Name, AudioDeviceFlow.Capture, selectedCaptures[0].IsDefault);
            // ★ 麦克风是采集端点，必须映射到 Mic 而不是 Device：
            // Device 在 ABI 里表示「渲染端点环回」，两者不是同一条 WASAPI 路径。
            SelectedAudioSource = AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.Mic);
        }
        else
        {
            SelectedAudioDevice = null;
            SelectedAudioSource = AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.None);
        }

        // 推断并同步预设显示（防止递归循环）
        if (!_isUpdatingAudioPreset)
        {
            _isUpdatingAudioPreset = true;
            try
            {
                AudioPreset matchingPreset;
                if (selected.Count == 0)
                {
                    matchingPreset = AudioPreset.None;
                }
                else if (selectedRenders.Count == 1 && selectedCaptures.Count == 0 &&
                         (selectedRenders[0].IsDefault || AudioDeviceList.Count(d => d.Flow == AudioDeviceFlow.Render) == 1))
                {
                    matchingPreset = AudioPreset.System;
                }
                else if (selectedCaptures.Count == 1 && selectedRenders.Count == 0 &&
                         (selectedCaptures[0].IsDefault || AudioDeviceList.Count(d => d.Flow == AudioDeviceFlow.Capture) == 1))
                {
                    matchingPreset = AudioPreset.Microphone;
                }
                else if (selectedRenders.Count == 1 && selectedCaptures.Count == 1 &&
                         (selectedRenders[0].IsDefault || AudioDeviceList.Count(d => d.Flow == AudioDeviceFlow.Render) == 1) &&
                         (selectedCaptures[0].IsDefault || AudioDeviceList.Count(d => d.Flow == AudioDeviceFlow.Capture) == 1))
                {
                    matchingPreset = AudioPreset.SystemAndMic;
                }
                else
                {
                    matchingPreset = AudioPreset.Custom;
                }

                SelectedAudioPreset = AudioPresetOptions.FirstOrDefault(p => p.Value == matchingPreset)
                    ?? AudioPresetOptions.FirstOrDefault(p => p.Value == AudioPreset.Custom);
            }
            finally
            {
                _isUpdatingAudioPreset = false;
            }
        }
    }

    /// <summary>枚举所有活跃的音频设备（输出与输入），更新勾选列表。</summary>
    [RelayCommand]
    public void RefreshAudioDevices()
    {
        var prevSelectedIds = AudioDeviceList
            .Where(d => d.IsSelected)
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in AudioDeviceList)
        {
            item.SelectionChanged -= OnAudioDeviceSelectionChanged;
        }
        AudioDeviceList.Clear();

        var allDevices = AudioDeviceEnumerator.EnumerateAllDevices();
        foreach (var dev in allDevices)
        {
            bool isSelected = prevSelectedIds.Contains(dev.Id);
            var item = new SelectableAudioDevice(dev, isSelected);
            item.SelectionChanged += OnAudioDeviceSelectionChanged;
            AudioDeviceList.Add(item);
        }

        HasAudioDevices = AudioDeviceList.Count > 0;

        if (prevSelectedIds.Count == 0)
        {
            if (SelectedAudioPreset == null || SelectedAudioPreset.Value == AudioPreset.None)
            {
                SelectedAudioPreset = AudioPresetOptions.FirstOrDefault(p => p.Value == AudioPreset.None)
                    ?? AudioPresetOptions.FirstOrDefault();
                ApplyAudioPreset(AudioPreset.None);
            }
            else
            {
                ApplyAudioPreset(SelectedAudioPreset.Value);
            }
        }
        else
        {
            UpdateAudioSummaryAndPresetFromList();
        }
    }

    partial void OnSelectedAudioSourceChanged(NamedOption<AudioSourceType>? value)
    {
        var source = value?.Value ?? AudioSourceType.None;
        if (source == AudioSourceType.None)
        {
            IsAudioDeviceSelectorVisible = false;
            SelectedAudioPreset = AudioPresetOptions.FirstOrDefault(p => p.Value == AudioPreset.None);
        }
        else if (source == AudioSourceType.System)
        {
            IsAudioDeviceSelectorVisible = true;
            SelectedAudioPreset = AudioPresetOptions.FirstOrDefault(p => p.Value == AudioPreset.System);
        }
        else if (source == AudioSourceType.Window)
        {
            IsAudioDeviceSelectorVisible = false;
        }
        else
        {
            // Device / Mic：预设已经由设备勾选列表推断好了（见
            // UpdateAudioSummaryAndPresetFromList）。这里若再覆盖会把「系统声音 +
            // 麦克风」直接压回单麦克风预设——选中两台设备被强制改成只留一台。
            IsAudioDeviceSelectorVisible = true;
        }
    }

    // ---------- 状态与统计 ----------

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private string _statFps = "--";

    [ObservableProperty]
    private string _statBitrate = "--";

    [ObservableProperty]
    private string _statFrames = "0";

    [ObservableProperty]
    private string _statDropped = "0";

    [ObservableProperty]
    private string _statQueue = "0";

    [ObservableProperty]
    private string _statElapsed = "00:00";

    [ObservableProperty]
    private string _statEncoder = "--";

    // ---------- 选项集合 ----------

    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    public ObservableCollection<SelectableWindowItem> Windows { get; } = new();

    public ObservableCollection<NamedOption<uint>> FpsOptions { get; } = new();

    public ObservableCollection<NamedOption<uint>> BitrateOptions { get; } = new();

    public ObservableCollection<NamedOption<EncoderPreference>> EncoderOptions { get; } = new();

    public ObservableCollection<NamedOption<AudioPreset>> AudioPresetOptions { get; } = new();

    public ObservableCollection<NamedOption<uint>> AudioBitrateOptions { get; } = new();

    public ObservableCollection<SelectableAudioDevice> AudioDeviceList { get; } = new();

    public ObservableCollection<NamedOption<AudioSourceType>> AudioSourceOptions { get; } = new();

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();

    // ---------- 命令 ----------

    /// <summary>全选所有可用窗口。</summary>
    [RelayCommand]
    private void SelectAllWindows()
    {
        foreach (var w in Windows)
        {
            w.IsSelected = true;
        }
        UpdateSelectedWindowsBounds();
    }

    /// <summary>清空所有已选窗口。</summary>
    [RelayCommand]
    private void ClearSelectedWindows()
    {
        foreach (var w in Windows)
        {
            w.IsSelected = false;
        }
        UpdateSelectedWindowsBounds();
    }

    private void UpdateSelectedWindowsBounds()
    {
        var selected = Windows.Where(w => w.IsSelected).Select(w => w.Window).ToList();
        OnPropertyChanged(nameof(SelectedWindow));

        if (selected.Count == 0)
        {
            _windowUnionBounds = default;
            HasSelectedWindows = false;
            SelectedWindowsSummary = "未选择任何窗口（请勾选上方窗口列表）";
            NotifyTargetBoundsChanged();
            return;
        }

        int minX = selected.Min(w => w.Bounds.X);
        int minY = selected.Min(w => w.Bounds.Y);
        int maxX = selected.Max(w => w.Bounds.Right);
        int maxY = selected.Max(w => w.Bounds.Bottom);
        _windowUnionBounds = new ScreenRect(minX, minY, maxX - minX, maxY - minY);

        HasSelectedWindows = true;
        if (selected.Count == 1)
        {
            var w = selected[0];
            SelectedWindowsSummary = $"已选择窗口「{w.Title}」({w.Bounds.Width} × {w.Bounds.Height})，走 WGC 独立表面采集";
        }
        else
        {
            SelectedWindowsSummary = $"已选择 {selected.Count} 个窗口，走 WGC 多源采集并按屏幕坐标贴入画布";
        }
        NotifyTargetBoundsChanged();
    }

    /// <summary>刷新显示器与窗口列表（窗口打开/关闭后手动刷新）。</summary>
    [RelayCommand]
    private void RefreshSources() => RefreshSourcesCore(includeWindows: true);

    /// <summary>
    /// 刷新录屏来源。
    /// <para>
    /// <paramref name="includeWindows"/> 为 false 时跳过窗口枚举：那一步要遍历系统全部可见窗口，
    /// 并逐个读取标题与进程名（实测约占本方法 90ms 的绝大部分），而结果只在录屏页真正打开时
    /// 才被用户看到。构造阶段先跳过，等进入录屏页调用 <see cref="RefreshSources"/> 时再补齐，
    /// 避免把这段开销算进首帧关键路径。
    /// </para>
    /// </summary>
    private void RefreshSourcesCore(bool includeWindows)
    {
        var previousMonitor = SelectedMonitor;
        var previouslySelectedHwnds = includeWindows
            ? Windows.Where(w => w.IsSelected).Select(w => w.Window.Hwnd).ToHashSet()
            : new HashSet<IntPtr>();

        Monitors.Clear();
        foreach (var m in MonitorEnumerator.Enumerate())
        {
            Monitors.Add(m);
        }
        var primary = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();
        // 保留仍有效的旧选择（导航返回时不被重置）；否则回落到主显示器
        SelectedMonitor = previousMonitor is not null && Monitors.Contains(previousMonitor)
            ? previousMonitor
            : primary;

        Windows.Clear();
        if (includeWindows)
        {
            foreach (var w in WindowEnumerator.Enumerate())
            {
                bool isSelected = previouslySelectedHwnds.Contains(w.Hwnd);
                Windows.Add(new SelectableWindowItem(w, isSelected, UpdateSelectedWindowsBounds));
            }

            // 首次或未选择时，默认勾选首个窗口
            if (Windows.Count > 0 && !Windows.Any(w => w.IsSelected))
            {
                Windows[0].IsSelected = true;
            }
        }

        UpdateSelectedWindowsBounds();

        // 仅当用户尚未框选过区域时，才用当前显示器矩形兜底
        if (Region.IsEmpty || Source != RecordingSource.Region)
        {
            Region = (SelectedMonitor ?? primary)?.Bounds ?? default;
        }

        if (FpsOptions.Count == 0)
        {
            FpsOptions.Add(new NamedOption<uint>(15, "15 fps（流畅优先）"));
            FpsOptions.Add(new NamedOption<uint>(24, "24 fps（电影质感）"));
            FpsOptions.Add(new NamedOption<uint>(30, "30 fps（均衡推荐）"));
            FpsOptions.Add(new NamedOption<uint>(60, "60 fps（高帧率）"));
            FpsOptions.Add(new NamedOption<uint>(90, "90 fps（超高帧率）"));
            FpsOptions.Add(new NamedOption<uint>(120, "120 fps（电竞高刷）"));
            FpsOptions.Add(new NamedOption<uint>(144, "144 fps（极高电竞刷）"));

            BitrateOptions.Add(new NamedOption<uint>(2000, "2 Mbps（标清）"));
            BitrateOptions.Add(new NamedOption<uint>(4000, "4 Mbps（中等）"));
            BitrateOptions.Add(new NamedOption<uint>(8000, "8 Mbps（推荐）"));
            BitrateOptions.Add(new NamedOption<uint>(12000, "12 Mbps（高清）"));
            BitrateOptions.Add(new NamedOption<uint>(20000, "20 Mbps（超清）"));
            BitrateOptions.Add(new NamedOption<uint>(30000, "30 Mbps（原画）"));
            BitrateOptions.Add(new NamedOption<uint>(50000, "50 Mbps（极清）"));
            BitrateOptions.Add(new NamedOption<uint>(80000, "80 Mbps（母带级）"));
            BitrateOptions.Add(new NamedOption<uint>(100000, "100 Mbps（极速无损）"));

            // 硬件探测在启动时后台预热（单次 MFTEnumEx 实测 100–430 ms），这里**只读缓存**，
            // 页面加载绝不阻塞在这上面。null = 还没探到，此时按普通文案显示，不谎报「未检测到」。
            bool? hasAv1 = RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuAv1);
            bool? hasHevc = RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuHevc);
            bool? hasH264 = RecordingService.TryGetHardwareEncoder(EncoderPreference.GpuH264);

            void AddHardwareOption(EncoderPreference pref, string name, string? tag, bool? available)
            {
                var label = available switch
                {
                    false => $"{name} (未检测到硬件支持，将回退到 CPU 软编)",
                    _ when tag is not null => $"{name} (硬件加速 · {tag})",
                    _ => $"{name} (硬件加速)",
                };
                EncoderOptions.Add(new NamedOption<EncoderPreference>(pref, label));
            }

            AddHardwareOption(EncoderPreference.GpuAv1, "GPU · AV1", "超高压缩比", hasAv1);
            AddHardwareOption(EncoderPreference.GpuHevc, "GPU · H.265 (HEVC)", "默认推荐", hasHevc);
            AddHardwareOption(EncoderPreference.GpuH264, "GPU · H.264 (AVC)", null, hasH264);
            EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.Auto, "自动检测（智能降级）"));
            EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.Soft, "CPU · H.264 (软编兼容模式)"));
        }

        uint targetFps = _settingsService?.Current.RecordingFps ?? 30;
        uint targetBitrate = _settingsService?.Current.RecordingBitrateKbps ?? 8000;
        int targetEncoderInt = _settingsService?.Current.RecordingEncoder ?? (int)EncoderPreference.GpuHevc;
        var targetEncoder = (EncoderPreference)targetEncoderInt;

        SelectedFps ??= FpsOptions.FirstOrDefault(o => o.Value == targetFps)
            ?? FpsOptions.FirstOrDefault(o => o.Value == 30)
            ?? FpsOptions.FirstOrDefault();

        SelectedBitrate ??= BitrateOptions.FirstOrDefault(o => o.Value == targetBitrate)
            ?? BitrateOptions.FirstOrDefault(o => o.Value == 8000)
            ?? BitrateOptions.FirstOrDefault();

        SelectedEncoder ??= EncoderOptions.FirstOrDefault(o => o.Value == targetEncoder)
            ?? EncoderOptions.FirstOrDefault(o => o.Value == EncoderPreference.GpuHevc)
            ?? EncoderOptions.FirstOrDefault();

        RefreshEncoderProbe();

        if (AudioBitrateOptions.Count == 0)
        {
            AudioBitrateOptions.Add(new NamedOption<uint>(320, "320 kbps (最高音质 · 母带级)"));
            AudioBitrateOptions.Add(new NamedOption<uint>(256, "256 kbps (极高品质)"));
            AudioBitrateOptions.Add(new NamedOption<uint>(192, "192 kbps (高品质)"));
            AudioBitrateOptions.Add(new NamedOption<uint>(160, "160 kbps (标准品质)"));
            AudioBitrateOptions.Add(new NamedOption<uint>(128, "128 kbps (适中品质)"));
            AudioBitrateOptions.Add(new NamedOption<uint>(96, "96 kbps (紧凑体积)"));
        }

        uint targetAudioBitrate = _settingsService?.Current.RecordingAudioBitrateKbps ?? 320;
        SelectedAudioBitrate ??= AudioBitrateOptions.FirstOrDefault(b => b.Value == targetAudioBitrate)
            ?? AudioBitrateOptions.FirstOrDefault(b => b.Value == 320)
            ?? AudioBitrateOptions.FirstOrDefault();

        if (AudioPresetOptions.Count == 0)
        {
            AudioPresetOptions.Add(new NamedOption<AudioPreset>(AudioPreset.None, "🔇 不录声音 (静音录制)"));
            AudioPresetOptions.Add(new NamedOption<AudioPreset>(AudioPreset.System, "🔊 仅系统声音 (默认扬声器)"));
            AudioPresetOptions.Add(new NamedOption<AudioPreset>(AudioPreset.Microphone, "🎙️ 仅麦克风 (默认麦克风)"));
            AudioPresetOptions.Add(new NamedOption<AudioPreset>(AudioPreset.SystemAndMic, "🎧 系统声音 + 麦克风"));
            AudioPresetOptions.Add(new NamedOption<AudioPreset>(AudioPreset.Custom, "⚙️ 自定义勾选"));
        }

        SelectedAudioPreset ??= AudioPresetOptions.FirstOrDefault(p => p.Value == AudioPreset.None)
            ?? AudioPresetOptions.FirstOrDefault();

        if (AudioSourceOptions.Count == 0)
        {
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.None, "🔇 无音频 (静音录制)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.System, "🔊 系统声音 (扬声器内录)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.Device, "🔈 指定输出设备 (内录)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.Mic, "🎙️ 麦克风 (人声讲解)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.Window, "🪟 仅目标窗口声音 (进程隔离)"));
        }

        SelectedAudioSource ??= AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.None)
            ?? AudioSourceOptions.FirstOrDefault();

        RefreshAudioDevices();

        UpdateRegionText();
        _sourcesLoaded = true;
    }

    /// <summary>仅框选自定义区域（不启动录制）：全屏截图 → 原生遮罩框选 → 物理像素矩形。</summary>
    [RelayCommand]
    private async Task SelectRegionAsync()
    {
        await SelectRegionCoreAsync(autoStartRecording: false);
    }

    /// <summary>框选自定义区域并直接开始录制。</summary>
    [RelayCommand]
    private async Task SelectRegionAndRecordAsync()
    {
        await SelectRegionCoreAsync(autoStartRecording: true);
    }

    private async Task SelectRegionCoreAsync(bool autoStartRecording)
    {
        if (IsRecording)
        {
            return;
        }

        try
        {
            StatusMessage = "请拖拽框选录制区域（ESC / 右键取消）…";
            var bounds = _capture.GetVirtualScreenBounds();
            var shot = await Task.Run(() => _capture.CaptureVirtualScreen());
            try
            {
                var result = await _selection.SelectRegionExAsync(shot, bounds);
                if (result is null)
                {
                    StatusMessage = "已取消区域选择";
                    return;
                }

                Region = result.ScreenBounds;
                Source = RecordingSource.Region;
                UpdateRegionText();
                if (autoStartRecording)
                {
                    StatusMessage = $"已选定录制区域：{Region.Width} × {Region.Height}，正在启动录制…";
                    _logger?.Info($"录屏区域选择并开录: {Region.Width}x{Region.Height} @ ({Region.X},{Region.Y})");
                    RequestRegionBorderPulse();
                    await StartRecordingAsync();
                }
                else
                {
                    StatusMessage = $"已选定录制区域：{Region.Width} × {Region.Height}（点击【开始录制】即可开录）";
                    _logger?.Info($"录屏区域选择: {Region.Width}x{Region.Height} @ ({Region.X},{Region.Y})");
                    RequestRegionBorderPulse();
                }
            }
            finally
            {
                shot.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("录屏区域选择异常", ex);
            ErrorMessage = $"区域选择失败：{ex.Message}";
            StatusMessage = "区域选择失败";
        }
    }

    /// <summary>指定物理像素区域并直接启动录制（例如从截图标注选区一键转入录屏）。</summary>
    public async Task StartRegionRecordingAsync(ScreenRect region)
    {
        if (IsRecording) return;
        Region = region;
        Source = RecordingSource.Region;
        UpdateRegionText();
        StatusMessage = $"已选择录屏区域：{Region.Width} × {Region.Height}";
        _logger?.Info($"指定区域录屏: {Region.Width}x{Region.Height} @ ({Region.X},{Region.Y})");
        RequestRegionBorderPulse();
        await StartRecordingAsync();
    }

    /// <summary>开始录制。</summary>
    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (IsRecording)
        {
            return;
        }

        ErrorMessage = string.Empty;

        // 来源列表可能尚未加载：构造阶段已刻意不刷新（省掉约 140ms 启动开销），
        // 正常流程是先进入录屏页由 RefreshSources 补齐；但热键可以直接开录，
        // 那时 Monitors 为空会让录制区域退化成 0×0、录出无效文件，所以这里必须兜底。
        if (!_sourcesLoaded)
        {
            RefreshSourcesCore(includeWindows: true);
        }

        var isWindowMode = Source == RecordingSource.Window;
        var selectedWindows = isWindowMode
            ? Windows.Where(w => w.IsSelected).Select(w => w.Window).ToList()
            : new List<WindowInfo>();

        if (isWindowMode)
        {
            bool restoredAny = false;
            foreach (var w in selectedWindows)
            {
                if (WindowEnumerator.IsIconic(w.Hwnd))
                {
                    WindowEnumerator.RestoreWindow(w.Hwnd);
                    restoredAny = true;
                }
            }
            if (restoredAny)
            {
                await Task.Delay(150);
                UpdateSelectedWindowsBounds();
            }
        }

        var bounds = ResolveTargetBounds();
        if (isWindowMode)
        {
            if (selectedWindows.Count == 0)
            {
                ErrorMessage = "录制目标无效：请至少勾选一个目标窗口";
                return;
            }
        }
        else if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            ErrorMessage = "录制目标无效：请检查来源选择（尚未框选有效区域）";
            return;
        }

        string output;
        try
        {
            output = EnsureOutputPath();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"输出路径无效：{ex.Message}";
            return;
        }

        int alignedW = Math.Max(2, (bounds.Width / 2) * 2);
        int alignedH = Math.Max(2, (bounds.Height / 2) * 2);
        bounds = new ScreenRect(bounds.X, bounds.Y, alignedW, alignedH);

        try
        {
            var selectedRenders = AudioDeviceList.Where(d => d.IsSelected && d.Flow == AudioDeviceFlow.Render).ToList();
            var selectedCaptures = AudioDeviceList.Where(d => d.IsSelected && d.Flow == AudioDeviceFlow.Capture).ToList();

            AudioSourceType targetAudioSource = AudioSourceType.None;
            string? targetAudioDeviceId = null;
            string? targetMicDeviceId = null;
            uint targetMicGainPercent = 0;
            ulong targetWindowHwnd = 0;
            uint targetProcessId = 0;

            // ★ 三条互斥的「主音源」分支 + 一条可叠加的麦克风。
            //
            // 历史实现是一条 if/else-if 链，`selectedRenders.Count > 0` 排在前面，
            // 于是「🎧 系统声音 + 麦克风」这个预设（同时勾了输出与输入设备）永远
            // 先命中渲染分支，麦克风**被静默丢掉**——录完只有系统声，没有任何提示。
            //
            // 现在把麦克风拆成独立的叠加层：主音源怎么定还是老规矩，只要不是
            // 麦克风本身，勾了输入设备就再混一路进去。
            if (SelectedAudioSource?.Value == AudioSourceType.Window)
            {
                // 进程环回：把窗口声当主音源，同样允许叠麦克风（解说 + 目标窗口声音）
                var targetWin = Windows.FirstOrDefault(w => w.IsSelected)?.Window;
                if (targetWin != null)
                {
                    targetAudioSource = AudioSourceType.Window;
                    targetWindowHwnd = (ulong)targetWin.Hwnd;
                    targetProcessId = targetWin.ProcessId;
                }
                else
                {
                    targetAudioSource = AudioSourceType.System;
                }

                AppendMicOverlay(selectedCaptures, ref targetMicDeviceId, ref targetMicGainPercent);
            }
            else if (SelectedAudioPreset?.Value == AudioPreset.None && selectedRenders.Count == 0 && selectedCaptures.Count == 0)
            {
                targetAudioSource = AudioSourceType.None;
            }
            else if (selectedRenders.Count > 0)
            {
                var render = selectedRenders.FirstOrDefault(d => d.IsDefault) ?? selectedRenders[0];
                if (render.IsDefault)
                {
                    // 默认端点用 System 而不是把 id 传下去：默认设备会随插拔漂移，
                    // 传死 id 反而会在换耳机后打不开。
                    targetAudioSource = AudioSourceType.System;
                    targetAudioDeviceId = null;
                }
                else
                {
                    targetAudioSource = AudioSourceType.Device;
                    targetAudioDeviceId = render.Id;
                }

                AppendMicOverlay(selectedCaptures, ref targetMicDeviceId, ref targetMicGainPercent);
            }
            else if (selectedCaptures.Count > 0)
            {
                // 只勾了输入设备 → 主音源就是麦克风，走**采集端点**（AudioSourceType.Mic）。
                // 早期这里传的是 AudioSourceType.Device，Rust 会按渲染端点环回打开
                // 采集端点，Initialize 被拒——表现就是「选麦克风就报音频设备不可用」。
                var mic = selectedCaptures.FirstOrDefault(d => d.IsDefault) ?? selectedCaptures[0];
                targetAudioSource = AudioSourceType.Mic;
                targetAudioDeviceId = mic.IsDefault ? null : mic.Id;
            }

            bool isLiveEffectActive = CaptureCursor && LiveCursorEffect;

            List<WindowSourceOption> windowSources = new();
            if (isWindowMode)
            {
                for (int idx = 0; idx < selectedWindows.Count; idx++)
                {
                    windowSources.Add(new WindowSourceOption
                    {
                        Hwnd = selectedWindows[idx].Hwnd,
                        Z = (uint)idx,
                        ClientAreaOnly = false,
                    });
                }

                // 在指定窗口录制模式下，预先初始化全屏透明涂鸦标注窗口，并以最高 Z 序作为 AlphaBlend 源加入 WGC 捕获
                var annotBounds = GetAnnotationBounds();
                var annotHwnd = AnnotationOverlayHwndProvider?.Invoke(annotBounds) ?? IntPtr.Zero;
                if (annotHwnd != IntPtr.Zero)
                {
                    windowSources.Add(new WindowSourceOption
                    {
                        Hwnd = annotHwnd,
                        Z = (uint)(selectedWindows.Count + 100),
                        AlphaBlend = true,
                    });
                }
            }

            var options = new RecordingOptions
            {
                CaptureMode = isWindowMode ? CaptureMode.Window : CaptureMode.Region,
                Region = isWindowMode ? ScreenRect.Empty : bounds,
                Sources = isWindowMode ? windowSources.ToArray() : Array.Empty<WindowSourceOption>(),
                Canvas = new CanvasOptions { Width = 0, Height = 0, Background = 0 },
                CaptureFlags = 1,
                Fps = SelectedFps?.Value ?? 30,
                BitrateKbps = SelectedBitrate?.Value ?? 8000,
                Encoder = SelectedEncoder?.Value ?? EncoderPreference.GpuHevc,
                CaptureCursor = CaptureCursor,
                // 若开启桌面实时演示模式，在全屏/区域录屏时由 C# 标注层在桌面直接渲染（DXGI 录制自然捕获录入）；
                // 但在窗口录屏模式（WGC 抓取窗口独立表面，无法截取外部桌面标注窗），必须由 Rust 核心硬件直接将波纹与光轨合成烧录进录制视频！
                CursorClickEffect = CaptureCursor && CursorClickEffect && (!LiveCursorEffect || isWindowMode),
                CursorTrailMs = (CaptureCursor && CursorTrail && (!LiveCursorEffect || isWindowMode)) ? Math.Clamp(CursorTrailMs, 40u, 3000u) : 0,
                OutputPath = output,
                AudioSource = targetAudioSource,
                AudioDeviceId = targetAudioDeviceId,
                AudioWindowHwnd = (IntPtr)targetWindowHwnd,
                AudioProcessId = targetProcessId,
                AudioBitrateKbps = SelectedAudioBitrate?.Value ?? 320,
                MicDeviceId = targetMicDeviceId,
                MicGainPercent = targetMicGainPercent,
            };

            await _service.StartAsync(options);
            _lastSavedFile = output;
            _recordingStartTime = DateTime.UtcNow;
            _poller.Start();
            IsRecording = true;
            StatusMessage = "● 录制中…";

            // 开录后是否最小化主窗口，由「开录后最小化主窗口」这个开关决定（默认开）。
            // 关掉时主窗口留在原地：适合边看界面边讲解，或者录制目标就是本程序的场景。
            // 停止入口不依赖"桌面上必须留个按钮"：即使最小化了，主窗口仍在任务栏，点一下即可恢复。
            if (MinimizeOnStart)
            {
                HostWindowMinimizeRequested?.Invoke(this, EventArgs.Empty);
            }

            // 悬浮控制条**只**由用户设置决定，不因为"刚刚最小化了"就自动弹出来。
            // 历史实现是 `IsFloatingBarEnabled || minimizeHost` —— 等于把用户关掉的开关
            // 在开录瞬间强行打开一次，屏幕上凭空多出一条控制栏。
            // 想临时叫出来用录屏热键（语义是呼出/收起控制条），那是用户主动行为，不算自动弹。
            if (ShouldShowFloatingBarOnStart(IsFloatingBarEnabled))
            {
                FloatingBarShowRequested?.Invoke(this, EventArgs.Empty);
            }
            if (isLiveEffectActive)
            {
                LiveCursorEffectShowRequested?.Invoke(this, EventArgs.Empty);
            }
            _logger?.Info($"录屏开始: {bounds.Width}x{bounds.Height} @ ({bounds.X},{bounds.Y}) -> {output}");
        }
        catch (RecordingException ex)
        {
            _logger?.Warn($"录屏启动失败: {ex.Message}");
            ErrorMessage = ex.Message;
            StatusMessage = "启动失败";
        }
        catch (Exception ex)
        {
            _logger?.Error("录屏启动异常", ex);
            ErrorMessage = $"启动失败：{ex.Message}";
            StatusMessage = "启动失败";
        }
    }

    /// <summary>
    /// 开录后要不要把悬浮控制条显示出来。
    ///
    /// **只看用户设置**，与「是否最小化了主窗口」无关。这一条是用户明确裁定的交互：
    /// 关掉控制条的人就是不想要屏幕上多一块东西，最小化后自己从任务栏把窗口点回来停止即可。
    /// 历史实现把最小化当成了「必须留一个停止入口」的理由（`floatingBar || minimized`），
    /// 结果用户关掉的开关会在每次开录时被强行打开一次 —— 别再改回去。
    ///
    /// 抽成静态方法是为了能被测试直接覆盖：<see cref="RecordingViewModel"/> 自身依赖
    /// Rust 录制服务，不便在单测里实例化。
    /// </summary>
    public static bool ShouldShowFloatingBarOnStart(bool floatingBarEnabled) => floatingBarEnabled;

    /// <summary>
    /// 计算麦克风叠加参数：勾了输入设备就再混一路进主音轨。
    /// </summary>
    /// <remarks>
    /// 用「增益百分比」而不是布尔开关来表达叠加，是 Rust ABI 的设计（v5）：
    /// 两路 PCM 直接相加会削顶，增益必须跟着开关一起有着落。这里默认 100%，
    /// 由 UI 上的滑块调整。
    ///
    /// 默认端点传 <c>null</c> 而不是它的 id：默认设备会随插拔漂移，传死 id
    /// 在换耳机后就打不开了；而用户显式选的那台必须传 id，不能让它漂。
    /// </remarks>
    private void AppendMicOverlay(
        List<SelectableAudioDevice> selectedCaptures,
        ref string? micDeviceId,
        ref uint micGainPercent)
    {
        if (selectedCaptures.Count == 0)
        {
            micDeviceId = null;
            micGainPercent = 0;
            return;
        }

        var mic = selectedCaptures.FirstOrDefault(d => d.IsDefault) ?? selectedCaptures[0];
        micDeviceId = mic.IsDefault ? null : mic.Id;
        // 夹到 [10, 300]：0 在 ABI 里等于「不叠加」，滑块拖到底会静默关麦。
        micGainPercent = (uint)Math.Clamp(Math.Round(MicGainPercent), 10d, 300d);
    }

    /// <summary>停止录制（阻塞到文件落定，自动写入 moov）。</summary>
    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        var duration = DateTime.UtcNow - _recordingStartTime;

        try
        {
            await _service.StopAsync();
            _poller.Stop();
            IsRecording = false;
            if (IsAnnotationActive)
            {
                CloseAnnotation();
            }
            LiveCursorEffectHideRequested?.Invoke(this, EventArgs.Empty);
            StatusMessage = File.Exists(_lastSavedFile)
                ? $"✓ 已保存：{_lastSavedFile}"
                : "✓ 录制已停止";
            _logger?.Info($"录屏停止: {_lastSavedFile}");

            if (File.Exists(_lastSavedFile))
            {
                RecordingCompleted?.Invoke(this, new RecordingSavedEventArgs(_lastSavedFile, duration));
            }
        }
        catch (RecordingException ex)
        {
            _logger?.Warn($"录屏停止失败: {ex.Message}");
            ErrorMessage = ex.Message;
            StatusMessage = "停止失败（录制文件可能不完整）";
        }
        catch (Exception ex)
        {
            _logger?.Error("录屏停止异常", ex);
            ErrorMessage = $"停止失败：{ex.Message}";
            StatusMessage = "停止失败（录制文件可能不完整）";
        }
        finally
        {
            ResetStats();
            if (IsAnnotationActive)
            {
                IsAnnotationActive = false;
                IsAnnotationClickThrough = false;
                AnnotationToggleRequested?.Invoke(this, false);
            }
            if (Source == RecordingSource.Region)
            {
                Region = default;
                RegionText = "未选择";
                NotifyTargetBoundsChanged();
            }
            // 自动换一个时间戳文件名，避免下一次录制覆盖刚保存的文件
            if (OutputPath == _lastSavedFile)
            {
                OutputPath = BuildDefaultOutputPath();
            }
        }
    }

    /// <summary>打开输出目录（已产生文件时定位到该文件）。</summary>
    [RelayCommand]
    private void OpenOutputFolder()
    {
        try
        {
            if (!string.IsNullOrEmpty(_lastSavedFile) && File.Exists(_lastSavedFile))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_lastSavedFile}\"")
                {
                    UseShellExecute = true,
                });
                return;
            }

            var dir = Path.GetDirectoryName(OutputPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                dir = EffectiveOutputDirectory;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger?.Warn($"打开输出目录失败: {ex.Message}");
        }
    }

    /// <summary>选择输出目录（文件夹选择器）。</summary>
    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is not Window owner)
        {
            return;
        }

        try
        {
            var start = await owner.StorageProvider.TryGetFolderFromPathAsync(EffectiveOutputDirectory);
            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择录制文件保存目录",
                SuggestedStartLocation = start,
            });
            var picked = folders.FirstOrDefault();
            if (picked?.TryGetLocalPath() is { Length: > 0 } local)
            {
                OutputPath = Path.Combine(local, Path.GetFileName(BuildDefaultOutputPath()));
                if (_settingsService != null)
                {
                    _settingsService.Current.RecordingOutputDir = local;
                    _settingsService.Save(_settingsService.Current);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"选择输出目录失败: {ex.Message}");
        }
    }

    // ---------- 内部实现 ----------

    /// <summary>当前生效的输出目录：优先配置中的录屏输出目录，其次 视频库\ShawMuOCR\录屏。</summary>
    public string EffectiveOutputDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settingsService?.Current.RecordingOutputDir)
                && Directory.Exists(_settingsService.Current.RecordingOutputDir))
            {
                return _settingsService.Current.RecordingOutputDir;
            }
            return FallbackDefaultOutputDirectory;
        }
    }

    /// <summary>系统默认输出目录：视频库\ShawMuOCR\录屏。</summary>
    public static string FallbackDefaultOutputDirectory
    {
        get
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrEmpty(videos))
            {
                videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            }
            if (string.IsNullOrEmpty(videos))
            {
                videos = AppContext.BaseDirectory;
            }
            return Path.Combine(videos, "ShawMuOCR", "录屏");
        }
    }

    /// <summary>默认输出目录（兼容静态访问）。</summary>
    public static string DefaultOutputDirectory => FallbackDefaultOutputDirectory;

    /// <summary>过滤或替换文件名中不合法字符，为空时回退为 MuRecorder。</summary>
    public static string SanitizeFilePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return "MuRecorder";
        }
        var invalid = Path.GetInvalidFileNameChars();
        var chars = prefix.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "MuRecorder" : result;
    }

    private string BuildDefaultOutputPath() =>
        Path.Combine(EffectiveOutputDirectory, $"{SanitizeFilePrefix(FilePrefix)}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

    /// <summary>
    /// 获取当前标注覆盖层的标准物理边界。
    /// 在指定窗口录制模式下，返回目标窗口所在显示器的物理矩形，支持全屏范围涂鸦且与录制画布 1:1 对齐；
    /// 在全屏/区域录制模式下，返回实际选定的目标边界。
    /// </summary>
    public ScreenRect GetAnnotationBounds()
    {
        if (Source == RecordingSource.Window)
        {
            var selectedWins = Windows.Where(w => w.IsSelected).Select(w => w.Window).ToList();
            var first = selectedWins.FirstOrDefault();
            var mon = (first != null ? Monitors.FirstOrDefault(m => m.Bounds.IntersectsWith(first.Bounds)) : null)
                ?? SelectedMonitor
                ?? Monitors.FirstOrDefault(m => m.IsPrimary)
                ?? Monitors.FirstOrDefault();
            return mon?.WorkingArea ?? CurrentTargetBounds;
        }
        var bounds = CurrentTargetBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = SelectedMonitor?.Bounds
                ?? (Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault())?.Bounds
                ?? new ScreenRect(0, 0, 1920, 1080);
        }
        return bounds;
    }

    private ScreenRect ResolveTargetBounds() => Source switch
    {
        RecordingSource.Screen => (Monitors.FirstOrDefault(m => m.IsPrimary) ?? SelectedMonitor)?.Bounds ?? default,
        RecordingSource.Monitor => SelectedMonitor?.Bounds ?? default,
        RecordingSource.Window => _windowUnionBounds,
        RecordingSource.Region => Region,
        _ => default,
    };

    private string EnsureOutputPath()
    {
        var path = string.IsNullOrWhiteSpace(OutputPath) ? BuildDefaultOutputPath() : OutputPath.Trim();
        if (!path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            path += ".mp4";
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        OutputPath = path;
        return path;
    }

    private void UpdateRegionText()
    {
        RegionText = Region.IsEmpty
            ? "未选择"
            : $"{Region.Width} × {Region.Height}  @ ({Region.X}, {Region.Y})";
    }

    private void OnStats(RecorderStats s)
    {
        StatFps = $"{s.CurrentFps} fps";
        StatBitrate = $"{s.BitrateKbps / 1000.0:F1} Mbps";
        StatFrames = s.FramesEncoded.ToString();
        StatDropped = s.FramesDropped.ToString();
        StatQueue = s.QueueDepth.ToString();
        StatElapsed = TimeSpan.FromMilliseconds(s.ElapsedMs).ToString(@"mm\:ss");
        StatEncoder = s.EncoderName;
    }

    private void ResetStats()
    {
        StatFps = "--";
        StatBitrate = "--";
        StatFrames = "0";
        StatDropped = "0";
        StatQueue = "0";
        StatElapsed = "00:00";
        StatEncoder = "--";
    }

    /// <summary>
    /// 异常停止（设备丢失 / 编码失败 / IO 错误等）。Rust 侧 mux 线程退出前仍会 finish()，
    /// 已落盘的部分可直接播放。
    /// </summary>
    private void OnServiceStopped(object? sender, RecordingStoppedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _poller.Stop();
            IsRecording = false;
            ResetStats();
            LiveCursorEffectHideRequested?.Invoke(this, EventArgs.Empty);
            if (Source == RecordingSource.Region)
            {
                Region = default;
                RegionText = "未选择";
                NotifyTargetBoundsChanged();
            }
            ErrorMessage = $"录制意外停止：{e.Message}";
            StatusMessage = File.Exists(_lastSavedFile)
                ? $"⚠ 录制意外中断，已保存至 {_lastSavedFile}"
                : "⚠ 录制意外中断";
            if (File.Exists(_lastSavedFile))
            {
                var duration = DateTime.UtcNow - _recordingStartTime;
                RecordingCompleted?.Invoke(this, new RecordingSavedEventArgs(_lastSavedFile, duration));
            }
            _logger?.Warn($"录屏异常停止: {e.Result} {e.Message}");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            if (IsRecording)
            {
                await _service.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"释放录屏服务时停止录制失败: {ex.Message}");
        }

        _poller.Dispose();
        RecordingService.HardwareProbeCompleted -= OnHardwareProbeCompleted;
        await _service.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
