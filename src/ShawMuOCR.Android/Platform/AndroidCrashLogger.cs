using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Android.Runtime;

namespace ShawMuOCR.Android.Platform;

/// <summary>
/// 专用于 Android 平台的自愈式高可靠诊断日志器
/// 直接将启动信息、运行轨迹与异常堆栈实时刷盘写入手机 /Download/ 目录
/// </summary>
public static class AndroidCrashLogger
{
    private static readonly object s_lock = new();
    private static string? s_primaryLogPath;
    private static string? s_fallbackLogPath;
    private static bool s_isInitialized;

    public static string? PrimaryLogPath => s_primaryLogPath;

    public static void Initialize(Context context)
    {
        if (s_isInitialized) return;

        lock (s_lock)
        {
            if (s_isInitialized) return;

            try
            {
                // 1. 优先尝试主路径：/storage/emulated/0/Download/shawmu_ocr.log
                string downloadDir = global::Android.OS.Environment.GetExternalStoragePublicDirectory(
                    global::Android.OS.Environment.DirectoryDownloads)?.AbsolutePath 
                    ?? "/storage/emulated/0/Download";

                if (!Directory.Exists(downloadDir))
                {
                    try { Directory.CreateDirectory(downloadDir); } catch { }
                }

                s_primaryLogPath = Path.Combine(downloadDir, "shawmu_ocr.log");

                // 2. 备用路径：/sdcard/Android/data/com.shawmu.ocr/files/shawmu_ocr.log (100% 无权限障碍)
                var extFiles = context.GetExternalFilesDir(null)?.AbsolutePath;
                if (!string.IsNullOrEmpty(extFiles))
                {
                    s_fallbackLogPath = Path.Combine(extFiles, "shawmu_ocr.log");
                }
            }
            catch (Exception ex)
            {
                s_fallbackLogPath = Path.Combine(context.FilesDir?.AbsolutePath ?? "/data/data/com.shawmu.ocr/files", "shawmu_ocr.log");
                System.Diagnostics.Debug.WriteLine($"[CrashLogger] 初始化路径异常: {ex.Message}");
            }

            // 全局未捕获异常捕获挂钩
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    LogException(ex, "AppDomain.CurrentDomain.UnhandledException (IsTerminating: " + e.IsTerminating + ")");
                }
                else
                {
                    Log($"[FATAL] 非托管未处理异常: {e.ExceptionObject}");
                }
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogException(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };

            AndroidEnvironment.UnhandledExceptionRaiser += (s, e) =>
            {
                LogException(e.Exception, "AndroidEnvironment.UnhandledExceptionRaiser");
                e.Handled = false; // 允许系统记录原生 crash
            };

            s_isInitialized = true;

            Log("================================================================================");
            Log($"[STARTUP] 小沐 OCR 启动日志初始化完成");
            string abis = (Build.SupportedAbis != null && Build.SupportedAbis.Count > 0) ? string.Join(", ", Build.SupportedAbis) : "arm64-v8a";
            Log($"[CPU] 架构: {abis} | 逻辑核心数: {System.Environment.ProcessorCount}");
            Log($"[TARGET LOG PATH] {s_primaryLogPath}");
            if (!string.IsNullOrEmpty(s_fallbackLogPath))
            {
                Log($"[FALLBACK LOG PATH] {s_fallbackLogPath}");
            }
            Log("================================================================================");
        }
    }

    public static void Log(string message)
    {
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        string line = $"[{timestamp}] {message}";

        System.Diagnostics.Debug.WriteLine(line);

        lock (s_lock)
        {
            WriteToFileSafe(s_primaryLogPath, line);
            if (!string.IsNullOrEmpty(s_fallbackLogPath) && s_fallbackLogPath != s_primaryLogPath)
            {
                WriteToFileSafe(s_fallbackLogPath, line);
            }
        }
    }

    public static void LogException(Exception ex, string contextMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[CRASH / EXCEPTION] 上下文: {contextMessage}");
        sb.AppendLine($"[TYPE] {ex.GetType().FullName}");
        sb.AppendLine($"[MESSAGE] {ex.Message}");
        sb.AppendLine($"[STACK TRACE]\n{ex.StackTrace}");

        var inner = ex.InnerException;
        int depth = 1;
        while (inner != null && depth <= 5)
        {
            sb.AppendLine($"   --> [INNER {depth}] {inner.GetType().FullName}: {inner.Message}");
            sb.AppendLine($"   --> [INNER STACK]\n{inner.StackTrace}");
            inner = inner.InnerException;
            depth++;
        }

        Log(sb.ToString().TrimEnd());
    }

    private static void WriteToFileSafe(string? path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs, Encoding.UTF8);
            sw.WriteLine(content);
            sw.Flush();
            fs.Flush(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CrashLogger] 写入日志文件失败 ({path}): {ex.Message}");
        }
    }
}
