using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using ShawMuOCR.Core.Editor;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 画布视口中心缩放与平移 (Zoom & Pan) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    // 画布中心缩放与平移状态
    private double _zoomFactor = 1.0;
    private double _panOffsetX = 0.0;
    private double _panOffsetY = 0.0;
    private bool _isPanning;
    private Point _panStartPoint;
    private double _panStartOffsetX;
    private double _panStartOffsetY;
    private readonly ScaleTransform _canvasScaleTransform = new(1.0, 1.0);
    private readonly TranslateTransform _canvasTranslateTransform = new(0.0, 0.0);
    private readonly TransformGroup _canvasTransformGroup = new();

    private void InitZoomPanTransform()
    {
        _canvasTransformGroup.Children.Add(_canvasScaleTransform);
        _canvasTransformGroup.Children.Add(_canvasTranslateTransform);
        CanvasContainer.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        CanvasContainer.RenderTransform = _canvasTransformGroup;
    }

    private void ResetZoomPan()
    {
        _zoomFactor = 1.0;
        _panOffsetX = 0.0;
        _panOffsetY = 0.0;
        UpdateCanvasTransform();
        UpdateDimensionText();
        UpdateCursor();
    }

    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (InlineTextBox.IsVisible) return;
        if (_currentTool == EditorTool.Crop) return;

        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.001) return;

        double step = delta > 0 ? 1.15 : (1.0 / 1.15);
        double targetZoom = _zoomFactor * step;

        if (targetZoom < 1.02)
        {
            _zoomFactor = 1.0;
            _panOffsetX = 0.0;
            _panOffsetY = 0.0;
        }
        else
        {
            _zoomFactor = Math.Min(10.0, targetZoom);
            ClampPanOffsets();
        }

        UpdateCanvasTransform();
        UpdateDimensionText();
        UpdateCursor();
        e.Handled = true;
    }

    private void UpdateCanvasTransform()
    {
        _canvasScaleTransform.ScaleX = _zoomFactor;
        _canvasScaleTransform.ScaleY = _zoomFactor;
        _canvasTranslateTransform.X = _panOffsetX;
        _canvasTranslateTransform.Y = _panOffsetY;
    }

    private void ClampPanOffsets()
    {
        if (_zoomFactor <= 1.001)
        {
            _panOffsetX = 0;
            _panOffsetY = 0;
            return;
        }

        double canvasW = CanvasContainer.Width;
        double canvasH = CanvasContainer.Height;
        double visualW = canvasW * _zoomFactor;
        double visualH = canvasH * _zoomFactor;

        double hostW = EditorCanvasHostBorder?.Bounds.Width ?? canvasW;
        double hostH = EditorCanvasHostBorder?.Bounds.Height ?? canvasH;

        double maxPanX = Math.Max((visualW - canvasW) / 2.0 + 80.0, Math.Max(0.0, (visualW - hostW) / 2.0 + 80.0));
        double maxPanY = Math.Max((visualH - canvasH) / 2.0 + 80.0, Math.Max(0.0, (visualH - hostH) / 2.0 + 80.0));

        _panOffsetX = Math.Clamp(_panOffsetX, -maxPanX, maxPanX);
        _panOffsetY = Math.Clamp(_panOffsetY, -maxPanY, maxPanY);
    }

    private void UpdateDimensionText()
    {
        if (DimensionText == null) return;
        int w = _doc.OriginalImage.Width;
        int h = _doc.OriginalImage.Height;
        if (Math.Abs(_zoomFactor - 1.0) < 0.01)
        {
            DimensionText.Text = $"{w} × {h}";
        }
        else
        {
            int percent = (int)Math.Round(_zoomFactor * 100);
            DimensionText.Text = $"{w} × {h}  ({percent}%)";
        }
    }

    private void OnHostBorderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(EditorCanvasHostBorder).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed) return;

        // 1. 中键按住始终为平移
        if (props.IsMiddleButtonPressed)
        {
            if (_zoomFactor > 1.001)
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(EditorCanvasHostBorder);
                _panStartOffsetX = _panOffsetX;
                _panStartOffsetY = _panOffsetY;
                EditorCanvasHostBorder.Cursor = new Cursor(StandardCursorType.Hand);
                e.Pointer.Capture(EditorCanvasHostBorder);
                e.Handled = true;
            }
            return;
        }

        // 2. 左键点击边界外区域：映射到 DrawingOverlayCanvas 坐标系
        var pt = e.GetPosition(DrawingOverlayCanvas);
        float imgX = (float)(pt.X / _displayScale);
        float imgY = (float)(pt.Y / _displayScale);

        // 检查是否命中了当前已选要素的手柄或边框（即使要素超出图片边界）
        bool hitInteractiveItem = false;
        if (_currentTool == EditorTool.Select)
        {
            if (_selectedElement != null && HitTestHandles(pt) != TransformMode.None)
            {
                hitInteractiveItem = true;
            }
            else if (_doc.HitTest(new SkiaSharp.SKPoint(imgX, imgY)) != null)
            {
                hitInteractiveItem = true;
            }
        }
        else if (_currentTool == EditorTool.Text)
        {
            if (_doc.HitTest(new SkiaSharp.SKPoint(imgX, imgY)) is TextElement)
            {
                hitInteractiveItem = true;
            }
        }

        if (hitInteractiveItem)
        {
            // 转发给 CanvasPointerPressed，使超出边界的图元与手柄具备 100% 完整的移动、缩放与选择能力
            OnCanvasPointerPressed(DrawingOverlayCanvas, e);
            return;
        }

        // 3. 点击了边界外的空白区域
        if (_zoomFactor > 1.001)
        {
            _isPanning = true;
            _panStartPoint = e.GetPosition(EditorCanvasHostBorder);
            _panStartOffsetX = _panOffsetX;
            _panStartOffsetY = _panOffsetY;
            EditorCanvasHostBorder.Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(EditorCanvasHostBorder);
            e.Handled = true;
        }
        else
        {
            // 单击空白区域：提交当前可能开启的内联文本输入框，并取消选中
            if (InlineTextBox.IsVisible)
            {
                CommitInlineText();
            }
            if (_selectedElement != null)
            {
                _selectedElement = null;
                _transformMode = TransformMode.None;
                UpdateSelectionHighlight();
                RefreshDisplay();
            }
        }
    }

    private void OnHostBorderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isPanning)
        {
            var currentPos = e.GetPosition(EditorCanvasHostBorder);
            double dx = currentPos.X - _panStartPoint.X;
            double dy = currentPos.Y - _panStartPoint.Y;
            _panOffsetX = _panStartOffsetX + dx;
            _panOffsetY = _panStartOffsetY + dy;
            ClampPanOffsets();
            UpdateCanvasTransform();
            e.Handled = true;
            return;
        }

        // 鼠标在边界外悬停移动时，实时更新控制手柄或要素的悬停光标提示
        if (_currentTool == EditorTool.Select && !_isTransforming)
        {
            var pt = e.GetPosition(DrawingOverlayCanvas);
            float imgX = (float)(pt.X / _displayScale);
            float imgY = (float)(pt.Y / _displayScale);

            if (_selectedElement != null)
            {
                var hit = HitTestHandles(pt);
                var newCursor = hit switch
                {
                    TransformMode.Rotate => new Cursor(StandardCursorType.Cross),
                    TransformMode.ResizeNW or TransformMode.ResizeSE => new Cursor(StandardCursorType.TopLeftCorner),
                    TransformMode.ResizeNE or TransformMode.ResizeSW => new Cursor(StandardCursorType.TopRightCorner),
                    TransformMode.Move => new Cursor(StandardCursorType.SizeAll),
                    _ => (_doc.HitTest(new SkiaSharp.SKPoint(imgX, imgY)) != null)
                        ? new Cursor(StandardCursorType.SizeAll)
                        : (_zoomFactor > 1.001 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow))
                };
                EditorCanvasHostBorder.Cursor = newCursor;
            }
            else
            {
                var hitElem = _doc.HitTest(new SkiaSharp.SKPoint(imgX, imgY));
                EditorCanvasHostBorder.Cursor = hitElem != null
                    ? new Cursor(StandardCursorType.SizeAll)
                    : (_zoomFactor > 1.001 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow));
            }
        }
    }

    private void OnHostBorderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            UpdateCursor();
            e.Handled = true;
        }
    }
}
