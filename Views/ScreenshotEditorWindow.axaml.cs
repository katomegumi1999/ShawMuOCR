using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Windows.Clipboard;
using ShawMuOCR.Windows.Win32;
using SkiaSharp;

namespace ShawMuOCR.Views;

/// <summary>
/// 现代化全功能截图标注主窗口 (生命周期、自适应布局、DPI 计算与工具栏状态管理)
/// </summary>
public partial class ScreenshotEditorWindow : Window
{
    private enum EditorTool
    {
        Select,
        Crop,
        Text,
        Line,
        Arrow,
        Rectangle,
        Ellipse,
        Pen,
        Mosaic,
        Eraser
    }

    private enum TransformMode
    {
        None,
        Move,
        ResizeNW,
        ResizeNE,
        ResizeSE,
        ResizeSW,
        Rotate
    }

    private enum CropHandle
    {
        None,
        Inside,
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left
    }

    private readonly ImageDocument _doc;
    private readonly IClipboardService _clipboardService;
    private readonly Func<OcrImage, Task>? _ocrCallback;
    private readonly Func<OcrImage, ScreenRect, Task>? _translateCallback;
    private readonly ISettingsService? _settingsService;
    private readonly ILoggerService? _logger;
    private readonly ScreenRect? _selectionBounds;

    // 工具与渲染状态
    private EditorTool _currentTool = EditorTool.Select;
    private SKColor _currentColor = new SKColor(229, 57, 53); // 默认红色
    private float _currentStrokeWidth = 4f; // 默认粗细 (4px)
    private float? _customFontSize = null; // 默认为空，留空时使用默认 40f
    private double _displayScale = 1.0;

    private OcrImage? _fullScreenshot;
    private ScreenPoint? _cropOffset;
    private readonly bool _isPrewarmInstance;
    private static bool s_isPrewarmed;

    public static bool IsPrewarmed => s_isPrewarmed;
    public static void ResetPrewarmedForTesting() => s_isPrewarmed = false;

    /// <summary>
    /// 在应用启动后后台空闲时段预热截图标注窗口，提前完成 XAML 解析、控件树实例化、Win32 HWND 生成与 Skia 渲染管线就绪，彻底消除首次截图呼出延迟。
    /// </summary>
    public static void Prewarm(ILoggerService? logger = null, string scene = "后台预热")
    {
        if (s_isPrewarmed) return;

        if (Avalonia.Application.Current == null)
        {
            return;
        }

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Prewarm(logger, scene), DispatcherPriority.Background);
            return;
        }

        if (s_isPrewarmed) return;
        s_isPrewarmed = true;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            logger?.Info($"[{scene}] 正在后台异步预热截图标注编辑器窗口 (完成 XAML 解析、控件树实例化、Win32 HWND 生成与 Skia 渲染管线就绪)...");

            using var dummyBmp = new SKBitmap(32, 32);
            using var dummyImg = new OcrImage(dummyBmp);
            var window = new ScreenshotEditorWindow(dummyImg, isPrewarm: true);

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    window.Close();
                    sw.Stop();
                    logger?.Info($"[{scene}] 截图标注编辑器无感预热完成 (完成 XAML 解析、控件树与 Skia 绘图管线实例化)，耗时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex)
                {
                    logger?.Warn($"[{scene}] 截图标注编辑器窗口清理异常: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            logger?.Warn($"[{scene}] 截图标注编辑器窗口预热异常: {ex.Message}");
        }
    }

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
        ScreenRect? selectionBounds = null,
        OcrImage? fullScreenshot = null,
        ScreenPoint? cropOffset = null,
        bool isPrewarm = false)
    {
        ArgumentNullException.ThrowIfNull(image);

        _isPrewarmInstance = isPrewarm;
        _doc = new ImageDocument(image);
        _clipboardService = clipboardService ?? new WindowsClipboardService();
        _ocrCallback = ocrCallback;
        _translateCallback = translateCallback;
        _settingsService = settingsService;
        _logger = logger;
        _selectionBounds = selectionBounds;
        _fullScreenshot = fullScreenshot;
        _cropOffset = cropOffset;

        if (_settingsService?.Current != null)
        {
            var s = _settingsService.Current;
            if (s.AnnotationStrokeWidth >= 1f && s.AnnotationStrokeWidth <= 32f)
            {
                _currentStrokeWidth = s.AnnotationStrokeWidth;
            }
            if (s.AnnotationFontSize >= 8f && s.AnnotationFontSize <= 200f)
            {
                _customFontSize = s.AnnotationFontSize;
            }
        }

        InitializeComponent();

        InitZoomPanTransform();

        if (_fullScreenshot == null && RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsVisible = false;
        }

        if (StrokeSlider != null)
        {
            StrokeSlider.Value = _currentStrokeWidth;
            if (StrokeValueLabel != null) StrokeValueLabel.Text = $"{_currentStrokeWidth:0}px";
        }
        if (_customFontSize.HasValue && FontSizeTextBox != null)
        {
            FontSizeTextBox.Text = _customFontSize.Value.ToString("0");
        }

        DimensionText.Text = $"{image.Width} × {image.Height}";

        if (_isPrewarmInstance)
        {
            ShowInTaskbar = false;
            Opacity = 0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(-32000, -32000);
            Width = 100;
            Height = 100;
        }
        else
        {
            ApplyDimensions(recenter: true);
        }

        RefreshDisplay();

        PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                UpdateMaximizeButtonIcon();
                ApplyDimensions(recenter: false);
                RefreshDisplay();
            }
        };

        Closed += (s, e) => SaveAnnotationSettings();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private Screen? GetTargetScreen()
    {
        try
        {
            if (_selectionBounds.HasValue && Screens != null)
            {
                var centerPt = new PixelPoint(
                    _selectionBounds.Value.X + _selectionBounds.Value.Width / 2,
                    _selectionBounds.Value.Y + _selectionBounds.Value.Height / 2);
                var screen = Screens.ScreenFromPoint(centerPt);
                if (screen != null) return screen;
            }
            return Screens?.Primary ?? Screens?.All.FirstOrDefault();
        }
        catch
        {
            return Screens?.Primary;
        }
    }

    private double GetScreenScaling()
    {
        var targetScreen = GetTargetScreen();
        if (targetScreen != null && targetScreen.Scaling > 0)
        {
            return targetScreen.Scaling;
        }

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

        return 1.0;
    }

    private void ApplyDimensions(bool recenter = false)
    {
        var targetScreen = GetTargetScreen();
        double scaling = GetScreenScaling();
        if (scaling <= 0) scaling = 1.0;

        int workX = targetScreen?.WorkingArea.X ?? 0;
        int workY = targetScreen?.WorkingArea.Y ?? 0;
        double workingW = targetScreen?.WorkingArea.Width ?? (1920 * scaling);
        double workingH = targetScreen?.WorkingArea.Height ?? (1080 * scaling);

        // 预留出顶栏 (32px)、工具栏与外边距 (约 150px) 以及工具栏完整最小宽度 (960px)
        const double ChromeExtraHeight = 150.0;
        const double ChromeExtraWidth = 32.0;

        double availDipW;
        double availDipH;

        if (WindowState == WindowState.Maximized)
        {
            double maxWinW = Bounds.Width > 100 ? Bounds.Width : (workingW / scaling);
            double maxWinH = Bounds.Height > 100 ? Bounds.Height : (workingH / scaling);
            availDipW = Math.Max(100.0, maxWinW - ChromeExtraWidth);
            availDipH = Math.Max(100.0, maxWinH - ChromeExtraHeight);
        }
        else
        {
            availDipW = Math.Max(100.0, (workingW / scaling) - ChromeExtraWidth);
            availDipH = Math.Max(100.0, (workingH / scaling) - ChromeExtraHeight);
        }

        double maxW = availDipW * 0.94;
        double maxH = availDipH * 0.92;

        double imgW = _doc.OriginalImage.Width;
        double imgH = _doc.OriginalImage.Height;

        double dipW = imgW / scaling;
        double dipH = imgH / scaling;

        double fitScale = 1.0;
        if (dipW > maxW || dipH > maxH)
        {
            fitScale = Math.Min(maxW / dipW, maxH / dipH);
        }

        _displayScale = (1.0 / scaling) * fitScale;

        double finalCanvasW = imgW * _displayScale;
        double finalCanvasH = imgH * _displayScale;

        CanvasContainer.Width = finalCanvasW;
        CanvasContainer.Height = finalCanvasH;

        BaseImageControl.Width = finalCanvasW;
        BaseImageControl.Height = finalCanvasH;

        DrawingOverlayCanvas.Width = finalCanvasW;
        DrawingOverlayCanvas.Height = finalCanvasH;

        if (WindowState != WindowState.Maximized)
        {
            // 确保窗口宽度保底 960px，彻底保证两行操作区的全部按钮（OCR、翻译、另存为、复制、置顶）完整可见
            double totalDipW = Math.Clamp(Math.Max(finalCanvasW + ChromeExtraWidth, 960.0), 960.0, workingW / scaling);
            double totalDipH = Math.Clamp(Math.Max(finalCanvasH + ChromeExtraHeight, 520.0), 500.0, workingH / scaling);

            Width = totalDipW;
            Height = totalDipH;

            if (recenter)
            {
                int totalPxW = (int)Math.Round(totalDipW * scaling);
                int totalPxH = (int)Math.Round(totalDipH * scaling);

                int posX = workX + Math.Max(0, ((int)workingW - totalPxW) / 2);
                int posY = workY + Math.Max(0, ((int)workingH - totalPxH) / 2);

                Position = new PixelPoint(posX, posY);

                try
                {
                    var handle = TryGetPlatformHandle();
                    if (handle != null && handle.Handle != IntPtr.Zero)
                    {
                        User32.SetWindowPos(
                            handle.Handle,
                            IntPtr.Zero,
                            posX,
                            posY,
                            totalPxW,
                            totalPxH,
                            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
                    }
                }
                catch { }
            }
        }

        ResetZoomPan();

        UpdateSelectionHighlight();
        if (_currentTool == EditorTool.Crop)
        {
            UpdateCropVisual();
        }
    }

    private void RefreshDisplay()
    {
        OcrImage rendered;
        bool shouldDispose = false;

        if (_doc.Elements.Count == 0 && _editingTextElement == null)
        {
            rendered = _doc.OriginalImage;
        }
        else
        {
            rendered = _doc.RenderToBitmap(skipElement: _editingTextElement);
            shouldDispose = true;
        }

        try
        {
            var wb = ConvertToBitmap(rendered);
            BaseImageControl.Source = wb;
        }
        finally
        {
            if (shouldDispose)
            {
                rendered.Dispose();
            }
        }

        UndoBtn.IsEnabled = _doc.UndoRedo.CanUndo;

        if (!_isDrawing)
        {
            var strayShapes = DrawingOverlayCanvas.Children.OfType<Avalonia.Controls.Shapes.Shape>().ToList();
            foreach (var s in strayShapes)
            {
                DrawingOverlayCanvas.Children.Remove(s);
            }
            _previewControl = null;
        }

        UpdateSelectionHighlight();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnHeaderMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnHeaderMaximizeClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void OnHeaderCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateMaximizeButtonIcon();
        ReactivateWindowAndIme();
    }

    public void ReactivateWindowAndIme()
    {
        Activate();
        Focus();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = TryGetPlatformHandle();
                if (handle != null && handle.Handle != IntPtr.Zero)
                {
                    User32.SetForegroundWindow(handle.Handle);
                    User32.SetActiveWindow(handle.Handle);
                    User32.SetFocus(handle.Handle);

                    IntPtr hImc = Imm32.ImmGetContext(handle.Handle);
                    if (hImc != IntPtr.Zero)
                    {
                        Imm32.ImmSetOpenStatus(hImc, true);
                        if (Imm32.ImmGetConversionStatus(hImc, out uint conversion, out uint sentence))
                        {
                            Imm32.ImmSetConversionStatus(hImc, conversion | Imm32.IME_CMODE_NATIVE, sentence);
                        }
                        Imm32.ImmReleaseContext(handle.Handle, hImc);
                    }
                }
            }
        }
        catch { }
    }

    private void UpdateMaximizeButtonIcon()
    {
        if (HeaderMaxBtn != null)
        {
            if (WindowState == WindowState.Maximized)
            {
                HeaderMaxBtn.Content = "🗗";
                ToolTip.SetTip(HeaderMaxBtn, "向下还原");
            }
            else
            {
                HeaderMaxBtn.Content = "🗖";
                ToolTip.SetTip(HeaderMaxBtn, "最大化");
            }
        }
    }

    private float GetCurrentFontSize() => _customFontSize ?? 40f;

    // ==========================================
    // 工具切换 (ToggleButtons, 支持反选返回 Select 模式)
    // ==========================================
    private void OnToolToggleClick(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();

        var clickedBtn = sender as ToggleButton;
        bool isNowChecked = clickedBtn?.IsChecked == true;

        if (_currentTool == EditorTool.Crop && clickedBtn != ToolCropBtn)
        {
            CancelCrop();
        }

        // 取消其他所有工具按钮的选中状态
        ToolTextBtn.IsChecked = (clickedBtn == ToolTextBtn && isNowChecked);
        ToolLineBtn.IsChecked = (clickedBtn == ToolLineBtn && isNowChecked);
        ToolArrowBtn.IsChecked = (clickedBtn == ToolArrowBtn && isNowChecked);
        ToolRectBtn.IsChecked = (clickedBtn == ToolRectBtn && isNowChecked);
        ToolEllipseBtn.IsChecked = (clickedBtn == ToolEllipseBtn && isNowChecked);
        ToolPenBtn.IsChecked = (clickedBtn == ToolPenBtn && isNowChecked);
        ToolMosaicBtn.IsChecked = (clickedBtn == ToolMosaicBtn && isNowChecked);
        ToolEraserBtn.IsChecked = (clickedBtn == ToolEraserBtn && isNowChecked);
        ToolCropBtn.IsChecked = (clickedBtn == ToolCropBtn && isNowChecked);

        if (!isNowChecked)
        {
            if (_currentTool == EditorTool.Crop)
            {
                CancelCrop();
            }
            _currentTool = EditorTool.Select;
        }
        else
        {
            if (clickedBtn == ToolTextBtn) _currentTool = EditorTool.Text;
            else if (clickedBtn == ToolLineBtn) _currentTool = EditorTool.Line;
            else if (clickedBtn == ToolArrowBtn) _currentTool = EditorTool.Arrow;
            else if (clickedBtn == ToolRectBtn) _currentTool = EditorTool.Rectangle;
            else if (clickedBtn == ToolEllipseBtn) _currentTool = EditorTool.Ellipse;
            else if (clickedBtn == ToolPenBtn) _currentTool = EditorTool.Pen;
            else if (clickedBtn == ToolMosaicBtn) _currentTool = EditorTool.Mosaic;
            else if (clickedBtn == ToolEraserBtn) _currentTool = EditorTool.Eraser;
            else if (clickedBtn == ToolCropBtn)
            {
                if (_zoomFactor > 1.001)
                {
                    ResetZoomPan();
                }
                _currentTool = EditorTool.Crop;
                StartCrop();
            }
        }

        if (_currentTool != EditorTool.Eraser && EraserIndicatorBorder != null)
        {
            EraserIndicatorBorder.IsVisible = false;
        }

        UpdateCursor();

        if (_currentTool != EditorTool.Select)
        {
            _selectedElement = null;
            UpdateSelectionHighlight();
        }
    }

    // ==========================================
    // 粗细、颜色、文字大小控制
    // ==========================================
    private void OnStrokeRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tagStr && float.TryParse(tagStr, out float stroke))
        {
            _currentStrokeWidth = stroke;

            if (StrokeSlider != null && Math.Abs(StrokeSlider.Value - stroke) > 0.1)
            {
                StrokeSlider.Value = stroke;
            }
            if (StrokeValueLabel != null)
            {
                StrokeValueLabel.Text = $"{stroke:0}px";
            }

            if (_selectedElement != null)
            {
                if (_selectedElement is RectangleElement r) r.StrokeWidth = _currentStrokeWidth;
                else if (_selectedElement is LineElement l) l.StrokeWidth = _currentStrokeWidth;
                else if (_selectedElement is ArrowElement a) a.StrokeWidth = _currentStrokeWidth;
                else if (_selectedElement is PenElement p) p.StrokeWidth = _currentStrokeWidth;
                else if (_selectedElement is MosaicElement m) m.StrokeWidth = _currentStrokeWidth;

                RefreshDisplay();
                UpdateSelectionHighlight();
            }
        }
    }

    private void OnStrokeSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        float stroke = (float)Math.Round(e.NewValue);
        if (Math.Abs(stroke - _currentStrokeWidth) < 0.01f)
        {
            return;
        }

        _currentStrokeWidth = stroke;

        if (StrokeValueLabel != null)
        {
            StrokeValueLabel.Text = $"{stroke:0}px";
        }

        // 同步 2/4/8/14 预设单选钮状态
        if (StrokeThinBtn != null && StrokeMediumBtn != null && StrokeThickBtn != null && StrokeExtraBtn != null)
        {
            StrokeThinBtn.IsChecked = Math.Abs(stroke - 2f) < 0.1f;
            StrokeMediumBtn.IsChecked = Math.Abs(stroke - 4f) < 0.1f;
            StrokeThickBtn.IsChecked = Math.Abs(stroke - 8f) < 0.1f;
            StrokeExtraBtn.IsChecked = Math.Abs(stroke - 14f) < 0.1f;
        }

        if (_selectedElement != null)
        {
            if (_selectedElement is RectangleElement r) r.StrokeWidth = _currentStrokeWidth;
            else if (_selectedElement is LineElement l) l.StrokeWidth = _currentStrokeWidth;
            else if (_selectedElement is ArrowElement a) a.StrokeWidth = _currentStrokeWidth;
            else if (_selectedElement is PenElement p) p.StrokeWidth = _currentStrokeWidth;
            else if (_selectedElement is MosaicElement m) m.StrokeWidth = _currentStrokeWidth;
            else if (_selectedElement is TextElement t)
            {
                t.StrokeWidth = _currentStrokeWidth;
                t.IsBold = _currentStrokeWidth > 2.5f;
            }

            RefreshDisplay();
            UpdateSelectionHighlight();
        }

        // 避免高频拖拽每像素写磁盘引发卡顿，粗细设置会在 Window.Closed 及动作执行时统一持久化保存
    }

    private void OnColorPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string hexStr && SKColor.TryParse(hexStr, out var skColor))
        {
            ApplyColor(skColor);
        }
    }

    private void OnPaletteColorChanged(object? sender, ColorChangedEventArgs e)
    {
        var col = e.NewColor;
        var skColor = new SKColor(col.R, col.G, col.B, col.A);
        ApplyColor(skColor);
    }

    private void ApplyColor(SKColor color)
    {
        _currentColor = color;

        var avaloniaColor = Color.FromArgb(_currentColor.Alpha, _currentColor.Red, _currentColor.Green, _currentColor.Blue);
        InlineTextBox.Foreground = new SolidColorBrush(avaloniaColor);
        InlineTextBox.CaretBrush = new SolidColorBrush(avaloniaColor);

        if (_selectedElement != null)
        {
            if (_selectedElement is RectangleElement r) r.Color = _currentColor;
            else if (_selectedElement is EllipseElement el) el.Color = _currentColor;
            else if (_selectedElement is LineElement l) l.Color = _currentColor;
            else if (_selectedElement is ArrowElement a) a.Color = _currentColor;
            else if (_selectedElement is TextElement t) t.Color = _currentColor;
            else if (_selectedElement is PenElement p) p.Color = _currentColor;
            else if (_selectedElement is WatermarkElement wm) wm.Color = _currentColor;

            RefreshDisplay();
        }
    }

    private void OnFontSizeTextChanged(object? sender, TextChangedEventArgs e)
    {
        string text = FontSizeTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            _customFontSize = null;
        }
        else if (float.TryParse(text, out float fs) && fs >= 1f)
        {
            _customFontSize = fs;
        }

        if (_selectedElement is TextElement textElem)
        {
            textElem.FontSize = GetCurrentFontSize();
            RefreshDisplay();
            UpdateSelectionHighlight();
        }

        SaveAnnotationSettings();
    }

    private void SaveAnnotationSettings()
    {
        if (_isPrewarmInstance || _settingsService == null) return;
        try
        {
            var cur = _settingsService.Current.Clone();
            cur.AnnotationStrokeWidth = _currentStrokeWidth;
            cur.AnnotationFontSize = GetCurrentFontSize();
            _settingsService.Save(cur);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _doc.Dispose();
        _fullScreenshot?.Dispose();
        _fullScreenshot = null;
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
