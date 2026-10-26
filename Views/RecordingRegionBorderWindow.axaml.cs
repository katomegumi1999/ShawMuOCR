using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public partial class RecordingRegionBorderWindow : Window
{
    private const int PaddingMargin = 12;
    private ScreenRect _currentRegion;
    private bool _isRecording;
    private CancellationTokenSource? _pulseCts;
    private static RecordingRegionBorderWindow? s_sharedOverlay;

    /// <summary>
    /// 全局便捷接口：显示或更新录屏区域高亮外边框。
    /// </summary>
    public static void ShowOrUpdateOverlay(ScreenRect rect, bool isRecording, string? statusText = null)
    {
        if (s_sharedOverlay == null)
        {
            s_sharedOverlay = new RecordingRegionBorderWindow();
        }
        s_sharedOverlay.UpdateRegion(rect, isRecording, statusText);
    }

    /// <summary>
    /// 触发呼吸灯脉冲提示。
    /// </summary>
    public static void PulseOverlay()
    {
        s_sharedOverlay?.TriggerBreathingLight();
    }

    /// <summary>
    /// 隐藏高亮边框。
    /// </summary>
    public static void HideOverlay()
    {
        s_sharedOverlay?.Hide();
    }

    public RecordingRegionBorderWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _pulseCts?.Cancel();
        _pulseCts?.Dispose();
        _pulseCts = null;
    }

    /// <summary>
    /// 设置窗口为绝对无焦点、完全点击穿透并对 DXGI / GDI 截屏完全隐身。
    /// </summary>
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

        // 关键：通知 DWM 本窗体对屏幕捕获完全隐身，录制视频内绝对不出现本边框
        User32.SetWindowDisplayAffinity(handle.Handle, User32.WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>
    /// 更新边框所圈定的物理像素屏幕区域与录制状态。
    /// </summary>
    public void UpdateRegion(ScreenRect rect, bool isRecording, string? statusText = null)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            Hide();
            return;
        }

        _currentRegion = rect;
        _isRecording = isRecording;

        int winX = rect.X - PaddingMargin;
        int winY = rect.Y - PaddingMargin;
        int winW = rect.Width + PaddingMargin * 2;
        int winH = rect.Height + PaddingMargin * 2;

        double scaling = GetCurrentScreenScaling();
        Width = winW / scaling;
        Height = winH / scaling;
        Position = new PixelPoint(winX, winY);

        var handle = TryGetPlatformHandle();
        if (handle != null)
        {
            User32.SetWindowPos(
                handle.Handle,
                User32.HWND_TOPMOST,
                winX,
                winY,
                winW,
                winH,
                User32.SWP_SHOWWINDOW | User32.SWP_NOACTIVATE);
        }

        if (isRecording)
        {
            RecIndicator.IsVisible = true;
            StatusText.Text = string.IsNullOrEmpty(statusText)
                ? $"● REC  |  {rect.Width} × {rect.Height}"
                : statusText;
        }
        else
        {
            RecIndicator.IsVisible = false;
            StatusText.Text = $"📐 录制区域: {rect.Width} × {rect.Height}";
        }

        if (!IsVisible)
        {
            Show();
        }
    }

    /// <summary>
    /// 触发呼吸灯脉冲提示（参考钉图 FlashHintBorder 风格，选中区域后高亮提示位置）。
    /// </summary>
    public void TriggerBreathingLight()
    {
        _pulseCts?.Cancel();
        _pulseCts?.Dispose();
        var cts = new CancellationTokenSource();
        _pulseCts = cts;

        _ = RunBreathingAnimationAsync(cts.Token);
    }

    private async Task RunBreathingAnimationAsync(CancellationToken token)
    {
        try
        {
            // 迅速点亮至 100%
            GlowBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            };
            GlowBorder.Opacity = 1.0;
            await Task.Delay(240, token);

            // 回落至 40%
            GlowBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(200),
                    Easing = new CubicEaseInOut()
                }
            };
            GlowBorder.Opacity = 0.4;
            await Task.Delay(220, token);

            // 再次充盈点亮
            GlowBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseOut()
                }
            };
            GlowBorder.Opacity = 1.0;
            await Task.Delay(240, token);

            // 平稳回落至常态 0.85
            GlowBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(600),
                    Easing = new CubicEaseOut()
                }
            };
            GlowBorder.Opacity = 0.85;

            // 若非录制中（仅预览脉冲），脉冲结束后 1 秒自动隐退，避免常驻留在桌面上
            if (!_isRecording && !token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
                Hide();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            GlowBorder.Opacity = 0.85;
        }
    }

    private double GetCurrentScreenScaling()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen?.Scaling ?? 1.0;
    }
}
