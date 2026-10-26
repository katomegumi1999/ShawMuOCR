using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public partial class CanvasBackdropWindow : Window
{
    private static CanvasBackdropWindow? s_sharedBackdrop;
    private static Action? s_onExitCallback;

    public CanvasBackdropWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closed += OnClosed;

        var exitBtn = this.FindControl<Button>("ExitCanvasBtn");
        if (exitBtn != null)
        {
            exitBtn.Click += (_, _) => CloseBackdrop();
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        s_onExitCallback?.Invoke();
        s_onExitCallback = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CloseBackdrop();
        }
    }

    private void ApplyWindowStyles()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;

        var exStyle = User32.GetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64()
            | User32.WS_EX_TOOLWINDOW
            | User32.WS_EX_NOACTIVATE;

        User32.SetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE, new IntPtr(newExStyle));
    }

    /// <summary>
    /// 打开全屏纯黑画布，并将已勾选的目标窗口提升到画布前景。
    /// </summary>
    public static void ShowBackdrop(ScreenRect monitorBounds, IEnumerable<IntPtr> selectedHwnds, Action? onExit = null)
    {
        s_onExitCallback = onExit;

        if (s_sharedBackdrop == null)
        {
            s_sharedBackdrop = new CanvasBackdropWindow();
        }

        double scaling = s_sharedBackdrop.GetCurrentScreenScaling();
        s_sharedBackdrop.Position = new PixelPoint(monitorBounds.X, monitorBounds.Y);
        s_sharedBackdrop.Width = monitorBounds.Width / scaling;
        s_sharedBackdrop.Height = monitorBounds.Height / scaling;

        if (!s_sharedBackdrop.IsVisible)
        {
            s_sharedBackdrop.Show();
        }

        var handle = s_sharedBackdrop.TryGetPlatformHandle();
        if (handle != null)
        {
            // 将黑幕铺在普通窗口最上层，但不是 Topmost（允许已选窗口被放在它上面）
            User32.SetWindowPos(
                handle.Handle,
                User32.HWND_TOP,
                monitorBounds.X,
                monitorBounds.Y,
                monitorBounds.Width,
                monitorBounds.Height,
                User32.SWP_SHOWWINDOW | User32.SWP_NOACTIVATE);
        }

        // 将所有勾选的目标窗口逐一抬升到黑幕画布之上
        IntPtr lastHwnd = IntPtr.Zero;
        foreach (var hwnd in selectedHwnds)
        {
            if (hwnd == IntPtr.Zero) continue;
            if (User32.IsIconic(hwnd))
            {
                User32.ShowWindow(hwnd, 9 /* SW_RESTORE */);
            }

            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOP,
                0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_SHOWWINDOW);

            lastHwnd = hwnd;
        }

        if (lastHwnd != IntPtr.Zero)
        {
            User32.SetForegroundWindow(lastHwnd);
        }
    }

    /// <summary>
    /// 关闭或隐藏自由画布。
    /// </summary>
    public static void CloseBackdrop()
    {
        if (s_sharedBackdrop != null && s_sharedBackdrop.IsVisible)
        {
            s_sharedBackdrop.Hide();
            s_onExitCallback?.Invoke();
            s_onExitCallback = null;
        }
    }

    public static bool IsOpen => s_sharedBackdrop?.IsVisible == true;

    private double GetCurrentScreenScaling()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen?.Scaling ?? 1.0;
    }
}
