using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Clipboard;
using SkiaSharp;
using System.Threading;
using System.Threading.Tasks;

namespace ShawMuOCR.Views;

public partial class PinnedImageWindow : Window
{
    private readonly OcrImage _image;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;
    private float _scale = 1.0f;
    private DispatcherTimer? _scaleBadgeTimer;
    private CancellationTokenSource? _flashCts;

    public PinnedImageWindow() : this(new OcrImage(new SKBitmap(100, 100)))
    {
    }

    public PinnedImageWindow(OcrImage image, PixelPoint? initialPos = null)
    {
        _image = image ?? throw new ArgumentNullException(nameof(image));
        _pixelWidth = image.Width;
        _pixelHeight = image.Height;

        InitializeComponent();

        var wb = ConvertToBitmap(image);
        PinnedImageControl.Source = wb;

        if (initialPos.HasValue)
        {
            Position = initialPos.Value;
        }

        double scaling = GetCurrentScreenScaling();
        Width = _pixelWidth / scaling;
        Height = _pixelHeight / scaling;

        AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnRootPointerWheelChanged, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);

        // 窗口就绪上屏后，触发高亮呼吸灯脉冲提示
        Opened += (s, e) =>
        {
            Dispatcher.UIThread.Post(async () =>
            {
                // 等待系统 DWM 与 GPU 完成首帧画面合成
                await Task.Delay(100);
                TriggerBreathingLight();
            }, DispatcherPriority.Render);
        };
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            // 双击重置为 100% 原始大小
            if (e.ClickCount == 2)
            {
                ResetZoom();
                e.Handled = true;
                return;
            }

            // 检查是否点中了关闭按钮
            if (CloseBtn.IsPointerOver)
            {
                return;
            }

            // 任意位置按住拖动窗口
            BeginMoveDrag(e);
        }
    }

    private void OnRootPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // 滚轮缩放：每次步长 10%
        float delta = (float)(e.Delta.Y * 0.1);
        float newScale = Math.Clamp(_scale + delta, 0.25f, 4.0f);

        if (Math.Abs(newScale - _scale) > 0.01f)
        {
            _scale = newScale;
            ApplyScale();
        }

        e.Handled = true;
    }

    private double GetCurrentScreenScaling()
    {
        if (Screens != null)
        {
            var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
            if (screen != null && screen.Scaling > 0.001)
            {
                return screen.Scaling;
            }
        }
        return DesktopScaling > 0.001 ? DesktopScaling : 1.0;
    }

    private void ApplyScale()
    {
        double scaling = GetCurrentScreenScaling();
        Width = (_pixelWidth * _scale) / scaling;
        Height = (_pixelHeight * _scale) / scaling;

        ScaleText.Text = $"{(int)(_scale * 100)}%";
        ScaleBadge.IsVisible = true;

        _scaleBadgeTimer?.Stop();
        _scaleBadgeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2)
        };
        _scaleBadgeTimer.Tick += (_, _) =>
        {
            ScaleBadge.IsVisible = false;
            _scaleBadgeTimer?.Stop();
        };
        _scaleBadgeTimer.Start();
    }

    private void ResetZoom()
    {
        _scale = 1.0f;
        ApplyScale();
        TriggerBreathingLight();
    }

    public void TriggerFlashHint() => TriggerBreathingLight();

    public void TriggerBreathingLight()
    {
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        var cts = new CancellationTokenSource();
        _flashCts = cts;

        _ = RunBreathingAnimationAsync(cts.Token);
    }

    private async Task RunBreathingAnimationAsync(CancellationToken token)
    {
        try
        {
            // 阶段 1: 迅速通电点亮冲顶至 100% (耗时约 260ms)
            FlashHintBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(260),
                    Easing = new CubicEaseOut()
                }
            };
            FlashHintBorder.Opacity = 1.0;
            await Task.Delay(280, token);

            // 阶段 2: 浅呼吸回落至 30% (耗时约 220ms)
            FlashHintBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new CubicEaseInOut()
                }
            };
            FlashHintBorder.Opacity = 0.3;
            await Task.Delay(240, token);

            // 阶段 3: 再次充盈点亮至 100% (耗时约 240ms)
            FlashHintBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(240),
                    Easing = new CubicEaseOut()
                }
            };
            FlashHintBorder.Opacity = 1.0;
            await Task.Delay(260, token);

            // 阶段 4: 优雅徐徐暗下熄灭至 0% (耗时约 1400ms)
            FlashHintBorder.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = TimeSpan.FromMilliseconds(1400),
                    Easing = new CubicEaseOut()
                }
            };
            FlashHintBorder.Opacity = 0.0;
            await Task.Delay(1450, token);
        }
        catch (OperationCanceledException)
        {
            // 被新的呼吸灯脉冲打断
        }
        catch (Exception)
        {
            FlashHintBorder.Opacity = 0.0;
        }
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.W && e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnEditImageClick(object? sender, RoutedEventArgs e)
    {
        var editor = new ScreenshotEditorWindow(
            _image.Clone(),
            selectionBounds: new ScreenRect(Position.X, Position.Y, _pixelWidth, _pixelHeight));
        editor.Show();
        Close();
    }

    private void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        _ = new WindowsClipboardService().SetImageAsync(_image);
    }

    private void OnResetZoomClick(object? sender, RoutedEventArgs e)
    {
        ResetZoom();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _flashCts?.Cancel();
        _flashCts?.Dispose();
        _flashCts = null;
        _scaleBadgeTimer?.Stop();
        _image.Dispose();
    }

    private static WriteableBitmap ConvertToBitmap(OcrImage image)
    {
        var wb = new WriteableBitmap(
            new PixelSize(image.Width, image.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = wb.Lock())
        {
            var bmp = image.Bitmap;
            if (bmp != null && !bmp.IsNull && bmp.Handle != IntPtr.Zero && fb.Address != IntPtr.Zero)
            {
                var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
                if (surface != null)
                {
                    surface.Canvas.DrawBitmap(bmp, 0, 0, SKSamplingOptions.Default);
                }
                else
                {
                    IntPtr pixels = bmp.GetPixels();
                    if (pixels != IntPtr.Zero)
                    {
                        unsafe
                        {
                            int height = image.Height;
                            int srcRowBytes = bmp.RowBytes;
                            int dstRowBytes = fb.RowBytes;
                            int bytesPerRow = Math.Min(srcRowBytes, dstRowBytes);
                            byte* pSrc = (byte*)pixels;
                            byte* pDst = (byte*)fb.Address;
                            for (int y = 0; y < height; y++)
                            {
                                Buffer.MemoryCopy(pSrc + (y * srcRowBytes), pDst + (y * dstRowBytes), dstRowBytes, bytesPerRow);
                            }
                        }
                    }
                }
            }
        }

        return wb;
    }
}
