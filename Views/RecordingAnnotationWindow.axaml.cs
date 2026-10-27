using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Views;

public enum ScreenAnnotationTool
{
    Pen = 0,
    Arrow = 1,
    Rectangle = 2,
    Ellipse = 3,
    Laser = 4
}

public sealed class ScreenAnnotationStroke
{
    public ScreenAnnotationTool Tool { get; init; }
    public Color Color { get; init; }
    public double StrokeWidth { get; init; }
    public List<Point> Points { get; } = new();
    public Point StartPoint { get; set; }
    public Point EndPoint { get; set; }
}

public partial class RecordingAnnotationWindow : Window
{
    private static RecordingAnnotationWindow? s_sharedOverlay;
    private static MuRecorder.ViewModels.RecordingViewModel? s_vm;

    private readonly AnnotationCanvasControl _canvas;
    private ScreenAnnotationTool _currentTool = ScreenAnnotationTool.Pen;
    private Color _currentColor = Color.FromRgb(255, 77, 79); // 鲜亮红
    private double _currentStrokeWidth = 4.0;
    private bool _isClickThrough;

    public static bool IsOpen => s_sharedOverlay?.IsVisible == true;
    public static bool CurrentIsClickThrough => s_sharedOverlay?._isClickThrough ?? false;

    public RecordingAnnotationWindow()
    {
        InitializeComponent();

        _canvas = new AnnotationCanvasControl(this);
        var root = this.FindControl<Panel>("RootPanel");
        if (root != null)
        {
            root.Children.Add(_canvas);
        }
        else
        {
            Content = _canvas;
        }

        Opened += OnOpened;
        KeyDown += OnWindowKeyDown;
        Closing += OnWindowClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyWindowStyles();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitAnnotation();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            Undo();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        e.Cancel = true;
        ExitAnnotation();
    }

    private void ApplyWindowStyles()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;

        var exStyle = User32.GetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | User32.WS_EX_TOOLWINDOW;

        if (_isClickThrough)
        {
            // 穿透模式：设置透明与不激活，鼠标与键盘直接穿透到底层窗口
            newExStyle |= (User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE);
        }
        else
        {
            // 绘制模式：移除穿透与不激活，允许接收键盘 Esc/快捷键与鼠标绘制事件
            newExStyle &= ~(User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE);
        }

        User32.SetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE, new IntPtr(newExStyle));
    }

    /// <summary>
    /// 显示并定位透明屏幕标注层。
    /// </summary>
    public static void ShowOverlay(ScreenRect bounds, MuRecorder.ViewModels.RecordingViewModel? vm = null)
    {
        if (vm != null)
        {
            s_vm = vm;
        }

        if (s_sharedOverlay == null)
        {
            s_sharedOverlay = new RecordingAnnotationWindow();
        }

        double scaling = s_sharedOverlay.GetCurrentScreenScaling();
        s_sharedOverlay.Position = new PixelPoint(bounds.X, bounds.Y);
        s_sharedOverlay.Width = bounds.Width / scaling;
        s_sharedOverlay.Height = bounds.Height / scaling;

        if (!s_sharedOverlay.IsVisible)
        {
            s_sharedOverlay.Show();
        }

        s_sharedOverlay.Activate();
        s_sharedOverlay.Focus();
        s_sharedOverlay.ApplyWindowStyles();
    }

    /// <summary>
    /// 隐藏屏幕标注层并清空笔迹。
    /// </summary>
    public static void HideOverlay()
    {
        if (s_sharedOverlay != null && s_sharedOverlay.IsVisible)
        {
            s_sharedOverlay.Hide();
            Clear();
        }
    }

    /// <summary>
    /// 安全退出屏幕标注模式并同步 ViewModel 状态。
    /// </summary>
    public static void ExitAnnotation()
    {
        HideOverlay();
        if (s_vm != null)
        {
            s_vm.IsAnnotationActive = false;
            s_vm.IsAnnotationClickThrough = false;
            s_vm.StatusMessage = "已退出屏幕标注模式";
        }
    }

    public static void SetTool(ScreenAnnotationTool tool)
    {
        if (s_sharedOverlay == null) return;
        s_sharedOverlay._currentTool = tool;
        // 选择任何画笔工具时自动取消穿透
        SetClickThrough(false);
    }

    public static void SetColor(Color color)
    {
        if (s_sharedOverlay == null) return;
        s_sharedOverlay._currentColor = color;
    }

    public static void SetStrokeWidth(double width)
    {
        if (s_sharedOverlay == null) return;
        s_sharedOverlay._currentStrokeWidth = Math.Clamp(width, 1.0, 32.0);
    }

    public static void SetClickThrough(bool clickThrough)
    {
        if (s_sharedOverlay == null) return;
        s_sharedOverlay._isClickThrough = clickThrough;
        s_sharedOverlay.ApplyWindowStyles();
    }

    public static void Undo() => s_sharedOverlay?._canvas.Undo();

    public static void Clear() => s_sharedOverlay?._canvas.Clear();

    private double GetCurrentScreenScaling()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        return screen?.Scaling ?? 1.0;
    }

    /// <summary>
    /// 内部高性能 Skia / Direct3D 向量绘制控件
    /// </summary>
    private sealed class AnnotationCanvasControl : Control
    {
        private readonly RecordingAnnotationWindow _owner;
        private readonly List<ScreenAnnotationStroke> _strokes = new();
        private readonly Stack<ScreenAnnotationStroke> _redoStack = new();

        private ScreenAnnotationStroke? _activeStroke;
        private Point _laserPosition;
        private bool _isLaserActive;

        public AnnotationCanvasControl(RecordingAnnotationWindow owner)
        {
            _owner = owner;
            ClipToBounds = true;
        }

        public void Undo()
        {
            if (_strokes.Count > 0)
            {
                var last = _strokes[^1];
                _strokes.RemoveAt(_strokes.Count - 1);
                _redoStack.Push(last);
                InvalidateVisual();
            }
        }

        public void Clear()
        {
            _strokes.Clear();
            _redoStack.Clear();
            _activeStroke = null;
            _isLaserActive = false;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_owner._isClickThrough) return;

            var ptProps = e.GetCurrentPoint(this).Properties;
            if (ptProps.IsRightButtonPressed)
            {
                // 鼠标右键单击：快速退出屏幕标注模式，一键恢复普通桌面鼠标操作
                ExitAnnotation();
                e.Handled = true;
                return;
            }

            if (!ptProps.IsLeftButtonPressed) return;

            var pt = e.GetPosition(this);
            e.Pointer.Capture(this);

            if (_owner._currentTool == ScreenAnnotationTool.Laser)
            {
                _laserPosition = pt;
                _isLaserActive = true;
                InvalidateVisual();
                return;
            }

            _activeStroke = new ScreenAnnotationStroke
            {
                Tool = _owner._currentTool,
                Color = _owner._currentColor,
                StrokeWidth = _owner._currentStrokeWidth,
                StartPoint = pt,
                EndPoint = pt
            };
            _activeStroke.Points.Add(pt);
            _redoStack.Clear();

            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_owner._isClickThrough) return;

            var pt = e.GetPosition(this);

            if (_owner._currentTool == ScreenAnnotationTool.Laser)
            {
                _laserPosition = pt;
                _isLaserActive = true;
                InvalidateVisual();
                return;
            }

            if (_activeStroke != null)
            {
                _activeStroke.EndPoint = pt;
                if (_activeStroke.Tool == ScreenAnnotationTool.Pen)
                {
                    _activeStroke.Points.Add(pt);
                }
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_owner._isClickThrough) return;

            e.Pointer.Capture(null);

            if (_owner._currentTool == ScreenAnnotationTool.Laser)
            {
                _isLaserActive = false;
                InvalidateVisual();
                return;
            }

            if (_activeStroke != null)
            {
                var pt = e.GetPosition(this);
                _activeStroke.EndPoint = pt;
                if (_activeStroke.Tool == ScreenAnnotationTool.Pen)
                {
                    _activeStroke.Points.Add(pt);
                }

                _strokes.Add(_activeStroke);
                _activeStroke = null;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // 1. 绘制历史所有已完成笔迹
            foreach (var stroke in _strokes)
            {
                RenderStroke(context, stroke);
            }

            // 2. 绘制当前正在拖拽的笔迹预览
            if (_activeStroke != null)
            {
                RenderStroke(context, _activeStroke);
            }

            // 3. 绘制激光笔
            if (_isLaserActive && _owner._currentTool == ScreenAnnotationTool.Laser)
            {
                // 红色发光外光圈
                var glowBrush = new SolidColorBrush(Color.FromArgb(80, 255, 77, 79));
                context.DrawEllipse(glowBrush, null, _laserPosition, 16, 16);

                // 核心纯红亮点
                var coreBrush = new SolidColorBrush(Color.FromRgb(255, 77, 79));
                context.DrawEllipse(coreBrush, null, _laserPosition, 5, 5);

                // 中心白芯
                var whiteBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                context.DrawEllipse(whiteBrush, null, _laserPosition, 2, 2);
            }
        }

        private static void RenderStroke(DrawingContext context, ScreenAnnotationStroke stroke)
        {
            var brush = new SolidColorBrush(stroke.Color);
            var pen = new Pen(brush, stroke.StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

            switch (stroke.Tool)
            {
                case ScreenAnnotationTool.Pen:
                    DrawPen(context, pen, stroke.Points);
                    break;
                case ScreenAnnotationTool.Arrow:
                    DrawArrow(context, pen, brush, stroke.StartPoint, stroke.EndPoint);
                    break;
                case ScreenAnnotationTool.Rectangle:
                    DrawRectangle(context, pen, stroke.StartPoint, stroke.EndPoint);
                    break;
                case ScreenAnnotationTool.Ellipse:
                    DrawEllipse(context, pen, stroke.StartPoint, stroke.EndPoint);
                    break;
            }
        }

        private static void DrawPen(DrawingContext context, Pen pen, IReadOnlyList<Point> points)
        {
            if (points.Count == 0) return;
            if (points.Count == 1)
            {
                context.DrawEllipse(pen.Brush, null, points[0], pen.Thickness / 2, pen.Thickness / 2);
                return;
            }

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(points[0], false);
                for (int i = 1; i < points.Count; i++)
                {
                    ctx.LineTo(points[i]);
                }
                ctx.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geom);
        }

        private static void DrawArrow(DrawingContext context, Pen pen, Brush brush, Point start, Point end)
        {
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 4) return;

            // 绘制主干线
            context.DrawLine(pen, start, end);

            // 绘制端点箭头头
            double angle = Math.Atan2(dy, dx);
            double headLength = Math.Clamp(len * 0.25, 14, 28);
            double headAngle = Math.PI / 6; // 30度角

            Point p1 = new(
                end.X - headLength * Math.Cos(angle - headAngle),
                end.Y - headLength * Math.Sin(angle - headAngle));
            Point p2 = new(
                end.X - headLength * Math.Cos(angle + headAngle),
                end.Y - headLength * Math.Sin(angle + headAngle));

            var headGeom = new StreamGeometry();
            using (var ctx = headGeom.Open())
            {
                ctx.BeginFigure(end, true);
                ctx.LineTo(p1);
                ctx.LineTo(p2);
                ctx.EndFigure(true);
            }
            context.DrawGeometry(brush, pen, headGeom);
        }

        private static void DrawRectangle(DrawingContext context, Pen pen, Point p1, Point p2)
        {
            double x = Math.Min(p1.X, p2.X);
            double y = Math.Min(p1.Y, p2.Y);
            double w = Math.Abs(p1.X - p2.X);
            double h = Math.Abs(p1.Y - p2.Y);
            if (w < 1 && h < 1) return;

            var rect = new Rect(x, y, w, h);
            context.DrawRectangle(null, pen, rect, 4, 4);
        }

        private static void DrawEllipse(DrawingContext context, Pen pen, Point p1, Point p2)
        {
            double cx = (p1.X + p2.X) / 2.0;
            double cy = (p1.Y + p2.Y) / 2.0;
            double rx = Math.Abs(p1.X - p2.X) / 2.0;
            double ry = Math.Abs(p1.Y - p2.Y) / 2.0;
            if (rx < 1 && ry < 1) return;

            context.DrawEllipse(null, pen, new Point(cx, cy), rx, ry);
        }
    }
}
