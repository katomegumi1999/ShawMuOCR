using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SkiaSharp;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 二次裁切交互与视觉渲染 (Crop) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    // 裁切状态
    private Rect _cropRectDIP;
    private CropHandle _activeCropHandle = CropHandle.None;
    private bool _isCroppingDrag;
    private Point _cropDragStartPoint;
    private Rect _initialCropRectDIP;
    private Canvas? _cropVisualCanvas;

    private void StartCrop()
    {
        _selectedElement = null;
        UpdateSelectionHighlight();

        double w = CanvasContainer.Width;
        double h = CanvasContainer.Height;
        _cropRectDIP = new Rect(0, 0, w, h);

        if (TopCropActionBar != null)
        {
            TopCropActionBar.IsVisible = true;
            int pxW = Math.Max(1, (int)Math.Round(w / _displayScale));
            int pxH = Math.Max(1, (int)Math.Round(h / _displayScale));
            TopCropDimensionText.Text = $"{pxW} × {pxH}";
        }

        UpdateCropVisual();
    }

    private CropHandle HitTestCropHandles(Point pt)
    {
        var r = _cropRectDIP;
        const double tol = 12.0;

        if (Math.Abs(pt.X - r.Left) <= tol && Math.Abs(pt.Y - r.Top) <= tol) return CropHandle.TopLeft;
        if (Math.Abs(pt.X - r.Right) <= tol && Math.Abs(pt.Y - r.Top) <= tol) return CropHandle.TopRight;
        if (Math.Abs(pt.X - r.Right) <= tol && Math.Abs(pt.Y - r.Bottom) <= tol) return CropHandle.BottomRight;
        if (Math.Abs(pt.X - r.Left) <= tol && Math.Abs(pt.Y - r.Bottom) <= tol) return CropHandle.BottomLeft;

        if (Math.Abs(pt.Y - r.Top) <= tol && pt.X >= r.Left && pt.X <= r.Right) return CropHandle.Top;
        if (Math.Abs(pt.Y - r.Bottom) <= tol && pt.X >= r.Left && pt.X <= r.Right) return CropHandle.Bottom;
        if (Math.Abs(pt.X - r.Left) <= tol && pt.Y >= r.Top && pt.Y <= r.Bottom) return CropHandle.Left;
        if (Math.Abs(pt.X - r.Right) <= tol && pt.Y >= r.Top && pt.Y <= r.Bottom) return CropHandle.Right;

        if (r.Contains(pt)) return CropHandle.Inside;

        return CropHandle.None;
    }

    private void UpdateCropCursor(Point pt)
    {
        var hit = HitTestCropHandles(pt);
        DrawingOverlayCanvas.Cursor = hit switch
        {
            CropHandle.TopLeft or CropHandle.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
            CropHandle.TopRight or CropHandle.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
            CropHandle.Top or CropHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
            CropHandle.Left or CropHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
            CropHandle.Inside => new Cursor(StandardCursorType.SizeAll),
            _ => new Cursor(StandardCursorType.Cross)
        };
    }

    private void UpdateCropVisual()
    {
        if (_cropVisualCanvas != null)
        {
            DrawingOverlayCanvas.Children.Remove(_cropVisualCanvas);
            _cropVisualCanvas = null;
        }

        if (_currentTool != EditorTool.Crop) return;

        double canvasW = CanvasContainer.Width;
        double canvasH = CanvasContainer.Height;

        var r = new Rect(
            Math.Clamp(_cropRectDIP.Left, 0, canvasW),
            Math.Clamp(_cropRectDIP.Top, 0, canvasH),
            Math.Clamp(_cropRectDIP.Width, 0, canvasW - _cropRectDIP.Left),
            Math.Clamp(_cropRectDIP.Height, 0, canvasH - _cropRectDIP.Top));

        _cropVisualCanvas = new Canvas
        {
            Width = canvasW,
            Height = canvasH,
            ClipToBounds = false,
            IsHitTestVisible = true
        };

        var maskBrush = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));

        // 1. 四周暗色遮罩
        if (r.Top > 0)
        {
            var topMask = new Border { Width = canvasW, Height = r.Top, Background = maskBrush, IsHitTestVisible = false };
            Canvas.SetLeft(topMask, 0); Canvas.SetTop(topMask, 0);
            _cropVisualCanvas.Children.Add(topMask);
        }
        if (r.Bottom < canvasH)
        {
            var btmMask = new Border { Width = canvasW, Height = canvasH - r.Bottom, Background = maskBrush, IsHitTestVisible = false };
            Canvas.SetLeft(btmMask, 0); Canvas.SetTop(btmMask, r.Bottom);
            _cropVisualCanvas.Children.Add(btmMask);
        }
        if (r.Left > 0 && r.Height > 0)
        {
            var leftMask = new Border { Width = r.Left, Height = r.Height, Background = maskBrush, IsHitTestVisible = false };
            Canvas.SetLeft(leftMask, 0); Canvas.SetTop(leftMask, r.Top);
            _cropVisualCanvas.Children.Add(leftMask);
        }
        if (r.Right < canvasW && r.Height > 0)
        {
            var rightMask = new Border { Width = canvasW - r.Right, Height = r.Height, Background = maskBrush, IsHitTestVisible = false };
            Canvas.SetLeft(rightMask, 0 + r.Right); Canvas.SetTop(rightMask, r.Top);
            _cropVisualCanvas.Children.Add(rightMask);
        }

        // 2. 裁切主矩形边框
        var cropBorder = new Border
        {
            Width = r.Width,
            Height = r.Height,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(cropBorder, r.Left);
        Canvas.SetTop(cropBorder, r.Top);
        _cropVisualCanvas.Children.Add(cropBorder);

        // 3. 九宫格辅助线
        double thirdW = r.Width / 3.0;
        double thirdH = r.Height / 3.0;
        var gridBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));

        AddCropGridLine(_cropVisualCanvas, r.Left, r.Top + thirdH, r.Right, r.Top + thirdH, gridBrush);
        AddCropGridLine(_cropVisualCanvas, r.Left, r.Top + thirdH * 2, r.Right, r.Top + thirdH * 2, gridBrush);
        AddCropGridLine(_cropVisualCanvas, r.Left + thirdW, r.Top, r.Left + thirdW, r.Bottom, gridBrush);
        AddCropGridLine(_cropVisualCanvas, r.Left + thirdW * 2, r.Top, r.Left + thirdW * 2, r.Bottom, gridBrush);

        // 4. 八个调节手柄 (四角 + 四边中点)
        AddCropHandleVisual(_cropVisualCanvas, r.Left - 5, r.Top - 5, 10, 10);
        AddCropHandleVisual(_cropVisualCanvas, r.Right - 5, r.Top - 5, 10, 10);
        AddCropHandleVisual(_cropVisualCanvas, r.Right - 5, r.Bottom - 5, 10, 10);
        AddCropHandleVisual(_cropVisualCanvas, r.Left - 5, r.Bottom - 5, 10, 10);

        AddCropHandleVisual(_cropVisualCanvas, r.Left + r.Width / 2.0 - 9, r.Top - 3, 18, 6);
        AddCropHandleVisual(_cropVisualCanvas, r.Left + r.Width / 2.0 - 9, r.Bottom - 3, 18, 6);
        AddCropHandleVisual(_cropVisualCanvas, r.Left - 3, r.Top + r.Height / 2.0 - 9, 6, 18);
        AddCropHandleVisual(_cropVisualCanvas, r.Right - 3, r.Top + r.Height / 2.0 - 9, 6, 18);

        // 5. 顶栏操作栏尺寸同步 (顶栏中央显示，彻底避免遮挡画面与手柄角落)
        int pxW = Math.Max(1, (int)Math.Round(r.Width / _displayScale));
        int pxH = Math.Max(1, (int)Math.Round(r.Height / _displayScale));

        if (TopCropActionBar != null)
        {
            TopCropActionBar.IsVisible = true;
            TopCropDimensionText.Text = $"{pxW} × {pxH}";
        }

        DrawingOverlayCanvas.Children.Add(_cropVisualCanvas);
    }

    private void OnTopCropOkClick(object? sender, RoutedEventArgs e) => CommitCrop();
    private void OnTopCropCancelClick(object? sender, RoutedEventArgs e) => CancelCrop();

    private static void AddCropGridLine(Canvas canvas, double x1, double y1, double x2, double y2, IBrush brush)
    {
        var line = new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Stroke = brush,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        canvas.Children.Add(line);
    }

    private static void AddCropHandleVisual(Canvas canvas, double left, double top, double w, double h)
    {
        var hnd = new Border
        {
            Width = w,
            Height = h,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(14, 99, 156)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(1.5),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(hnd, left);
        Canvas.SetTop(hnd, top);
        canvas.Children.Add(hnd);
    }

    private void CommitCrop()
    {
        if (_currentTool != EditorTool.Crop) return;

        if (TopCropActionBar != null) TopCropActionBar.IsVisible = false;

        double normLeft = Math.Min(_cropRectDIP.Left, _cropRectDIP.Right);
        double normTop = Math.Min(_cropRectDIP.Top, _cropRectDIP.Bottom);
        double normWidth = Math.Abs(_cropRectDIP.Width);
        double normHeight = Math.Abs(_cropRectDIP.Height);

        int x = (int)Math.Round(normLeft / _displayScale);
        int y = (int)Math.Round(normTop / _displayScale);
        int w = (int)Math.Round(normWidth / _displayScale);
        int h = (int)Math.Round(normHeight / _displayScale);

        if (w >= 10 && h >= 10)
        {
            _doc.Crop(new SKRectI(x, y, x + w, y + h));
            DimensionText.Text = $"{_doc.OriginalImage.Width} × {_doc.OriginalImage.Height}";

            ToastNotificationWindow.Show(
                $"已裁切至 {_doc.OriginalImage.Width} × {_doc.OriginalImage.Height} (可按 Ctrl+Z 撤销)",
                TimeSpan.FromSeconds(2),
                "✂ 裁切完成");
        }

        if (_cropVisualCanvas != null)
        {
            DrawingOverlayCanvas.Children.Remove(_cropVisualCanvas);
            _cropVisualCanvas = null;
        }

        ToolCropBtn.IsChecked = false;
        _currentTool = EditorTool.Select;
        UpdateCursor();
        ApplyDimensions(recenter: false);
        RefreshDisplay();
    }

    private void CancelCrop()
    {
        if (TopCropActionBar != null) TopCropActionBar.IsVisible = false;

        if (_cropVisualCanvas != null)
        {
            DrawingOverlayCanvas.Children.Remove(_cropVisualCanvas);
            _cropVisualCanvas = null;
        }

        ToolCropBtn.IsChecked = false;
        _currentTool = EditorTool.Select;
        UpdateCursor();
        RefreshDisplay();
    }
}
