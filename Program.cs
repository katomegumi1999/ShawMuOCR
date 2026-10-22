using Avalonia;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR;

sealed class Program
{
    public static bool IsClipStandaloneMode { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        string processName = AppDomain.CurrentDomain.FriendlyName;
        if (Array.Exists(args, a => a.Equals("--clip", StringComparison.OrdinalIgnoreCase) || a.Equals("--muclip", StringComparison.OrdinalIgnoreCase))
            || processName.Contains("MuClip", StringComparison.OrdinalIgnoreCase))
        {
            IsClipStandaloneMode = true;
        }

        // 禁用 Sdcb.SimdPaddleOCR 2.0 默认请求的 VK_QUEUE_GLOBAL_PRIORITY_HIGH_KHR (需要 Windows 管理员 SeIncreaseBasePriorityPrivilege 权限)，
        // 防止非管理员模式下部分显卡驱动在拒绝高优先级 Vulkan 队列或二次重试 vkCreateDevice 时发生原生层崩溃退出
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMD_OCR_VK_PRIORITY")))
        {
            Environment.SetEnvironmentVariable("SIMD_OCR_VK_PRIORITY", "off");
        }

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

        Mutex? mutex = null;
        bool createdNew = false;
        try
        {
            mutex = new Mutex(true, mutexName, out createdNew);
            TryGrantEveryoneMutexAccess(mutex);
        }
        catch (UnauthorizedAccessException)
        {
            // 已存在由管理员权限创建的实例，当前非管理员进程无权直接创建同名 Mutex，视为已有实例在运行
            createdNew = false;
        }
        catch (Exception ex)
        {
            WriteBootstrapCrashLog("创建单实例 Mutex 异常", ex);
            createdNew = true;
        }

        using (mutex)
        {
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
            EventWaitHandle? singleInstanceWakeupEvent = null;
            try
            {
                singleInstanceWakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, wakeupEventName);
                TryGrantEveryoneEventAccess(singleInstanceWakeupEvent);
            }
            catch (Exception ex)
            {
                WriteBootstrapCrashLog("创建单实例唤醒事件异常", ex);
            }

            using (singleInstanceWakeupEvent)
            using (var cts = new CancellationTokenSource())
            {
                if (singleInstanceWakeupEvent != null)
                {
                    var waitEvent = singleInstanceWakeupEvent;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var waitHandles = new WaitHandle[] { waitEvent, cts.Token.WaitHandle };
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
                }

                try
                {
                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                }
                catch (Exception ex)
                {
                    WriteBootstrapCrashLog("程序启动或主循环发生未捕获致命异常", ex);
                    throw;
                }
                finally
                {
                    cts.Cancel();
                }
            }
        }
    }

    private static void TryGrantEveryoneMutexAccess(Mutex mutex)
    {
        try
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                MutexRights.FullControl,
                AccessControlType.Allow));
            mutex.SetAccessControl(security);
        }
        catch
        {
        }
    }

    private static void TryGrantEveryoneEventAccess(EventWaitHandle waitHandle)
    {
        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                EventWaitHandleRights.FullControl,
                AccessControlType.Allow));
            waitHandle.SetAccessControl(security);
        }
        catch
        {
        }
    }

    internal static void WriteBootstrapCrashLog(string context, Exception ex)
    {
        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"[{time}] [FATAL] {context} | {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}{Environment.NewLine}";

        try
        {
            string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, $"shawmu_ocr_{DateTime.Now:yyyyMMdd}.log"), line);
            return;
        }
        catch
        {
        }

        try
        {
            string fallbackDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShawMuOCR",
                "logs");
            Directory.CreateDirectory(fallbackDir);
            File.AppendAllText(Path.Combine(fallbackDir, $"shawmu_ocr_{DateTime.Now:yyyyMMdd}.log"), line);
        }
        catch
        {
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
