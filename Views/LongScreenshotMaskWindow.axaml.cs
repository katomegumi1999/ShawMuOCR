using System;
using Avalonia;
using Avalonia.Controls;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public partial class LongScreenshotMaskWindow : Window
{
    private readonly ScreenRect _region;
    private readonly ScreenRect _virtualBounds;

    public LongScreenshotMaskWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    public LongScreenshotMaskWindow(ScreenRect region, ScreenRect virtualBounds) : this()
    {
        _region = region;
        _virtualBounds = virtualBounds;

        double scaling = GetCurrentScreenScaling();
        Position = new PixelPoint(virtualBounds.X, virtualBounds.Y);
        Width = virtualBounds.Width / scaling;
        Height = virtualBounds.Height / scaling;

        LayoutMask(scaling);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void ApplyWindowStyles()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;

        var exStyle = User32.GetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64()
            | User32.WS_EX_TRANSPARENT
            | User32.WS_EX_TOOLWINDOW
            | User32.WS_EX_NOACTIVATE
            | User32.WS_EX_LAYERED;

        User32.SetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE, new IntPtr(newExStyle));
        User32.SetWindowDisplayAffinity(handle.Handle, User32.WDA_EXCLUDEFROMCAPTURE);
    }

    private void LayoutMask(double scaling)
    {
        double winW = _virtualBounds.Width / scaling;
        double winH = _virtualBounds.Height / scaling;

        double rx = (_region.X - _virtualBounds.X) / scaling;
        double ry = (_region.Y - _virtualBounds.Y) / scaling;
        double rw = _region.Width / scaling;
        double rh = _region.Height / scaling;

        // Top Mask
        Canvas.SetLeft(TopMask, 0);
        Canvas.SetTop(TopMask, 0);
        TopMask.Width = winW;
        TopMask.Height = Math.Max(0, ry);

        // Bottom Mask
        Canvas.SetLeft(BottomMask, 0);
        Canvas.SetTop(BottomMask, ry + rh);
        BottomMask.Width = winW;
        BottomMask.Height = Math.Max(0, winH - (ry + rh));

        // Left Mask
        Canvas.SetLeft(LeftMask, 0);
        Canvas.SetTop(LeftMask, ry);
        LeftMask.Width = Math.Max(0, rx);
        LeftMask.Height = Math.Max(0, rh);

        // Right Mask
        Canvas.SetLeft(RightMask, rx + rw);
        Canvas.SetTop(RightMask, ry);
        RightMask.Width = Math.Max(0, winW - (rx + rw));
        RightMask.Height = Math.Max(0, rh);

        // Highlight border
        Canvas.SetLeft(HighlightBorder, rx);
        Canvas.SetTop(HighlightBorder, ry);
        HighlightBorder.Width = rw;
        HighlightBorder.Height = rh;

        // Corners
        Canvas.SetLeft(CornerTopLeft, rx);
        Canvas.SetTop(CornerTopLeft, ry);

        Canvas.SetLeft(CornerTopRight, rx + rw - 14);
        Canvas.SetTop(CornerTopRight, ry);

        Canvas.SetLeft(CornerBottomLeft, rx);
        Canvas.SetTop(CornerBottomLeft, ry + rh - 14);

        Canvas.SetLeft(CornerBottomRight, rx + rw - 14);
        Canvas.SetTop(CornerBottomRight, ry + rh - 14);

        // Badge
        BadgeText.Text = $"📜 长截屏区域 ({_region.Width} × {_region.Height})";
        double badgeY = ry >= 32 ? ry - 28 : ry + 6;
        Canvas.SetLeft(BadgeBorder, rx + 6);
        Canvas.SetTop(BadgeBorder, badgeY);
    }

    private double GetCurrentScreenScaling()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen?.Scaling ?? 1.0;
    }
}
