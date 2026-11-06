using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using MuRecorder.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class RecordingPageView : UserControl
{
    private RecordingViewModel? _currentVm;
    private Window? _observedWindow;

    public RecordingPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        var host = this.FindControl<RecordingPreviewControl>("PreviewHost");
        if (host != null)
        {
            host.HwndCreated += (_, _) => SyncPreviewState();
            host.HwndDestroyed += (_, _) => SyncPreviewState();
            host.PropertyChanged += (s, e) =>
            {
                if (e.Property == Visual.IsVisibleProperty)
                {
                    SyncPreviewState();
                }
            };
        }

        PropertyChanged += (s, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                SyncPreviewState();
            }
        };

        // 页面每次加载/重绘布局时做一次状态同步，确保切换页面或最小化恢复后 100% 恢复渲染
        LayoutUpdated += (s, e) =>
        {
            if (ShouldPreviewBeActive())
            {
                var h = this.FindControl<RecordingPreviewControl>("PreviewHost");
                if (h != null && h.Hwnd != IntPtr.Zero)
                {
                    UpdatePreviewHwnd(h.Hwnd);
                }
            }
        };
    }

    private void UpdatePreviewHwnd(IntPtr hwnd)
    {
        _currentVm?.SetPreviewHwnd(hwnd);
    }

    private void SyncPreviewState()
    {
        var host = this.FindControl<RecordingPreviewControl>("PreviewHost");
        if (host == null || host.Hwnd == IntPtr.Zero)
        {
            UpdatePreviewHwnd(IntPtr.Zero);
            return;
        }

        bool active = ShouldPreviewBeActive();
        UpdatePreviewHwnd(active ? host.Hwnd : IntPtr.Zero);
    }

    private bool ShouldPreviewBeActive()
    {
        if (_currentVm == null || !_currentVm.IsRecording) return false;
        if (!IsEffectivelyVisible) return false;

        var topLevel = _observedWindow ?? (TopLevel.GetTopLevel(this) as Window);
        if (topLevel == null) return false;
        if (!topLevel.IsVisible) return false;
        if (topLevel.WindowState == WindowState.Minimized) return false;

        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookViewModel(DataContext as RecordingViewModel);

        if (_observedWindow != null)
        {
            _observedWindow.PropertyChanged -= OnWindowPropertyChanged;
        }

        _observedWindow = TopLevel.GetTopLevel(this) as Window;
        if (_observedWindow != null)
        {
            _observedWindow.PropertyChanged += OnWindowPropertyChanged;
        }

        SyncPreviewState();
        SyncOverlay();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_observedWindow != null)
        {
            _observedWindow.PropertyChanged -= OnWindowPropertyChanged;
            _observedWindow = null;
        }

        UpdatePreviewHwnd(IntPtr.Zero);
        // 如果当前没有处于录制中，切离页面时隐藏边框
        if (_currentVm != null && !_currentVm.IsRecording)
        {
            RecordingRegionBorderWindow.HideOverlay();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == Visual.IsVisibleProperty)
        {
            SyncPreviewState();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        HookViewModel(DataContext as RecordingViewModel);
        SyncOverlay();
    }

    private void HookViewModel(RecordingViewModel? vm)
    {
        if (ReferenceEquals(_currentVm, vm)) return;

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged -= OnVmPropertyChanged;
            _currentVm.RegionBorderPulseRequested -= OnPulseRequested;
            _currentVm.FloatingBarShowRequested -= OnFloatingBarShowRequested;
            _currentVm.FloatingBarHideRequested -= OnFloatingBarHideRequested;
            _currentVm.FloatingBarToggleRequested -= OnFloatingBarToggleRequested;
            _currentVm.HostWindowMinimizeRequested -= OnHostWindowMinimizeRequested;
            _currentVm.LiveCursorEffectShowRequested -= OnLiveCursorEffectShowRequested;
            _currentVm.LiveCursorEffectHideRequested -= OnLiveCursorEffectHideRequested;
            _currentVm.AnnotationToggleRequested -= OnAnnotationToggleRequested;
            _currentVm.AnnotationToolChangeRequested -= OnAnnotationToolChangeRequested;
            _currentVm.AnnotationColorChangeRequested -= OnAnnotationColorChangeRequested;
            _currentVm.AnnotationClickThroughChangeRequested -= OnAnnotationClickThroughChangeRequested;
            _currentVm.AnnotationUndoRequested -= OnAnnotationUndoRequested;
            _currentVm.AnnotationClearRequested -= OnAnnotationClearRequested;
        }

        _currentVm = vm;

        if (_currentVm != null)
        {
            _currentVm.PropertyChanged += OnVmPropertyChanged;
            _currentVm.RegionBorderPulseRequested += OnPulseRequested;
            _currentVm.FloatingBarShowRequested += OnFloatingBarShowRequested;
            _currentVm.FloatingBarHideRequested += OnFloatingBarHideRequested;
            _currentVm.FloatingBarToggleRequested += OnFloatingBarToggleRequested;
            _currentVm.HostWindowMinimizeRequested += OnHostWindowMinimizeRequested;
            _currentVm.AnnotationOverlayHwndProvider = (bounds) =>
                RecordingAnnotationWindow.EnsureInitialized(bounds, _currentVm);
            _currentVm.LiveCursorEffectShowRequested += OnLiveCursorEffectShowRequested;
            _currentVm.LiveCursorEffectHideRequested += OnLiveCursorEffectHideRequested;
            _currentVm.AnnotationToggleRequested += OnAnnotationToggleRequested;
            _currentVm.AnnotationToolChangeRequested += OnAnnotationToolChangeRequested;
            _currentVm.AnnotationColorChangeRequested += OnAnnotationColorChangeRequested;
            _currentVm.AnnotationClickThroughChangeRequested += OnAnnotationClickThroughChangeRequested;
            _currentVm.AnnotationUndoRequested += OnAnnotationUndoRequested;
            _currentVm.AnnotationClearRequested += OnAnnotationClearRequested;
        }
    }

    private void OnAnnotationToggleRequested(object? sender, bool active)
    {
        if (active)
        {
            var bounds = _currentVm?.GetAnnotationBounds()
                ?? new ShawMuOCR.Core.Capture.ScreenRect(0, 0, 1920, 1080);
            RecordingAnnotationWindow.ShowOverlay(bounds, _currentVm);
            if (_currentVm != null)
            {
                RecordingFloatingWindow.ShowOverlay(_currentVm);
                RecordingFloatingWindow.BringToFront();
            }
        }
        else
        {
            RecordingAnnotationWindow.ExitAnnotation();
        }
    }

    private void OnAnnotationToolChangeRequested(object? sender, string tool)
    {
        var screenTool = tool switch
        {
            "Pen" => ScreenAnnotationTool.Pen,
            "Arrow" => ScreenAnnotationTool.Arrow,
            "Rectangle" => ScreenAnnotationTool.Rectangle,
            "Ellipse" => ScreenAnnotationTool.Ellipse,
            "Laser" => ScreenAnnotationTool.Laser,
            "Text" => ScreenAnnotationTool.Text,
            _ => ScreenAnnotationTool.Pen
        };
        RecordingAnnotationWindow.SetTool(screenTool);
    }

    private void OnAnnotationColorChangeRequested(object? sender, string hex)
    {
        if (Avalonia.Media.Color.TryParse(hex, out var col))
        {
            RecordingAnnotationWindow.SetColor(col);
        }
    }

    private void OnAnnotationClickThroughChangeRequested(object? sender, bool clickThrough)
    {
        RecordingAnnotationWindow.SetClickThrough(clickThrough);
    }

    private void OnAnnotationUndoRequested(object? sender, EventArgs e)
    {
        RecordingAnnotationWindow.Undo();
    }

    private void OnAnnotationClearRequested(object? sender, EventArgs e)
    {
        RecordingAnnotationWindow.Clear();
    }


    private void OnFloatingBarShowRequested(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            RecordingFloatingWindow.ShowOverlay(_currentVm);
        }
    }

    private void OnFloatingBarHideRequested(object? sender, EventArgs e)
    {
        RecordingFloatingWindow.HideOverlay();
    }

    private void OnFloatingBarToggleRequested(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            RecordingFloatingWindow.ToggleOverlay(_currentVm);
        }
    }

    /// <summary>
    /// 开录后把主窗口最小化，让出桌面给录制内容。
    /// </summary>
    private void OnHostWindowMinimizeRequested(object? sender, EventArgs e)
    {
        var window = _observedWindow ?? (TopLevel.GetTopLevel(this) as Window);
        if (window == null) return;

        // 录制目标包含本窗口时绝不能最小化：最小化后画面是空的，
        // 用户会得到一段纯黑/纯桌面的录像，且很难察觉。
        if (IsRecordingOwnWindow(window)) return;

        window.WindowState = WindowState.Minimized;
    }

    private bool IsRecordingOwnWindow(Window window)
    {
        if (_currentVm == null || !_currentVm.IsSourceWindow) return false;

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;

        return _currentVm.Windows.Any(w => w.IsSelected && w.Window.Hwnd == handle);
    }

    private void OnLiveCursorEffectShowRequested(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            var bounds = _currentVm.GetAnnotationBounds();
            RecordingAnnotationWindow.StartLiveCursorEffect(bounds, _currentVm);
        }
    }

    private void OnLiveCursorEffectHideRequested(object? sender, EventArgs e)
    {
        RecordingAnnotationWindow.StopLiveCursorEffect();
        if (_currentVm?.IsRecording != true)
        {
            RecordingAnnotationWindow.HideOverlay();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecordingViewModel.CurrentTargetBounds)
            or nameof(RecordingViewModel.TargetAspectRatio)
            or nameof(RecordingViewModel.ShowRegionBorder)
            or nameof(RecordingViewModel.IsRecording)
            or nameof(RecordingViewModel.StatElapsed)
            or nameof(RecordingViewModel.Source))
        {
            SyncOverlay();
            var host = this.FindControl<RecordingPreviewControl>("PreviewHost");
            host?.InvalidateMeasure();
        }

        if (e.PropertyName == nameof(RecordingViewModel.IsRecording))
        {
            SyncPreviewState();
        }
    }

    private void OnPulseRequested(object? sender, EventArgs e)
    {
        if (_currentVm != null)
        {
            var bounds = _currentVm.CurrentTargetBounds;
            if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
            {
                RecordingRegionBorderWindow.ShowOrUpdateOverlay(bounds, false, $"📐 目标区域: {bounds.Width} × {bounds.Height}");
                RecordingRegionBorderWindow.PulseOverlay();
            }
        }
    }

    private void SyncOverlay()
    {
        if (_currentVm == null)
        {
            RecordingRegionBorderWindow.HideOverlay();
            return;
        }

        // 关键防护：仅在【处于实际录制中】时才常驻展示边框，录制停止或未录制时坚决隐藏，杜绝原地残留窗口
        bool shouldShow = _currentVm.ShowRegionBorder
            && _currentVm.IsRecording
            && (_currentVm.IsSourceRegion || (_currentVm.IsSourceWindow && _currentVm.HasSelectedWindows));

        if (!shouldShow)
        {
            RecordingRegionBorderWindow.HideOverlay();
            return;
        }

        var bounds = _currentVm.CurrentTargetBounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            RecordingRegionBorderWindow.HideOverlay();
            return;
        }

        string? statusText = $"● REC  {_currentVm.StatElapsed}  |  {bounds.Width} × {bounds.Height}";
        RecordingRegionBorderWindow.ShowOrUpdateOverlay(bounds, true, statusText);
    }
}
