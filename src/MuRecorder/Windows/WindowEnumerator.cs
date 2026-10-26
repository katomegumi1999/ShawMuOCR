using System.Diagnostics;
using System.Runtime.InteropServices;
using ShawMuOCR.Core.Capture;

namespace MuRecorder.Windows;

/// <summary>
/// 顶层窗口枚举：HWND + PID + 标题 + 进程名 + 物理像素矩形。C# 只把 HWND/PID 传给 Rust，
/// Rust 不负责窗口枚举（设计文档 1 的职责划分）。
/// </summary>
public sealed record WindowInfo(IntPtr Hwnd, uint ProcessId, string Title, string ProcessName, ScreenRect Bounds)
{
    public override string ToString() =>
        string.IsNullOrEmpty(ProcessName) ? Title : $"{Title} — {ProcessName}";
}

public static partial class WindowEnumerator
{
    [ThreadStatic]
    private static List<WindowInfo>? t_collector;

    [ThreadStatic]
    private static int t_selfPid;

    /// <summary>
    /// 枚举可录制的顶层窗口：可见、有标题、非工具窗、非最小化、非本进程。
    /// </summary>
    public static IReadOnlyList<WindowInfo> Enumerate()
    {
        var list = new List<WindowInfo>();
        t_collector = list;
        t_selfPid = Environment.ProcessId;
        try
        {
            unsafe
            {
                delegate* unmanaged[Stdcall]<IntPtr, void*, int> cb = &OnEnumWindow;
                NativeMethods.EnumWindows((IntPtr)(void*)cb, IntPtr.Zero);
            }
        }
        finally
        {
            t_collector = null;
        }
        return list;
    }

    /// <summary>判断窗口是否处于最小化状态。</summary>
    public static bool IsIconic(IntPtr hwnd) => NativeMethods.IsIconic(hwnd);

    /// <summary>将最小化窗口还原到前台正常显示。</summary>
    public static void RestoreWindow(IntPtr hwnd) => NativeMethods.ShowWindow(hwnd, 9 /* SW_RESTORE */);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    private static unsafe int OnEnumWindow(IntPtr hwnd, void* data)
    {
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                return 1;
            }

            int exStyle = NativeMethods.GetWindowLongW(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return 1; // 工具窗（如浮动面板、输入法候选）不进列表
            }

            // 过滤 DWM 挂起/隐藏/虚拟桌面幽灵窗口（典型如 Windows 输入体验 TextInputHost）
            if (NativeMethods.DwmGetWindowAttributeCloaked(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return 1;
            }

            int titleLen = NativeMethods.GetWindowTextLengthW(hwnd);
            if (titleLen <= 0)
            {
                return 1;
            }

            uint pid = 0;
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, ref pid);
            if (pid == 0 || pid == t_selfPid)
            {
                return 1; // 本进程窗口（主界面自身）不录
            }

            string processName = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch
            {
                // 忽略特权进程无法读取 ProcessName 的权限错误，不阻断窗口录制
            }

            // 明确排除系统输入法宿主及桌面管理器
            if (string.Equals(processName, "TextInputHost", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            Span<char> titleBuf = stackalloc char[512];
            int copied;
            fixed (char* pTitle = titleBuf)
            {
                copied = NativeMethods.GetWindowTextW(hwnd, pTitle, titleBuf.Length);
            }
            string title = copied > 0 ? new string(titleBuf[..copied]).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(title) || title == "Program Manager")
            {
                return 1;
            }

            bool isIconic = NativeMethods.IsIconic(hwnd);
            WindowRect rc;
            if (isIconic)
            {
                var wp = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
                if (NativeMethods.GetWindowPlacement(hwnd, ref wp))
                {
                    rc = wp.RcNormalPosition;
                }
                else if (!NativeMethods.GetWindowRect(hwnd, out rc))
                {
                    return 1;
                }
            }
            else
            {
                if (NativeMethods.DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var dwmRc, Marshal.SizeOf<WindowRect>()) == 0)
                {
                    rc = dwmRc;
                }
                else if (!NativeMethods.GetWindowRect(hwnd, out rc))
                {
                    return 1;
                }
            }

            var bounds = rc.ToScreenRect();
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return 1;
            }

            string displayTitle = isIconic ? $"[最小化] {title}" : title;
            t_collector?.Add(new WindowInfo(hwnd, pid, displayTitle, processName, bounds));
        }
        catch
        {
            // 防御：回调内任何异常都不外泄。
        }
        return 1;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("dwmapi.dll")]
        internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out WindowRect pvAttribute, int cbAttribute);

        [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        internal static partial int DwmGetWindowAttributeCloaked(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        // 同 MonitorEnumerator：回调用 IntPtr 传函数指针（生成器不支持 delegate* 参数）。
        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static unsafe partial bool EnumWindows(IntPtr cb, IntPtr data);

        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static partial int GetWindowLongW(IntPtr hwnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static partial int GetWindowTextLengthW(IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static unsafe partial int GetWindowTextW(IntPtr hwnd, char* lpString, int nMaxCount);

        // 注意：GetWindowThreadProcessId 返回 DWORD（线程 ID），不是 BOOL——勿加 [return: MarshalAs(UnmanagedType.Bool)]（SYSLIB1052）。
        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        internal static partial uint GetWindowThreadProcessId(IntPtr hwnd, ref uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowRect(IntPtr hwnd, out WindowRect lpRect);

        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetWindowPlacement(IntPtr hwnd, ref WindowPlacement lpwndpl);

        [LibraryImport("user32.dll")]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr hwnd, int nCmdShow);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point PtMinPosition;
        public Point PtMaxPosition;
        public WindowRect RcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary>Win32 RECT（物理像素，虚拟桌面坐标系）。进程需为 DPI Aware，否则坐标会被系统虚拟化。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public ScreenRect ToScreenRect() => new(Left, Top, Width, Height);
    }
}
