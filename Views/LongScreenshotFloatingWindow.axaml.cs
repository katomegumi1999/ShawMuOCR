using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public partial class LongScreenshotFloatingWindow : Window
{
    private Action? _onStop;
    private Action? _onCancel;

    public LongScreenshotFloatingWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        RootBorder.PointerPressed += OnRootPointerPressed;
        FinishButton.Click += (_, _) => _onStop?.Invoke();
        CancelButton.Click += (_, _) => _onCancel?.Invoke();
    }

    public LongScreenshotFloatingWindow(
        ScreenRect region,
        ScreenRect virtualBounds,
        Action onStop,
        Action onCancel) : this()
    {
        _onStop = onStop;
        _onCancel = onCancel;

        PositionAtRegion(region, virtualBounds);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _onCancel?.Invoke();
            e.Handled = true;
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
        User32.SetWindowDisplayAffinity(handle.Handle, User32.WDA_EXCLUDEFROMCAPTURE);
    }

    private void PositionAtRegion(ScreenRect region, ScreenRect virtualBounds)
    {
        int centerX = region.X + region.Width / 2;
        int targetX = centerX - 240; // 预计宽度约 480px
        int targetY = region.Y + region.Height + 16;

        if (targetY + 70 > virtualBounds.Y + virtualBounds.Height)
        {
            targetY = region.Y - 60;
        }

        targetX = Math.Max(virtualBounds.X + 16, Math.Min(targetX, virtualBounds.X + virtualBounds.Width - 490));
        targetY = Math.Max(virtualBounds.Y + 16, Math.Min(targetY, virtualBounds.Y + virtualBounds.Height - 70));

        Position = new PixelPoint(targetX, targetY);
    }

    public void UpdateStatus(string message)
    {
        StatusTextBlock.Text = message;
    }
}
