using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Win32;
using SkiaSharp;

namespace ShawMuOCR.Views;

public partial class OverlaySelectionWindow : Window
{
    private readonly OcrImage? _screenshot;
    private readonly ScreenRect _virtualBounds;
    private readonly Bitmap? _avaloniaBitmap;
    private readonly TaskCompletionSource<OcrImage?> _tcs = new();

    private bool _isDragging;
    private Point _startPoint;
    private Point _currentPoint;

    public OverlaySelectionWindow()
    {
        InitializeComponent();
    }

    public OverlaySelectionWindow(OcrImage screenshot, ScreenRect virtualBounds) : this()
    {
        _screenshot = screenshot;
        _virtualBounds = virtualBounds;

        // 高性能零拷贝：直接从 SKBitmap 内存复制到 Avalonia WriteableBitmap，避免大图 PNG 编解码与 LOH 内存暴涨
        var wb = new WriteableBitmap(
            new PixelSize(_screenshot.Width, _screenshot.Height),
            new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);

        using (var fb = wb.Lock())
        {
            if (fb.Address != IntPtr.Zero)
            {
                var info = new SKImageInfo(_screenshot.Width, _screenshot.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
                if (surface != null)
                {
                    surface.Canvas.DrawBitmap(_screenshot.Bitmap, 0, 0, SKSamplingOptions.Default);
                }
                else
                {
                    unsafe
                    {
                        int height = _screenshot.Height;
                        int srcRowBytes = _screenshot.Bitmap.RowBytes;
                        int dstRowBytes = fb.RowBytes;
                        int bytesPerRow = Math.Min(srcRowBytes, dstRowBytes);
                        byte* pSrc = (byte*)_screenshot.Bitmap.GetPixels();
                        byte* pDst = (byte*)fb.Address;
                        for (int y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(pSrc + (y * srcRowBytes), pDst + (y * dstRowBytes), dstRowBytes, bytesPerRow);
                        }
                    }
                }
            }
        }
        _avaloniaBitmap = wb;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(_virtualBounds.X, _virtualBounds.Y);

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        var handle = TryGetPlatformHandle();
        if (handle != null)
        {
            User32.SetWindowPos(
                handle.Handle,
                User32.HWND_TOPMOST,
                _virtualBounds.X,
                _virtualBounds.Y,
                _virtualBounds.Width,
                _virtualBounds.Height,
                User32.SWP_SHOWWINDOW);
        }
        Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _avaloniaBitmap?.Dispose();
        _tcs.TrySetResult(null);
    }

    public static async Task<OcrImage?> ShowAndSelectAsync(OcrImage screenshot, ScreenRect virtualBounds)
    {
        var window = new OverlaySelectionWindow(screenshot, virtualBounds);
        window.Show();
        var result = await window._tcs.Task;
        return result;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var clientBounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (clientBounds.Width <= 0 || clientBounds.Height <= 0 || _avaloniaBitmap == null || _screenshot == null)
        {
            return;
        }

        // 1. 绘制底层冻结的物理全屏截图
        context.DrawImage(_avaloniaBitmap, clientBounds);

        // 2. 覆盖半透明暗色遮罩
        var maskBrush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        context.FillRectangle(maskBrush, clientBounds);

        if (_isDragging)
        {
            var selRect = GetNormalizedSelectionRect();
            if (selRect.Width > 1 && selRect.Height > 1)
            {
                // 计算在原图中的像素对应矩形
                double scaleX = (double)_screenshot.Width / Bounds.Width;
                double scaleY = (double)_screenshot.Height / Bounds.Height;

                var srcRect = new Rect(
                    selRect.X * scaleX,
                    selRect.Y * scaleY,
                    selRect.Width * scaleX,
                    selRect.Height * scaleY);

                // 3. 抠图显示明亮原图（无遮罩）
                context.DrawImage(_avaloniaBitmap, srcRect, selRect);

                // 4. 绘制高亮选取边框 (现代蓝)
                var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 120, 215)), 2);
                context.DrawRectangle(null, borderPen, selRect);

                // 5. 绘制选区尺寸提示标签
                int pixelW = (int)Math.Round(srcRect.Width);
                int pixelH = (int)Math.Round(srcRect.Height);
                string sizeText = $"{pixelW} × {pixelH}";

                var formattedText = new FormattedText(
                    sizeText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium),
                    12,
                    Brushes.White);

                double badgeWidth = formattedText.Width + 16;
                double badgeHeight = formattedText.Height + 8;
                double badgeX = selRect.X;
                double badgeY = selRect.Y - badgeHeight - 4;
                if (badgeY < 4)
                {
                    badgeY = selRect.Y + 4;
                }

                var badgeRect = new Rect(badgeX, badgeY, badgeWidth, badgeHeight);
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(200, 30, 30, 30)), badgeRect, 4);
                context.DrawText(formattedText, new Point(badgeX + 8, badgeY + 4));
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var prop = e.GetCurrentPoint(this).Properties;
        if (prop.IsLeftButtonPressed)
        {
            _isDragging = true;
            _startPoint = e.GetPosition(this);
            _currentPoint = _startPoint;
            InvalidateVisual();
        }
        else if (prop.IsRightButtonPressed)
        {
            // 右键取消截屏
            CancelAndClose();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            _currentPoint = e.GetPosition(this);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            if (_screenshot == null)
            {
                InvalidateVisual();
                return;
            }

            var selRect = GetNormalizedSelectionRect();

            // 计算物理像素裁剪尺寸
            double scaleX = (double)_screenshot.Width / Bounds.Width;
            double scaleY = (double)_screenshot.Height / Bounds.Height;

            int cropX = (int)Math.Round(selRect.X * scaleX);
            int cropY = (int)Math.Round(selRect.Y * scaleY);
            int cropW = (int)Math.Round(selRect.Width * scaleX);
            int cropH = (int)Math.Round(selRect.Height * scaleY);

            if (_screenshot != null && cropW >= 5 && cropH >= 5)
            {
                var cropped = _screenshot.Crop(cropX, cropY, cropW, cropH);
                _tcs.TrySetResult(cropped);
                Close();
            }
            else
            {
                // 点按未拖动有效矩形则刷新视图
                InvalidateVisual();
            }
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            CancelAndClose();
        }
    }

    private Rect GetNormalizedSelectionRect()
    {
        double x = Math.Min(_startPoint.X, _currentPoint.X);
        double y = Math.Min(_startPoint.Y, _currentPoint.Y);
        double w = Math.Abs(_currentPoint.X - _startPoint.X);
        double h = Math.Abs(_currentPoint.Y - _startPoint.Y);
        return new Rect(x, y, w, h);
    }

    private void CancelAndClose()
    {
        _tcs.TrySetResult(null);
        Close();
    }
}
