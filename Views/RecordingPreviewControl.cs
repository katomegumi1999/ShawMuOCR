using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

/// <summary>
/// 录屏实时画面原生硬件宿主控件（继承 NativeControlHost）。
/// 在 Windows 上分配一个无边框的原生子窗口 HWND，交由 Rust 直接通过 D3D11 交换链进行零拷贝硬件直通渲染。
/// 支持按 TargetAspectRatio 保持原始宽高比适应可用区域大小居中缩放。
/// </summary>
public class RecordingPreviewControl : NativeControlHost
{
    private IntPtr _hwnd = IntPtr.Zero;

    public IntPtr Hwnd => _hwnd;

    public static readonly StyledProperty<double> TargetAspectRatioProperty =
        AvaloniaProperty.Register<RecordingPreviewControl, double>(nameof(TargetAspectRatio), 16.0 / 9.0);

    /// <summary>
    /// 目标录制画面的宽高比（宽 / 高）。控件在此比例约束下自动等比例适应父容器大小。
    /// </summary>
    public double TargetAspectRatio
    {
        get => GetValue(TargetAspectRatioProperty);
        set => SetValue(TargetAspectRatioProperty, value);
    }

    static RecordingPreviewControl()
    {
        AffectsMeasure<RecordingPreviewControl>(TargetAspectRatioProperty);
    }

    public event EventHandler<IntPtr>? HwndCreated;
    public event EventHandler? HwndDestroyed;

    protected override Size MeasureOverride(Size availableSize)
    {
        double ratio = TargetAspectRatio;
        if (ratio <= 0.05 || double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            ratio = 16.0 / 9.0;
        }

        double maxW = availableSize.Width;
        double maxH = availableSize.Height;

        if (double.IsInfinity(maxW) && double.IsInfinity(maxH))
        {
            return new Size(320, Math.Round(320 / ratio));
        }
        if (double.IsInfinity(maxW))
        {
            return new Size(Math.Round(maxH * ratio), maxH);
        }
        if (double.IsInfinity(maxH))
        {
            return new Size(maxW, Math.Round(maxW / ratio));
        }

        if (maxW <= 0 || maxH <= 0)
        {
            return new Size(0, 0);
        }

        double containerRatio = maxW / maxH;
        double desiredW;
        double desiredH;

        if (containerRatio > ratio)
        {
            // 容器比画面更宽：受高度限制，按高度计算等比宽度
            desiredH = maxH;
            desiredW = Math.Round(maxH * ratio);
        }
        else
        {
            // 容器比画面更高：受宽度限制，按宽度计算等比高度
            desiredW = maxW;
            desiredH = Math.Round(maxW / ratio);
        }

        return new Size(Math.Max(1, desiredW), Math.Max(1, desiredH));
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (OperatingSystem.IsWindows())
        {
            const uint WS_CHILD = 0x40000000;
            const uint WS_VISIBLE = 0x10000000;
            const uint WS_CLIPSIBLINGS = 0x04000000;

            var hwnd = User32.CreateWindowEx(
                0,
                "static",
                string.Empty,
                WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
                0, 0, 100, 100,
                parent.Handle,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            _hwnd = hwnd;
            HwndCreated?.Invoke(this, hwnd);
            return new PlatformHandle(hwnd, "HWND");
        }

        return base.CreateNativeControlCore(parent);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows() && control.Handle != IntPtr.Zero)
        {
            _hwnd = IntPtr.Zero;
            HwndDestroyed?.Invoke(this, EventArgs.Empty);
            User32.DestroyWindow(control.Handle);
        }
        base.DestroyNativeControlCore(control);
    }
}
