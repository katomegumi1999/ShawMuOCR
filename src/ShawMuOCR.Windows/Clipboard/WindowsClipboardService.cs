using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Windows.Clipboard;

/// <summary>
/// 全局唯一的 Win32 剪贴板读写与变动监听服务（OCR 与 MuClip 共用同一实例与同一进程级剪贴板互斥锁）
/// - 统一封装 OpenClipboard / EmptyClipboard / SetClipboardData / GetClipboardData 及重试退避；
/// - 基于 GetClipboardSequenceNumber 与进程内互斥锁实现确定性自写回显抑制（Echo Suppression）与防重入。
/// </summary>
public sealed partial class WindowsClipboardService : IClipboardService, IDisposable
{
    public static WindowsClipboardService Shared { get; } = new();

    private static readonly object s_clipboardLock = new();
    private static uint s_suppressedSequenceNumber;
    private static string? s_suppressedText;
    private static long s_suppressedUntilTick;
    private static uint s_lastHandledSequenceNumber;
    private static string? s_lastHandledText;
    private static long s_lastHandledTick;
    private static WindowsClipboardService? s_activeListener;

    private Thread? _listenerThread;
    private IntPtr _hwnd;
    private ManualResetEventSlim? _startedEvent;
    private bool _disposed;

    public bool IsListening => _hwnd != IntPtr.Zero && _listenerThread is { IsAlive: true };

    public event Action<string>? ExternalTextChanged;
    public event Action? HotkeyTriggered;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public ushort* lpszMenuName;
        public ushort* lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern unsafe ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    public async Task<bool> SetTextAsync(string text)
    {
        text ??= string.Empty;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetTextOnce(text, suppressNotification: false))
            {
                return true;
            }
            await Task.Delay(25 * (attempt + 1)).ConfigureAwait(false);
        }
        return false;
    }

    public bool SetText(string text, bool suppressNotification = false)
    {
        text ??= string.Empty;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (TrySetTextOnce(text, suppressNotification))
            {
                return true;
            }
            Thread.Sleep(25 * (attempt + 1));
        }
        return false;
    }

    public Task<string?> GetTextAsync()
    {
        return Task.FromResult(GetText());
    }

    public string? GetText()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (TryGetTextOnce(out string? result, out bool formatUnavailable))
            {
                return result;
            }
            if (formatUnavailable)
            {
                return null;
            }
            Thread.Sleep(25 * (attempt + 1));
        }
        return null;
    }

    public async Task<bool> SetImageAsync(OcrImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (TrySetImageOnce(image))
            {
                return true;
            }
            await Task.Delay(25 * (attempt + 1)).ConfigureAwait(false);
        }

        return false;
    }

    public void StartListening()
    {
        lock (s_clipboardLock)
        {
            if (IsListening) return;

            s_activeListener = this;
            _startedEvent?.Dispose();
            _startedEvent = new ManualResetEventSlim(false);
            _listenerThread = new Thread(ListenerThreadProc)
            {
                IsBackground = true,
                Name = "ShawMuOCR_ClipboardListener"
            };
            _listenerThread.SetApartmentState(ApartmentState.STA);
            _listenerThread.Start();
        }
        _startedEvent?.Wait(2000);
    }

    public void StopListening()
    {
        IntPtr hwndToClose;
        Thread? threadToJoin;
        lock (s_clipboardLock)
        {
            hwndToClose = _hwnd;
            threadToJoin = _listenerThread;
            _hwnd = IntPtr.Zero;
            _listenerThread = null;
            if (s_activeListener == this)
            {
                s_activeListener = null;
            }
        }

        if (hwndToClose != IntPtr.Zero)
        {
            User32.UnregisterHotKey(hwndToClose, 1);
            User32.PostMessage(hwndToClose, User32.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
        }
        if (threadToJoin is { IsAlive: true })
        {
            threadToJoin.Join(1000);
        }
    }

    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        IntPtr hwnd = _hwnd;
        return hwnd != IntPtr.Zero && User32.RegisterHotKey(hwnd, id, modifiers, vk);
    }

    private unsafe void ListenerThreadProc()
    {
        string className = "ShawMuOCR_UnifiedClipboardListener_" + Guid.NewGuid().ToString("N");
        IntPtr hInstance = Kernel32.GetModuleHandle(null);

        fixed (char* pClassName = className)
        {
            WNDCLASSEXW wndClass = new()
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &ListenerWndProc,
                hInstance = hInstance,
                lpszClassName = (ushort*)pClassName
            };
            RegisterClassExW(&wndClass);
        }

        IntPtr hwnd = User32.CreateWindowEx(
            0,
            className,
            "ShawMuOCRUnifiedClipboardWindow",
            0,
            0, 0, 0, 0,
            User32.HWND_MESSAGE,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            User32.AddClipboardFormatListener(hwnd);
        }

        _hwnd = hwnd;
        _startedEvent?.Set();

        while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            User32.TranslateMessage(ref msg);
            User32.DispatchMessage(ref msg);
        }

        if (hwnd != IntPtr.Zero)
        {
            User32.RemoveClipboardFormatListener(hwnd);
            User32.DestroyWindow(hwnd);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr ListenerWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == User32.WM_CLIPBOARDUPDATE)
        {
            s_activeListener?.OnNativeClipboardUpdated();
            return IntPtr.Zero;
        }
        if (msg == User32.WM_HOTKEY)
        {
            s_activeListener?.HotkeyTriggered?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == User32.WM_DESTROY)
        {
            User32.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void OnNativeClipboardUpdated()
    {
        uint seq = User32.GetClipboardSequenceNumber();
        long nowTick = Environment.TickCount64;

        lock (s_clipboardLock)
        {
            // 1. 同一剪贴板序列号去重：过滤单次复制触发的重复 WM_CLIPBOARDUPDATE 消息
            if (seq != 0 && seq == s_lastHandledSequenceNumber)
            {
                return;
            }

            // 2. 序列号精确匹配自写抑制：若本次序列号正是本进程 SetText(..., suppressNotification: true) 产生，直接忽略
            if (seq != 0 && seq == s_suppressedSequenceNumber)
            {
                s_lastHandledSequenceNumber = seq;
                s_suppressedSequenceNumber = 0;
                s_suppressedText = null;
                return;
            }
        }

        string? text = GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        lock (s_clipboardLock)
        {
            if (seq != 0 && seq == s_lastHandledSequenceNumber)
            {
                return;
            }
            s_lastHandledSequenceNumber = seq;

            // 若剪贴板辅助进程二次改写了序列号，但仍在自写抑制文本窗口内，同样精确拦截
            if (s_suppressedText != null &&
                nowTick <= s_suppressedUntilTick &&
                string.Equals(text, s_suppressedText, StringComparison.Ordinal))
            {
                s_suppressedSequenceNumber = 0;
                s_suppressedText = null;
                s_lastHandledText = text;
                s_lastHandledTick = nowTick;
                return;
            }

            // 过滤单次物理 Ctrl+C 在 150ms 内因外部应用延迟渲染触发的连发重复通知
            if (string.Equals(text, s_lastHandledText, StringComparison.Ordinal) &&
                nowTick - s_lastHandledTick < 150)
            {
                return;
            }

            s_lastHandledText = text;
            s_lastHandledTick = nowTick;
            s_suppressedText = null;
            s_suppressedSequenceNumber = 0;
        }

        ExternalTextChanged?.Invoke(text);
    }

    private static unsafe bool TrySetTextOnce(string text, bool suppressNotification)
    {
        lock (s_clipboardLock)
        {
            if (suppressNotification)
            {
                s_suppressedText = text;
                s_suppressedUntilTick = Environment.TickCount64 + 600;
            }

            if (!User32.OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            bool ok = false;
            try
            {
                User32.EmptyClipboard();

                int bytesNeeded = (text.Length + 1) * sizeof(char);
                IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)bytesNeeded);
                if (hGlobal == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
                if (targetPtr == IntPtr.Zero)
                {
                    Kernel32.GlobalFree(hGlobal);
                    return false;
                }

                try
                {
                    fixed (char* srcChars = text)
                    {
                        Buffer.MemoryCopy(srcChars, (void*)targetPtr, bytesNeeded, text.Length * sizeof(char));
                    }
                    Marshal.WriteInt16(targetPtr, text.Length * sizeof(char), 0);
                }
                finally
                {
                    Kernel32.GlobalUnlock(hGlobal);
                }

                ok = User32.SetClipboardData(User32.CF_UNICODETEXT, hGlobal) != IntPtr.Zero;
                if (!ok)
                {
                    Kernel32.GlobalFree(hGlobal);
                }
                return ok;
            }
            finally
            {
                User32.CloseClipboard();
                if (ok && suppressNotification)
                {
                    s_suppressedSequenceNumber = User32.GetClipboardSequenceNumber();
                    s_lastHandledText = text;
                    s_lastHandledTick = Environment.TickCount64;
                }
            }
        }
    }

    private static bool TryGetTextOnce(out string? text, out bool formatUnavailable)
    {
        text = null;
        formatUnavailable = false;

        lock (s_clipboardLock)
        {
            if (!User32.IsClipboardFormatAvailable(User32.CF_UNICODETEXT))
            {
                formatUnavailable = true;
                return false;
            }

            if (!User32.OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                IntPtr handle = User32.GetClipboardData(User32.CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                {
                    formatUnavailable = true;
                    return false;
                }

                IntPtr pointer = Kernel32.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    text = Marshal.PtrToStringUni(pointer);
                    return true;
                }
                finally
                {
                    Kernel32.GlobalUnlock(handle);
                }
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
    }

    private static unsafe bool TrySetImageOnce(OcrImage image)
    {
        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0) return false;

        int stride = width * 4;
        int headerSize = Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>();
        int totalSize = headerSize + stride * height;

        lock (s_clipboardLock)
        {
            if (!User32.OpenClipboard(IntPtr.Zero))
            {
                return false;
            }

            try
            {
                User32.EmptyClipboard();

                IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)totalSize);
                if (hGlobal == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
                if (targetPtr == IntPtr.Zero)
                {
                    Kernel32.GlobalFree(hGlobal);
                    return false;
                }

                try
                {
                    var bmiHeader = new Gdi32.BITMAPINFOHEADER
                    {
                        biSize = (uint)headerSize,
                        biWidth = width,
                        biHeight = height, // 正数：自底向上 DIB
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = Gdi32.BI_RGB,
                        biSizeImage = (uint)(stride * height),
                        biXPelsPerMeter = 0,
                        biYPelsPerMeter = 0,
                        biClrUsed = 0,
                        biClrImportant = 0
                    };
                    Marshal.StructureToPtr(bmiHeader, targetPtr, false);

                    byte* src = (byte*)image.Bitmap.GetPixels();
                    byte* dst = (byte*)targetPtr + headerSize;
                    int srcRowBytes = image.Bitmap.RowBytes;

                    for (int y = 0; y < height; y++)
                    {
                        byte* srcRow = src + y * srcRowBytes;
                        byte* dstRow = dst + (height - 1 - y) * stride;

                        for (int x = 0; x < width; x++)
                        {
                            int px = x * 4;
                            dstRow[px + 0] = srcRow[px + 0]; // B
                            dstRow[px + 1] = srcRow[px + 1]; // G
                            dstRow[px + 2] = srcRow[px + 2]; // R
                            dstRow[px + 3] = 0xFF;           // A: 确保不透明
                        }
                    }
                }
                finally
                {
                    Kernel32.GlobalUnlock(hGlobal);
                }

                bool ok = User32.SetClipboardData(User32.CF_DIB, hGlobal) != IntPtr.Zero;
                if (!ok)
                {
                    Kernel32.GlobalFree(hGlobal);
                }
                return ok;
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            StopListening();
            _startedEvent?.Dispose();
            _startedEvent = null;
        }
    }
}
