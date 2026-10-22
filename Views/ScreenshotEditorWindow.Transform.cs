using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ShawMuOCR.Core.Editor;
using SkiaSharp;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 图元选择、手柄碰撞与几何变换 (Transform & Selection) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    // 交互与变换状态 (选择、四角缩放、旋转)
    private IImageElement? _selectedElement;
    private TransformMode _transformMode = TransformMode.None;
    private bool _isTransforming;
    private Point _transformStartPoint;
    private Point _dragOriginPoint;
    private bool _hasMovedPastThreshold;
    private SKRect _initialBounds;
    private IImageElement? _initialElementState;
    private Canvas? _selectionVisual;
    private Border? _rotationBadge;

    private TransformMode HitTestHandles(Point pt)
    {
        if (_selectedElement == null) return TransformMode.None;

        var box = _selectedElement.BoundingBox;
        var boxDIP = new Rect(box.Left * _displayScale, box.Top * _displayScale, Math.Max(16, box.Width * _displayScale), Math.Max(16, box.Height * _displayScale));
        var centerDIP = boxDIP.Center;

        Point localPt = pt;
        if (Math.Abs(_selectedElement.Rotation) > 0.01f)
        {
            double rad = -_selectedElement.Rotation * (Math.PI / 180.0);
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);
            double dx = pt.X - centerDIP.X;
            double dy = pt.Y - centerDIP.Y;
            localPt = new Point(centerDIP.X + dx * cos - dy * sin, centerDIP.Y + dx * sin + dy * cos);
        }

        var rotateKnob = new Point(centerDIP.X, boxDIP.Top - 20);
        if (Math.Abs(localPt.X - rotateKnob.X) <= 12 && Math.Abs(localPt.Y - rotateKnob.Y) <= 12)
        {
            return TransformMode.Rotate;
        }

        const double tol = 10.0;
        if (Math.Abs(localPt.X - boxDIP.Left) <= tol && Math.Abs(localPt.Y - boxDIP.Top) <= tol) return TransformMode.ResizeNW;
        if (Math.Abs(localPt.X - boxDIP.Right) <= tol && Math.Abs(localPt.Y - boxDIP.Top) <= tol) return TransformMode.ResizeNE;
        if (Math.Abs(localPt.X - boxDIP.Right) <= tol && Math.Abs(localPt.Y - boxDIP.Bottom) <= tol) return TransformMode.ResizeSE;
        if (Math.Abs(localPt.X - boxDIP.Left) <= tol && Math.Abs(localPt.Y - boxDIP.Bottom) <= tol) return TransformMode.ResizeSW;

        if (boxDIP.Contains(localPt))
        {
            return TransformMode.Move;
        }

        return TransformMode.None;
    }

    private void UpdateSelectionHighlight()
    {
        if (_selectionVisual != null)
        {
            DrawingOverlayCanvas.Children.Remove(_selectionVisual);
            _selectionVisual = null;
        }

        if (_selectedElement != null && _currentTool == EditorTool.Select)
        {
            var box = _selectedElement.BoundingBox;
            double x = box.Left * _displayScale;
            double y = box.Top * _displayScale;
            double w = Math.Max(20, box.Width * _displayScale);
            double h = Math.Max(20, box.Height * _displayScale);

            _selectionVisual = new Canvas
            {
                Width = w,
                Height = h,
                ClipToBounds = false,
                IsHitTestVisible = false
            };

            var border = new Border
            {
                Width = w,
                Height = h,
                BorderBrush = new SolidColorBrush(Color.FromRgb(64, 169, 255)),
                BorderThickness = new Thickness(1.5),
                Background = new SolidColorBrush(Color.FromArgb(20, 64, 169, 255)),
                CornerRadius = new CornerRadius(2)
            };
            Canvas.SetLeft(border, 0);
            Canvas.SetTop(border, 0);
            _selectionVisual.Children.Add(border);

            AddCornerHandle(_selectionVisual, -4, -4);
            AddCornerHandle(_selectionVisual, w - 4, -4);
            AddCornerHandle(_selectionVisual, w - 4, h - 4);
            AddCornerHandle(_selectionVisual, -4, h - 4);

            var stem = new Avalonia.Controls.Shapes.Line
            {
                StartPoint = new Point(w / 2.0, 0),
                EndPoint = new Point(w / 2.0, -18),
                Stroke = new SolidColorBrush(Color.FromRgb(64, 169, 255)),
                StrokeThickness = 1.5
            };
            _selectionVisual.Children.Add(stem);

            var rotKnob = new Border
            {
                Width = 11,
                Height = 11,
                CornerRadius = new CornerRadius(5.5),
                Background = new SolidColorBrush(Color.FromRgb(14, 99, 156)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1.5)
            };
            Canvas.SetLeft(rotKnob, w / 2.0 - 5.5);
            Canvas.SetTop(rotKnob, -24);
            _selectionVisual.Children.Add(rotKnob);

            Canvas.SetLeft(_selectionVisual, x);
            Canvas.SetTop(_selectionVisual, y);

            if (Math.Abs(_selectedElement.Rotation) > 0.01f)
            {
                _selectionVisual.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
                _selectionVisual.RenderTransform = new RotateTransform(_selectedElement.Rotation);
            }

            DrawingOverlayCanvas.Children.Add(_selectionVisual);
        }
    }

    private static void AddCornerHandle(Canvas parent, double left, double top)
    {
        var handle = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(1.5),
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(14, 99, 156)),
            BorderThickness = new Thickness(1.5)
        };
        Canvas.SetLeft(handle, left);
        Canvas.SetTop(handle, top);
        parent.Children.Add(handle);
    }

    private void ShowRotationTooltip(Point pt, double angle, bool isSnapped)
    {
        if (_rotationBadge == null)
        {
            _rotationBadge = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold
                }
            };
            DrawingOverlayCanvas.Children.Add(_rotationBadge);
        }

        if (_rotationBadge.Child is TextBlock tb)
        {
            int roundAngle = (int)Math.Round(angle);
            tb.Text = isSnapped ? $"🧲 {roundAngle}°" : $"{roundAngle}°";
            tb.Foreground = isSnapped ? new SolidColorBrush(Color.FromRgb(64, 169, 255)) : Brushes.White;
        }

        Canvas.SetLeft(_rotationBadge, pt.X + 16);
        Canvas.SetTop(_rotationBadge, pt.Y - 24);
        _rotationBadge.IsVisible = true;
    }

    private void HideRotationTooltip()
    {
        if (_rotationBadge != null)
        {
            _rotationBadge.IsVisible = false;
        }
    }
}
