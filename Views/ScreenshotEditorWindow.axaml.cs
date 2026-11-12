using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
/// 现代化全功能截图标注独立弹窗 (无边框、多屏自适应、置顶与操作系统窗口生命周期管理)
/// 内部核心标注图元绘制与全功能两行悬浮工具栏统一委托给 <see cref="Components.ScreenshotEditorControl"/>。
/// </summary>
public partial class ScreenshotEditorWindow : Window
{
    private static bool s_isPrewarmed;
    public static bool IsPrewarmed => s_isPrewarmed;
    public static void ResetPrewarmedForTesting() => s_isPrewarmed = false;

    private readonly bool _isPrewarmInstance;

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
        bool isPrewarm = false,
        Func<ScreenRect, Task>? longScreenshotCallback = null,
        Func<ScreenRect, Task>? regionRecordCallback = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        _isPrewarmInstance = isPrewarm;

        InitializeComponent();

        EditorControl.Initialize(
            image,
            clipboardService,
            ocrCallback,
            translateCallback,
            settingsService,
            logger,
            selectionBounds,
            fullScreenshot,
            cropOffset,
            isPrewarm,
            longScreenshotCallback,
            regionRecordCallback);

        EditorControl.RequestClose += (_, _) => Close();
        EditorControl.RequestMinimize += (_, _) => WindowState = WindowState.Minimized;
        EditorControl.RequestToggleMaximize += (_, _) => ToggleMaximize();
        EditorControl.RequestMoveDrag += (_, e) => BeginMoveDrag(e);
        EditorControl.RequestApplyDimensions += (_, recenter) => ApplyDimensions(recenter);

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

        PropertyChanged += (s, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                EditorControl.UpdateMaximizeButtonIcon();
                ApplyDimensions(recenter: false);
                EditorControl.RefreshDisplay();
            }
        };

        Closed += (s, e) => EditorControl.SaveAnnotationSettings();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        EditorControl.UpdateMaximizeButtonIcon();
        EditorControl.ReactivateWindowAndIme();
    }

    private Screen? GetTargetScreen()
    {
        try
        {
            var bounds = EditorControl.SelectionBounds;
            if (bounds.HasValue && Screens != null)
            {
                var centerPt = new PixelPoint(
                    bounds.Value.X + bounds.Value.Width / 2,
                    bounds.Value.Y + bounds.Value.Height / 2);
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

        double imgW = EditorControl.Document.OriginalImage.Width;
        double imgH = EditorControl.Document.OriginalImage.Height;

        double dipW = imgW / scaling;
        double dipH = imgH / scaling;

        double fitScale = 1.0;
        if (dipW > maxW || dipH > maxH)
        {
            fitScale = Math.Min(maxW / dipW, maxH / dipH);
        }

        double displayScale = (1.0 / scaling) * fitScale;

        double finalCanvasW = imgW * displayScale;
        double finalCanvasH = imgH * displayScale;

        EditorControl.SetCanvasAndDisplayScale(finalCanvasW, finalCanvasH, displayScale);

        if (WindowState != WindowState.Maximized)
        {
            double totalDipW = Math.Clamp(Math.Max(finalCanvasW + ChromeExtraWidth, 890.0), 890.0, workingW / scaling);
            double totalDipH = Math.Clamp(Math.Max(finalCanvasH + ChromeExtraHeight, 480.0), 480.0, workingH / scaling);

            Width = totalDipW;
            Height = totalDipH;

            if (recenter)
            {
                int totalPxW = (int)Math.Round(totalDipW * scaling);
                int totalPxH = (int)Math.Round(totalDipH * scaling);

                int posX;
                int posY;

                var sel = EditorControl.SelectionBounds;
                if (sel.HasValue)
                {
                    int selCenterX = sel.Value.X + sel.Value.Width / 2;
                    int selCenterY = sel.Value.Y + sel.Value.Height / 2;

                    posX = selCenterX - (totalPxW / 2);
                    posY = selCenterY - (totalPxH / 2);

                    int minX = workX + 10;
                    int maxX = workX + (int)workingW - totalPxW - 10;
                    int minY = workY + 10;
                    int maxY = workY + (int)workingH - totalPxH - 10;

                    if (maxX < minX) posX = minX;
                    else posX = Math.Clamp(posX, minX, maxX);

                    if (maxY < minY) posY = minY;
                    else posY = Math.Clamp(posY, minY, maxY);
                }
                else
                {
                    posX = workX + (int)((workingW - totalPxW) / 2);
                    posY = workY + (int)((workingH - totalPxH) / 2);
                }

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
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        EditorControl.DisposeResources();
    }
}
