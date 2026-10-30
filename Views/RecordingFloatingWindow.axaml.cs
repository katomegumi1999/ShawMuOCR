using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MuRecorder.ViewModels;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

/// <summary>
/// 录屏桌面悬浮控制条：
/// - 纯无边框、置顶、可随意拖拽停靠；
/// - 关键特性：调用 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)，
///   系统 DWM/DXGI 桌面复制管线对其完全隐身，录制出的 MP4 绝对不会出现本悬浮窗！
/// </summary>
public partial class RecordingFloatingWindow : Window
{
    private static RecordingFloatingWindow? s_instance;
    private bool _hasPositioned;

    public static RecordingFloatingWindow EnsureInstance(RecordingViewModel vm)
    {
        if (s_instance == null)
        {
            s_instance = new RecordingFloatingWindow(vm);
        }
        else
        {
            s_instance.DataContext = vm;
            s_instance.AttachVm(vm);
        }
        return s_instance;
    }

    public static void ShowOverlay(RecordingViewModel vm)
    {
        var win = EnsureInstance(vm);
        win.PositionAtDefaultIfFirstTime();
        win.Show();
        win.Topmost = false;
        win.Topmost = true;
        win.Activate();
    }

    public static void HideOverlay()
    {
        s_instance?.CloseAndExitAnnotation();
    }

    /// <summary>
    /// 关闭或隐藏悬浮栏；如果屏幕标注当前仍在开启状态，则跟随其一同退出关闭。
    /// </summary>
    public void CloseAndExitAnnotation()
    {
        Hide();
        var vm = _attachedVm ?? (DataContext as RecordingViewModel);
        if (vm?.IsAnnotationActive == true)
        {
            vm.CloseAnnotation();
        }
        else
        {
            RecordingAnnotationWindow.ExitAnnotation();
        }
    }

    /// <summary>
    /// 将录屏悬浮工具栏置于所有最前端窗口顶层（包括透明屏幕标注层）。
    /// </summary>
    public static void BringToFront()
    {
        // 1. 先确保全屏透明光轨/标注图层处于 HWND_TOPMOST 顶层（高于浏览器、编辑器等一切第三方应用）
        RecordingAnnotationWindow.BringToFront();

        // 2. 再确保悬浮控制栏居于最顶层（高于透明屏幕标注层）
        if (s_instance != null && s_instance.IsVisible)
        {
            s_instance.Topmost = false;
            s_instance.Topmost = true;
            s_instance.Activate();

            var handle = s_instance.TryGetPlatformHandle();
            if (handle != null)
            {
                User32.SetWindowPos(handle.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);
            }
        }
    }

    public static void ToggleOverlay(RecordingViewModel vm)
    {
        var win = EnsureInstance(vm);
        if (win.IsVisible)
        {
            win.CloseAndExitAnnotation();
        }
        else
        {
            ShowOverlay(vm);
        }
    }

    public RecordingFloatingWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        RootBorder.PointerPressed += OnRootPointerPressed;
        CloseButton.Click += (_, _) =>
        {
            CloseAndExitAnnotation();
        };
        Closing += (_, e) =>
        {
            e.Cancel = true;
            CloseAndExitAnnotation();
        };
    }

    private RecordingViewModel? _attachedVm;

    private void AttachVm(RecordingViewModel vm)
    {
        if (ReferenceEquals(_attachedVm, vm)) return;
        _attachedVm = vm;

        vm.FloatingBarShowRequested += (s, e) => ShowOverlay(vm);
        vm.FloatingBarToggleRequested += (s, e) => ToggleOverlay(vm);
        vm.FloatingBarHideRequested += (s, e) => HideOverlay();

        vm.AnnotationToggleRequested += (s, active) =>
        {
            if (active)
            {
                var bounds = vm.CurrentTargetBounds;
                if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    bounds = vm.SelectedMonitor?.Bounds
                        ?? (vm.Monitors.FirstOrDefault(m => m.IsPrimary) ?? vm.Monitors.FirstOrDefault())?.Bounds
                        ?? new ShawMuOCR.Core.Capture.ScreenRect(0, 0, 1920, 1080);
                }
                RecordingAnnotationWindow.ShowOverlay(bounds, vm);
                BringToFront();
            }
            else
            {
                RecordingAnnotationWindow.ExitAnnotation();
            }
        };

        vm.AnnotationToolChangeRequested += (s, tool) =>
        {
            var screenTool = tool switch
            {
                "Pen" => ScreenAnnotationTool.Pen,
                "Arrow" => ScreenAnnotationTool.Arrow,
                "Rectangle" => ScreenAnnotationTool.Rectangle,
                "Ellipse" => ScreenAnnotationTool.Ellipse,
                "Laser" => ScreenAnnotationTool.Laser,
                _ => ScreenAnnotationTool.Pen
            };
            RecordingAnnotationWindow.SetTool(screenTool);
            BringToFront();
        };

        vm.AnnotationColorChangeRequested += (s, hex) =>
        {
            if (Avalonia.Media.Color.TryParse(hex, out var col))
            {
                RecordingAnnotationWindow.SetColor(col);
            }
            BringToFront();
        };

        vm.AnnotationClickThroughChangeRequested += (s, clickThrough) =>
        {
            RecordingAnnotationWindow.SetClickThrough(clickThrough);
            BringToFront();
        };

        vm.AnnotationUndoRequested += (s, e) => RecordingAnnotationWindow.Undo();
        vm.AnnotationClearRequested += (s, e) => RecordingAnnotationWindow.Clear();

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(RecordingViewModel.ExcludeFloatingBarFromCapture))
            {
                UpdateCaptureAffinity();
            }
        };
    }

    public RecordingFloatingWindow(RecordingViewModel vm) : this()
    {
        DataContext = vm;
        AttachVm(vm);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 关键防护：如果点击源是 Button 或其内部子元素，绝不触发窗口拖拽，确保 Click 事件 100% 灵敏响应
        if (e.Source is Visual visual)
        {
            for (var cur = visual; cur != null && !ReferenceEquals(cur, this); cur = cur.GetVisualParent())
            {
                if (cur is Button)
                {
                    return;
                }
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void PositionAtDefaultIfFirstTime()
    {
        if (_hasPositioned)
        {
            return;
        }
        _hasPositioned = true;

        if (Screens.Primary is { } primary)
        {
            // 默认居中停靠在主屏幕顶部下方 36px 处，避免遮挡顶栏
            int x = primary.WorkingArea.X + (primary.WorkingArea.Width - 360) / 2;
            int y = primary.WorkingArea.Y + 36;
            Position = new PixelPoint(x, y);
        }
    }

    public void UpdateCaptureAffinity()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;

        bool isRemoteSession = User32.GetSystemMetrics(User32.SM_REMOTESESSION) != 0;
        bool shouldExclude = _attachedVm?.ExcludeFloatingBarFromCapture ?? true;

        // 核心技术保证：
        // 1. 若处于远程桌面 (RDP 等) 环境，系统屏幕推流管线会被 WDA_EXCLUDEFROMCAPTURE 误杀导致远程用户看不见悬浮栏；
        // 2. 若用户主动关闭隐身（例如使用向日葵/ToDesk等第三方远程软件），则恢复 WDA_NONE 正常渲染；
        // 3. 本地正常录制且开启隐身时，设置 WDA_EXCLUDEFROMCAPTURE，录制出的 MP4 绝不包含悬浮工具栏！
        uint affinity = (!isRemoteSession && shouldExclude)
            ? User32.WDA_EXCLUDEFROMCAPTURE
            : User32.WDA_NONE;

        User32.SetWindowDisplayAffinity(handle.Handle, affinity);
    }

    private void ApplyWindowStyles()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null)
        {
            return;
        }

        var exStyle = User32.GetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64()
            | User32.WS_EX_TOOLWINDOW
            | User32.WS_EX_NOACTIVATE;

        User32.SetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE, new IntPtr(newExStyle));
        User32.SetWindowPos(handle.Handle, IntPtr.Zero, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_FRAMECHANGED);

        UpdateCaptureAffinity();
    }
}
