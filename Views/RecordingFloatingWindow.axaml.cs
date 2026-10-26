using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        }
        return s_instance;
    }

    public static void ShowOverlay(RecordingViewModel vm)
    {
        var win = EnsureInstance(vm);
        win.PositionAtDefaultIfFirstTime();
        win.Show();
        win.Activate();
    }

    public static void HideOverlay()
    {
        s_instance?.Hide();
    }

    public static void ToggleOverlay(RecordingViewModel vm)
    {
        var win = EnsureInstance(vm);
        if (win.IsVisible)
        {
            win.Hide();
        }
        else
        {
            win.PositionAtDefaultIfFirstTime();
            win.Show();
            win.Activate();
        }
    }

    public RecordingFloatingWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        RootBorder.PointerPressed += OnRootPointerPressed;
        CloseButton.Click += (_, _) => Hide();
    }

    public RecordingFloatingWindow(RecordingViewModel vm) : this()
    {
        DataContext = vm;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
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

        // 核心技术保证：通知 Windows DWM 硬件合成器对屏幕录制/截屏完全隐身！
        // 即使悬浮栏位于录制区域正中央，录出来的 MP4 视频画面也 100% 不会包含本窗口。
        User32.SetWindowDisplayAffinity(handle.Handle, User32.WDA_EXCLUDEFROMCAPTURE);
    }
}
