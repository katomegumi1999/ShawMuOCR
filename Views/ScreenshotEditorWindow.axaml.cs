using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.Core.Logging;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Windows.Clipboard;
using ShawMuOCR.Windows.Win32;
using SkiaSharp;
using Point = Avalonia.Point;

namespace ShawMuOCR.Views;

public partial class ScreenshotEditorWindow : Window
{
    private readonly ImageDocument _doc;
    private readonly IClipboardService _clipboardService;
    private readonly Func<OcrImage, Task>? _ocrCallback;
    private readonly Func<OcrImage, ScreenRect, Task>? _translateCallback;
    private readonly ISettingsService? _settingsService;
    private readonly ILoggerService? _logger;
    private readonly ScreenRect? _selectionBounds;
    private bool _isProcessingAction;

    private EditorTool _currentTool = EditorTool.Rectangle;
    private SKColor _currentColor = new SKColor(229, 57, 53); // 默认鲜红
    private double _displayScale = 1.0;

    private bool _isDrawing;
    private Point _startPoint;
    private Control? _previewControl;

    private IImageElement? _selectedElement;
    private bool _isDraggingElement;
    private Point _dragStartPoint;
    private Border? _selectionHighlightBox;
    private Point _textInsertPoint;

    public ScreenshotEditorWindow() : this(new OcrImage(new SKBitmap(100, 100)))
    {
    }

    public ScreenshotEditorWindow(
        OcrImage image,
        IClipboardService? clipboardService = null,
        Func<OcrImage, Task>? ocrCallback = null,
        Func<OcrImage, ScreenRect, Task>? translateCallback = null,
        ISettingsService? settingsService = null,
        ILoggerService? logger = null,
        ScreenRect? selectionBounds = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        _doc = new ImageDocument(image);
        _clipboardService = clipboardService ?? new WindowsClipboardService();
        _ocrCallback = ocrCallback;
        _translateCallback = translateCallback;
        _settingsService = settingsService;
        _logger = logger;
        _selectionBounds = selectionBounds;

        InitializeComponent();

        DimensionText.Text = $"{image.Width} × {image.Height}";

        ApplyDimensions();
        RefreshDisplay();

        Opened += (s, e) => ApplyDimensions();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private double GetScreenScaling()
    {
        if (DesktopScaling > 0 && Math.Abs(DesktopScaling - 1.0) > 0.001)
        {
            return DesktopScaling;
        }

        try
        {
            var handle = TryGetPlatformHandle();
            if (handle != null && handle.Handle != IntPtr.Zero)
            {
                uint dpi = User32.GetDpiForWindow(handle.Handle);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch { }

        try
        {
            uint sysDpi = User32.GetDpiForSystem();
            if (sysDpi > 0) return sysDpi / 96.0;
        }
        catch { }

        try
        {
            var primary = Screens.Primary;
            if (primary != null && primary.Scaling > 0)
            {
                return primary.Scaling;
            }
        }
        catch { }

        return DesktopScaling > 0 ? DesktopScaling : 1.0;
    }

    private void ApplyDimensions()
    {
        double scaling = GetScreenScaling();
        if (scaling <= 0) scaling = 1.0;

        double maxW = (Screens.Primary?.WorkingArea.Width ?? 1920) * 0.88 / scaling;
        double maxH = (Screens.Primary?.WorkingArea.Height ?? 1080) * 0.82 / scaling;

        double imgW = _doc.OriginalImage.Width;
        double imgH = _doc.OriginalImage.Height;

        // 核心修复：1:1 物理像素在 Avalonia 逻辑 DIP 空间中的真实大小，避免高分屏放大模糊
        double dipW = imgW / scaling;
        double dipH = imgH / scaling;

        double fitScale = 1.0;
        if (dipW > maxW || dipH > maxH)
        {
            fitScale = Math.Min(maxW / dipW, maxH / dipH);
        }

        // _displayScale 是从图像原始物理像素到 UI 画布 (DIP) 的换算系数:
        // canvasDIP = imgPixel * _displayScale;
        // imgPixel = canvasDIP / _displayScale;
        _displayScale = (1.0 / scaling) * fitScale;

        CanvasContainer.Width = imgW * _displayScale;
        CanvasContainer.Height = imgH * _displayScale;

        BaseImageControl.Width = imgW * _displayScale;
        BaseImageControl.Height = imgH * _displayScale;

        DrawingOverlayCanvas.Width = imgW * _displayScale;
        DrawingOverlayCanvas.Height = imgH * _displayScale;

        UpdateSelectionHighlight();
    }

    private void RefreshDisplay()
    {
        using var rendered = _doc.RenderToBitmap();
        var wb = ConvertToBitmap(rendered);
        BaseImageControl.Source = wb;

        UndoBtn.IsEnabled = _doc.UndoRedo.CanUndo;
        RedoBtn.IsEnabled = _doc.UndoRedo.CanRedo;

        UpdateSelectionHighlight();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnToolChanged(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        if (ToolSelectBtn.IsChecked == true) _currentTool = EditorTool.Select;
        else if (ToolTextBtn.IsChecked == true) _currentTool = EditorTool.Text;
        else if (ToolLineBtn.IsChecked == true) _currentTool = EditorTool.Line;
        else if (ToolArrowBtn.IsChecked == true) _currentTool = EditorTool.Arrow;
        else if (ToolRectBtn.IsChecked == true) _currentTool = EditorTool.Rectangle;

        UpdateCursor();

        if (_currentTool != EditorTool.Select)
        {
            _selectedElement = null;
            UpdateSelectionHighlight();
        }
    }

    private void OnColorChanged(object? sender, RoutedEventArgs e)
    {
        if (ColorRedBtn.IsChecked == true) _currentColor = new SKColor(229, 57, 53);
        else if (ColorGreenBtn.IsChecked == true) _currentColor = new SKColor(67, 160, 71);
        else if (ColorBlueBtn.IsChecked == true) _currentColor = new SKColor(30, 136, 229);
        else if (ColorYellowBtn.IsChecked == true) _currentColor = new SKColor(253, 216, 53);

        var avaloniaColor = Color.FromRgb(_currentColor.Red, _currentColor.Green, _currentColor.Blue);
        InlineTextBox.Foreground = new SolidColorBrush(avaloniaColor);
        InlineTextBox.BorderBrush = new SolidColorBrush(avaloniaColor);
    }

    private void UpdateCursor()
    {
        DrawingOverlayCanvas.Cursor = _currentTool switch
        {
            EditorTool.Select => new Cursor(StandardCursorType.Hand),
            EditorTool.Text => new Cursor(StandardCursorType.Ibeam),
            _ => new Cursor(StandardCursorType.Cross)
        };
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetPosition(DrawingOverlayCanvas);
        var props = e.GetCurrentPoint(DrawingOverlayCanvas).Properties;

        if (!props.IsLeftButtonPressed) return;

        CommitInlineText();

        float imgX = (float)(pt.X / _displayScale);
        float imgY = (float)(pt.Y / _displayScale);

        if (_currentTool == EditorTool.Select)
        {
            var hit = _doc.HitTest(new SKPoint(imgX, imgY));
            if (hit != null)
            {
                _selectedElement = hit;
                _isDraggingElement = true;
                _dragStartPoint = pt;
                UpdateSelectionHighlight();
            }
            else
            {
                _selectedElement = null;
                UpdateSelectionHighlight();
            }
            return;
        }

        if (_currentTool == EditorTool.Text)
        {
            _textInsertPoint = pt;
            ShowInlineTextBox(pt);
            return;
        }

        // 几何图形绘制开始 (Rectangle, Line, Arrow)
        _isDrawing = true;
        _startPoint = pt;

        var colorBrush = new SolidColorBrush(Color.FromRgb(_currentColor.Red, _currentColor.Green, _currentColor.Blue));

        if (_currentTool == EditorTool.Rectangle)
        {
            var rect = new Avalonia.Controls.Shapes.Rectangle
            {
                Stroke = colorBrush,
                StrokeThickness = 3,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(rect, pt.X);
            Canvas.SetTop(rect, pt.Y);
            _previewControl = rect;
            DrawingOverlayCanvas.Children.Add(rect);
        }
        else if (_currentTool is EditorTool.Line or EditorTool.Arrow)
        {
            var line = new Avalonia.Controls.Shapes.Line
            {
                Stroke = colorBrush,
                StrokeThickness = 3,
                StrokeLineCap = PenLineCap.Round,
                StartPoint = pt,
                EndPoint = pt
            };
            _previewControl = line;
            DrawingOverlayCanvas.Children.Add(line);
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        var pt = e.GetPosition(DrawingOverlayCanvas);

        if (_isDraggingElement && _selectedElement != null)
        {
            float dx = (float)((pt.X - _dragStartPoint.X) / _displayScale);
            float dy = (float)((pt.Y - _dragStartPoint.Y) / _displayScale);

            if (_selectionHighlightBox != null)
            {
                var box = _selectedElement.BoundingBox;
                Canvas.SetLeft(_selectionHighlightBox, (box.Left + dx) * _displayScale - 4);
                Canvas.SetTop(_selectionHighlightBox, (box.Top + dy) * _displayScale - 4);
            }
            return;
        }

        if (!_isDrawing || _previewControl == null) return;

        if (_currentTool == EditorTool.Rectangle && _previewControl is Avalonia.Controls.Shapes.Rectangle rect)
        {
            double x = Math.Min(_startPoint.X, pt.X);
            double y = Math.Min(_startPoint.Y, pt.Y);
            double w = Math.Abs(pt.X - _startPoint.X);
            double h = Math.Abs(pt.Y - _startPoint.Y);

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = w;
            rect.Height = h;
        }
        else if ((_currentTool is EditorTool.Line or EditorTool.Arrow) && _previewControl is Avalonia.Controls.Shapes.Line line)
        {
            line.EndPoint = pt;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pt = e.GetPosition(DrawingOverlayCanvas);

        if (_isDraggingElement && _selectedElement != null)
        {
            float dx = (float)((pt.X - _dragStartPoint.X) / _displayScale);
            float dy = (float)((pt.Y - _dragStartPoint.Y) / _displayScale);

            _isDraggingElement = false;

            if (Math.Abs(dx) > 1 || Math.Abs(dy) > 1)
            {
                _doc.MoveElement(_selectedElement, dx, dy);
                RefreshDisplay();
            }
            return;
        }

        if (!_isDrawing) return;
        _isDrawing = false;

        if (_previewControl != null)
        {
            DrawingOverlayCanvas.Children.Remove(_previewControl);
            _previewControl = null;
        }

        float startX = (float)(_startPoint.X / _displayScale);
        float startY = (float)(_startPoint.Y / _displayScale);
        float endX = (float)(pt.X / _displayScale);
        float endY = (float)(pt.Y / _displayScale);

        float dist = (float)Math.Sqrt((endX - startX) * (endX - startX) + (endY - startY) * (endY - startY));
        if (dist < 4f) return;

        if (_currentTool == EditorTool.Rectangle)
        {
            _doc.AddElement(new RectangleElement
            {
                Left = Math.Min(startX, endX),
                Top = Math.Min(startY, endY),
                Right = Math.Max(startX, endX),
                Bottom = Math.Max(startY, endY),
                Color = _currentColor,
                StrokeWidth = 3f
            });
        }
        else if (_currentTool == EditorTool.Line)
        {
            _doc.AddElement(new LineElement
            {
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                Color = _currentColor,
                StrokeWidth = 3f
            });
        }
        else if (_currentTool == EditorTool.Arrow)
        {
            _doc.AddElement(new ArrowElement
            {
                StartX = startX,
                StartY = startY,
                EndX = endX,
                EndY = endY,
                Color = _currentColor,
                StrokeWidth = 3.5f
            });
        }

        RefreshDisplay();
    }

    private void ShowInlineTextBox(Point pt)
    {
        InlineTextBox.Text = string.Empty;
        Canvas.SetLeft(InlineTextBox, Math.Clamp(pt.X, 0, CanvasContainer.Width - 120));
        Canvas.SetTop(InlineTextBox, Math.Clamp(pt.Y, 0, CanvasContainer.Height - 40));
        InlineTextBox.IsVisible = true;
        InlineTextBox.Focus();
    }

    private void CommitInlineText()
    {
        if (!InlineTextBox.IsVisible) return;

        string text = InlineTextBox.Text?.Trim() ?? string.Empty;
        InlineTextBox.IsVisible = false;

        if (!string.IsNullOrEmpty(text))
        {
            float imgX = (float)(_textInsertPoint.X / _displayScale);
            float imgY = (float)(_textInsertPoint.Y / _displayScale);

            _doc.AddElement(new TextElement
            {
                X = imgX,
                Y = imgY,
                Text = text,
                Color = _currentColor,
                FontSize = 22f,
                IsBold = true
            });

            RefreshDisplay();
        }
    }

    private void OnInlineTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
    }

    private void OnInlineTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            CommitInlineText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            InlineTextBox.IsVisible = false;
            e.Handled = true;
        }
    }

    private void UpdateSelectionHighlight()
    {
        if (_selectionHighlightBox != null)
        {
            DrawingOverlayCanvas.Children.Remove(_selectionHighlightBox);
            _selectionHighlightBox = null;
        }

        if (_selectedElement != null && _currentTool == EditorTool.Select)
        {
            var box = _selectedElement.BoundingBox;
            _selectionHighlightBox = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 169, 255)),
                BorderThickness = new Thickness(1.5),
                Background = new SolidColorBrush(Color.FromArgb(30, 64, 169, 255)),
                CornerRadius = new CornerRadius(3),
                Width = box.Width * _displayScale + 8,
                Height = box.Height * _displayScale + 8,
                IsHitTestVisible = false
            };

            Canvas.SetLeft(_selectionHighlightBox, box.Left * _displayScale - 4);
            Canvas.SetTop(_selectionHighlightBox, box.Top * _displayScale - 4);
            DrawingOverlayCanvas.Children.Add(_selectionHighlightBox);
        }
    }

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
        _doc.UndoRedo.Undo();
        RefreshDisplay();
    }

    private void OnRedoClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
        _doc.UndoRedo.Redo();
        RefreshDisplay();
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            using var rendered = _doc.RenderToBitmap();
            bool copied = await _clipboardService.SetImageAsync(rendered);

            if (copied)
            {
                ToastNotificationWindow.Show(
                    "带标注图片已复制到剪贴板，可直接在微信/QQ/Word中粘贴",
                    TimeSpan.Zero,
                    "✓ 标注已复制到剪贴板");
            }

            Close();
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注复制图片异常", ex);
        }
    }

    private async void OnQuickSaveClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        string? lastDir = _settingsService?.Current.LastSaveDirectory;
        if (string.IsNullOrWhiteSpace(lastDir) || !Directory.Exists(lastDir))
        {
            // 尚未配置有效路径，平滑引导至保存设置窗口选择位置
            ToastNotificationWindow.Show("尚未记录保存路径，请先选择一次目标文件夹");
            OnSaveClick(sender, e);
            return;
        }

        try
        {
            var cur = _settingsService?.Current ?? new AppSettings();
            var fmt = Enum.TryParse<ImageSaveFormat>(cur.DefaultSaveFormat, true, out var parsedFmt) ? parsedFmt : ImageSaveFormat.Png;
            int quality = Math.Clamp(cur.DefaultSaveQuality, 10, 100);
            float scale = cur.DefaultSaveScale > 0 ? cur.DefaultSaveScale / 100f : 1.0f;
            int targetW = Math.Max(1, (int)Math.Round(_doc.OriginalImage.Width * scale));
            int targetH = Math.Max(1, (int)Math.Round(_doc.OriginalImage.Height * scale));

            var options = new ImageExportOptions
            {
                Format = fmt,
                Quality = quality,
                TargetWidth = targetW,
                TargetHeight = targetH
            };

            string extension = ImageExportOptions.GetExtension(fmt);
            string fileName = $"ShawMuOCR_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
            string filePath = System.IO.Path.Combine(lastDir, fileName);

            byte[] bytes = _doc.Encode(options);
            await System.IO.File.WriteAllBytesAsync(filePath, bytes);

            ToastNotificationWindow.Show(
                $"已保存至: {fileName}\n目录: {lastDir}",
                TimeSpan.FromSeconds(3),
                "✓ 已自动保存到上次路径");

            Close();
        }
        catch (Exception ex)
        {
            _logger?.Error("快速保存截图失败", ex);
            ToastNotificationWindow.Show($"快速保存图片失败: {ex.Message}");
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var dialog = new SaveImageDialog(_doc, _settingsService);
        var result = await dialog.ShowDialog<bool>(this);
        if (result && dialog.ShouldCloseEditor)
        {
            Close();
        }
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            var rendered = _doc.RenderToBitmap();
            var pinWin = new PinnedImageWindow(rendered, Position);
            pinWin.Show();

            Close();
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注贴图异常", ex);
        }
    }

    private async void OnOcrClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            // 将标注图层与底图合成渲染为全新的 OcrImage
            var rendered = _doc.RenderToBitmap();

            // 立即关闭截图标注窗口，为用户提供流畅即时的视觉反馈
            Close();

            if (_ocrCallback != null)
            {
                try
                {
                    // 移交图像所有权给 OCR 处理流程（由 OcrSession 接管生命周期并最终 Dispose）
                    await _ocrCallback(rendered);
                }
                catch (Exception ex)
                {
                    rendered.Dispose();
                    _logger?.Error("截图标注 OCR 识别任务执行异常", ex);
                }
            }
            else
            {
                rendered.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注点击 OCR 失败", ex);
        }
    }

    private async void OnTranslateClick(object? sender, RoutedEventArgs e)
    {
        if (_isProcessingAction) return;
        _isProcessingAction = true;

        try
        {
            CommitInlineText();

            // 将标注图层与底图合成渲染为全新的 OcrImage
            var rendered = _doc.RenderToBitmap();

            // 获取选区物理坐标（优先使用选区捕获时的物理坐标，回退至当前编辑器窗口物理位置）
            var bounds = _selectionBounds ?? new ScreenRect(
                (int)Position.X,
                (int)Position.Y,
                (int)Bounds.Width,
                (int)Bounds.Height);

            // 立即关闭截图标注编辑器窗口
            Close();

            if (_translateCallback != null)
            {
                try
                {
                    await _translateCallback(rendered, bounds);
                }
                catch (Exception ex)
                {
                    rendered.Dispose();
                    _logger?.Error("截图标注翻译回调执行异常", ex);
                }
            }
            else
            {
                rendered.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("截图标注点击翻译失败", ex);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (InlineTextBox.IsVisible) return;

        if (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnUndoClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnRedoClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                OnQuickSaveClick(sender, e);
            }
            else
            {
                OnSaveClick(sender, e);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OnCopyClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if ((e.Key is Key.Delete or Key.Back) && _selectedElement != null)
        {
            _doc.RemoveElement(_selectedElement);
            _selectedElement = null;
            RefreshDisplay();
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            ToolSelectBtn.IsChecked = true;
            e.Handled = true;
        }
        else if (e.Key == Key.T)
        {
            ToolTextBtn.IsChecked = true;
            e.Handled = true;
        }
        else if (e.Key == Key.L)
        {
            ToolLineBtn.IsChecked = true;
            e.Handled = true;
        }
        else if (e.Key == Key.A)
        {
            ToolArrowBtn.IsChecked = true;
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            ToolRectBtn.IsChecked = true;
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _doc.Dispose();
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
