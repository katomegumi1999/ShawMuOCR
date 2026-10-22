using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class OcrPageView : UserControl
{
    private ScaleTransform _scaleTransform = new(1, 1);
    private TranslateTransform _translateTransform = new(0, 0);

    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartTx;
    private double _panStartTy;
    private bool _isFitMode = true;

    public OcrPageView()
    {
        InitializeComponent();

        EnsureTransforms();

        DataContextChanged += OnDataContextChanged;

        // 监听视口尺寸变动：自适应模式下自动重算居中缩放
        ImageViewportPanel.SizeChanged += OnImageViewportSizeChanged;

        // 监听图片源变化：新图片载入时自动居中自适应或长图智能展开
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
                    }
                    _isFitMode = true;
                    ResetToFit();
                }, DispatcherPriority.Loaded);
            }
        };

        ImageViewportPanel.PointerCaptureLost += (s, e) =>
        {
            _isPanning = false;
            UpdateCursorState();
        };
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

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RequestOpenFilePicker -= OnRequestOpenFilePicker;
            vm.RequestOpenFilePicker += OnRequestOpenFilePicker;
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

        // 仅当渲染尺寸超过视口边界时才可以平移
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
            ImageViewportPanel.Cursor = CanPan() ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow);
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

        // 双击快速切换：自适应全图 vs 放大查看
        if (e.ClickCount == 2)
        {
            ToggleZoomFitOrActual();
            e.Handled = true;
            return;
        }

        // 仅当放大的图片尺寸超过视口边界时才可以移动
        if (!CanPan()) return;

        var point = e.GetCurrentPoint(ImageViewportPanel);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
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
        if (!_isPanning)
        {
            UpdateCursorState();
            return;
        }

        var currentPoint = e.GetPosition(ImageViewportPanel);
        var dx = currentPoint.X - _panStartPoint.X;
        var dy = currentPoint.Y - _panStartPoint.Y;

        if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2)
        {
            _isFitMode = false;
        }

        double targetX = _panStartTx + dx;
        double targetY = _panStartTy + dy;

        ClampOrCenter(_scaleTransform.ScaleX, ref targetX, ref targetY);

        _translateTransform.X = targetX;
        _translateTransform.Y = targetY;
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

    #endregion

    #region 缩放与居中核心算法

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

        // 宽度未超视口：严格锁死在中心 0；宽度超视口：限制在 [-maxOffsetX, maxOffsetX] 之间，防止留白
        if (renderedW <= viewW + 1.0)
        {
            tx = 0;
        }
        else
        {
            double maxOffsetX = (renderedW - viewW) / 2.0;
            tx = Math.Clamp(tx, -maxOffsetX, maxOffsetX);
        }

        // 高度未超视口：严格锁死在中心 0；高度超视口：限制在 [-maxOffsetY, maxOffsetY] 之间，防止留白
        if (renderedH <= viewH + 1.0)
        {
            ty = 0;
        }
        else
        {
            double maxOffsetY = (renderedH - viewH) / 2.0;
            ty = Math.Clamp(ty, -maxOffsetY, maxOffsetY);
        }
    }

    private void ZoomByFactor(double factor)
    {
        double oldScale = _scaleTransform.ScaleX;
        double newScale = Math.Clamp(oldScale * factor, 0.05, 30.0);
        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        _isFitMode = false;

        double ratio = newScale / oldScale;

        // 固定中心缩放：偏移量根据缩放比例同步微调；缩小或未超界时自动收拢归零
        double targetX = _translateTransform.X * ratio;
        double targetY = _translateTransform.Y * ratio;

        ClampOrCenter(newScale, ref targetX, ref targetY);

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
        _translateTransform.X = targetX;
        _translateTransform.Y = targetY;

        UpdateCursorState();
        UpdateZoomPercentText();
    }

    private void ResetToFit(bool smartExpandLong = true)
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;

        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;

        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        UpdateContainerSize(imgSize.Width, imgSize.Height);

        double scaleX = viewW / imgSize.Width;
        double scaleY = viewH / imgSize.Height;
        double fitScale = Math.Min(scaleX, scaleY);

        double targetScale = fitScale;
        double initialTx = 0;
        double initialTy = 0;

        // 当截图高度或宽度过长时（如超长纵向截图或超宽横幅截图）：
        // 若高度过长，全图缩放会导致宽度过窄无法看清内容；
        // 因此按比例适应宽度放大显示，保留原始完整分辨率绝不裁切，用户可自行拖拽上下平移查看；
        // 若宽度过长，则适应高度放大展示，用户可自行左右拖拽平移查看。
        bool isVeryTall = smartExpandLong && (imgSize.Height / imgSize.Width) > 1.8 && (scaleX > scaleY * 1.4);
        bool isVeryWide = smartExpandLong && (imgSize.Width / imgSize.Height) > 2.2 && (scaleY > scaleX * 1.4);

        if (isVeryTall)
        {
            targetScale = Math.Min(scaleX, 1.5);
            double renderedH = imgSize.Height * targetScale;
            if (renderedH > viewH)
            {
                initialTy = (renderedH - viewH) / 2.0; // 对齐顶部
            }
        }
        else if (isVeryWide)
        {
            targetScale = Math.Min(scaleY, 1.5);
            double renderedW = imgSize.Width * targetScale;
            if (renderedW > viewW)
            {
                initialTx = (renderedW - viewW) / 2.0; // 对齐左侧
            }
        }
        else
        {
            if (imgSize.Width <= viewW && imgSize.Height <= viewH)
            {
                targetScale = 1.0;
            }
            else
            {
                targetScale = fitScale;
            }
        }

        targetScale = Math.Clamp(targetScale, 0.05, 30.0);

        ClampOrCenter(targetScale, ref initialTx, ref initialTy);

        _scaleTransform.ScaleX = targetScale;
        _scaleTransform.ScaleY = targetScale;
        _translateTransform.X = initialTx;
        _translateTransform.Y = initialTy;

        _isFitMode = true;
        UpdateCursorState();
        UpdateZoomPercentText();
    }

    private void ResetToActualSize()
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;

        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;

        UpdateContainerSize(imgSize.Width, imgSize.Height);

        double scale = 1.0;
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;

        double targetX = 0;
        double targetY = 0;
        ClampOrCenter(scale, ref targetX, ref targetY);

        _translateTransform.X = targetX;
        _translateTransform.Y = targetY;

        _isFitMode = false;
        UpdateCursorState();
        UpdateZoomPercentText();
    }

    private void ToggleZoomFitOrActual()
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;

        var imgSize = imgSource.Size;
        if (imgSize.Width <= 0 || imgSize.Height <= 0) return;

        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0) return;

        UpdateContainerSize(imgSize.Width, imgSize.Height);

        double fitScale = Math.Min(viewW / imgSize.Width, viewH / imgSize.Height);

        // 若当前缩放比例接近自适应比例，则切为较大尺寸（原始 100% 或放大查看），否则重置为铺满居中
        if (Math.Abs(_scaleTransform.ScaleX - fitScale) < 0.05)
        {
            double targetScale = fitScale >= 1.0 ? fitScale * 2.0 : 1.0;
            _scaleTransform.ScaleX = targetScale;
            _scaleTransform.ScaleY = targetScale;

            double targetX = 0;
            double targetY = 0;
            ClampOrCenter(targetScale, ref targetX, ref targetY);

            _translateTransform.X = targetX;
            _translateTransform.Y = targetY;
            _isFitMode = false;
            UpdateCursorState();
            UpdateZoomPercentText();
        }
        else
        {
            ResetToFit(smartExpandLong: true);
        }
    }

    private void UpdateZoomPercentText()
    {
        if (ZoomPercentTextBlock != null)
        {
            ZoomPercentTextBlock.Text = $"{Math.Round(_scaleTransform.ScaleX * 100)}%";
        }
    }

    #endregion

    #region 悬浮控制胶囊按钮事件

    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        ZoomByFactor(1.0 / 1.2);
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        ZoomByFactor(1.2);
    }

    private void OnResetActualSizeClick(object? sender, RoutedEventArgs e)
    {
        ResetToActualSize();
    }

    private void OnResetFitClick(object? sender, RoutedEventArgs e)
    {
        var imgSource = PreviewImageControl.Source;
        if (imgSource == null) return;
        var imgSize = imgSource.Size;
        double viewW = ImageViewportPanel.Bounds.Width;
        double viewH = ImageViewportPanel.Bounds.Height;
        if (viewW <= 0 || viewH <= 0 || imgSize.Width <= 0 || imgSize.Height <= 0) return;

        double fitScale = Math.Min(viewW / imgSize.Width, viewH / imgSize.Height);
        // 若当前缩放已经非常接近全局自适应比例，则切换到长图按宽/高智能放大比例；否则自适应铺满全图
        if (Math.Abs(_scaleTransform.ScaleX - fitScale) < 0.02)
        {
            ResetToFit(smartExpandLong: true);
        }
        else
        {
            ResetToFit(smartExpandLong: false);
        }
    }

    #endregion

    #region 文件选择对话框逻辑

    private async void OnRequestOpenFilePicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要识别的图片或 PDF 文档",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("所有支持的文件 (*.png, *.jpg, *.jpeg, *.bmp, *.webp, *.pdf)")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff", "*.ico", "*.pdf" }
                },
                new FilePickerFileType("图片文件 (*.png, *.jpg, *.jpeg, *.bmp, *.webp)")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff", "*.ico" }
                },
                new FilePickerFileType("PDF 文档 (*.pdf)")
                {
                    Patterns = new[] { "*.pdf" }
                },
                new FilePickerFileType("所有文件 (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files != null && files.Count > 0 && DataContext is MainViewModel vm)
        {
            var path = files[0].Path.LocalPath;
            await vm.RecognizeFileAsync(path);
        }
    }

    #endregion
}
