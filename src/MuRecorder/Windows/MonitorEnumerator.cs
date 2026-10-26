using System.Buffers;
using System.Runtime.InteropServices;
using ShawMuOCR.Core.Capture;

namespace MuRecorder.Windows;

/// <summary>
/// 显示器枚举：物理像素边界（虚拟桌面坐标系）+ 每屏 DPI + 主屏标记。
/// 供录屏区域选择与 DIP→物理像素换算使用（设计文档 5.4 的易错点）。
/// 纯 Win32 查询，不依赖 Avalonia TopLevel——托盘/全局热键发起录屏时同样可用。
/// </summary>
public sealed record MonitorInfo(
    IntPtr Handle,
    string DeviceName,
    ScreenRect Bounds,
    uint DpiX,
    uint DpiY,
    bool IsPrimary)
{
    /// <summary>DPI 缩放比（96 = 100%）。</summary>
    public double Scale => DpiX / 96.0;

    /// <summary>DIP → 物理像素（含显示器在虚拟桌面中的偏移）。</summary>
    public ScreenRect DipToPhysical(double dipX, double dipY, double dipW, double dipH)
    {
        double s = Scale;
        return new ScreenRect(
            Bounds.X + (int)Math.Round(dipX * s),
            Bounds.Y + (int)Math.Round(dipY * s),
            (int)Math.Round(dipW * s),
            (int)Math.Round(dipH * s));
    }

    /// <summary>物理像素 → DIP（用于把 Avalonia 窗口摆到指定显示器）。</summary>
    public (double X, double Y, double W, double H) PhysicalToDip(ScreenRect rect)
    {
        double s = Scale;
        return (
            (rect.X - Bounds.X) / s,
            (rect.Y - Bounds.Y) / s,
            rect.Width / s,
            rect.Height / s);
    }

    public override string ToString() =>
        IsPrimary
            ? $"{DeviceName}（主显示器 {Bounds.Width}×{Bounds.Height} @ {DpiX * 100 / 96}%）"
            : $"{DeviceName}（{Bounds.Width}×{Bounds.Height} @ {DpiX * 100 / 96}%）";
}

public static partial class MonitorEnumerator
{
    [ThreadStatic]
    private static List<MonitorInfo>? t_collector;

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        t_collector = list;
        try
        {
            unsafe
            {
                delegate* unmanaged[Stdcall]<IntPtr, IntPtr, NativeRect*, void*, int> cb = &OnEnumDisplayMonitor;
                NativeMethods.EnumDisplayMonitors(IntPtr.Zero, null, (IntPtr)(void*)cb, IntPtr.Zero);
            }
        }
        finally
        {
            t_collector = null;
        }
        return list;
    }

    /// <summary>包含指定虚拟桌面坐标点的显示器（找不到时返回主屏）。</summary>
    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = Enumerate();
        foreach (var m in all)
        {
            if (x >= m.Bounds.X && x < m.Bounds.Right && y >= m.Bounds.Y && y < m.Bounds.Bottom)
            {
                return m;
            }
        }
        return all.First(m => m.IsPrimary);
    }

    /// <summary>
    /// EnumDisplayMonitors 回调。注意：绝不让异常逃逸到 native（会直接杀进程），
    /// 任何失败都记一条并返回 1（继续枚举）。
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static unsafe int OnEnumDisplayMonitor(IntPtr hMonitor, IntPtr hdc, NativeRect* rc, void* data)
    {
        try
        {
            var info = new MONITORINFOEXW
            {
                cbSize = (uint)sizeof(MONITORINFOEXW),
            };
            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                return 1;
            }

            uint dpiX = 96, dpiY = 96;
            try
            {
                NativeMethods.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
            }
            catch (DllNotFoundException)
            {
                // 旧系统无 shcore.dll：按 96 DPI 处理（与 GDI 逻辑 DPI 一致）。
            }
            catch (EntryPointNotFoundException)
            {
                // 同上的降级路径。
            }

            var bounds = new ScreenRect(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);

            ReadOnlySpan<char> name = MemoryMarshal.CreateReadOnlySpan(ref info.szDevice[0], 32);
            int len = name.IndexOf('\0');
            if (len < 0)
            {
                len = 32;
            }
            string device = name[..len].ToString();

            t_collector?.Add(new MonitorInfo(
                hMonitor,
                device.Length > 0 ? device : "显示器",
                bounds,
                dpiX,
                dpiY,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
        }
        catch
        {
            // 防御：回调内任何异常都不外泄。
        }
        return 1;
    }

    private const uint MONITORINFOF_PRIMARY = 1;

    internal enum MONITOR_DPI_TYPE
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT = MDT_EFFECTIVE_DPI,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct MONITORINFOEXW
    {
        public uint cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
        public fixed char szDevice[32];
    }

    private static unsafe partial class NativeMethods
    {
        // 注意：回调参数必须是 IntPtr——LibraryImport 源生成器不支持 delegate* 参数类型，
        // 传函数指针时用 (IntPtr)(void*)cb 转换（调用处见 Enumerate）。
        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool EnumDisplayMonitors(
            IntPtr hdc,
            NativeRect* clip,
            IntPtr cb,
            IntPtr data);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

        [LibraryImport("shcore.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static partial int GetDpiForMonitor(IntPtr hMonitor, MONITOR_DPI_TYPE dpiType, out uint dpiX, out uint dpiY);
    }
}
