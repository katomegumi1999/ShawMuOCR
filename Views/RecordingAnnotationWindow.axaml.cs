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
    private System.Threading.CancellationTokenSource? _clickMonitorCts;
    private bool _isLiveCursorActive;

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
        StopClickMonitoring();
        ExitAnnotation();
    }

    private void ApplyWindowStyles()
    {
        var handle = TryGetPlatformHandle();
        if (handle == null) return;

        var exStyle = User32.GetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE);
        long newExStyle = exStyle.ToInt64() | User32.WS_EX_TOOLWINDOW | User32.WS_EX_LAYERED | User32.WS_EX_TOPMOST;

        if (_isClickThrough)
        {
            // 穿透模式：必须同时具备 WS_EX_LAYERED 与 WS_EX_TRANSPARENT，Windows 才会把鼠标事件完全穿透到底层窗口
            newExStyle |= (User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE);
            IsHitTestVisible = false;
        }
        else
        {
            // 绘制模式：移除穿透与不激活，保留 WS_EX_LAYERED 分层特性，允许接收鼠标绘制与键盘事件
            newExStyle &= ~(User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE);
            IsHitTestVisible = true;
        }

        User32.SetWindowLongPtr(handle.Handle, User32.GWL_EXSTYLE, new IntPtr(newExStyle));

        // 强行指定 HWND_TOPMOST 并移除 SWP_NOZORDER，确保无论怎么切换穿透或退出标注，
        // 透明图层都绝对居于所有常规前台窗口的最顶层
        User32.SetWindowPos(handle.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);

        Topmost = true;
    }

    /// <summary>
    /// 将透明光轨/标注图层强行置于所有常规桌面应用窗口最前端。
    /// </summary>
    public static void BringToFront()
    {
        if (s_sharedOverlay != null && s_sharedOverlay.IsVisible)
        {
            var handle = s_sharedOverlay.TryGetPlatformHandle();
            if (handle != null)
            {
                User32.SetWindowPos(handle.Handle, User32.HWND_TOPMOST, 0, 0, 0, 0,
                    User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            }
        }
    }

    /// <summary>
    /// 开启桌面实时鼠标波纹特效（完全穿透模式，不干扰日常桌面操作）。
    /// </summary>
    public static void StartLiveCursorEffect(ScreenRect bounds, MuRecorder.ViewModels.RecordingViewModel? vm = null)
    {
        if (vm != null)
        {
            s_vm = vm;
        }

        if (s_sharedOverlay == null)
        {
            s_sharedOverlay = new RecordingAnnotationWindow();
        }

        var screen = s_sharedOverlay.Screens.ScreenFromPoint(new PixelPoint(bounds.X, bounds.Y))
            ?? s_sharedOverlay.Screens.Primary;
        double scaling = screen?.Scaling ?? 1.0;
        s_sharedOverlay.Position = new PixelPoint(bounds.X, bounds.Y);
        s_sharedOverlay.Width = bounds.Width / scaling;
        s_sharedOverlay.Height = bounds.Height / scaling;

        s_sharedOverlay._isLiveCursorActive = true;

        // 若当前未在使用标注画笔工具，则保持穿透模式，保证 0 延迟穿透到任何底层软件
        if (s_vm?.IsAnnotationActive != true)
        {
            s_sharedOverlay._isClickThrough = true;
        }

        if (!s_sharedOverlay.IsVisible)
        {
            s_sharedOverlay.Show();
        }

        s_sharedOverlay.ApplyWindowStyles();
        s_sharedOverlay.StartClickMonitoring(bounds, scaling);
    }

    /// <summary>
    /// 停止桌面实时鼠标波纹特效。
    /// </summary>
    public static void StopLiveCursorEffect()
    {
        if (s_sharedOverlay != null)
        {
            s_sharedOverlay._isLiveCursorActive = false;
            s_sharedOverlay.StopClickMonitoring();
            s_sharedOverlay._canvas.ClearLiveEffects();

            // 若当前未在使用屏幕标注画笔，则隐藏窗口
            if (s_vm?.IsAnnotationActive != true)
            {
                s_sharedOverlay.Hide();
            }
        }
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

        var screen = s_sharedOverlay.Screens.ScreenFromPoint(new PixelPoint(bounds.X, bounds.Y))
            ?? s_sharedOverlay.Screens.Primary;
        double scaling = screen?.Scaling ?? 1.0;
        s_sharedOverlay.Position = new PixelPoint(bounds.X, bounds.Y);
        s_sharedOverlay.Width = bounds.Width / scaling;
        s_sharedOverlay.Height = bounds.Height / scaling;

        // 进入标注绘制模式：取消穿透，使窗口能够接收画笔拖拽
        s_sharedOverlay._isClickThrough = false;

        if (!s_sharedOverlay.IsVisible)
        {
            s_sharedOverlay.Show();
        }

        s_sharedOverlay.Activate();
        s_sharedOverlay.Focus();
        s_sharedOverlay.ApplyWindowStyles();

        // 若处于录制中且开启了桌面实时特效，确保后台鼠标采集线程持续工作
        if (s_sharedOverlay._clickMonitorCts == null && s_vm?.IsRecording == true &&
            (s_vm?.LiveCursorEffect == true || s_vm?.CursorTrail == true))
        {
            s_sharedOverlay.StartClickMonitoring(bounds, scaling);
        }
    }

    /// <summary>
    /// 隐藏屏幕标注层并清空笔迹。
    /// </summary>
    public static void HideOverlay()
    {
        if (s_sharedOverlay != null && s_sharedOverlay.IsVisible)
        {
            s_sharedOverlay.StopClickMonitoring();
            s_sharedOverlay._isLiveCursorActive = false;
            s_sharedOverlay.Hide();
            Clear();
        }
    }

    /// <summary>
    /// 安全退出屏幕标注模式并同步 ViewModel 状态。
    /// 若处于录制中且开启了桌面实时特效（波纹或光轨），则无缝切回穿透模式继续展示特效，绝不关闭窗口或销毁监测线程。
    /// </summary>
    public static void ExitAnnotation()
    {
        bool shouldKeepLive = (s_sharedOverlay?._isLiveCursorActive == true) ||
                              (s_vm?.IsRecording == true && (s_vm?.LiveCursorEffect == true || s_vm?.CursorTrail == true));

        if (s_sharedOverlay != null)
        {
            if (shouldKeepLive)
            {
                // 若仍在录屏且开启了实时光标特效，切回完全穿透模式继续展示光轨与波纹，清空墨迹但不关闭窗口
                s_sharedOverlay._isLiveCursorActive = true;
                SetClickThrough(true);
                Clear();

                // 确保点击与光轨后台监控线程保持运行
                if (s_sharedOverlay._clickMonitorCts == null && s_vm != null)
                {
                    var bounds = s_vm.CurrentTargetBounds;
                    if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        bounds = s_vm.SelectedMonitor?.Bounds
                            ?? (s_vm.Monitors.FirstOrDefault(m => m.IsPrimary) ?? s_vm.Monitors.FirstOrDefault())?.Bounds
                            ?? new ScreenRect(0, 0, 1920, 1080);
                    }
                    var screen = s_sharedOverlay.Screens.ScreenFromPoint(new PixelPoint(bounds.X, bounds.Y)) ?? s_sharedOverlay.Screens.Primary;
                    double scaling = screen?.Scaling ?? 1.0;
                    s_sharedOverlay.StartClickMonitoring(bounds, scaling);
                }
            }
            else
            {
                HideOverlay();
            }
        }
        if (s_vm != null)
        {
            s_vm.IsAnnotationActive = false;
            s_vm.IsAnnotationClickThrough = false;
            s_vm.StatusMessage = "已退出屏幕标注模式";
        }
        RecordingFloatingWindow.BringToFront();
    }

    private void StartClickMonitoring(ScreenRect bounds, double scaling)
    {
        StopClickMonitoring();
        var cts = new System.Threading.CancellationTokenSource();
        _clickMonitorCts = cts;
        var token = cts.Token;

        var thread = new System.Threading.Thread(() =>
        {
            bool wasLDown = false;
            bool wasRDown = false;
            int lastPtX = int.MinValue;
            int lastPtY = int.MinValue;

            while (!token.IsCancellationRequested)
            {
                // 读取鼠标左键 (0x01) 与右键 (0x02) 的瞬时物理状态
                bool isLDown = (User32.GetAsyncKeyState(0x01) & 0x8000) != 0;
                bool isRDown = (User32.GetAsyncKeyState(0x02) & 0x8000) != 0;

                if (User32.GetCursorPos(out var pt))
                {
                    double localX = (pt.x - bounds.X) / scaling;
                    double localY = (pt.y - bounds.Y) / scaling;
                    bool inBounds = localX >= 0 && localX <= bounds.Width / scaling &&
                                    localY >= 0 && localY <= bounds.Height / scaling;

                    // 上升沿检测：从未按下变为按下瞬间（点击波纹）
                    if (s_vm?.CursorClickEffect != false && ((isLDown && !wasLDown) || (isRDown && !wasRDown)))
                    {
                        bool isRight = isRDown && !wasRDown;
                        if (inBounds)
                        {
                            var clickPt = new Point(localX, localY);
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                _canvas.AddClickRipple(clickPt, isRight);
                            }, Avalonia.Threading.DispatcherPriority.Input);
                        }
                    }

                    // 实时光轨采集：开启光轨且光标在目标录屏区域内移动
                    if (s_vm?.CursorTrail == true && inBounds)
                    {
                        if (pt.x != lastPtX || pt.y != lastPtY)
                        {
                            lastPtX = pt.x;
                            lastPtY = pt.y;
                            var trailPt = new Point(localX, localY);
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                _canvas.AddTrailPoint(trailPt);
                            }, Avalonia.Threading.DispatcherPriority.Input);
                        }
                    }
                }

                wasLDown = isLDown;
                wasRDown = isRDown;

                try
                {
                    // 8ms 轮询周期 (~125 Hz)，兼顾实时光轨高帧率追踪与按键响应，CPU 占用极低 (<0.02%)
                    System.Threading.Thread.Sleep(8);
                }
                catch
                {
                    break;
                }
            }
        })
        {
            IsBackground = true,
            Name = "LiveCursorEffectMonitor"
        };
        thread.Start();
    }

    private void StopClickMonitoring()
    {
        if (_clickMonitorCts != null)
        {
            _clickMonitorCts.Cancel();
            _clickMonitorCts.Dispose();
            _clickMonitorCts = null;
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
        RecordingFloatingWindow.BringToFront();
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
    private sealed class AnnotationCanvasControl : Control, Avalonia.Rendering.ICustomHitTest
    {
        private readonly RecordingAnnotationWindow _owner;
        private readonly List<ScreenAnnotationStroke> _strokes = new();
        private readonly Stack<ScreenAnnotationStroke> _redoStack = new();

        private ScreenAnnotationStroke? _activeStroke;
        private Point _laserPosition;
        private bool _isLaserActive;

        private sealed class ClickRippleItem
        {
            public Point Center { get; }
            public long StartTime { get; }
            public bool IsRightClick { get; }

            public ClickRippleItem(Point center, long startTime, bool isRightClick)
            {
                Center = center;
                StartTime = startTime;
                IsRightClick = isRightClick;
            }
        }

        private sealed class TrailPointItem
        {
            public Point Center { get; }
            public long Timestamp { get; }

            public TrailPointItem(Point center, long timestamp)
            {
                Center = center;
                Timestamp = timestamp;
            }
        }

        private readonly List<ClickRippleItem> _activeRipples = new();
        private readonly List<TrailPointItem> _activeTrail = new();
        private Avalonia.Threading.DispatcherTimer? _effectTimer;

        public void AddClickRipple(Point pt, bool isRightClick)
        {
            lock (_activeRipples)
            {
                _activeRipples.Add(new ClickRippleItem(pt, System.Diagnostics.Stopwatch.GetTimestamp(), isRightClick));
            }

            EnsureEffectTimer();
            InvalidateVisual();
        }

        public void AddTrailPoint(Point pt)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            lock (_activeTrail)
            {
                if (_activeTrail.Count > 0)
                {
                    var last = _activeTrail[^1];
                    double dx = pt.X - last.Center.X;
                    double dy = pt.Y - last.Center.Y;
                    if (dx * dx + dy * dy < 2.0) return; // 距离微小则跳过，避免点位扎堆
                }
                _activeTrail.Add(new TrailPointItem(pt, now));
                if (_activeTrail.Count > 40)
                {
                    _activeTrail.RemoveAt(0);
                }
            }

            EnsureEffectTimer();
            InvalidateVisual();
        }

        private void EnsureEffectTimer()
        {
            if (_effectTimer == null)
            {
                _effectTimer = new Avalonia.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16) // ~60fps 平滑补间
                };
                _effectTimer.Tick += OnEffectTimerTick;
                _effectTimer.Start();
            }
        }

        private void OnEffectTimerTick(object? sender, EventArgs e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double freq = System.Diagnostics.Stopwatch.Frequency;
            bool hasRipples = false;
            bool hasTrail = false;

            lock (_activeRipples)
            {
                // 350ms 扩散生命周期
                _activeRipples.RemoveAll(r => (now - r.StartTime) / freq > 0.35);
                hasRipples = _activeRipples.Count > 0;
            }

            double trailLifetime = (s_vm != null && s_vm.CursorTrailMs > 0 ? s_vm.CursorTrailMs : 200) / 1000.0;
            lock (_activeTrail)
            {
                _activeTrail.RemoveAll(t => (now - t.Timestamp) / freq > trailLifetime);
                hasTrail = _activeTrail.Count > 0;
            }

            if (!hasRipples && !hasTrail)
            {
                _effectTimer?.Stop();
                _effectTimer = null;
            }

            InvalidateVisual();
        }

        public void ClearLiveEffects()
        {
            lock (_activeRipples)
            {
                _activeRipples.Clear();
            }
            lock (_activeTrail)
            {
                _activeTrail.Clear();
            }
            _effectTimer?.Stop();
            _effectTimer = null;
            InvalidateVisual();
        }

        public AnnotationCanvasControl(RecordingAnnotationWindow owner)
        {
            _owner = owner;
            ClipToBounds = true;
            Focusable = true;
        }

        public bool HitTest(Point point)
        {
            return !_owner._isClickThrough && new Rect(Bounds.Size).Contains(point);
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
            lock (_activeRipples)
            {
                _activeRipples.Clear();
            }
            lock (_activeTrail)
            {
                _activeTrail.Clear();
            }
            _effectTimer?.Stop();
            _effectTimer = null;
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
            e.Handled = true;
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
                e.Handled = true;
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
                e.Handled = true;
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
                e.Handled = true;
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
                e.Handled = true;
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // 绘制极低透明度像素（Alpha=1），确保整个画布几何区域常驻 Avalonia 命中测试渲染树
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, new Rect(Bounds.Size));

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

            // 4. 绘制桌面实时点击水波纹 (350ms 扩散淡出圆环)
            lock (_activeRipples)
            {
                if (_activeRipples.Count > 0)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double freq = System.Diagnostics.Stopwatch.Frequency;

                    foreach (var ripple in _activeRipples)
                    {
                        double elapsed = (now - ripple.StartTime) / freq;
                        if (elapsed < 0 || elapsed > 0.35) continue;

                        double t = Math.Clamp(elapsed / 0.35, 0.0, 1.0);
                        // 4px -> 24px ease-out 紧凑平滑扩散曲线（直径 48px，与 Rust 核心保持绝对一致）
                        double easeOut = 1.0 - Math.Pow(1.0 - t, 2.0);
                        double radius = 4.0 + 20.0 * easeOut;
                        double alpha = 1.0 - t;

                        // 双层描边设计：浅色背景有深色描边衬托，深色背景有亮色主体发光，任何场景均清晰可辨
                        byte darkAlpha = (byte)(alpha * 140);
                        byte mainAlpha = (byte)(alpha * 240);

                        // 外层对比深色圈 (半径 r，笔宽 2.8)
                        var darkPen = new Pen(new SolidColorBrush(Color.FromArgb(darkAlpha, 0, 0, 0)), 2.8);
                        context.DrawEllipse(null, darkPen, ripple.Center, radius, radius);

                        // 内层亮色主体圈 (半径 r，笔宽 1.6)
                        // 左键为科技青蓝 (#00E5FF)，右键为活力金黄 (#FFB300)
                        Color mainCol = ripple.IsRightClick
                            ? Color.FromArgb(mainAlpha, 255, 179, 0)
                            : Color.FromArgb(mainAlpha, 0, 229, 255);
                        var mainPen = new Pen(new SolidColorBrush(mainCol), 2.0);
                        context.DrawEllipse(null, mainPen, ripple.Center, radius, radius);
                    }
                }
            }

            // 5. 绘制桌面实时流光光轨 (Cursor Trail - 连续圆润流光平滑曲线)
            lock (_activeTrail)
            {
                if (_activeTrail.Count >= 2)
                {
                    long now = System.Diagnostics.Stopwatch.GetTimestamp();
                    double freq = System.Diagnostics.Stopwatch.Frequency;
                    double lifetime = (s_vm != null && s_vm.CursorTrailMs > 0 ? s_vm.CursorTrailMs : 200) / 1000.0;

                    // 1. 过滤有效时间内的采样点，并去除微小抖动的密邻点 (距离 < 3px)
                    var rawPoints = new List<(Point Pos, double NormA)>(_activeTrail.Count);
                    for (int i = 0; i < _activeTrail.Count; i++)
                    {
                        var p = _activeTrail[i];
                        double elapsed = (now - p.Timestamp) / freq;
                        if (elapsed < 0 || elapsed > lifetime) continue;

                        double remain = Math.Clamp(1.0 - (elapsed / lifetime), 0.0, 1.0);
                        double normA = Math.Pow(remain, 1.5);
                        if (normA <= 0.02) continue;

                        if (rawPoints.Count > 0)
                        {
                            var prev = rawPoints[^1].Pos;
                            double dx = p.Center.X - prev.X;
                            double dy = p.Center.Y - prev.Y;
                            if (dx * dx + dy * dy < 9.0) continue;
                        }
                        rawPoints.Add((p.Center, normA));
                    }

                    if (rawPoints.Count >= 2)
                    {
                        // 2. 使用 Chaikin 拐角细分算法生成无折线、无自交的自然平滑曲线节点
                        var smoothPts = rawPoints;
                        if (smoothPts.Count >= 3)
                        {
                            for (int round = 0; round < 2; round++)
                            {
                                var next = new List<(Point Pos, double NormA)>(smoothPts.Count * 2);
                                next.Add(smoothPts[0]); // 保持尾梢起点
                                for (int i = 0; i < smoothPts.Count - 1; i++)
                                {
                                    var p0 = smoothPts[i];
                                    var p1 = smoothPts[i + 1];

                                    var qPos = new Point(0.75 * p0.Pos.X + 0.25 * p1.Pos.X, 0.75 * p0.Pos.Y + 0.25 * p1.Pos.Y);
                                    double qA = 0.75 * p0.NormA + 0.25 * p1.NormA;
                                    next.Add((qPos, qA));

                                    var rPos = new Point(0.25 * p0.Pos.X + 0.75 * p1.Pos.X, 0.25 * p0.Pos.Y + 0.75 * p1.Pos.Y);
                                    double rA = 0.25 * p0.NormA + 0.75 * p1.NormA;
                                    next.Add((rPos, rA));
                                }
                                next.Add(smoothPts[^1]); // 保持指针头部终点
                                smoothPts = next;
                            }
                        }

                        // 3. 将平滑节点分成 3~5 段连续整条的 StreamGeometry 矢量路径绘制
                        // 严禁对每个微小线段独立调用 DrawLine（否则大量端点半透明 Round Cap 会叠加形成密密麻麻的点）
                        int numSections = Math.Clamp(smoothPts.Count / 4, 2, 5);
                        var sectionGeoms = new List<(StreamGeometry Geom, Pen GlowPen, Pen CorePen)>(numSections);

                        for (int s = 0; s < numSections; s++)
                        {
                            int idxStart = s * (smoothPts.Count - 1) / numSections;
                            int idxEnd = (s + 1) * (smoothPts.Count - 1) / numSections;
                            if (idxEnd <= idxStart) continue;

                            double normA = smoothPts[idxEnd].NormA;
                            if (normA <= 0.02) continue;

                            double glowWidth = 1.6 + 5.0 * normA;
                            double coreWidth = 0.8 + 2.0 * normA;

                            byte glowAlpha = (byte)Math.Clamp(normA * 190.0, 0, 255);
                            byte coreAlpha = (byte)Math.Clamp(normA * 245.0, 0, 255);

                            var glowPen = new Pen(
                                new SolidColorBrush(Color.FromArgb(glowAlpha, 0, 229, 255)),
                                glowWidth,
                                lineCap: PenLineCap.Round,
                                lineJoin: PenLineJoin.Round);

                            var corePen = new Pen(
                                new SolidColorBrush(Color.FromArgb(coreAlpha, 255, 255, 255)),
                                coreWidth,
                                lineCap: PenLineCap.Round,
                                lineJoin: PenLineJoin.Round);

                            var geom = new StreamGeometry();
                            using (var ctx = geom.Open())
                            {
                                ctx.BeginFigure(smoothPts[idxStart].Pos, false);
                                for (int k = idxStart + 1; k <= idxEnd; k++)
                                {
                                    ctx.LineTo(smoothPts[k].Pos);
                                }
                            }

                            sectionGeoms.Add((geom, glowPen, corePen));
                        }

                        // 第一遍：绘制所有连续区段的外层青蓝发光 (#00E5FF)
                        foreach (var item in sectionGeoms)
                        {
                            context.DrawGeometry(null, item.GlowPen, item.Geom);
                        }

                        // 第二遍：在上层绘制所有连续区段的高亮纯白核心 (#FFFFFF)
                        foreach (var item in sectionGeoms)
                        {
                            context.DrawGeometry(null, item.CorePen, item.Geom);
                        }
                    }
                }
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
