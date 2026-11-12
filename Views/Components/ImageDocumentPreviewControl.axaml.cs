using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ShawMuOCR.Core.Editor;
using ShawMuOCR.ViewModels;
using SkiaSharp;

namespace ShawMuOCR.Views.Components;

public partial class ImageDocumentPreviewControl : UserControl
{
    public static readonly StyledProperty<IImage?> SourceProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, IImage?>(nameof(Source));

    public static readonly StyledProperty<bool> ShowTopBarProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, bool>(nameof(ShowTopBar), true);

    public static readonly StyledProperty<bool> ShowAnnotationToolbarProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, bool>(nameof(ShowAnnotationToolbar), false);

    public static readonly DirectProperty<ImageDocumentPreviewControl, bool> IsAnnotationToolbarVisibleProperty =
        AvaloniaProperty.RegisterDirect<ImageDocumentPreviewControl, bool>(
            nameof(IsAnnotationToolbarVisible),
            o => o.IsAnnotationToolbarVisible);

    public static readonly StyledProperty<string> HeaderTitleProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, string>(
            nameof(HeaderTitle), "图像/文档预览 (支持拖拽图片或 PDF 至此)");

    public static readonly StyledProperty<ICommand?> RotateCommandProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, ICommand?>(nameof(RotateCommand));

    public static readonly StyledProperty<ICommand?> SaveCommandProperty =
        AvaloniaProperty.Register<ImageDocumentPreviewControl, ICommand?>(nameof(SaveCommand));

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool ShowTopBar
    {
        get => GetValue(ShowTopBarProperty);
        set => SetValue(ShowTopBarProperty, value);
    }

    public bool ShowAnnotationToolbar
    {
        get => GetValue(ShowAnnotationToolbarProperty);
        set => SetValue(ShowAnnotationToolbarProperty, value);
    }

    public bool IsAnnotationToolbarVisible => ShowAnnotationToolbar && Source != null;

    public string HeaderTitle
    {
        get => GetValue(HeaderTitleProperty);
        set => SetValue(HeaderTitleProperty, value);
    }

    public ICommand? RotateCommand
    {
        get => GetValue(RotateCommandProperty);
        set => SetValue(RotateCommandProperty, value);
    }

    public ICommand? SaveCommand
    {
        get => GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    private ScaleTransform _scaleTransform = new(1, 1);
    private TranslateTransform _translateTransform = new(0, 0);

    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartTx;
    private double _panStartTy;
    private bool _isFitMode = true;

    // ==========================================
    // 标注状态与模型
    // ==========================================
    private ImageDocument? _doc;
    private EditorTool _currentTool = EditorTool.Select;
    private SKColor _currentColor = new SKColor(229, 57, 53); // 默认红色 #E53935
    private float _currentStrokeWidth = 4f; // 默认中号 4px
    private bool _isDrawing;
    private Point _startPoint;
    private Control? _previewControl;
    private readonly List<SKPoint> _activePoints = new();
    private Point _textInsertPoint;

    public ImageDocumentPreviewControl()
    {
        InitializeComponent();

        EnsureTransforms();

        ImageViewportPanel.SizeChanged += OnImageViewportSizeChanged;

        PreviewImageControl.PropertyChanged += (s, e) =>
        {
            if (e.Property == Image.SourceProperty)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (PreviewImageControl.Source != null)
                    {
                        var size = PreviewImageControl.Source.Size;
                        UpdateContainerSize(size.Width, size.Height);
                        InitImageDocumentIfNeeded();
                    }
                    _isFitMode = true;
                    ResetToFit();
                    RaisePropertyChanged(IsAnnotationToolbarVisibleProperty, !IsAnnotationToolbarVisible, IsAnnotationToolbarVisible);
                }, DispatcherPriority.Loaded);
            }
        };

        ImageViewportPanel.PointerCaptureLost += (s, e) =>
        {
            _isPanning = false;
            UpdateCursorState();
        };

        DataContextChanged += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                if (RotateCommand == null) RotateCommand = vm.RotatePreviewImageCommand;
                if (SaveCommand == null) SaveCommand = vm.SavePreviewImageCommand;
                if (Source == null && vm.PreviewImage != null) Source = vm.PreviewImage;
            }
        };

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        UpdateDrawingCanvasHitTest();
    }

    private void InitImageDocumentIfNeeded()
    {
        if (DataContext is MainViewModel vm)
        {
            var bmp = vm.GetCurrentSkBitmapCopy();
            if (bmp != null)
            {
                _doc?.Dispose();
                _doc = new ImageDocument(new ShawMuOCR.Core.OCR.OcrImage(bmp));
            }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (Source != null)
                {
                    var size = Source.Size;
                    UpdateContainerSize(size.Width, size.Height);
                    InitImageDocumentIfNeeded();
                }
                _isFitMode = true;
                ResetToFit();
                RaisePropertyChanged(IsAnnotationToolbarVisibleProperty, !IsAnnotationToolbarVisible, IsAnnotationToolbarVisible);
            }, DispatcherPriority.Loaded);
        }
        else if (change.Property == ShowAnnotationToolbarProperty)
        {
            RaisePropertyChanged(IsAnnotationToolbarVisibleProperty, !IsAnnotationToolbarVisible, IsAnnotationToolbarVisible);
            UpdateDrawingCanvasHitTest();
        }
    }

    private void EnsureTransforms()
    {
        ImageContainerCanvas.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);

        if (ImageContainerCanvas.RenderTransform is TransformGroup group)
        {
            var st = group.Children.OfType<ScaleTransform>().FirstOrDefault();
            var tt = group.Children.OfType<TranslateTransform>().FirstOrDefault();

            if (st != null) _scaleTransform = st;
            if (tt != null) _translateTransform = tt;
        }
        else
        {
            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            var newGroup = new TransformGroup();
            newGroup.Children.Add(_scaleTransform);
            newGroup.Children.Add(_translateTransform);
            ImageContainerCanvas.RenderTransform = newGroup;
        }
    }

    private void UpdateContainerSize(double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        ImageContainerCanvas.Width = w;
        ImageContainerCanvas.Height = h;
        PreviewImageControl.Width = w;
        PreviewImageControl.Height = h;
        DrawingOverlayCanvas.Width = w;
        DrawingOverlayCanvas.Height = h;
    }

    private void OnImageViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isFitMode)
        {
            ResetToFit();
        }
        else
        {
            double tx = _translateTransform.X;
            double ty = _translateTransform.Y;
            ClampOrCenter(_scaleTransform.ScaleX, ref tx, ref ty);
            _translateTransform.X = tx;
            _translateTransform.Y = ty;
            UpdateCursorState();
        }
    }

    #region 鼠标无级平移与滚轮缩放

    private bool CanPan()
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return false;
        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return false;

        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return false;

        double renderedW = imgSize.Width * _scaleTransform.ScaleX;
        double renderedH = imgSize.Height * _scaleTransform.ScaleY;

        return renderedW > viewW + 1.0 || renderedH > viewH + 1.0;
    }

    private void UpdateCursorState()
    {
        if (_isPanning)
        {
            ImageViewportPanel.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else
        {
            if (_currentTool == EditorTool.Select)
            {
                ImageViewportPanel.Cursor = CanPan() ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow);
            }
            else if (_currentTool == EditorTool.Text)
            {
                ImageViewportPanel.Cursor = new Cursor(StandardCursorType.Ibeam);
            }
            else
            {
                ImageViewportPanel.Cursor = new Cursor(StandardCursorType.Cross);
            }
        }
    }

    private void OnImageViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (PreviewImageControl.Source == null) return;

        double factor = e.Delta.Y > 0 ? 1.15 : (1.0 / 1.15);
        ZoomByFactor(factor);
        e.Handled = true;
    }

    private void OnImageViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PreviewImageControl.Source == null) return;

        if (e.ClickCount == 2)
        {
            ToggleZoomFitOrActual();
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(ImageViewportPanel);
        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed || (_currentTool == EditorTool.Select && point.Properties.IsLeftButtonPressed))
        {
            if (!CanPan()) return;
            _isPanning = true;
            _panStartPoint = e.GetPosition(ImageViewportPanel);
            _panStartTx = _translateTransform.X;
            _panStartTy = _translateTransform.Y;
            UpdateCursorState();
            e.Pointer.Capture(ImageViewportPanel);
            e.Handled = true;
        }
    }

    private void OnImageViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        var currentPoint = e.GetPosition(ImageViewportPanel);
        double dx = currentPoint.X - _panStartPoint.X;
        double dy = currentPoint.Y - _panStartPoint.Y;

        double targetTx = _panStartTx + dx;
        double targetTy = _panStartTy + dy;

        ClampOrCenter(_scaleTransform.ScaleX, ref targetTx, ref targetTy);

        _translateTransform.X = targetTx;
        _translateTransform.Y = targetTy;
        _isFitMode = false;
        e.Handled = true;
    }

    private void OnImageViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            UpdateCursorState();
            e.Handled = true;
        }
    }

    private void ZoomByFactor(double factor)
    {
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.05, 30.0);
        if (Math.Abs(oldScale - newScale) < 0.0001) return;

        double tx = _translateTransform.X;
        double ty = _translateTransform.Y;
        ClampOrCenter(newScale, ref tx, ref ty);

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
        _translateTransform.X = tx;
        _translateTransform.Y = ty;
        _isFitMode = false;

        UpdateZoomLabel();
        UpdateCursorState();
    }

    private void ClampOrCenter(double scale, ref double tx, ref double ty)
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;
        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;

        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        double renderedW = imgSize.Width * scale;
        double renderedH = imgSize.Height * scale;

        if (renderedW <= viewW)
        {
            tx = 0;
        }
        else
        {
            double maxPanX = (renderedW - viewW) / 2.0;
            tx = Math.Clamp(tx, -maxPanX, maxPanX);
        }

        if (renderedH <= viewH)
        {
            ty = 0;
        }
        else
        {
            double maxPanY = (renderedH - viewH) / 2.0;
            ty = Math.Clamp(ty, -maxPanY, maxPanY);
        }
    }

    private void ResetToFit()
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;
        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;

        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        double scaleX = viewW / imgSize.Width;
        double scaleY = viewH / imgSize.Height;
        double fitScale = Math.Min(scaleX, scaleY);
        fitScale = Math.Min(fitScale, 1.0); // 默认不超过 100%

        _scaleTransform.ScaleX = fitScale;
        _scaleTransform.ScaleY = fitScale;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;
        _isFitMode = true;

        UpdateZoomLabel();
        UpdateCursorState();
    }

    private void ResetToActualSize()
    {
        _scaleTransform.ScaleX = 1.0;
        _scaleTransform.ScaleY = 1.0;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;
        _isFitMode = false;

        UpdateZoomLabel();
        UpdateCursorState();
    }

    private void ToggleZoomFitOrActual()
    {
        if (_isFitMode || Math.Abs(_scaleTransform.ScaleX - 1.0) > 0.05)
        {
            ResetToActualSize();
        }
        else
        {
            ResetToFit();
        }
    }

    private void UpdateZoomLabel()
    {
        if (ZoomPercentTextBlock != null)
        {
            int percent = (int)Math.Round(_scaleTransform.ScaleX * 100);
            ZoomPercentTextBlock.Text = $"{percent}%";
        }
    }

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => ZoomByFactor(1.0 / 1.25);
    private void OnZoomInClick(object? sender, RoutedEventArgs e) => ZoomByFactor(1.25);
    private void OnResetActualSizeClick(object? sender, RoutedEventArgs e) => ResetToActualSize();
    private void OnResetFitClick(object? sender, RoutedEventArgs e) => ResetToFit();

    #endregion

    #region 文件拖拽进入 (仅限本预览控件)

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var storageItems = e.DataTransfer.TryGetFiles();
        if (storageItems == null || storageItems.Length == 0) return;

        var file = storageItems.FirstOrDefault();
        if (file == null) return;

        string path = file.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        e.Handled = true;

        if (DataContext is MainViewModel vm)
        {
            await vm.RecognizeFileAsync(path);
        }
    }

    #endregion

    #region 标注工具与画布交互

    private void UpdateDrawingCanvasHitTest()
    {
        if (DrawingOverlayCanvas == null) return;
        bool isAnnotating = ShowAnnotationToolbar && _currentTool != EditorTool.Select;
        DrawingOverlayCanvas.IsHitTestVisible = isAnnotating;
        UpdateCursorState();
    }

    private void OnToolRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _currentTool = tag switch
            {
                "Rect" => EditorTool.Rectangle,
                "Ellipse" => EditorTool.Ellipse,
                "Arrow" => EditorTool.Arrow,
                "Line" => EditorTool.Line,
                "Pen" => EditorTool.Pen,
                "Mosaic" => EditorTool.Mosaic,
                "Text" => EditorTool.Text,
                _ => EditorTool.Select
            };
            CommitInlineText();
            UpdateDrawingCanvasHitTest();
        }
    }

    private void OnStrokeRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tag && float.TryParse(tag, out float w))
        {
            _currentStrokeWidth = w;
        }
    }

    private void OnColorRadioClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string hex)
        {
            if (SKColor.TryParse(hex, out var color))
            {
                _currentColor = color;
            }
        }
    }

    private void OnDrawingCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (PreviewImageControl.Source == null || _currentTool == EditorTool.Select) return;

        var props = e.GetCurrentPoint(DrawingOverlayCanvas).Properties;
        if (!props.IsLeftButtonPressed) return;

        InitImageDocumentIfNeeded();
        if (_doc == null) return;

        var pt = e.GetPosition(DrawingOverlayCanvas);

        if (InlineTextBox.IsVisible)
        {
            CommitInlineText();
            return;
        }

        if (_currentTool == EditorTool.Text)
        {
            _textInsertPoint = pt;
            ShowInlineTextBox(pt);
            e.Handled = true;
            return;
        }

        _isDrawing = true;
        _startPoint = pt;
        _activePoints.Clear();
        _activePoints.Add(new SKPoint((float)pt.X, (float)pt.Y));

        e.Pointer.Capture(DrawingOverlayCanvas);

        var colorBrush = new SolidColorBrush(Color.FromArgb(_currentColor.Alpha, _currentColor.Red, _currentColor.Green, _currentColor.Blue));
        double strokeThick = _currentStrokeWidth;

        if (_currentTool == EditorTool.Rectangle)
        {
            var rect = new Rectangle
            {
                Stroke = colorBrush,
                StrokeThickness = strokeThick
            };
            Canvas.SetLeft(rect, pt.X);
            Canvas.SetTop(rect, pt.Y);
            _previewControl = rect;
            DrawingOverlayCanvas.Children.Add(rect);
        }
        else if (_currentTool == EditorTool.Ellipse)
        {
            var ellipse = new Ellipse
            {
                Stroke = colorBrush,
                StrokeThickness = strokeThick
            };
            Canvas.SetLeft(ellipse, pt.X);
            Canvas.SetTop(ellipse, pt.Y);
            _previewControl = ellipse;
            DrawingOverlayCanvas.Children.Add(ellipse);
        }
        else if (_currentTool is EditorTool.Line or EditorTool.Arrow)
        {
            var line = new Line
            {
                Stroke = colorBrush,
                StrokeThickness = strokeThick,
                StrokeLineCap = PenLineCap.Round,
                StartPoint = pt,
                EndPoint = pt
            };
            _previewControl = line;
            DrawingOverlayCanvas.Children.Add(line);
        }
        else if (_currentTool == EditorTool.Pen)
        {
            var poly = new Polyline
            {
                Stroke = colorBrush,
                StrokeThickness = strokeThick,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Points = new Points { pt }
            };
            _previewControl = poly;
            DrawingOverlayCanvas.Children.Add(poly);
        }
        else if (_currentTool == EditorTool.Mosaic)
        {
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(180, 100, 100, 100)),
                StrokeThickness = Math.Max(12.0, strokeThick * 2.5),
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Points = new Points { pt }
            };
            _previewControl = poly;
            DrawingOverlayCanvas.Children.Add(poly);
        }

        e.Handled = true;
    }

    private void OnDrawingCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDrawing) return;

        var pt = e.GetPosition(DrawingOverlayCanvas);
        _activePoints.Add(new SKPoint((float)pt.X, (float)pt.Y));

        if (_previewControl is Rectangle rect)
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
        else if (_previewControl is Ellipse ellipse)
        {
            double x = Math.Min(_startPoint.X, pt.X);
            double y = Math.Min(_startPoint.Y, pt.Y);
            double w = Math.Abs(pt.X - _startPoint.X);
            double h = Math.Abs(pt.Y - _startPoint.Y);
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            ellipse.Width = w;
            ellipse.Height = h;
        }
        else if (_previewControl is Line line)
        {
            line.EndPoint = pt;
        }
        else if (_previewControl is Polyline poly)
        {
            poly.Points.Add(pt);
        }

        e.Handled = true;
    }

    private void OnDrawingCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDrawing || _doc == null) return;
        _isDrawing = false;
        e.Pointer.Capture(null);

        var endPt = e.GetPosition(DrawingOverlayCanvas);

        if (_previewControl != null)
        {
            DrawingOverlayCanvas.Children.Remove(_previewControl);
            _previewControl = null;
        }

        IImageElement? element = null;

        if (_currentTool == EditorTool.Rectangle)
        {
            float x1 = (float)Math.Min(_startPoint.X, endPt.X);
            float y1 = (float)Math.Min(_startPoint.Y, endPt.Y);
            float x2 = (float)Math.Max(_startPoint.X, endPt.X);
            float y2 = (float)Math.Max(_startPoint.Y, endPt.Y);
            if (x2 - x1 >= 3 && y2 - y1 >= 3)
            {
                element = new RectangleElement
                {
                    Left = x1,
                    Top = y1,
                    Right = x2,
                    Bottom = y2,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                };
            }
        }
        else if (_currentTool == EditorTool.Ellipse)
        {
            float x1 = (float)Math.Min(_startPoint.X, endPt.X);
            float y1 = (float)Math.Min(_startPoint.Y, endPt.Y);
            float x2 = (float)Math.Max(_startPoint.X, endPt.X);
            float y2 = (float)Math.Max(_startPoint.Y, endPt.Y);
            if (x2 - x1 >= 3 && y2 - y1 >= 3)
            {
                element = new EllipseElement
                {
                    Left = x1,
                    Top = y1,
                    Right = x2,
                    Bottom = y2,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                };
            }
        }
        else if (_currentTool == EditorTool.Line)
        {
            float dist = (float)Math.Sqrt(Math.Pow(endPt.X - _startPoint.X, 2) + Math.Pow(endPt.Y - _startPoint.Y, 2));
            if (dist >= 3)
            {
                element = new LineElement
                {
                    StartX = (float)_startPoint.X,
                    StartY = (float)_startPoint.Y,
                    EndX = (float)endPt.X,
                    EndY = (float)endPt.Y,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                };
            }
        }
        else if (_currentTool == EditorTool.Arrow)
        {
            float dist = (float)Math.Sqrt(Math.Pow(endPt.X - _startPoint.X, 2) + Math.Pow(endPt.Y - _startPoint.Y, 2));
            if (dist >= 4)
            {
                element = new ArrowElement
                {
                    StartX = (float)_startPoint.X,
                    StartY = (float)_startPoint.Y,
                    EndX = (float)endPt.X,
                    EndY = (float)endPt.Y,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                };
            }
        }
        else if (_currentTool == EditorTool.Pen)
        {
            if (_activePoints.Count >= 2)
            {
                var penElem = new PenElement
                {
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                };
                penElem.Points.AddRange(_activePoints);
                element = penElem;
            }
        }
        else if (_currentTool == EditorTool.Mosaic)
        {
            if (_activePoints.Count >= 2)
            {
                var mosaicElem = new MosaicElement
                {
                    StrokeWidth = Math.Max(12f, _currentStrokeWidth * 2.5f)
                };
                mosaicElem.Points.AddRange(_activePoints);
                element = mosaicElem;
            }
        }

        if (element != null)
        {
            _doc.AddElement(element);
            ApplyAnnotationToComposedBitmap();
        }

        e.Handled = true;
    }

    private void ApplyAnnotationToComposedBitmap()
    {
        if (_doc == null) return;

        using var rendered = _doc.RenderToBitmap();
        if (rendered?.Bitmap != null && !rendered.Bitmap.IsNull)
        {
            if (DataContext is MainViewModel vm)
            {
                vm.UpdateAnnotatedBitmap(rendered.Bitmap);
            }
        }
    }

    private void ShowInlineTextBox(Point pt)
    {
        Canvas.SetLeft(InlineTextBox, pt.X);
        Canvas.SetTop(InlineTextBox, pt.Y);
        InlineTextBox.Text = string.Empty;
        InlineTextBox.IsVisible = true;
        InlineTextBox.Focus();
    }

    private void CommitInlineText()
    {
        if (!InlineTextBox.IsVisible || _doc == null) return;

        string text = InlineTextBox.Text?.Trim() ?? string.Empty;
        InlineTextBox.IsVisible = false;

        if (!string.IsNullOrEmpty(text))
        {
            float fontSize = Math.Max(14f, _currentStrokeWidth * 5.5f);
            var textElem = new TextElement
            {
                X = (float)_textInsertPoint.X,
                Y = (float)_textInsertPoint.Y,
                Text = text,
                Color = _currentColor,
                FontSize = fontSize
            };
            _doc.AddElement(textElem);
            ApplyAnnotationToComposedBitmap();
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

    private void OnUndoClick(object? sender, RoutedEventArgs e)
    {
        if (_doc != null && _doc.UndoRedo.CanUndo)
        {
            _doc.UndoRedo.Undo();
            ApplyAnnotationToComposedBitmap();
        }
    }

    private void OnClearAnnotationsClick(object? sender, RoutedEventArgs e)
    {
        if (_doc != null && _doc.Elements.Count > 0)
        {
            _doc.ClearElements();
            ApplyAnnotationToComposedBitmap();
        }
    }

    #endregion
}
