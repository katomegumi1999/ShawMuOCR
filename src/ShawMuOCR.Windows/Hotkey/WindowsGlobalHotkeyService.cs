using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Windows.Hotkey;

public sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int DEFAULT_HOTKEY_ID = 1;

    private readonly ConcurrentDictionary<int, (HotkeyGesture Gesture, Action Callback)> _registeredHotkeys = new();
    private readonly ConcurrentQueue<Action> _actionQueue = new();
    private readonly Thread _messageLoopThread;
    private readonly ManualResetEventSlim _windowCreatedEvent = new(false);

    private IntPtr _hwnd = IntPtr.Zero;
    private User32.WndProc? _wndProcDelegate;
    private bool _disposed;
    private volatile bool _isPaused;

    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    public bool IsRegistered => IsRegisteredId(DEFAULT_HOTKEY_ID);
    public HotkeyGesture CurrentGesture => GetGesture(DEFAULT_HOTKEY_ID);

    public WindowsGlobalHotkeyService()
    {
        _messageLoopThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "ShawMuOCR_HotkeyMessageLoop"
        };
        _messageLoopThread.Start();

        // 等待后台消息接收窗口创建就绪（最多 3 秒）
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

        if (gesture.VirtualKey == 0) return false;

        return InvokeOnLoopThread(() =>
        {
            if (_hwnd == IntPtr.Zero) return false;

            // 若已有该 ID 的注册，先注销旧的热键绑定
            if (_registeredHotkeys.ContainsKey(id))
            {
                User32.UnregisterHotKey(_hwnd, id);
                _registeredHotkeys.TryRemove(id, out _);
            }

            uint fsModifiers = User32.MOD_NOREPEAT;
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) fsModifiers |= User32.MOD_ALT;
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) fsModifiers |= User32.MOD_CONTROL;
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) fsModifiers |= User32.MOD_SHIFT;
            if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) fsModifiers |= User32.MOD_WIN;

            bool success = User32.RegisterHotKey(_hwnd, id, fsModifiers, (uint)gesture.VirtualKey);
            if (success)
            {
                _registeredHotkeys[id] = (gesture, callback);
                return true;
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[WindowsGlobalHotkeyService] RegisterHotKey 失败，ID={id}, 热键={gesture}, Win32错误码={err}");
                return false;
            }
        });
    }

    public void Unregister(int id)
    {
        if (_disposed) return;

        InvokeOnLoopThread(() =>
        {
            if (_hwnd != IntPtr.Zero)
            {
                User32.UnregisterHotKey(_hwnd, id);
            }
            _registeredHotkeys.TryRemove(id, out _);
        });
    }

    private T InvokeOnLoopThread<T>(Func<T> func)
    {
        if (_disposed) return default!;
        if (Thread.CurrentThread == _messageLoopThread)
        {
            return func();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actionQueue.Enqueue(() =>
        {
            try
            {
                tcs.SetResult(func());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (_hwnd != IntPtr.Zero)
        {
            User32.PostMessage(_hwnd, (uint)User32.WM_EXECUTE_ACTION, IntPtr.Zero, IntPtr.Zero);
        }

        // 同步等待执行结果（设置 2 秒防死锁）
        if (tcs.Task.Wait(TimeSpan.FromSeconds(2)))
        {
            return tcs.Task.Result;
        }

        return default!;
    }

    private void InvokeOnLoopThread(Action action)
    {
        if (_disposed) return;
        if (Thread.CurrentThread == _messageLoopThread)
        {
            action();
            return;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _actionQueue.Enqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (_hwnd != IntPtr.Zero)
        {
            User32.PostMessage(_hwnd, (uint)User32.WM_EXECUTE_ACTION, IntPtr.Zero, IntPtr.Zero);
        }

        tcs.Task.Wait(TimeSpan.FromSeconds(2));
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

        // 创建纯消息专用窗口（HWND_MESSAGE），轻量且完全不占用桌面与任务栏任何资源
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

        _windowCreatedEvent.Set();

        while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == (uint)User32.WM_CLOSE_HOTKEY_WINDOW)
            {
                foreach (var id in _registeredHotkeys.Keys)
                {
                    User32.UnregisterHotKey(_hwnd, id);
                }
                _registeredHotkeys.Clear();

                if (_hwnd != IntPtr.Zero)
                {
                    User32.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
                User32.PostQuitMessage(0);
                break;
            }

            if (msg.message == (uint)User32.WM_EXECUTE_ACTION)
            {
                while (_actionQueue.TryDequeue(out var action))
                {
                    action();
                }
                continue;
            }

            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }

        User32.UnregisterClass(className, wndClass.hInstance);
    }

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == (uint)User32.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (!_isPaused && _registeredHotkeys.TryGetValue(id, out var item))
            {
                // 消除 Alt 菜单副作用：
                // 如果热键包含 Alt 修饰键，模拟下发一次虚拟无害按键 vkE8，
                // 打破 Windows 对独立 Alt 键松开时激活前台活动窗口菜单栏的判定！
                if (item.Gesture.Modifiers.HasFlag(HotkeyModifiers.Alt))
                {
                    User32.keybd_event(User32.VK_DUMMY_MASK, 0, 0, UIntPtr.Zero);
                    User32.keybd_event(User32.VK_DUMMY_MASK, 0, User32.KEYEVENTF_KEYUP, UIntPtr.Zero);
                }

                // 授予前台窗口转移焦点特权
                User32.AllowSetForegroundWindow(User32.ASFW_ANY);

                // 异步派发热键业务逻辑，绝不阻塞当前消息循环
                Task.Run(item.Callback);
            }
            return IntPtr.Zero;
        }

        if (msg == (uint)User32.WM_EXECUTE_ACTION)
        {
            while (_actionQueue.TryDequeue(out var action))
            {
                action();
            }
            return IntPtr.Zero;
        }

        return User32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_hwnd != IntPtr.Zero)
            {
                User32.PostMessage(_hwnd, (uint)User32.WM_CLOSE_HOTKEY_WINDOW, IntPtr.Zero, IntPtr.Zero);
            }

            // 退出路径上的等待要短：消息循环线程是后台线程（IsBackground = true），
            // 即使没能在 250ms 内收尾也不会阻止进程结束，句柄随进程释放即可。
            // 历史上这里等满 1 秒，直接叠在"点退出 → 托盘图标消失"的延迟上。
            bool joined = _messageLoopThread.Join(TimeSpan.FromMilliseconds(250));
            if (joined)
            {
                _windowCreatedEvent.Dispose();
            }
        }
    }
}
