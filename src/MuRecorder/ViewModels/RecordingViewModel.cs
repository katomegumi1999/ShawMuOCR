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
    private readonly RecordingPoller _poller;
    private readonly IScreenCaptureService _capture;
    private readonly IScreenSelectionService _selection;
    private readonly ILoggerService? _logger;
    private readonly ISettingsService? _settingsService;
    private string _lastSavedFile = string.Empty;
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
        OnPropertyChanged(nameof(CurrentTargetBounds));
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
        }

        OutputPath = BuildDefaultOutputPath();
        RefreshSources();
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

    [ObservableProperty]
    private NamedOption<AudioSourceType>? _selectedAudioSource;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private bool _isAudioDeviceSelectorVisible;

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
    private string _regionText = "未选择";

    /// <summary>当前录制或选定的目标矩形区域（物理像素）。</summary>
    public ScreenRect CurrentTargetBounds => ResolveTargetBounds();

    /// <summary>请求区域边界呼吸灯高亮闪烁提示。</summary>
    public event EventHandler? RegionBorderPulseRequested;

    /// <summary>请求显示录屏悬浮工具栏。</summary>
    public event EventHandler? FloatingBarShowRequested;

    /// <summary>请求切换（显示/隐藏）录屏悬浮工具栏。</summary>
    public event EventHandler? FloatingBarToggleRequested;

    /// <summary>请求进入自由画布黑幕模式。</summary>
    public event EventHandler? CanvasModeEnterRequested;

    /// <summary>请求退出自由画布黑幕模式。</summary>
    public event EventHandler? CanvasModeExitRequested;

    [ObservableProperty]
    private bool _isCanvasModeActive;

    public void RequestRegionBorderPulse()
    {
        RegionBorderPulseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>切换桌面录屏悬浮工具栏。</summary>
    [RelayCommand]
    public void ToggleFloatingBar()
    {
        FloatingBarToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启动自由画布模式（纯黑底全屏，仅选中的窗口置顶可见，其它未选窗口及桌面被遮蔽）。</summary>
    [RelayCommand]
    public void EnterCanvasMode()
    {
        if (Windows.Count > 0 && !Windows.Any(w => w.IsSelected))
        {
            Windows[0].IsSelected = true;
            UpdateSelectedWindowsBounds();
        }

        var selected = Windows.Where(w => w.IsSelected).Select(w => w.Window).ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "请至少勾选一个要在画布上展示的目标窗口";
            return;
        }

        IsCanvasModeActive = true;
        Source = RecordingSource.Screen; // 画布模式录制全屏
        CanvasModeEnterRequested?.Invoke(this, EventArgs.Empty);
        FloatingBarShowRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = $"已进入自由画布模式（已置顶 {selected.Count} 个窗口，黑幕全屏录制）";
    }

    /// <summary>退出自由画布模式。</summary>
    [RelayCommand]
    public void ExitCanvasMode()
    {
        if (!IsCanvasModeActive) return;
        IsCanvasModeActive = false;
        CanvasModeExitRequested?.Invoke(this, EventArgs.Empty);
        StatusMessage = "已退出自由画布模式";
    }

    // ---------- 屏幕实时标注事件与状态 ----------

    /// <summary>请求切换屏幕实时标注覆盖层 (参数为是否启用)。</summary>
    public event EventHandler<bool>? AnnotationToggleRequested;

    /// <summary>请求变更屏幕实时标注工具 (Pen/Arrow/Rectangle/Ellipse/Laser)。</summary>
    public event EventHandler<string>? AnnotationToolChangeRequested;

    /// <summary>请求变更屏幕标注颜色 (HEX 字符串)。</summary>
    public event EventHandler<string>? AnnotationColorChangeRequested;

    /// <summary>请求切换穿透模式。</summary>
    public event EventHandler<bool>? AnnotationClickThroughChangeRequested;

    /// <summary>请求撤销上一笔标注。</summary>
    public event EventHandler? AnnotationUndoRequested;

    /// <summary>请求清空所有屏幕标注。</summary>
    public event EventHandler? AnnotationClearRequested;

    [ObservableProperty]
    private bool _isAnnotationActive;

    [ObservableProperty]
    private bool _isAnnotationClickThrough;

    [ObservableProperty]
    private int _selectedAnnotationToolIndex; // 0: Pen, 1: Arrow, 2: Rect, 3: Ellipse, 4: Laser

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
    }

    /// <summary>设置标注工具类型。</summary>
    [RelayCommand]
    public void SetAnnotationTool(string toolName)
    {
        SelectedAnnotationToolIndex = toolName switch
        {
            "Pen" => 0,
            "Arrow" => 1,
            "Rectangle" => 2,
            "Ellipse" => 3,
            "Laser" => 4,
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

    partial void OnRegionChanged(ScreenRect value)
    {
        UpdateRegionText();
        OnPropertyChanged(nameof(CurrentTargetBounds));
    }

    partial void OnShowRegionBorderChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentTargetBounds));
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

    partial void OnSelectedAudioSourceChanged(NamedOption<AudioSourceType>? value)
    {
        var source = value?.Value ?? AudioSourceType.None;
        if (source == AudioSourceType.None)
        {
            IsAudioDeviceSelectorVisible = false;
            AudioDevices.Clear();
            SelectedAudioDevice = null;
        }
        else
        {
            IsAudioDeviceSelectorVisible = true;
            RefreshAudioDeviceList(source);
        }
    }

    /// <summary>重新枚举并刷新当前分类下的音频设备列表。</summary>
    [RelayCommand]
    public void RefreshAudioDevices()
    {
        var source = SelectedAudioSource?.Value ?? AudioSourceType.None;
        if (source != AudioSourceType.None)
        {
            RefreshAudioDeviceList(source);
        }
    }

    private void RefreshAudioDeviceList(AudioSourceType source)
    {
        var prevId = SelectedAudioDevice?.Id;
        AudioDevices.Clear();

        IReadOnlyList<AudioDeviceInfo> list = source switch
        {
            AudioSourceType.System => AudioDeviceEnumerator.EnumerateRenderDevices(),
            AudioSourceType.Device => AudioDeviceEnumerator.EnumerateCaptureDevices(),
            _ => Array.Empty<AudioDeviceInfo>()
        };

        AudioDeviceLabel = source switch
        {
            AudioSourceType.System => "系统扬声器 / 耳机 (内录输出)",
            AudioSourceType.Device => "麦克风设备 (人声输入)",
            _ => "音频设备"
        };

        foreach (var dev in list)
        {
            AudioDevices.Add(dev);
        }

        SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == prevId)
            ?? AudioDevices.FirstOrDefault(d => d.IsDefault)
            ?? AudioDevices.FirstOrDefault();
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
            return;
        }

        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        foreach (var w in selected)
        {
            minX = Math.Min(minX, w.Bounds.X);
            minY = Math.Min(minY, w.Bounds.Y);
            maxX = Math.Max(maxX, w.Bounds.X + w.Bounds.Width);
            maxY = Math.Max(maxY, w.Bounds.Y + w.Bounds.Height);
        }

        int unionW = Math.Max(2, ((maxX - minX) / 2) * 2);
        int unionH = Math.Max(2, ((maxY - minY) / 2) * 2);
        _windowUnionBounds = new ScreenRect(minX, minY, unionW, unionH);
        HasSelectedWindows = true;

        bool spansMultipleMonitors = false;
        if (Monitors.Count > 1)
        {
            spansMultipleMonitors = !Monitors.Any(m =>
                _windowUnionBounds.X >= m.Bounds.X &&
                _windowUnionBounds.Y >= m.Bounds.Y &&
                _windowUnionBounds.X + _windowUnionBounds.Width <= m.Bounds.X + m.Bounds.Width &&
                _windowUnionBounds.Y + _windowUnionBounds.Height <= m.Bounds.Y + m.Bounds.Height);
        }

        if (spansMultipleMonitors)
        {
            SelectedWindowsSummary = $"已合并 {selected.Count} 个窗口：{unionW} × {unionH} ⚠️ 提示：窗口跨越了多个显示器，建议排列在同一屏幕下";
        }
        else
        {
            SelectedWindowsSummary = $"已合并 {selected.Count} 个窗口区域：{unionW} × {unionH} @ ({minX}, {minY})";
        }
    }

    /// <summary>刷新显示器与窗口列表（窗口打开/关闭后手动刷新）。</summary>
    [RelayCommand]
    private void RefreshSources()
    {
        var previousMonitor = SelectedMonitor;
        var previouslySelectedHwnds = Windows.Where(w => w.IsSelected).Select(w => w.Window.Hwnd).ToHashSet();

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

            bool hasAv1 = RecordingService.HasHardwareEncoder(EncoderPreference.GpuAv1);
            if (hasAv1)
            {
                EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.GpuAv1, "GPU · AV1 (硬件加速 · 超高压缩比)"));
            }
            else
            {
                EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.GpuAv1, "GPU · AV1 (未检测到硬件支持，将自动降级)"));
            }
            EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.GpuHevc, "GPU · H.265 (HEVC 硬件加速 · 默认推荐)"));
            EncoderOptions.Add(new NamedOption<EncoderPreference>(EncoderPreference.GpuH264, "GPU · H.264 (AVC 硬件加速)"));
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

        if (AudioSourceOptions.Count == 0)
        {
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.None, "🔇 无音频 (静音录制)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.System, "🔊 系统声音 (扬声器内录)"));
            AudioSourceOptions.Add(new NamedOption<AudioSourceType>(AudioSourceType.Device, "🎙️ 麦克风 (人声讲解)"));
        }

        SelectedAudioSource ??= AudioSourceOptions.FirstOrDefault(o => o.Value == AudioSourceType.None)
            ?? AudioSourceOptions.FirstOrDefault();

        UpdateRegionText();
    }

    /// <summary>框选自定义区域：全屏截图 → 原生遮罩框选 → 物理像素矩形。</summary>
    [RelayCommand]
    private async Task SelectRegionAsync()
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
                StatusMessage = $"已选择区域：{Region.Width} × {Region.Height}（物理像素）";
                _logger?.Info($"录屏区域选择: {Region.Width}x{Region.Height} @ ({Region.X},{Region.Y})");
                RequestRegionBorderPulse();

                // 框选完成后直接自动开始录屏
                await StartRecordingAsync();
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

        if (Source == RecordingSource.Window)
        {
            var selectedWindows = Windows.Where(w => w.IsSelected).Select(w => w.Window).ToList();
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
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            ErrorMessage = Source == RecordingSource.Window
                ? "录制目标无效：请至少勾选一个目标窗口"
                : "录制目标无效：请检查来源选择（窗口可能已关闭，或尚未框选区域）";
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
            var options = new RecordingOptions
            {
                Region = bounds,
                Fps = SelectedFps?.Value ?? 30,
                BitrateKbps = SelectedBitrate?.Value ?? 8000,
                Encoder = SelectedEncoder?.Value ?? EncoderPreference.GpuHevc,
                CaptureCursor = true,
                OutputPath = output,
                AudioSource = SelectedAudioSource?.Value ?? AudioSourceType.None,
                AudioDeviceId = SelectedAudioDevice?.Id,
            };

            await _service.StartAsync(options);
            _lastSavedFile = output;
            _poller.Start();
            IsRecording = true;
            StatusMessage = "● 录制中…";
            FloatingBarShowRequested?.Invoke(this, EventArgs.Empty);
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

    /// <summary>停止录制（阻塞到文件落定，自动写入 moov）。</summary>
    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        try
        {
            await _service.StopAsync();
            _poller.Stop();
            IsRecording = false;
            StatusMessage = File.Exists(_lastSavedFile)
                ? $"✓ 已保存：{_lastSavedFile}"
                : "✓ 录制已停止";
            _logger?.Info($"录屏停止: {_lastSavedFile}");
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
                OnPropertyChanged(nameof(CurrentTargetBounds));
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
            if (Source == RecordingSource.Region)
            {
                Region = default;
                RegionText = "未选择";
                OnPropertyChanged(nameof(CurrentTargetBounds));
            }
            ErrorMessage = $"录制意外停止：{e.Message}";
            StatusMessage = File.Exists(_lastSavedFile)
                ? $"⚠ 录制意外中断，已保存至 {_lastSavedFile}"
                : "⚠ 录制意外中断";
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
        await _service.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
