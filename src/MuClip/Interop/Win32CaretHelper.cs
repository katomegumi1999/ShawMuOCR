using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MuClip.Interop;

public static partial class Win32CaretHelper
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>
    /// 获取当前活动窗口中的光标键入物理屏幕位置。若无法检测到有效键入光标，则回退为鼠标指针屏幕坐标。
    /// </summary>
    public static (int X, int Y, bool IsCaret) GetCaretOrCursorScreenPosition()
    {
        try
        {
            IntPtr fgWnd = GetForegroundWindow();
            if (fgWnd != IntPtr.Zero)
            {
                uint threadId = GetWindowThreadProcessId(fgWnd, out _);
                if (threadId != 0)
                {
                    GUITHREADINFO gui = new()
                    {
                        cbSize = Marshal.SizeOf<GUITHREADINFO>()
                    };

                    if (GetGUIThreadInfo(threadId, ref gui) && gui.hwndCaret != IntPtr.Zero)
                    {
                        POINT pt = new()
                        {
                            X = gui.rcCaret.Left,
                            Y = gui.rcCaret.Bottom
                        };

                        if (ClientToScreen(gui.hwndCaret, ref pt))
                        {
                            if (pt.X > 0 || pt.Y > 0)
                            {
                                return (pt.X, pt.Y + 4, true);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略异常，降级回退
        }

        // 回退至鼠标指针屏幕坐标
        if (GetCursorPos(out POINT cursorPt))
        {
            return (cursorPt.X, cursorPt.Y + 12, false);
        }

        return (200, 200, false);
    }

    /// <summary>
    /// 在面板关闭并将焦点归还上一活动窗口后，模拟发送 Ctrl+V 实现自动就地粘贴
    /// </summary>
    public static void SimulatePaste(int delayMs = 60)
    {
        Task.Run(async () =>
        {
            await Task.Delay(delayMs);
            keybd_event(VK_CONTROL, 0, 0, 0);
            keybd_event(VK_V, 0, 0, 0);
            keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
        });
    }
}
