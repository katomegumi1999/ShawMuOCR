using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShawMuOCR.Core.Editor;
using SkiaSharp;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 画布图元绘制、擦除与动态交互预览 (Drawing & Interaction) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    // 绘制状态
    private bool _isDrawing;
    private Point _startPoint;
    private Control? _previewControl;
    private readonly List<SKPoint> _activePoints = new();
    private MosaicElement? _liveMosaicElement;
    private EraserElement? _liveEraserElement;

    // ==========================================
    // 还原清空逻辑
    // ==========================================
    private void OnCancelResetClick(object? sender, RoutedEventArgs e)
    {
        ResetAllBtn.Flyout?.Hide();
        ReactivateWindowAndIme();
    }

    private void OnConfirmResetClick(object? sender, RoutedEventArgs e)
    {
        ResetAllBtn.Flyout?.Hide();
        CommitInlineText();

        // 彻底清理 DrawingOverlayCanvas 上的任何残留 Shape 预览
        var shapesToRemove = DrawingOverlayCanvas.Children.OfType<Avalonia.Controls.Shapes.Shape>().ToList();
        foreach (var shape in shapesToRemove)
        {
            DrawingOverlayCanvas.Children.Remove(shape);
        }
        _previewControl = null;

        if (_doc.Elements.Count > 0)
        {
            _doc.ClearElements();
            _selectedElement = null;
            RefreshDisplay();
        }
        else
        {
            _selectedElement = null;
            RefreshDisplay();
        }

        // 立即激活窗体并恢复输入法，杜绝焦点脱轨导致输入法切换失灵
        ReactivateWindowAndIme();
    }

    // ==========================================
    // 画布光标与指针交互
    // ==========================================
    private void UpdateCursor()
    {
        var cursor = _currentTool switch
        {
            EditorTool.Select => _zoomFactor > 1.001 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow),
            EditorTool.Text => new Cursor(StandardCursorType.Ibeam),
            EditorTool.Eraser => new Cursor(StandardCursorType.Cross),
            EditorTool.Pen => new Cursor(StandardCursorType.Cross),
            EditorTool.Crop => new Cursor(StandardCursorType.Cross),
            _ => new Cursor(StandardCursorType.Cross)
        };
        DrawingOverlayCanvas.Cursor = cursor;
        if (EditorCanvasHostBorder != null)
        {
            EditorCanvasHostBorder.Cursor = cursor;
        }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        var pt = e.GetPosition(DrawingOverlayCanvas);
        var props = e.GetCurrentPoint(DrawingOverlayCanvas).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed) return;

        // 无论何种工具模式，中键按住均支持画布平移拖拽
        if (props.IsMiddleButtonPressed)
        {
            if (_zoomFactor > 1.001)
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(EditorCanvasHostBorder);
                _panStartOffsetX = _panOffsetX;
                _panStartOffsetY = _panOffsetY;
                DrawingOverlayCanvas.Cursor = new Cursor(StandardCursorType.Hand);
                e.Pointer.Capture(DrawingOverlayCanvas);
            }
            return;
        }

        if (_previewControl != null)
        {
            DrawingOverlayCanvas.Children.Remove(_previewControl);
            _previewControl = null;
        }

        e.Pointer.Capture(DrawingOverlayCanvas);

        float imgX = (float)(pt.X / _displayScale);
        float imgY = (float)(pt.Y / _displayScale);

        if (InlineTextBox.IsVisible)
        {
            CommitInlineText();
            // 如果用户当前仍是文字标注模式，点击新位置时：若命中既有文字则编辑，否则开启新输入框
            if (_currentTool == EditorTool.Text)
            {
                var hitText = _doc.HitTest(new SKPoint(imgX, imgY)) as TextElement;
                if (hitText != null)
                {
                    StartEditingTextElement(hitText);
                    return;
                }

                _editingTextElement = null;
                _textInsertPoint = pt;
                ShowInlineTextBox(pt);
            }
            return;
        }

        // 0. 裁切模式交互
        if (_currentTool == EditorTool.Crop)
        {
            if (e.ClickCount == 2 && _cropRectDIP.Contains(pt))
            {
                CommitCrop();
                return;
            }

            var cropHit = HitTestCropHandles(pt);
            if (cropHit != CropHandle.None)
            {
                _activeCropHandle = cropHit;
                _isCroppingDrag = true;
                _cropDragStartPoint = pt;
                _initialCropRectDIP = _cropRectDIP;
            }
            else
            {
                _activeCropHandle = CropHandle.BottomRight;
                _isCroppingDrag = true;
                _cropDragStartPoint = pt;
                _initialCropRectDIP = new Rect(pt.X, pt.Y, 0, 0);
                _cropRectDIP = _initialCropRectDIP;
                UpdateCropVisual();
            }
            return;
        }

        // 1. 默认选择与移动模式 (含四角缩放和旋转句柄点击判断)
        if (_currentTool == EditorTool.Select)
        {
            if (_selectedElement != null)
            {
                var handleHit = HitTestHandles(pt);
                if (handleHit != TransformMode.None)
                {
                    _transformMode = handleHit;
                    _isTransforming = true;
                    _transformStartPoint = pt;
                    _dragOriginPoint = pt;
                    _hasMovedPastThreshold = handleHit != TransformMode.Move;
                    _initialBounds = _selectedElement.BoundingBox;
                    _initialElementState = _selectedElement.Clone();
                    return;
                }
            }

            var hitElem = _doc.HitTest(new SKPoint(imgX, imgY));
            if (hitElem != null)
            {
                if (e.ClickCount == 2 && hitElem is TextElement targetText)
                {
                    StartEditingTextElement(targetText);
                    return;
                }

                _selectedElement = hitElem;
                _transformMode = TransformMode.Move;
                _isTransforming = true;
                _transformStartPoint = pt;
                _dragOriginPoint = pt;
                _hasMovedPastThreshold = false;
                _initialBounds = hitElem.BoundingBox;
                _initialElementState = hitElem.Clone();

                // 同步所选要素的粗细到工具栏滑块
                if (hitElem is TextElement te)
                {
                    _currentStrokeWidth = te.StrokeWidth;
                    if (StrokeSlider != null) StrokeSlider.Value = te.StrokeWidth;
                }
                else if (hitElem is RectangleElement re)
                {
                    _currentStrokeWidth = re.StrokeWidth;
                    if (StrokeSlider != null) StrokeSlider.Value = re.StrokeWidth;
                }
                else if (hitElem is LineElement le)
                {
                    _currentStrokeWidth = le.StrokeWidth;
                    if (StrokeSlider != null) StrokeSlider.Value = le.StrokeWidth;
                }
                else if (hitElem is ArrowElement ae)
                {
                    _currentStrokeWidth = ae.StrokeWidth;
                    if (StrokeSlider != null) StrokeSlider.Value = ae.StrokeWidth;
                }
                else if (hitElem is PenElement pe)
                {
                    _currentStrokeWidth = pe.StrokeWidth;
                    if (StrokeSlider != null) StrokeSlider.Value = pe.StrokeWidth;
                }

                UpdateSelectionHighlight();
            }
            else
            {
                _selectedElement = null;
                _transformMode = TransformMode.None;
                UpdateSelectionHighlight();

                // 放大超过原来图大小的边界时，当鼠标按住且无元素就能移动 (Pan)
                if (_zoomFactor > 1.001)
                {
                    _isPanning = true;
                    _panStartPoint = e.GetPosition(EditorCanvasHostBorder);
                    _panStartOffsetX = _panOffsetX;
                    _panStartOffsetY = _panOffsetY;
                    DrawingOverlayCanvas.Cursor = new Cursor(StandardCursorType.Hand);
                    e.Pointer.Capture(DrawingOverlayCanvas);
                }
            }
            return;
        }

        // 2. 文字标注模式：仅在文字标注功能下点击文字元素时才重新编辑，否则新建文字
        if (_currentTool == EditorTool.Text)
        {
            var hitText = _doc.HitTest(new SKPoint(imgX, imgY)) as TextElement;
            if (hitText != null)
            {
                StartEditingTextElement(hitText);
                return;
            }

            _editingTextElement = null;
            _textInsertPoint = pt;
            ShowInlineTextBox(pt);
            return;
        }

        // 3. 橡皮擦模式 (实时笔刷擦除，直接擦除要素而非删除要素)
        if (_currentTool == EditorTool.Eraser)
        {
            _isDrawing = true;
            _liveEraserElement = new EraserElement
            {
                StrokeWidth = Math.Max(6f, _currentStrokeWidth * 1.6f)
            };
            _liveEraserElement.Points.Add(new SKPoint(imgX, imgY));

            using var rendered = _doc.RenderToBitmap(extraPreviewElement: _liveEraserElement);
            BaseImageControl.Source = ConvertToBitmap(rendered);
            return;
        }

        // 4. 其他图元绘制初始化
        _isDrawing = true;
        _startPoint = pt;
        _activePoints.Clear();
        _activePoints.Add(new SKPoint(imgX, imgY));

        var colorBrush = new SolidColorBrush(Color.FromArgb(_currentColor.Alpha, _currentColor.Red, _currentColor.Green, _currentColor.Blue));
        double dipStroke = Math.Max(1.0, _currentStrokeWidth * _displayScale);

        if (_currentTool == EditorTool.Rectangle)
        {
            var rect = new Avalonia.Controls.Shapes.Rectangle
            {
                Stroke = colorBrush,
                StrokeThickness = dipStroke
            };
            Canvas.SetLeft(rect, pt.X);
            Canvas.SetTop(rect, pt.Y);
            _previewControl = rect;
            DrawingOverlayCanvas.Children.Add(rect);
        }
        else if (_currentTool == EditorTool.Ellipse)
        {
            var ellipse = new Avalonia.Controls.Shapes.Ellipse
            {
                Stroke = colorBrush,
                StrokeThickness = dipStroke
            };
            Canvas.SetLeft(ellipse, pt.X);
            Canvas.SetTop(ellipse, pt.Y);
            _previewControl = ellipse;
            DrawingOverlayCanvas.Children.Add(ellipse);
        }
        else if (_currentTool is EditorTool.Line or EditorTool.Arrow)
        {
            var line = new Avalonia.Controls.Shapes.Line
            {
                Stroke = colorBrush,
                StrokeThickness = dipStroke,
                StrokeLineCap = PenLineCap.Round,
                StartPoint = pt,
                EndPoint = pt
            };
            _previewControl = line;
            DrawingOverlayCanvas.Children.Add(line);
        }
        else if (_currentTool == EditorTool.Pen)
        {
            var polyline = new Polyline
            {
                Stroke = colorBrush,
                StrokeThickness = dipStroke,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                Points = new Points { pt }
            };
            _previewControl = polyline;
            DrawingOverlayCanvas.Children.Add(polyline);
        }
        else if (_currentTool == EditorTool.Mosaic)
        {
            _liveMosaicElement = new MosaicElement
            {
                StrokeWidth = _currentStrokeWidth
            };
            _liveMosaicElement.Points.Add(new SKPoint(imgX, imgY));
            UpdateLiveMosaicPreview();
        }
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        e.Handled = true;

        if (_isPanning)
        {
            var currentPos = e.GetPosition(EditorCanvasHostBorder);
            double dx = currentPos.X - _panStartPoint.X;
            double dy = currentPos.Y - _panStartPoint.Y;
            _panOffsetX = _panStartOffsetX + dx;
            _panOffsetY = _panStartOffsetY + dy;
            ClampPanOffsets();
            UpdateCanvasTransform();
            return;
        }

        var pt = e.GetPosition(DrawingOverlayCanvas);

        // 0. 裁切拖拽调整
        if (_currentTool == EditorTool.Crop)
        {
            if (_isCroppingDrag)
            {
                double maxW = CanvasContainer.Width;
                double maxH = CanvasContainer.Height;

                if (_activeCropHandle == CropHandle.Inside)
                {
                    double dx = pt.X - _cropDragStartPoint.X;
                    double dy = pt.Y - _cropDragStartPoint.Y;
                    double newL = Math.Clamp(_initialCropRectDIP.Left + dx, 0, maxW - _initialCropRectDIP.Width);
                    double newT = Math.Clamp(_initialCropRectDIP.Top + dy, 0, maxH - _initialCropRectDIP.Height);
                    _cropRectDIP = new Rect(newL, newT, _initialCropRectDIP.Width, _initialCropRectDIP.Height);
                }
                else if (_activeCropHandle == CropHandle.TopLeft)
                {
                    double newL = Math.Clamp(pt.X, 0, _initialCropRectDIP.Right - 20);
                    double newT = Math.Clamp(pt.Y, 0, _initialCropRectDIP.Bottom - 20);
                    _cropRectDIP = new Rect(newL, newT, _initialCropRectDIP.Right - newL, _initialCropRectDIP.Bottom - newT);
                }
                else if (_activeCropHandle == CropHandle.TopRight)
                {
                    double newR = Math.Clamp(pt.X, _initialCropRectDIP.Left + 20, maxW);
                    double newT = Math.Clamp(pt.Y, 0, _initialCropRectDIP.Bottom - 20);
                    _cropRectDIP = new Rect(_initialCropRectDIP.Left, newT, newR - _initialCropRectDIP.Left, _initialCropRectDIP.Bottom - newT);
                }
                else if (_activeCropHandle == CropHandle.BottomRight)
                {
                    double newR = Math.Clamp(pt.X, _initialCropRectDIP.Left + 20, maxW);
                    double newB = Math.Clamp(pt.Y, _initialCropRectDIP.Top + 20, maxH);
                    _cropRectDIP = new Rect(_initialCropRectDIP.Left, _initialCropRectDIP.Top, newR - _initialCropRectDIP.Left, newB - _initialCropRectDIP.Top);
                }
                else if (_activeCropHandle == CropHandle.BottomLeft)
                {
                    double newL = Math.Clamp(pt.X, 0, _initialCropRectDIP.Right - 20);
                    double newB = Math.Clamp(pt.Y, _initialCropRectDIP.Top + 20, maxH);
                    _cropRectDIP = new Rect(newL, _initialCropRectDIP.Top, _initialCropRectDIP.Right - newL, newB - _initialCropRectDIP.Top);
                }
                else if (_activeCropHandle == CropHandle.Top)
                {
                    double newT = Math.Clamp(pt.Y, 0, _initialCropRectDIP.Bottom - 20);
                    _cropRectDIP = new Rect(_initialCropRectDIP.Left, newT, _initialCropRectDIP.Width, _initialCropRectDIP.Bottom - newT);
                }
                else if (_activeCropHandle == CropHandle.Bottom)
                {
                    double newB = Math.Clamp(pt.Y, _initialCropRectDIP.Top + 20, maxH);
                    _cropRectDIP = new Rect(_initialCropRectDIP.Left, _initialCropRectDIP.Top, _initialCropRectDIP.Width, newB - _initialCropRectDIP.Top);
                }
                else if (_activeCropHandle == CropHandle.Left)
                {
                    double newL = Math.Clamp(pt.X, 0, _initialCropRectDIP.Right - 20);
                    _cropRectDIP = new Rect(newL, _initialCropRectDIP.Top, _initialCropRectDIP.Right - newL, _initialCropRectDIP.Height);
                }
                else if (_activeCropHandle == CropHandle.Right)
                {
                    double newR = Math.Clamp(pt.X, _initialCropRectDIP.Left + 20, maxW);
                    _cropRectDIP = new Rect(_initialCropRectDIP.Left, _initialCropRectDIP.Top, newR - _initialCropRectDIP.Left, _initialCropRectDIP.Height);
                }

                UpdateCropVisual();
            }
            else
            {
                UpdateCropCursor(pt);
            }
            return;
        }

        float imgX = (float)(pt.X / _displayScale);
        float imgY = (float)(pt.Y / _displayScale);

        // A. 变换操作 (移动/缩放/旋转) 拖拽更新
        if (_isTransforming && _selectedElement != null)
        {
            if (_transformMode == TransformMode.Rotate)
            {
                var box = _selectedElement.BoundingBox;
                var center = new Point((box.Left + box.Width / 2.0) * _displayScale, (box.Top + box.Height / 2.0) * _displayScale);
                double angleRad = Math.Atan2(pt.Y - center.Y, pt.X - center.X);
                double angleDeg = angleRad * (180.0 / Math.PI) + 90.0;
                while (angleDeg < 0) angleDeg += 360.0;
                while (angleDeg >= 360.0) angleDeg -= 360.0;

                // 智能角度吸附：接近 0, 45, 90, 135, 180, 225, 270, 315 度时自动吸附
                bool isSnapped = false;
                double[] snapAngles = { 0, 45, 90, 135, 180, 225, 270, 315, 360 };
                foreach (var snap in snapAngles)
                {
                    if (Math.Abs(angleDeg - snap) <= 3.5)
                    {
                        angleDeg = snap % 360;
                        isSnapped = true;
                        break;
                    }
                }

                _selectedElement.Rotation = (float)angleDeg;
                ShowRotationTooltip(pt, angleDeg, isSnapped);
                RefreshDisplay();
                return;
            }

            if (_transformMode == TransformMode.Move)
            {
                double dragDx = pt.X - _dragOriginPoint.X;
                double dragDy = pt.Y - _dragOriginPoint.Y;
                if (!_hasMovedPastThreshold && (dragDx * dragDx + dragDy * dragDy >= 16))
                {
                    _hasMovedPastThreshold = true;
                }

                double dx = (pt.X - _transformStartPoint.X) / _displayScale;
                double dy = (pt.Y - _transformStartPoint.Y) / _displayScale;
                _selectedElement.MoveBy((float)dx, (float)dy);
                _transformStartPoint = pt;
                RefreshDisplay();
                return;
            }

            if (_transformMode is TransformMode.ResizeNW or TransformMode.ResizeNE or TransformMode.ResizeSE or TransformMode.ResizeSW)
            {
                float dx = (float)((pt.X - _transformStartPoint.X) / _displayScale);
                float dy = (float)((pt.Y - _transformStartPoint.Y) / _displayScale);

                float initW = _initialBounds.Width;
                float initH = _initialBounds.Height;
                if (initW <= 0.001f) initW = 10f;
                if (initH <= 0.001f) initH = 10f;
                float aspectRatio = initW / initH;

                float minScale = Math.Max(10f / initW, 10f / initH);
                float scale;

                if (_transformMode == TransformMode.ResizeSE)
                {
                    float rawW = initW + dx;
                    float rawH = initH + dy;
                    float sx = rawW / initW;
                    float sy = rawH / initH;
                    scale = Math.Abs(dx) >= Math.Abs(dy) * aspectRatio ? sx : sy;
                    scale = Math.Max(minScale, scale);

                    float newW = initW * scale;
                    float newH = initH * scale;
                    _selectedElement.Resize(new SKRect(_initialBounds.Left, _initialBounds.Top, _initialBounds.Left + newW, _initialBounds.Top + newH));
                }
                else if (_transformMode == TransformMode.ResizeNW)
                {
                    float rawW = initW - dx;
                    float rawH = initH - dy;
                    float sx = rawW / initW;
                    float sy = rawH / initH;
                    scale = Math.Abs(dx) >= Math.Abs(dy) * aspectRatio ? sx : sy;
                    scale = Math.Max(minScale, scale);

                    float newW = initW * scale;
                    float newH = initH * scale;
                    _selectedElement.Resize(new SKRect(_initialBounds.Right - newW, _initialBounds.Bottom - newH, _initialBounds.Right, _initialBounds.Bottom));
                }
                else if (_transformMode == TransformMode.ResizeNE)
                {
                    float rawW = initW + dx;
                    float rawH = initH - dy;
                    float sx = rawW / initW;
                    float sy = rawH / initH;
                    scale = Math.Abs(dx) >= Math.Abs(dy) * aspectRatio ? sx : sy;
                    scale = Math.Max(minScale, scale);

                    float newW = initW * scale;
                    float newH = initH * scale;
                    _selectedElement.Resize(new SKRect(_initialBounds.Left, _initialBounds.Bottom - newH, _initialBounds.Left + newW, _initialBounds.Bottom));
                }
                else if (_transformMode == TransformMode.ResizeSW)
                {
                    float rawW = initW - dx;
                    float rawH = initH + dy;
                    float sx = rawW / initW;
                    float sy = rawH / initH;
                    scale = Math.Abs(dx) >= Math.Abs(dy) * aspectRatio ? sx : sy;
                    scale = Math.Max(minScale, scale);

                    float newW = initW * scale;
                    float newH = initH * scale;
                    _selectedElement.Resize(new SKRect(_initialBounds.Right - newW, _initialBounds.Top, _initialBounds.Right, _initialBounds.Top + newH));
                }

                RefreshDisplay();
                return;
            }
        }

        // B. 悬停在句柄上的鼠标光标提示 (在选择模式下)
        if (_currentTool == EditorTool.Select && !_isTransforming && !_isPanning)
        {
            if (_selectedElement != null)
            {
                var hit = HitTestHandles(pt);
                var newCursor = hit switch
                {
                    TransformMode.Rotate => new Cursor(StandardCursorType.Cross),
                    TransformMode.ResizeNW or TransformMode.ResizeSE => new Cursor(StandardCursorType.TopLeftCorner),
                    TransformMode.ResizeNE or TransformMode.ResizeSW => new Cursor(StandardCursorType.TopRightCorner),
                    TransformMode.Move => new Cursor(StandardCursorType.SizeAll),
                    _ => _zoomFactor > 1.001 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow)
                };
                DrawingOverlayCanvas.Cursor = newCursor;
                if (EditorCanvasHostBorder != null)
                {
                    EditorCanvasHostBorder.Cursor = newCursor;
                }
            }
            else
            {
                float hoverImgX = (float)(pt.X / _displayScale);
                float hoverImgY = (float)(pt.Y / _displayScale);
                var hitElem = _doc.HitTest(new SKPoint(hoverImgX, hoverImgY));
                var newCursor = hitElem != null
                    ? new Cursor(StandardCursorType.Hand)
                    : (_zoomFactor > 1.001 ? new Cursor(StandardCursorType.Hand) : new Cursor(StandardCursorType.Arrow));
                DrawingOverlayCanvas.Cursor = newCursor;
                if (EditorCanvasHostBorder != null)
                {
                    EditorCanvasHostBorder.Cursor = newCursor;
                }
            }
        }

        // C. 橡皮擦连续擦除与尺寸指示圈动态跟随 (实时 Clear 混合模式擦除要素)
        if (_currentTool == EditorTool.Eraser)
        {
            double indicatorSize = Math.Max(16.0, _currentStrokeWidth * _displayScale * 1.5);
            EraserIndicatorBorder.Width = indicatorSize;
            EraserIndicatorBorder.Height = indicatorSize;
            Canvas.SetLeft(EraserIndicatorBorder, pt.X - indicatorSize / 2.0);
            Canvas.SetTop(EraserIndicatorBorder, pt.Y - indicatorSize / 2.0);
            EraserIndicatorBorder.IsVisible = true;

            if (_isDrawing && _liveEraserElement != null && e.GetCurrentPoint(DrawingOverlayCanvas).Properties.IsLeftButtonPressed)
            {
                _liveEraserElement.Points.Add(new SKPoint(imgX, imgY));
                using var rendered = _doc.RenderToBitmap(extraPreviewElement: _liveEraserElement);
                BaseImageControl.Source = ConvertToBitmap(rendered);
            }
            return;
        }

        // 马赛克连续涂抹动态跟随
        if (_currentTool == EditorTool.Mosaic)
        {
            if (_isDrawing && _liveMosaicElement != null && e.GetCurrentPoint(DrawingOverlayCanvas).Properties.IsLeftButtonPressed)
            {
                if (_liveMosaicElement.Points.Count > 0)
                {
                    var last = _liveMosaicElement.Points[^1];
                    float dx = imgX - last.X;
                    float dy = imgY - last.Y;
                    if (dx * dx + dy * dy >= 4f) // 移动距离大于 2 像素才采样，平滑并节约性能
                    {
                        _activePoints.Add(new SKPoint(imgX, imgY));
                        _liveMosaicElement.Points.Add(new SKPoint(imgX, imgY));
                        UpdateLiveMosaicPreview();
                    }
                }
                else
                {
                    _activePoints.Add(new SKPoint(imgX, imgY));
                    _liveMosaicElement.Points.Add(new SKPoint(imgX, imgY));
                    UpdateLiveMosaicPreview();
                }
            }
            return;
        }

        // D. 绘制预览更新
        if (!_isDrawing || _previewControl == null) return;

        float currImgX = (float)(pt.X / _displayScale);
        float currImgY = (float)(pt.Y / _displayScale);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_currentTool == EditorTool.Rectangle && _previewControl is Avalonia.Controls.Shapes.Rectangle rect)
        {
            double x = Math.Min(_startPoint.X, pt.X);
            double y = Math.Min(_startPoint.Y, pt.Y);
            double w = Math.Abs(pt.X - _startPoint.X);
            double h = Math.Abs(pt.Y - _startPoint.Y);
            if (isShift)
            {
                double side = Math.Max(w, h);
                w = side;
                h = side;
                if (pt.X < _startPoint.X) x = _startPoint.X - side;
                if (pt.Y < _startPoint.Y) y = _startPoint.Y - side;
            }
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = w;
            rect.Height = h;
        }
        else if (_currentTool == EditorTool.Ellipse && _previewControl is Avalonia.Controls.Shapes.Ellipse ellipse)
        {
            double x = Math.Min(_startPoint.X, pt.X);
            double y = Math.Min(_startPoint.Y, pt.Y);
            double w = Math.Abs(pt.X - _startPoint.X);
            double h = Math.Abs(pt.Y - _startPoint.Y);
            if (isShift)
            {
                double side = Math.Max(w, h);
                w = side;
                h = side;
                if (pt.X < _startPoint.X) x = _startPoint.X - side;
                if (pt.Y < _startPoint.Y) y = _startPoint.Y - side;
            }
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            ellipse.Width = w;
            ellipse.Height = h;
        }
        else if ((_currentTool is EditorTool.Line or EditorTool.Arrow) && _previewControl is Avalonia.Controls.Shapes.Line line)
        {
            line.EndPoint = pt;
        }
        else if (_currentTool == EditorTool.Pen && _previewControl is Polyline poly)
        {
            poly.Points.Add(pt);
            _activePoints.Add(new SKPoint(currImgX, currImgY));
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
        e.Pointer.Capture(null);

        if (_isPanning)
        {
            _isPanning = false;
            UpdateCursor();
            return;
        }

        var pt = e.GetPosition(DrawingOverlayCanvas);
        HideRotationTooltip();

        if (_currentTool == EditorTool.Crop)
        {
            _isCroppingDrag = false;
            _cropRectDIP = new Rect(
                Math.Min(_cropRectDIP.Left, _cropRectDIP.Right),
                Math.Min(_cropRectDIP.Top, _cropRectDIP.Bottom),
                Math.Max(20, Math.Abs(_cropRectDIP.Width)),
                Math.Max(20, Math.Abs(_cropRectDIP.Height)));
            UpdateCropVisual();
            return;
        }

        if (_isTransforming && _selectedElement != null)
        {
            bool actuallyMoved = _transformMode != TransformMode.Move || _hasMovedPastThreshold;
            _isTransforming = false;
            _transformMode = TransformMode.None;
            _hasMovedPastThreshold = false;

            if (actuallyMoved && _initialElementState != null)
            {
                _doc.UndoRedo.Execute(new TransformElementCommand(_selectedElement, _initialElementState, _selectedElement.Clone()));
            }

            RefreshDisplay();
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

        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_currentTool == EditorTool.Rectangle)
        {
            if (dist < 4f) return;
            float w = Math.Abs(endX - startX);
            float h = Math.Abs(endY - startY);
            if (isShift)
            {
                float side = Math.Max(w, h);
                w = side;
                h = side;
            }
            float finalLeft = (endX >= startX) ? startX : (startX - w);
            float finalTop = (endY >= startY) ? startY : (startY - h);

            _doc.AddElement(new RectangleElement
            {
                Left = finalLeft,
                Top = finalTop,
                Right = finalLeft + w,
                Bottom = finalTop + h,
                Color = _currentColor,
                StrokeWidth = _currentStrokeWidth
            });
        }
        else if (_currentTool == EditorTool.Ellipse)
        {
            if (dist < 4f) return;
            float w = Math.Abs(endX - startX);
            float h = Math.Abs(endY - startY);
            if (isShift)
            {
                float side = Math.Max(w, h);
                w = side;
                h = side;
            }
            float finalLeft = (endX >= startX) ? startX : (startX - w);
            float finalTop = (endY >= startY) ? startY : (startY - h);

            _doc.AddElement(new EllipseElement
            {
                Left = finalLeft,
                Top = finalTop,
                Right = finalLeft + w,
                Bottom = finalTop + h,
                Color = _currentColor,
                StrokeWidth = _currentStrokeWidth
            });
        }
        else if (_currentTool == EditorTool.Line)
        {
            if (dist >= 4f)
            {
                _doc.AddElement(new LineElement
                {
                    StartX = startX,
                    StartY = startY,
                    EndX = endX,
                    EndY = endY,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                });
            }
        }
        else if (_currentTool == EditorTool.Arrow)
        {
            if (dist >= 4f)
            {
                _doc.AddElement(new ArrowElement
                {
                    StartX = startX,
                    StartY = startY,
                    EndX = endX,
                    EndY = endY,
                    Color = _currentColor,
                    StrokeWidth = _currentStrokeWidth
                });
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
                _doc.AddElement(penElem);
            }
        }
        else if (_currentTool == EditorTool.Mosaic)
        {
            if (_liveMosaicElement != null && _liveMosaicElement.Points.Count >= 1)
            {
                _doc.AddElement(_liveMosaicElement);
                _liveMosaicElement = null;
            }
        }
        else if (_currentTool == EditorTool.Eraser)
        {
            if (_liveEraserElement != null && _liveEraserElement.Points.Count >= 1)
            {
                _doc.AddElement(_liveEraserElement);
                _liveEraserElement = null;
            }
        }

        RefreshDisplay();
    }

    private void UpdateLiveMosaicPreview()
    {
        if (_liveMosaicElement == null || _liveMosaicElement.Points.Count == 0) return;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in _liveMosaicElement.Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        float pad = Math.Max(_currentStrokeWidth * 1.5f, 12f);
        var bounds = new SKRect(minX - pad, minY - pad, maxX + pad, maxY + pad);
        _liveMosaicElement.Bounds = bounds;

        int blockSize = (int)Math.Max(6, _currentStrokeWidth * 1.4f);
        _liveMosaicElement.PixelatedPatch?.Dispose();
        _liveMosaicElement.PixelatedPatch = MosaicElement.CreatePixelatedPatch(
            _doc.OriginalImage.Bitmap,
            bounds,
            blockSize,
            out int alignedLeft,
            out int alignedTop);
        _liveMosaicElement.PatchLeft = alignedLeft;
        _liveMosaicElement.PatchTop = alignedTop;

        using var rendered = _doc.RenderToBitmap(extraPreviewElement: _liveMosaicElement);
        BaseImageControl.Source = ConvertToBitmap(rendered);
    }
}
