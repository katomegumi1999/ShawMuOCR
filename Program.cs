using Avalonia;
using System;
using System.Threading;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 关键：尽早开启 Per-Monitor V2 DPI 感知，确保所有线程、全屏快照及 Win32 GDI 函数获取 100% 真实物理像素，根除截图发虚
        try
        {
            if (!User32.SetProcessDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                User32.SetProcessDPIAware();
            }
        }
        catch
        {
            // 兼容性兜底
        }

        const string mutexName = "ShawMuOCR_SingleInstance_Mutex_9B8C7A";
        const string wakeupEventName = "ShawMuOCR_Wakeup_Event_9B8C7A";

        using var mutex = new Mutex(true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例在运行：优先通过命名事件通知已有实例唤醒主窗口
            try
            {
                if (EventWaitHandle.TryOpenExisting(wakeupEventName, out var wakeupEvent))
                {
                    wakeupEvent.Set();
                    wakeupEvent.Dispose();
                    return;
                }
            }
            catch
            {
            }

            // 兜底：尝试通过 Win32 查找已有窗口并还原置顶
            var existingWnd = User32.FindWindow(null, "小沐 OCR");
            if (existingWnd != IntPtr.Zero)
            {
                User32.ShowWindow(existingWnd, User32.SW_RESTORE);
                User32.SetForegroundWindow(existingWnd);
            }
            return;
        }

        // 创建全局命名事件，用于接收后续重复启动时的唤醒信号
        using var singleInstanceWakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, wakeupEventName);
        using var cts = new CancellationTokenSource();

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var waitHandles = new WaitHandle[] { singleInstanceWakeupEvent, cts.Token.WaitHandle };
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    int index = WaitHandle.WaitAny(waitHandles);
                    if (index == 0 && !cts.Token.IsCancellationRequested)
                    {
                        App.WakeupMainWindow();
                    }
                    else
                    {
                        break;
                    }
                }
                catch
                {
                    break;
                }
            }
        });

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            cts.Cancel();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
