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
using Avalonia.Platform.Storage;
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

namespace ShawMuOCR.Views.Components;

/// <summary>
/// 现代化全功能截图标注控件 (生命周期、自适应布局、DPI 计算、图元交互与全套两行悬浮工具栏)
/// 既可作为独立窗口 (ScreenshotEditorWindow) 的核心工作台，也可直接内嵌在截屏工具页面 (CapturePageView) 中。
/// </summary>
public partial class ScreenshotEditorControl : UserControl
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

    private ImageDocument _doc;
    private IClipboardService _clipboardService;
    private Func<OcrImage, Task>? _ocrCallback;
    private Func<OcrImage, ScreenRect, Task>? _translateCallback;
    private Func<ScreenRect, Task>? _longScreenshotCallback;
    private Func<ScreenRect, Task>? _regionRecordCallback;
    private ISettingsService? _settingsService;
    private ILoggerService? _logger;
    private ScreenRect? _selectionBounds;

    // 工具与渲染状态
    private EditorTool _currentTool = EditorTool.Select;
    private SKColor _currentColor = new SKColor(229, 57, 53); // 默认红色
    private float _currentStrokeWidth = 4f; // 默认粗细 (4px)
    private float? _customFontSize = null; // 默认为空，留空时使用默认 40f
    private double _displayScale = 1.0;

    private OcrImage? _fullScreenshot;
    private ScreenPoint? _cropOffset;
    private bool _isPrewarmInstance;

    // 窗口与嵌入模式事件
    public event EventHandler? RequestClose;
    public event EventHandler? RequestMinimize;
    public event EventHandler? RequestToggleMaximize;
    public event EventHandler<PointerPressedEventArgs>? RequestMoveDrag;
    public event EventHandler<bool>? RequestApplyDimensions;
    public event EventHandler? RequestClearImage;

    private bool _isEmbedded;
    public bool IsEmbedded
    {
        get => _isEmbedded;
        set
        {
            _isEmbedded = value;
            UpdateEmbeddedUiState();
        }
    }

    public bool ShouldCloseOnAction => !_isEmbedded;

    public ImageDocument Document => _doc;
    public double DisplayScale => _displayScale;
    public ScreenRect? SelectionBounds => _selectionBounds;
    public OcrImage? FullScreenshot => _fullScreenshot;

    public ScreenshotEditorControl() : this(new OcrImage(new SKBitmap(100, 100)))
    {
    }

    public ScreenshotEditorControl(
        OcrImage image,
        IClipboardService? clipboardService = null,
        Func<OcrImage, Task>? ocrCallback = null,
        Func<OcrImage, ScreenRect, Task>? translateCallback = null,
        ISettingsService? settingsService = null,
        ILoggerService? logger = null,
        ScreenRect? selectionBounds = null,
        OcrImage? fullScreenshot = null,
        ScreenPoint? cropOffset = null,
        bool isPrewarm = false,
        Func<ScreenRect, Task>? longScreenshotCallback = null,
        Func<ScreenRect, Task>? regionRecordCallback = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        _doc = new ImageDocument(image);
        _clipboardService = clipboardService ?? new WindowsClipboardService();
        _ocrCallback = ocrCallback;
        _translateCallback = translateCallback;
        _longScreenshotCallback = longScreenshotCallback;
        _regionRecordCallback = regionRecordCallback;
        _settingsService = settingsService;
        _logger = logger;
        _selectionBounds = selectionBounds;
        _fullScreenshot = fullScreenshot;
        _cropOffset = cropOffset;
        _isPrewarmInstance = isPrewarm;

        InitializeComponent();
        InitZoomPanTransform();

        ApplySettingsConfig();

        if (_fullScreenshot == null && RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsVisible = false;
        }

        UpdateToolControlsState();

        DimensionText.Text = $"{image.Width} × {image.Height}";
        if (EmbeddedDimensionText != null)
        {
            EmbeddedDimensionText.Text = $"{image.Width} × {image.Height}";
        }

        AddHandler(KeyDownEvent, OnControlKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnControlDrop);
        AddHandler(DragDrop.DragOverEvent, OnControlDragOver);
    }

    public void Initialize(
        OcrImage image,
        IClipboardService? clipboardService = null,
        Func<OcrImage, Task>? ocrCallback = null,
        Func<OcrImage, ScreenRect, Task>? translateCallback = null,
        ISettingsService? settingsService = null,
        ILoggerService? logger = null,
        ScreenRect? selectionBounds = null,
        OcrImage? fullScreenshot = null,
        ScreenPoint? cropOffset = null,
        bool isPrewarm = false,
        Func<ScreenRect, Task>? longScreenshotCallback = null,
        Func<ScreenRect, Task>? regionRecordCallback = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        _isPrewarmInstance = isPrewarm;
        _doc?.Dispose();
        _doc = new ImageDocument(image);
        if (clipboardService != null) _clipboardService = clipboardService;
        _ocrCallback = ocrCallback;
        _translateCallback = translateCallback;
        _longScreenshotCallback = longScreenshotCallback;
        _regionRecordCallback = regionRecordCallback;
        if (settingsService != null) _settingsService = settingsService;
        if (logger != null) _logger = logger;
        _selectionBounds = selectionBounds;
        _fullScreenshot?.Dispose();
        _fullScreenshot = fullScreenshot;
        _cropOffset = cropOffset;

        ApplySettingsConfig();

        if (RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsVisible = _fullScreenshot != null;
        }

        UpdateToolControlsState();
        DimensionText.Text = $"{image.Width} × {image.Height}";
        if (EmbeddedDimensionText != null)
        {
            EmbeddedDimensionText.Text = $"{image.Width} × {image.Height}";
        }

        ApplyDimensions(recenter: true);
        RefreshDisplay();
    }

    public void ConfigureServicesAndCallbacks(
        IOcrCoordinator coordinator,
        IClipboardService? clipboardService = null,
        ISettingsService? settingsService = null,
        ILoggerService? logger = null)
    {
        if (clipboardService != null) _clipboardService = clipboardService;
        if (settingsService != null) _settingsService = settingsService;
        if (logger != null) _logger = logger;

        _ocrCallback = async img => await coordinator.RunOcrAsync(img);
        _translateCallback = async (img, bounds) => await coordinator.TranslateImageAsync(img);
        _longScreenshotCallback = async bounds =>
        {
            if (bounds.Width > 20 && bounds.Height > 20)
            {
                await coordinator.StartLongScreenshotAsync(bounds);
            }
            else
            {
                await coordinator.StartLongScreenshotAsync();
            }
        };
        _regionRecordCallback = bounds =>
        {
            if (coordinator is ShawMuOCR.Services.OcrCoordinator oc)
            {
                oc.RegionRecordingHandler?.Invoke(bounds);
            }
            return Task.CompletedTask;
        };
    }

    private void ApplySettingsConfig()
    {
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
    }

    private void UpdateToolControlsState()
    {
        if (StrokeSlider != null)
        {
            StrokeSlider.Value = _currentStrokeWidth;
            if (StrokeValueLabel != null) StrokeValueLabel.Text = $"{_currentStrokeWidth:0}px";
        }
        if (_customFontSize.HasValue && FontSizeTextBox != null)
        {
            FontSizeTextBox.Text = _customFontSize.Value.ToString("0");
        }
    }

    public void SetImage(SKBitmap bitmap, OcrImage? fullScreenshot = null, ScreenRect? selectionBounds = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        SetImage(new OcrImage(bitmap.Copy()), fullScreenshot, selectionBounds);
    }

    public void SetImage(OcrImage image, OcrImage? fullScreenshot = null, ScreenRect? selectionBounds = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        _selectionBounds = selectionBounds;
        _doc?.Dispose();
        _doc = new ImageDocument(image);
        _fullScreenshot?.Dispose();
        _fullScreenshot = fullScreenshot;
        _selectedElement = null;

        if (RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsVisible = _fullScreenshot != null;
            RestoreFullSnapshotBtn.IsEnabled = _fullScreenshot != null;
        }

        DimensionText.Text = $"{image.Width} × {image.Height}";
        if (EmbeddedDimensionText != null)
        {
            EmbeddedDimensionText.Text = $"{image.Width} × {image.Height}";
        }
        ApplyDimensions(recenter: true);
        RefreshDisplay();
    }

    private void UpdateEmbeddedUiState()
    {
        if (HeaderTitlePanel != null) HeaderTitlePanel.IsVisible = !_isEmbedded;
        if (OpenImageBtn != null) OpenImageBtn.IsVisible = _isEmbedded;
        if (PopoutEditorBtn != null) PopoutEditorBtn.IsVisible = _isEmbedded;
        if (ClearImageBtn != null) ClearImageBtn.IsVisible = _isEmbedded;
        if (EmbeddedDimensionBadge != null) EmbeddedDimensionBadge.IsVisible = _isEmbedded;
        if (EmbeddedDimensionText != null && DimensionText != null)
        {
            EmbeddedDimensionText.Text = DimensionText.Text;
        }
        if (HeaderMinBtn != null) HeaderMinBtn.IsVisible = !_isEmbedded;
        if (HeaderMaxBtn != null) HeaderMaxBtn.IsVisible = !_isEmbedded;
        if (HeaderCloseBtn != null) HeaderCloseBtn.IsVisible = !_isEmbedded;

        if (_isEmbedded)
        {
            Background = Brushes.Transparent;
            if (WindowBorder != null)
            {
                WindowBorder.Background = Brushes.Transparent;
                WindowBorder.BorderBrush = null;
                WindowBorder.BorderThickness = new Thickness(0);
            }
            if (TopBarBorder != null)
            {
                TopBarBorder.Background = Brushes.Transparent;
                TopBarBorder.BorderBrush = null;
                TopBarBorder.BorderThickness = new Thickness(0);
            }
            if (EditorCanvasHostBorder != null)
            {
                EditorCanvasHostBorder.Background = Brushes.Transparent;
                EditorCanvasHostBorder.BorderBrush = null;
                EditorCanvasHostBorder.BorderThickness = new Thickness(0);
            }
            if (BottomToolbarBorder != null)
            {
                BottomToolbarBorder.Background = Brushes.Transparent;
                BottomToolbarBorder.BorderBrush = null;
                BottomToolbarBorder.BorderThickness = new Thickness(0);
                BottomToolbarBorder.BoxShadow = default;
            }
        }
        else
        {
            Background = new SolidColorBrush(Color.Parse("#181818"));
            if (WindowBorder != null)
            {
                WindowBorder.Background = new SolidColorBrush(Color.Parse("#181818"));
                WindowBorder.BorderBrush = new SolidColorBrush(Color.Parse("#383838"));
                WindowBorder.BorderThickness = new Thickness(1);
            }
            if (TopBarBorder != null)
            {
                TopBarBorder.Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
                TopBarBorder.BorderThickness = new Thickness(0, 0, 0, 1);
            }
            if (EditorCanvasHostBorder != null)
            {
                EditorCanvasHostBorder.Background = new SolidColorBrush(Color.Parse("#121212"));
                EditorCanvasHostBorder.BorderBrush = new SolidColorBrush(Color.Parse("#2A2A2A"));
                EditorCanvasHostBorder.BorderThickness = new Thickness(1);
            }
            if (BottomToolbarBorder != null)
            {
                BottomToolbarBorder.Background = new SolidColorBrush(Color.Parse("#1E1E1E"));
                BottomToolbarBorder.BorderBrush = new SolidColorBrush(Color.Parse("#333333"));
                BottomToolbarBorder.BorderThickness = new Thickness(1);
                BottomToolbarBorder.BoxShadow = BoxShadows.Parse("0 8 28 0 #70000000");
            }
        }

        DragDrop.SetAllowDrop(this, _isEmbedded);
    }

    private void OnPopoutEditorClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            CommitInlineText();
            using var rendered = _doc.RenderToBitmap();
            var bmpCopy = rendered?.Bitmap?.Copy();
            if (bmpCopy == null || bmpCopy.IsNull) return;

            User32.AllowSetForegroundWindow(User32.ASFW_ANY);
            var ocrImg = new OcrImage(bmpCopy);
            var editor = new ScreenshotEditorWindow(
                ocrImg,
                _clipboardService,
                _ocrCallback,
                _translateCallback,
                _settingsService,
                _logger,
                _selectionBounds ?? new ScreenRect(100, 100, bmpCopy.Width, bmpCopy.Height),
                fullScreenshot: _fullScreenshot?.Clone(),
                cropOffset: _cropOffset,
                longScreenshotCallback: _longScreenshotCallback,
                regionRecordCallback: _regionRecordCallback);

            editor.Show();
        }
        catch (Exception ex)
        {
            _logger?.Error("呼出弹窗标注窗口异常", ex);
        }
    }

    public void ResetToEmpty()
    {
        _doc?.Dispose();
        using var emptyBmp = new SKBitmap(100, 100);
        emptyBmp.Erase(SKColors.Transparent);
        _doc = new ImageDocument(new OcrImage(emptyBmp));
        _fullScreenshot?.Dispose();
        _fullScreenshot = null;
        _selectionBounds = null;
        _selectedElement = null;

        if (RestoreFullSnapshotBtn != null)
        {
            RestoreFullSnapshotBtn.IsVisible = false;
            RestoreFullSnapshotBtn.IsEnabled = false;
        }

        if (DimensionText != null) DimensionText.Text = "-- × --";
        if (EmbeddedDimensionText != null) EmbeddedDimensionText.Text = "-- × --";

        ApplyDimensions(recenter: true);
        RefreshDisplay();
    }

    private void OnClearImageClick(object? sender, RoutedEventArgs e)
    {
        ResetToEmpty();
        RequestClearImage?.Invoke(this, EventArgs.Empty);
        ToastNotificationWindow.Show("已清空当前截屏工作区图片", TimeSpan.FromSeconds(2), "🗑️ 清空工作区");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateEmbeddedUiState();

        if (_isEmbedded && EditorCanvasHostBorder != null)
        {
            EditorCanvasHostBorder.PropertyChanged += (s, args) =>
            {
                if (args.Property == BoundsProperty)
                {
                    ApplyDimensions(recenter: false);
                }
            };
        }

        ApplyDimensions(recenter: true);
        RefreshDisplay();
    }

    public void ApplyDimensions(bool recenter = false)
    {
        if (_isEmbedded)
        {
            double availW = EditorCanvasHostBorder?.Bounds.Width ?? 0;
            double availH = EditorCanvasHostBorder?.Bounds.Height ?? 0;

            if (availW <= 10 || availH <= 10)
            {
                availW = 860;
                availH = 380;
            }

            double imgW = _doc.OriginalImage.Width;
            double imgH = _doc.OriginalImage.Height;

            double maxW = Math.Max(50.0, availW - 24);
            double maxH = Math.Max(50.0, availH - 24);

            double fitScale = 1.0;
            if (imgW > maxW || imgH > maxH)
            {
                fitScale = Math.Min(maxW / imgW, maxH / imgH);
            }

            _displayScale = fitScale;

            double finalCanvasW = imgW * _displayScale;
            double finalCanvasH = imgH * _displayScale;

            if (CanvasContainer != null)
            {
                CanvasContainer.Width = finalCanvasW;
                CanvasContainer.Height = finalCanvasH;
            }
            if (BaseImageControl != null)
            {
                BaseImageControl.Width = finalCanvasW;
                BaseImageControl.Height = finalCanvasH;
            }
            if (DrawingOverlayCanvas != null)
            {
                DrawingOverlayCanvas.Width = finalCanvasW;
                DrawingOverlayCanvas.Height = finalCanvasH;
            }

            if (recenter)
            {
                ResetZoomPan();
            }

            if (_currentTool == EditorTool.Crop && _activeCropHandle != CropHandle.None)
            {
                UpdateCropVisual();
            }
            return;
        }

        RequestApplyDimensions?.Invoke(this, recenter);
    }

    public void SetCanvasAndDisplayScale(double canvasW, double canvasH, double displayScale)
    {
        _displayScale = displayScale;
        if (CanvasContainer != null)
        {
            CanvasContainer.Width = canvasW;
            CanvasContainer.Height = canvasH;
        }
        if (BaseImageControl != null)
        {
            BaseImageControl.Width = canvasW;
            BaseImageControl.Height = canvasH;
        }
        if (DrawingOverlayCanvas != null)
        {
            DrawingOverlayCanvas.Width = canvasW;
            DrawingOverlayCanvas.Height = canvasH;
        }

        if (_currentTool == EditorTool.Crop && _activeCropHandle != CropHandle.None)
        {
            UpdateCropVisual();
        }
    }

    public PixelPoint GetControlScreenPosition()
    {
        if (VisualRoot is Window win)
        {
            return win.Position;
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            var p = this.TranslatePoint(new Point(0, 0), topLevel);
            if (p.HasValue)
            {
                return topLevel.PointToScreen(p.Value);
            }
        }
        return new PixelPoint(100, 100);
    }

    private void TryClose()
    {
        if (ShouldCloseOnAction)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    private WindowState? GetHostWindowState() => (VisualRoot as Window)?.WindowState;

    private void SetHostWindowState(WindowState state)
    {
        if (VisualRoot is Window win)
        {
            win.WindowState = state;
        }
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !_isEmbedded)
        {
            RequestMoveDrag?.Invoke(this, e);
        }
    }

    private void OnHeaderMinimizeClick(object? sender, RoutedEventArgs e)
    {
        RequestMinimize?.Invoke(this, EventArgs.Empty);
    }

    private void OnHeaderMaximizeClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void OnHeaderCloseClick(object? sender, RoutedEventArgs e)
    {
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async void OnOpenImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择要标注的本地图片",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("图像文件 (*.png;*.jpg;*.jpeg;*.bmp;*.webp)")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.gif", "*.ico" }
                    }
                }
            });

            if (files != null && files.Count > 0)
            {
                string path = files[0].Path.LocalPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    var skBmp = await Task.Run(() => SKBitmap.Decode(path));
                    if (skBmp != null && !skBmp.IsNull)
                    {
                        SetImage(skBmp);
                        ToastNotificationWindow.Show(
                            $"已载入图像: {Path.GetFileName(path)}",
                            TimeSpan.FromSeconds(2.5),
                            "📁 图片已载入标注画布");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("打开图片异常", ex);
        }
    }

    private void OnControlDragOver(object? sender, DragEventArgs e)
    {
        if (!_isEmbedded)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnControlDrop(object? sender, DragEventArgs e)
    {
        if (!_isEmbedded) return;

        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var storageItems = e.DataTransfer.TryGetFiles();
        if (storageItems == null || storageItems.Length == 0) return;

        var firstFile = storageItems.FirstOrDefault();
        if (firstFile == null) return;

        string path = firstFile.Path.LocalPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var skBmp = await Task.Run(() => SKBitmap.Decode(path));
            if (skBmp != null && !skBmp.IsNull)
            {
                SetImage(skBmp);
                ToastNotificationWindow.Show(
                    $"已载入图像: {Path.GetFileName(path)}",
                    TimeSpan.FromSeconds(2.5),
                    "📁 拖拽图片已载入标注画布");
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("拖入图片载入异常", ex);
        }
    }

    private void ToggleMaximize()
    {
        if (_isEmbedded) return;
        RequestToggleMaximize?.Invoke(this, EventArgs.Empty);
        UpdateMaximizeButtonIcon();
        ReactivateWindowAndIme();
    }

    public void ReactivateWindowAndIme()
    {
        Focus();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle();
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

    public void UpdateMaximizeButtonIcon()
    {
        if (HeaderMaxBtn != null)
        {
            if (GetHostWindowState() == WindowState.Maximized)
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

    public void RefreshDisplay()
    {
        if (_doc == null) return;

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
            if (BaseImageControl != null)
            {
                BaseImageControl.Source = wb;
            }
        }
        finally
        {
            if (shouldDispose)
            {
                rendered.Dispose();
            }
        }

        if (UndoBtn != null)
        {
            UndoBtn.IsEnabled = _doc.UndoRedo.CanUndo;
        }

        if (!_isDrawing && DrawingOverlayCanvas != null)
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
        if (InlineTextBox != null)
        {
            InlineTextBox.Foreground = new SolidColorBrush(avaloniaColor);
            InlineTextBox.CaretBrush = new SolidColorBrush(avaloniaColor);
        }

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
        string text = FontSizeTextBox?.Text?.Trim() ?? string.Empty;
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

    public void SaveAnnotationSettings()
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

    public void DisposeResources()
    {
        _doc?.Dispose();
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
