using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public partial class ToastNotificationWindow : Window
{
    private static ToastNotificationWindow? _activeToast;
    private readonly DispatcherTimer _closeTimer;
    private bool _isClosing;

    public ToastNotificationWindow()
    {
        InitializeComponent();

        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _closeTimer.Tick += (s, e) =>
        {
            _closeTimer.Stop();
            CloseToast();
        };

        PointerPressed += (s, e) => CloseToast();
    }

    public static void Show(string text) => Show(text, TimeSpan.Zero, null);

    public static void Show(string text, string title) => Show(text, TimeSpan.Zero, title);

    public static void Show(string text, TimeSpan elapsed, string? title = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 关闭上一个气泡，避免堆叠
                _activeToast?.CloseToast(instant: true);

                var toast = new ToastNotificationWindow();
                _activeToast = toast;

                string cleanText = (text ?? string.Empty).Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                if (string.IsNullOrWhiteSpace(cleanText))
                {
                    cleanText = "（未识别到有效文字）";
                }

                if (!string.IsNullOrEmpty(title))
                {
                    toast.TitleTextBlock.Text = title;
                }
                toast.SnippetTextBlock.Text = cleanText;
                toast.ElapsedTextBlock.Text = elapsed > TimeSpan.Zero ? $"耗时: {elapsed.TotalMilliseconds:F0} ms" : string.Empty;

                toast.Opened += (s, e) =>
                {
                    var handle = toast.TryGetPlatformHandle();
                    if (handle != null)
                    {
                        // 1. 扩展 DWM 帧至全客户区，实现真正的亚像素全透与半透 Alpha 合成，杜绝四角灰色直角
                        var margins = DwmApi.MARGINS.FullWindow;
                        DwmApi.DwmExtendFrameIntoClientArea(handle.Handle, ref margins);

                        // 2. 设置 WS_EX_NOACTIVATE 确保绝不抢占用户当前窗口的键盘焦点
                        long exStyle = User32.GetWindowLongPtr(handle.Handle, -20).ToInt64();
                        exStyle |= User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW;
                        User32.SetWindowLongPtr(handle.Handle, -20, new IntPtr(exStyle));

                        // 3. 获取当前鼠标所在屏幕（或主屏幕）的真实物理工作区绝对坐标（自动避开任务栏）
                        Screen? targetScreen = null;
                        if (User32.GetCursorPos(out var pt))
                        {
                            targetScreen = toast.Screens.ScreenFromPoint(new PixelPoint(pt.x, pt.y));
                        }
                        var screen = targetScreen ?? toast.Screens.Primary ?? toast.Screens.All.FirstOrDefault();
                        double scaling = screen?.Scaling ?? 1.0;
                        var workArea = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

                        // 4. 测量卡片尺寸并换算为物理像素
                        toast.Measure(new Size(320, double.PositiveInfinity));
                        double cardW = toast.DesiredSize.Width > 0 ? toast.DesiredSize.Width : 320;
                        double cardH = toast.DesiredSize.Height > 0 ? toast.DesiredSize.Height : 50;

                        int pixelW = (int)Math.Ceiling(cardW * scaling);
                        int pixelH = (int)Math.Ceiling(cardH * scaling);

                        // 5. 屏幕底部正中央绝对对齐，离工作区底边上浮 35 逻辑像素
                        int posX = workArea.X + (workArea.Width - pixelW) / 2;
                        int posY = workArea.Y + workArea.Height - pixelH - (int)Math.Round(35 * scaling);

                        toast.Position = new PixelPoint(posX, posY);

                        User32.SetWindowPos(
                            handle.Handle,
                            User32.HWND_TOPMOST,
                            posX,
                            posY,
                            pixelW,
                            pixelH,
                            User32.SWP_SHOWWINDOW | User32.SWP_NOACTIVATE);
                    }

                    // 4. 触发平滑淡入并升起动画
                    Dispatcher.UIThread.Post(() =>
                    {
                        toast.RootCard.Opacity = 1.0;
                        toast.RootCard.RenderTransform = new TranslateTransform(0, 0);
                    }, DispatcherPriority.Render);
                };

                toast.Show();
                toast._closeTimer.Start();
            }
            catch
            {
                // 异常保护
            }
        });
    }

    private void CloseToast(bool instant = false)
    {
        _closeTimer.Stop();

        if (instant)
        {
            if (_activeToast == this) _activeToast = null;
            Close();
            return;
        }

        if (_isClosing) return;
        _isClosing = true;

        // 平滑淡出与微沉动画
        RootCard.Opacity = 0.0;
        RootCard.RenderTransform = new TranslateTransform(0, 10);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (_activeToast == this) _activeToast = null;
            Close();
        };
        timer.Start();
    }
}
