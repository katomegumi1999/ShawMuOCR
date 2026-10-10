using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MuClip.Interop;

/// <summary>
/// 目标活动窗口捕获、焦点精准还原与原子级按键模拟器
/// 负责在呼出面板前/激活前固化目标输入窗口句柄，并在粘贴时安全还原焦点并发送 Ctrl+V
/// </summary>
public sealed partial class WindowsInputTarget
{
    private const uint ASFW_ANY = unchecked((uint)-1);
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "AllowSetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(uint dwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static unsafe partial uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    private IntPtr _targetHwnd = IntPtr.Zero;

    public IntPtr TargetHwnd => _targetHwnd;
    public bool HasValidTarget => _targetHwnd != IntPtr.Zero && IsWindow(_targetHwnd);

    /// <summary>
    /// 捕获当前前台活动窗口句柄（忽略指定自身窗口句柄）
    /// </summary>
    public void Capture(IntPtr excludeHwnd = default)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != excludeHwnd && IsWindow(fg))
        {
            _targetHwnd = fg;
        }
    }

    /// <summary>
    /// 强行恢复目标窗口为前台窗口
    /// </summary>
    public bool Restore()
    {
        if (HasValidTarget)
        {
            AllowSetForegroundWindow(ASFW_ANY);
            return SetForegroundWindow(_targetHwnd);
        }
        return false;
    }

    /// <summary>
    /// 基于 Windows 原生 SendInput 发送原子级 Ctrl+V 粘贴消息（非已过时的 keybd_event）
    /// </summary>
    public static unsafe void SendPaste()
    {
        INPUT[] inputs = new INPUT[4];

        // 1. Ctrl Down
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_CONTROL;
        inputs[0].u.ki.dwFlags = 0;

        // 2. V Down
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_V;
        inputs[1].u.ki.dwFlags = 0;

        // 3. V Up
        inputs[2].type = INPUT_KEYBOARD;
        inputs[2].u.ki.wVk = VK_V;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;

        // 4. Ctrl Up
        inputs[3].type = INPUT_KEYBOARD;
        inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;

        fixed (INPUT* p = inputs)
        {
            SendInput(4, p, sizeof(INPUT));
        }
    }

    /// <summary>
    /// 恢复原目标窗口焦点并就地模拟粘贴
    /// </summary>
    public void RestoreAndPaste(int delayMs = 35)
    {
        Restore();
        Task.Run(async () =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
            SendPaste();
        });
    }

    /// <summary>
    /// 当原窗口从始至终未失活时（WS_EX_NOACTIVATE 模式），直接就地无缝粘贴
    /// </summary>
    public static void DirectPasteWithoutActivating(int delayMs = 20)
    {
        Task.Run(async () =>
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }
            SendPaste();
        });
    }
}
