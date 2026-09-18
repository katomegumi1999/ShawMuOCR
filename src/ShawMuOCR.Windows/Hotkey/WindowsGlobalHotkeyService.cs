using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Windows.Hotkey;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int DEFAULT_HOTKEY_ID = 1;
    private const int ALT_FLUSH_DELAY_MS = 250;
    private static readonly UIntPtr REPLAY_MAGIC = new(0x53484157); // "SHAW"

    private readonly ConcurrentDictionary<int, (HotkeyGesture Gesture, Action Callback)> _registeredHotkeys = new();
    private readonly Thread _messageLoopThread;
    private readonly ManualResetEventSlim _windowCreatedEvent = new(false);
    private readonly User32.LowLevelKeyboardProc _hookProc;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hookHandle = IntPtr.Zero;
    private User32.WndProc? _wndProcDelegate;
    private bool _disposed;

    // --- 按键状态维护 (需求 2 & 6) ---
    private bool _isLAltDown;
    private bool _isRAltDown;
    private bool _isLCtrlDown;
    private bool _isRCtrlDown;
    private bool _isLShiftDown;
    private bool _isRShiftDown;
    private bool _isLWinDown;
    private bool _isRWinDown;

    private bool IsAltDown => _isLAltDown || _isRAltDown;
    private bool IsCtrlDown => _isLCtrlDown || _isRCtrlDown;
    private bool IsShiftDown => _isLShiftDown || _isRShiftDown;
    private bool IsWinDown => _isLWinDown || _isRWinDown;

    // --- Alt 键暂缓缓冲与重放状态 (需求 3, 4, 7, 8) ---
    private readonly object _altBufferLock = new();
    private bool _altBuffered;
    private bool _altFlushedToOS;
    private User32.KBDLLHOOKSTRUCT _bufferedAltStruct;
    private readonly System.Threading.Timer _altFlushTimer;
    private volatile bool _isReplaying;

    // --- 热键触发后的抬起拦截状态 (需求 4) ---
    private volatile bool _swallowNextAltUp;
    private volatile int _swallowedVk;
    private volatile bool _isHotkeyDown;

    private volatile bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            _isPaused = value;
            if (value)
            {
                lock (_altBufferLock)
                {
                    _altBuffered = false;
                    _altFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    _isHotkeyDown = false;
                    _swallowedVk = 0;
                    _swallowNextAltUp = false;
                }
            }
        }
    }

    public bool IsRegistered => IsRegisteredId(DEFAULT_HOTKEY_ID);
    public HotkeyGesture CurrentGesture => GetGesture(DEFAULT_HOTKEY_ID);

    public WindowsGlobalHotkeyService()
    {
        _hookProc = LowLevelKeyboardHandler;
        _altFlushTimer = new System.Threading.Timer(OnAltFlushTimeout, null, Timeout.Infinite, Timeout.Infinite);

        _messageLoopThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "ShawMuOCR_HotkeyMessageLoop"
        };
        _messageLoopThread.Start();
        _windowCreatedEvent.Wait(TimeSpan.FromSeconds(3));
    }

    public bool Register(HotkeyGesture gesture, Action callback) => Register(DEFAULT_HOTKEY_ID, gesture, callback);

    public void Unregister() => Unregister(DEFAULT_HOTKEY_ID);

    public bool IsRegisteredId(int id) => _registeredHotkeys.ContainsKey(id);

    public HotkeyGesture GetGesture(int id) => _registeredHotkeys.TryGetValue(id, out var item) ? item.Gesture : default;

    public bool Register(int id, HotkeyGesture gesture, Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);

        if (_hookHandle == IntPtr.Zero)
        {
            return false;
        }

        _registeredHotkeys[id] = (gesture, callback);
        return true;
    }

    public void Unregister(int id)
    {
        _registeredHotkeys.TryRemove(id, out _);
    }

    private bool HasAnyAltHotkey()
    {
        foreach (var entry in _registeredHotkeys.Values)
        {
            if (entry.Gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
            {
                return true;
            }
        }
        return false;
    }

    private void RunMessageLoop()
    {
        string className = $"ShawMuOCR_HotkeyWindow_{Guid.NewGuid():N}";
        _wndProcDelegate = WndProcHandler;

        var wndClass = new User32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = Kernel32.GetModuleHandle(null),
            lpszClassName = className
        };

        ushort atom = User32.RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            _windowCreatedEvent.Set();
            return;
        }

        _hwnd = User32.CreateWindowEx(
            0,
            className,
            "ShawMuOCR_HotkeyReceiver",
            0,
            0, 0, 0, 0,
            User32.HWND_MESSAGE,
            IntPtr.Zero,
            wndClass.hInstance,
            IntPtr.Zero);

        // 安装 WH_KEYBOARD_LL 全局低级键盘钩子 (需求 1)
        _hookHandle = User32.SetWindowsHookEx(
            User32.WH_KEYBOARD_LL,
            _hookProc,
            Kernel32.GetModuleHandle(null),
            0);

        if (_hookHandle == IntPtr.Zero)
        {
            Debug.WriteLine($"[WindowsGlobalHotkeyService] SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}");
        }

        _windowCreatedEvent.Set();

        while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == User32.WM_CLOSE_HOTKEY_WINDOW)
            {
                if (_hookHandle != IntPtr.Zero)
                {
                    User32.UnhookWindowsHookEx(_hookHandle);
                    _hookHandle = IntPtr.Zero;
                }
                User32.DestroyWindow(_hwnd);
                User32.PostQuitMessage(0);
            }
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }

        if (_hookHandle != IntPtr.Zero)
        {
            User32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        User32.UnregisterClass(className, wndClass.hInstance);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnAltFlushTimeout(object? state)
    {
        FlushBufferedAlt("timeout");
    }

    private void FlushBufferedAlt(string? flushReason = null)
    {
        lock (_altBufferLock)
        {
            if (!_altBuffered) return;
            _altBuffered = false;
            _altFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _altFlushedToOS = true;

            uint flags = 0;
            if ((_bufferedAltStruct.flags & User32.LLKHF_EXTENDED) != 0)
            {
                flags |= User32.KEYEVENTF_EXTENDEDKEY;
            }

            _isReplaying = true;
            try
            {
                User32.keybd_event(
                    (byte)_bufferedAltStruct.vkCode,
                    (byte)_bufferedAltStruct.scanCode,
                    flags,
                    REPLAY_MAGIC);
            }
            finally
            {
                _isReplaying = false;
            }
        }
    }

    private IntPtr LowLevelKeyboardHandler(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _isPaused)
        {
            return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        uint msg = (uint)wParam;
        // 同时处理所有按键消息 (需求 5)
        if (msg != User32.WM_KEYDOWN && msg != User32.WM_KEYUP &&
            msg != User32.WM_SYSKEYDOWN && msg != User32.WM_SYSKEYUP)
        {
            return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        unsafe
        {
            var kbd = *(User32.KBDLLHOOKSTRUCT*)lParam;

            // 遇重放按键无条件直接放行，避免死循环拦截
            if (_isReplaying || kbd.dwExtraInfo == REPLAY_MAGIC)
            {
                return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            uint vk = kbd.vkCode;
            bool isDown = (msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN);
            bool isUp = (msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP);

            // 维护修饰键状态 (需求 2 & 6)
            UpdateModifierStates(vk, isDown);

            if (isUp)
            {
                // A. 拦截已命中热键的主键抬起 (例如 Z Up，需求 4)
                if (_swallowedVk != 0 && vk == (uint)_swallowedVk)
                {
                    _swallowedVk = 0;
                    _isHotkeyDown = false;
                    return (IntPtr)1;
                }

                // B. 拦截已命中热键的 Alt 抬起 (Alt Up，需求 4)
                if (_swallowNextAltUp && (vk is User32.VK_MENU or User32.VK_LMENU or User32.VK_RMENU))
                {
                    if (!_isLAltDown && !_isRAltDown)
                    {
                        _swallowNextAltUp = false;
                    }
                    return (IntPtr)1; // 彻底拦截 Alt Up，防止前台窗口弹出菜单提示
                }

                // C. 如果 Alt Down 还在被缓冲时收到了 Alt Up（单纯单击了 Alt 键后松开，需求 7）
                if (vk is User32.VK_MENU or User32.VK_LMENU or User32.VK_RMENU)
                {
                    bool wasBuffered;
                    lock (_altBufferLock)
                    {
                        wasBuffered = _altBuffered;
                    }

                    if (wasBuffered)
                    {
                        // 用户单纯敲击 Alt 键，必须放行正常按键：立即重放 Alt Down，随后放行当前的 Alt Up
                        FlushBufferedAlt("alt_tap");
                        return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                    }

                    if (_altFlushedToOS)
                    {
                        if (!_isLAltDown && !_isRAltDown)
                        {
                            _altFlushedToOS = false;
                        }
                    }
                }

                return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // --- isDown 分支 ---

            // 1. 如果当前按下的键是 Alt (需求 6)
            if (vk is User32.VK_MENU or User32.VK_LMENU or User32.VK_RMENU)
            {
                if (HasAnyAltHotkey())
                {
                    lock (_altBufferLock)
                    {
                        if (_altBuffered)
                        {
                            // 长按 Alt 自动重复，吞噬多余 Down
                            return (IntPtr)1;
                        }

                        if (!_altFlushedToOS)
                        {
                            _altBuffered = true;
                            _bufferedAltStruct = kbd;
                            _altFlushTimer.Change(ALT_FLUSH_DELAY_MS, Timeout.Infinite);
                            return (IntPtr)1; // 暂缓下发 Alt Down
                        }
                    }
                }

                return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // 2. 如果当前按下的键是其它修饰键 (Ctrl / Shift / Win)
            if (IsOtherModifierKey(vk))
            {
                return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            // 3. 当前按键是非修饰键的主键 (例如 Z, C, X, Tab, F4 等)
            HotkeyModifiers currentModifiers = GetEffectiveModifiers();

            // 检查是否有注册的热键匹配
            Action? matchedCallback = null;
            foreach (var item in _registeredHotkeys.Values)
            {
                if (item.Gesture.Modifiers == currentModifiers && item.Gesture.VirtualKey == (int)vk)
                {
                    matchedCallback = item.Callback;
                    break;
                }
            }

            if (matchedCallback != null)
            {
                // === 命中注册热键 (例如 Alt+Z，需求 3 & 4) ===
                lock (_altBufferLock)
                {
                    if (_altBuffered)
                    {
                        _altFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
                        _altBuffered = false;
                        // Alt Down 彻底被拦截并未发送给 OS，标记 Alt Up 也必须拦截
                        _swallowNextAltUp = true;
                    }
                    else if (!_altFlushedToOS && currentModifiers.HasFlag(HotkeyModifiers.Alt))
                    {
                        _swallowNextAltUp = true;
                    }
                }

                _swallowedVk = (int)vk;

                // 防止长按反复触发截图
                if (!_isHotkeyDown)
                {
                    _isHotkeyDown = true;

                    // 立即授予前台焦点转移特权
                    User32.AllowSetForegroundWindow(User32.ASFW_ANY);

                    // 立即异步触发截图/OCR 功能
                    Task.Run(matchedCallback);
                }

                // 彻底拦截本次主键 (Z Down)
                return (IntPtr)1;
            }
            else
            {
                // === 未命中热键 (如 Alt+Tab, Alt+F4, 或普通打字，需求 7) ===
                // 若之前缓冲了 Alt Down，立即冲刷给系统，保证正常快捷键与系统操作丝滑放行
                FlushBufferedAlt("non_hotkey_key_down");

                return User32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }
    }

    private void UpdateModifierStates(uint vk, bool isDown)
    {
        switch (vk)
        {
            case User32.VK_LMENU:
                _isLAltDown = isDown;
                break;
            case User32.VK_RMENU:
                _isRAltDown = isDown;
                break;
            case User32.VK_MENU:
                if (!isDown) { _isLAltDown = false; _isRAltDown = false; }
                else if (!_isLAltDown && !_isRAltDown) { _isLAltDown = true; }
                break;

            case User32.VK_LCONTROL:
                _isLCtrlDown = isDown;
                break;
            case User32.VK_RCONTROL:
                _isRCtrlDown = isDown;
                break;
            case User32.VK_CONTROL:
                if (!isDown) { _isLCtrlDown = false; _isRCtrlDown = false; }
                else if (!_isLCtrlDown && !_isRCtrlDown) { _isLCtrlDown = true; }
                break;

            case User32.VK_LSHIFT:
                _isLShiftDown = isDown;
                break;
            case User32.VK_RSHIFT:
                _isRShiftDown = isDown;
                break;
            case User32.VK_SHIFT:
                if (!isDown) { _isLShiftDown = false; _isRShiftDown = false; }
                else if (!_isLShiftDown && !_isRShiftDown) { _isLShiftDown = true; }
                break;

            case User32.VK_LWIN:
                _isLWinDown = isDown;
                break;
            case User32.VK_RWIN:
                _isRWinDown = isDown;
                break;
        }
    }

    private static bool IsOtherModifierKey(uint vk)
    {
        return vk is User32.VK_CONTROL or User32.VK_LCONTROL or User32.VK_RCONTROL
            or User32.VK_SHIFT or User32.VK_LSHIFT or User32.VK_RSHIFT
            or User32.VK_LWIN or User32.VK_RWIN;
    }

    private HotkeyModifiers GetEffectiveModifiers()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (IsCtrlDown || (User32.GetAsyncKeyState(User32.VK_CONTROL) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Ctrl;

        if (IsShiftDown || (User32.GetAsyncKeyState(User32.VK_SHIFT) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Shift;

        bool altBufferedLocal;
        lock (_altBufferLock)
        {
            altBufferedLocal = _altBuffered;
        }

        if (IsAltDown || altBufferedLocal || (User32.GetAsyncKeyState(User32.VK_MENU) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Alt;

        if (IsWinDown || (User32.GetAsyncKeyState(User32.VK_LWIN) & 0x8000) != 0 || (User32.GetAsyncKeyState(User32.VK_RWIN) & 0x8000) != 0)
            modifiers |= HotkeyModifiers.Win;

        return modifiers;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            lock (_altBufferLock)
            {
                if (_altBuffered)
                {
                    FlushBufferedAlt("dispose");
                }
            }

            _altFlushTimer.Dispose();

            if (_hookHandle != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _registeredHotkeys.Clear();

            if (_hwnd != IntPtr.Zero)
            {
                User32.PostMessage(_hwnd, User32.WM_CLOSE_HOTKEY_WINDOW, IntPtr.Zero, IntPtr.Zero);
            }

            _windowCreatedEvent.Dispose();
        }
    }
}
