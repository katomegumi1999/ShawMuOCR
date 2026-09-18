using System.Diagnostics;
using System.Runtime.InteropServices;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Windows.Win32;

namespace ShawMuOCR.Windows.ScreenCapture;

public sealed class WindowsNativeScreenSelectionService : IScreenSelectionService
{
    private static readonly object _threadLock = new();
    private static Thread? _currentSelectionThread;

    public Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
    {
        return SelectRegionExAsync(fullScreenshot, virtualBounds).ContinueWith(t => t.Result?.Image);
    }

    public Task<ScreenSelectionResult?> SelectRegionExAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
    {
        IntPtr previousForeground = User32.GetForegroundWindow();

        lock (_threadLock)
        {
            if (_currentSelectionThread?.IsAlive == true)
            {
                _currentSelectionThread.Join(500);
            }

            var tcs = new TaskCompletionSource<ScreenSelectionResult?>();

            _currentSelectionThread = new Thread(() =>
            {
                try
                {
                    RunSelectionLoop(fullScreenshot, virtualBounds, tcs, previousForeground);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WindowsNativeScreenSelectionService] Exception in selection loop: {ex}");
                    tcs.TrySetResult(null);
                }
                finally
                {
                    tcs.TrySetResult(null); // 绝对保证不会处于未完成状态
                }
            })
            {
                IsBackground = true,
                Name = "ShawMuOCR_NativeSelectionThread"
            };

            _currentSelectionThread.SetApartmentState(ApartmentState.STA);
            _currentSelectionThread.Start();

            return tcs.Task;
        }
    }

    private const int ESC_HOTKEY_ID = 9002;

    private static void RunSelectionLoop(
        OcrImage fullScreenshot,
        ScreenRect virtualBounds,
        TaskCompletionSource<ScreenSelectionResult?> tcs,
        IntPtr previousForeground)
    {
        try
        {
            User32.SetThreadDpiAwarenessContext(User32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        // 若传入句柄失效，兜底重新获取前台窗口
        if (previousForeground == IntPtr.Zero || !User32.IsWindow(previousForeground))
        {
            previousForeground = User32.GetForegroundWindow();
        }

        string className = "ShawMuOCR_NativeOverlay_" + Guid.NewGuid().ToString("N");
        var state = new OverlayWindowState(fullScreenshot, virtualBounds, previousForeground);
        User32.WndProc wndProc = state.WndProcHandler;

        IntPtr hbrDefault = Gdi32.CreateSolidBrush(Gdi32.RGB(30, 30, 30));
        var wndClass = new User32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<User32.WNDCLASSEX>(),
            style = User32.CS_DBLCLKS | User32.CS_HREDRAW | User32.CS_VREDRAW,
            lpfnWndProc = wndProc,
            hInstance = Kernel32.GetModuleHandle(null),
            lpszClassName = className,
            hCursor = User32.LoadCursor(IntPtr.Zero, (IntPtr)User32.IDC_CROSS),
            hbrBackground = hbrDefault
        };

        ushort atom = User32.RegisterClassEx(ref wndClass);
        if (atom == 0)
        {
            Gdi32.DeleteObject(hbrDefault);
            tcs.TrySetResult(null);
            return;
        }

        IntPtr hwnd = IntPtr.Zero;
        IntPtr hookHandle = IntPtr.Zero;
        User32.LowLevelKeyboardProc? hookProc = null;
        bool escRegistered = false;
        try
        {
            // 核心机制：赋予 WS_EX_NOACTIVATE，创建绝不抢占前台与焦点的全屏顶层遮罩
            hwnd = User32.CreateWindowEx(
                User32.WS_EX_TOPMOST | User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE,
                className,
                "ShawMuOCR_Overlay",
                User32.WS_POPUP, // 初始不带 WS_VISIBLE，确保 GDI 缓冲区就绪前不提前派发无效绘制
                virtualBounds.X,
                virtualBounds.Y,
                virtualBounds.Width,
                virtualBounds.Height,
                IntPtr.Zero,
                IntPtr.Zero,
                wndClass.hInstance,
                IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                tcs.TrySetResult(null);
                return;
            }

            state.InitGdi(hwnd);

            // 临时注册 ESC 全局热键监听退出请求（无激活窗口无法接收常规键盘焦点事件）
            escRegistered = User32.RegisterHotKey(hwnd, ESC_HOTKEY_ID, 0, (uint)User32.VK_ESCAPE);
            if (!escRegistered)
            {
                // 若热键注册被其他应用阻断，自动挂载极短生命周期的 WH_KEYBOARD_LL 作为保底
                hookProc = (nCode, wp, lp) =>
                {
                    if (nCode >= 0 && (wp == (IntPtr)User32.WM_KEYDOWN || wp == (IntPtr)User32.WM_SYSKEYDOWN))
                    {
                        int vkCode = Marshal.ReadInt32(lp);
                        if (vkCode == User32.VK_ESCAPE)
                        {
                            User32.PostMessage(hwnd, User32.WM_HOTKEY, (IntPtr)ESC_HOTKEY_ID, IntPtr.Zero);
                            return (IntPtr)1; // 吞噬按键
                        }
                    }
                    return User32.CallNextHookEx(hookHandle, nCode, wp, lp);
                };
                hookHandle = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, hookProc, Kernel32.GetModuleHandle(null), 0);
            }

            // 无激活显示与置顶：使用 SWP_NOACTIVATE 和 SW_SHOWNOACTIVATE，用户当前窗口高亮与焦点丝毫不动
            User32.SetWindowPos(
                hwnd,
                User32.HWND_TOPMOST,
                virtualBounds.X,
                virtualBounds.Y,
                virtualBounds.Width,
                virtualBounds.Height,
                User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);

            User32.ShowWindow(hwnd, User32.SW_SHOWNOACTIVATE);

            // 同步强制向 DWM 发送重绘请求，确保窗口瞬间呈现灰化遮罩
            User32.RedrawWindow(
                hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                User32.RDW_INVALIDATE | User32.RDW_UPDATENOW | User32.RDW_ERASE | User32.RDW_ALLCHILDREN);

            while (User32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                User32.TranslateMessage(ref msg);
                User32.DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (escRegistered && hwnd != IntPtr.Zero)
            {
                User32.UnregisterHotKey(hwnd, ESC_HOTKEY_ID);
            }
            if (hookHandle != IntPtr.Zero)
            {
                User32.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
            GC.KeepAlive(hookProc);

            // 关键时机：在注销窗口前，先将前台接力棒交还给原目标窗口
            SafeRestoreForegroundWindow(previousForeground);

            if (hwnd != IntPtr.Zero && User32.IsWindow(hwnd))
            {
                User32.DestroyWindow(hwnd);
            }
            User32.UnregisterClass(className, wndClass.hInstance);
            if (hbrDefault != IntPtr.Zero)
            {
                Gdi32.DeleteObject(hbrDefault);
            }

            // 再次确保恢复，防止底层旧窗口意外冒出
            SafeRestoreForegroundWindow(previousForeground);

            GC.KeepAlive(wndProc);
            tcs.TrySetResult(state.ResultEx);
        }
    }

    public static bool SafeRestoreForegroundWindow(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero || !User32.IsWindow(targetHwnd))
            return false;

        IntPtr currentForeground = User32.GetForegroundWindow();
        if (currentForeground == targetHwnd)
            return true; // 已经是当前前台，无需重复调用干扰其内部焦点状态

        User32.AllowSetForegroundWindow(User32.ASFW_ANY);

        // 1. 如果当前处于前台，直接利用合法前台特权交接（零副作用，最平滑）
        User32.BringWindowToTop(targetHwnd);
        if (User32.SetForegroundWindow(targetHwnd))
        {
            return true;
        }

        // 2. 如果直接交接失败（已被降级为后台），启用 AttachThreadInput 兜底保底方案
        uint currentThreadId = Kernel32.GetCurrentThreadId();
        uint targetThreadId = User32.GetWindowThreadProcessId(targetHwnd, out _);

        bool attached = false;
        try
        {
            if (currentThreadId != targetThreadId && targetThreadId != 0)
            {
                attached = User32.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            User32.BringWindowToTop(targetHwnd);
            return User32.SetForegroundWindow(targetHwnd);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (attached)
            {
                User32.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
    }


    private sealed class OverlayWindowState
    {
        private readonly OcrImage _fullScreenshot;
        private readonly ScreenRect _virtualBounds;

        private readonly IntPtr _hCursor;
        private readonly IntPtr _hBrushBadge;
        private readonly IntPtr _hBrushBlue;
        private readonly IntPtr _hFont;

        private IntPtr _hdcScreen;
        private IntPtr _hdcOrig;
        private IntPtr _hdcDark;
        private IntPtr _hdcBack;
        private IntPtr _hBmpOrig;
        private IntPtr _hBmpDark;
        private IntPtr _hBmpBack;

        private IntPtr _hOldBmpOrig;
        private IntPtr _hOldBmpDark;
        private IntPtr _hOldBmpBack;

        private readonly IntPtr _previousForeground;
        private bool _isDragging;
        private int _paintCount;
        private int _startX;
        private int _startY;
        private int _curX;
        private int _curY;
        private OcrImage? _result;
        private ScreenRect _resultBounds;

        public OcrImage? Result => _result;
        public ScreenSelectionResult? ResultEx => _result != null ? new ScreenSelectionResult { Image = _result, ScreenBounds = _resultBounds } : null;

        public OverlayWindowState(
            OcrImage fullScreenshot,
            ScreenRect virtualBounds,
            IntPtr previousForeground)
        {
            _fullScreenshot = fullScreenshot;
            _virtualBounds = virtualBounds;
            _previousForeground = previousForeground;

            _hCursor = User32.LoadCursor(IntPtr.Zero, (IntPtr)User32.IDC_CROSS);
            _hBrushBadge = Gdi32.CreateSolidBrush(Gdi32.RGB(35, 35, 35));
            _hBrushBlue = Gdi32.CreateSolidBrush(Gdi32.RGB(0, 120, 215));

            _hFont = Gdi32.CreateFont(
                -13,
                0, 0, 0,
                600, // FW_SEMIBOLD
                0, 0, 0, 1,
                0, 0, 5, // CLEARTYPE_QUALITY
                0, "Segoe UI");
        }

        public void InitGdi(IntPtr hwnd)
        {
            int w = _virtualBounds.Width;
            int h = _virtualBounds.Height;

            _hdcScreen = User32.GetDC(IntPtr.Zero);
            _hdcOrig = Gdi32.CreateCompatibleDC(_hdcScreen);
            _hdcDark = Gdi32.CreateCompatibleDC(_hdcScreen);
            _hdcBack = Gdi32.CreateCompatibleDC(_hdcScreen);

            _hBmpOrig = Gdi32.CreateCompatibleBitmap(_hdcScreen, w, h);
            _hBmpDark = Gdi32.CreateCompatibleBitmap(_hdcScreen, w, h);
            _hBmpBack = Gdi32.CreateCompatibleBitmap(_hdcScreen, w, h);

            // 保存原始旧位图句柄，以便销毁时必须还原才能彻底释放显存
            _hOldBmpOrig = Gdi32.SelectObject(_hdcOrig, _hBmpOrig);
            _hOldBmpDark = Gdi32.SelectObject(_hdcDark, _hBmpDark);
            _hOldBmpBack = Gdi32.SelectObject(_hdcBack, _hBmpBack);

            var bmi = new Gdi32.BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = w;
            bmi.bmiHeader.biHeight = -h; // 自顶向下
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = (uint)Gdi32.BI_RGB;

            IntPtr pixelsPtr = _fullScreenshot.Bitmap.GetPixels();
            Gdi32.SetDIBits(_hdcOrig, _hBmpOrig, 0, (uint)h, pixelsPtr, ref bmi, Gdi32.DIB_RGB_COLORS);

            // 预生成暗化底图：使用非托管内存 Marshal.AllocHGlobal，避免在托管堆(LOH)上分配几十兆的垃圾 byte[]
            int totalBytes = w * h * 4;
            IntPtr darkPixels = Marshal.AllocHGlobal(totalBytes);
            try
            {
                unsafe
                {
                    byte* src = (byte*)pixelsPtr;
                    byte* dst = (byte*)darkPixels;
                    for (int i = 0; i < totalBytes; i += 4)
                    {
                        dst[i]     = (byte)((src[i]     * 120) >> 8); // B
                        dst[i + 1] = (byte)((src[i + 1] * 120) >> 8); // G
                        dst[i + 2] = (byte)((src[i + 2] * 120) >> 8); // R
                        dst[i + 3] = 255;                              // A
                    }
                }

                Gdi32.SetDIBits(_hdcDark, _hBmpDark, 0, (uint)h, darkPixels, ref bmi, Gdi32.DIB_RGB_COLORS);
            }
            finally
            {
                Marshal.FreeHGlobal(darkPixels);
            }

            // 预先将暗化底图拷贝到后台双缓冲中，保证任意首次绘制调用均能瞬间输出
            Gdi32.BitBlt(_hdcBack, 0, 0, w, h, _hdcDark, 0, 0, Gdi32.SRCCOPY);
        }

        public IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case User32.WM_MOUSEACTIVATE:
                    // 核心关键：禁止激活遮罩窗口，保持底层原始窗口的焦点与前台状态不变，同时不丢弃鼠标消息
                    return (IntPtr)User32.MA_NOACTIVATE;

                case User32.WM_HOTKEY:
                    if ((int)wParam.ToInt64() == ESC_HOTKEY_ID)
                    {
                        CloseOverlay(hWnd, null);
                        return IntPtr.Zero;
                    }
                    break;

                case User32.WM_ACTIVATE:
                    int wa = (int)(wParam.ToInt64() & 0xFFFF);
                    if (wa == User32.WA_INACTIVE)
                    {
                        // 若被系统其他窗口意外挤占焦点，立即强制重置为最顶层
                        User32.SetWindowPos(
                            hWnd,
                            User32.HWND_TOPMOST,
                            0, 0, 0, 0,
                            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                    }
                    return IntPtr.Zero;

                case User32.WM_SETCURSOR:
                    User32.SetCursor(_hCursor);
                    // 若 DWM 在初次绘制时错过呈现，当鼠标移入遮罩时立即触发同步重绘
                    if (_paintCount < 2)
                    {
                        User32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        User32.UpdateWindow(hWnd);
                    }
                    return (IntPtr)1;

                case User32.WM_ERASEBKGND:
                    // 直接输出暗化底图，避免窗口产生闪烁或空白
                    if (_hdcDark != IntPtr.Zero)
                    {
                        Gdi32.BitBlt(wParam, 0, 0, _virtualBounds.Width, _virtualBounds.Height, _hdcDark, 0, 0, Gdi32.SRCCOPY);
                    }
                    return (IntPtr)1;

                case User32.WM_PAINT:
                    OnPaint(hWnd);
                    _paintCount++;
                    return IntPtr.Zero;

                case User32.WM_LBUTTONDOWN:
                    User32.SetCapture(hWnd);
                    _isDragging = true;
                    _startX = ClampX((short)(lParam.ToInt64() & 0xFFFF));
                    _startY = ClampY((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                    _curX = _startX;
                    _curY = _startY;
                    User32.InvalidateRect(hWnd, IntPtr.Zero, false);
                    User32.UpdateWindow(hWnd);
                    return IntPtr.Zero;

                case User32.WM_MOUSEMOVE:
                    // 悬停阶段若首帧未被 DWM 完整捕获，立即强制补绘一次
                    if (_paintCount < 2)
                    {
                        User32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        User32.UpdateWindow(hWnd);
                    }
                    if (_isDragging)
                    {
                        _curX = ClampX((short)(lParam.ToInt64() & 0xFFFF));
                        _curY = ClampY((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        User32.InvalidateRect(hWnd, IntPtr.Zero, false);
                        User32.UpdateWindow(hWnd);
                    }
                    return IntPtr.Zero;

                case User32.WM_LBUTTONUP:
                    if (_isDragging)
                    {
                        int selX = Math.Min(_startX, _curX);
                        int selY = Math.Min(_startY, _curY);
                        int selW = Math.Abs(_curX - _startX);
                        int selH = Math.Abs(_curY - _startY);

                        if (selW >= 5 && selH >= 5)
                        {
                            var cropped = _fullScreenshot.Crop(selX, selY, selW, selH);
                            CloseOverlay(hWnd, cropped, new ScreenRect(selX + _virtualBounds.X, selY + _virtualBounds.Y, selW, selH));
                        }
                        else
                        {
                            User32.ReleaseCapture();
                            _isDragging = false;
                            User32.InvalidateRect(hWnd, IntPtr.Zero, false);
                            User32.UpdateWindow(hWnd);
                        }
                    }
                    return IntPtr.Zero;

                case User32.WM_RBUTTONDOWN:
                    // 仅吸收右键按下事件，绝不此时销毁窗口，防止随后的 WM_RBUTTONUP 穿透到底层触发系统右键菜单
                    return IntPtr.Zero;

                case User32.WM_RBUTTONUP:
                    // 用户在遮罩上松开右键时才取消退出，完整吞噬一套右键流程
                    CloseOverlay(hWnd, null);
                    return IntPtr.Zero;

                case User32.WM_CONTEXTMENU:
                    // 彻底屏蔽上下文菜单消息
                    return IntPtr.Zero;

                case User32.WM_KEYDOWN:
                    if ((int)wParam.ToInt64() == User32.VK_ESCAPE)
                    {
                        CloseOverlay(hWnd, null);
                        return IntPtr.Zero;
                    }
                    break;

                case User32.WM_DESTROY:
                    CleanupGdi();
                    User32.PostQuitMessage(0);
                    return IntPtr.Zero;
            }

            return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void CloseOverlay(IntPtr hWnd, OcrImage? result, ScreenRect bounds = default)
        {
            if (_isDragging)
            {
                User32.ReleaseCapture();
                _isDragging = false;
            }
            _result = result;
            _resultBounds = bounds;
            SafeRestoreForegroundWindow(_previousForeground);
            User32.DestroyWindow(hWnd);
        }

        private void OnPaint(IntPtr hWnd)
        {
            User32.BeginPaint(hWnd, out var ps);

            int w = _virtualBounds.Width;
            int h = _virtualBounds.Height;

            // 1. 将预先暗化的底图全部拷贝到后台缓冲
            Gdi32.BitBlt(_hdcBack, 0, 0, w, h, _hdcDark, 0, 0, Gdi32.SRCCOPY);

            if (_isDragging)
            {
                int selX = Math.Min(_startX, _curX);
                int selY = Math.Min(_startY, _curY);
                int selW = Math.Abs(_curX - _startX);
                int selH = Math.Abs(_curY - _startY);

                if (selW > 0 && selH > 0)
                {
                    // 2. 抠图：将选区内清晰原图从 hdcOrig 拷贝到后台缓冲
                    Gdi32.BitBlt(_hdcBack, selX, selY, selW, selH, _hdcOrig, selX, selY, Gdi32.SRCCOPY);

                    // 3. 绘制 2 像素蓝色高亮边框
                    var outerRc = new User32.RECT(selX, selY, selX + selW, selY + selH);
                    User32.FrameRect(_hdcBack, ref outerRc, _hBrushBlue);
                    var innerRc = new User32.RECT(selX + 1, selY + 1, selX + selW - 1, selY + selH - 1);
                    User32.FrameRect(_hdcBack, ref innerRc, _hBrushBlue);

                    // 4. 绘制尺寸标签标牌（如 "800 × 600"）
                    string sizeText = $"{selW} × {selH}";
                    IntPtr hOldFont = Gdi32.SelectObject(_hdcBack, _hFont);

                    var textRc = new User32.RECT(0, 0, 0, 0);
                    User32.DrawText(_hdcBack, sizeText, sizeText.Length, ref textRc, User32.DT_CALCRECT | User32.DT_SINGLELINE);

                    int badgeW = textRc.Width + 16;
                    int badgeH = textRc.Height + 8;
                    int badgeX = selX;
                    int badgeY = selY - badgeH - 4;
                    if (badgeY < 4) badgeY = selY + 4;
                    if (badgeX + badgeW > w) badgeX = w - badgeW - 4;

                    var badgeRc = new User32.RECT(badgeX, badgeY, badgeX + badgeW, badgeY + badgeH);
                    User32.FillRect(_hdcBack, ref badgeRc, _hBrushBadge);

                    Gdi32.SetTextColor(_hdcBack, Gdi32.RGB(255, 255, 255));
                    Gdi32.SetBkMode(_hdcBack, Gdi32.TRANSPARENT);
                    User32.DrawText(_hdcBack, sizeText, sizeText.Length, ref badgeRc, User32.DT_CENTER | User32.DT_VCENTER | User32.DT_SINGLELINE);

                    Gdi32.SelectObject(_hdcBack, hOldFont);
                }
            }

            // 5. 将后台缓冲瞬间输出到窗口屏幕
            Gdi32.BitBlt(ps.hdc, 0, 0, w, h, _hdcBack, 0, 0, Gdi32.SRCCOPY);

            User32.EndPaint(hWnd, ref ps);
        }

        private int ClampX(int x) => Math.Clamp(x, 0, _virtualBounds.Width);
        private int ClampY(int y) => Math.Clamp(y, 0, _virtualBounds.Height);

        private void CleanupGdi()
        {
            if (_hBrushBadge != IntPtr.Zero) { Gdi32.DeleteObject(_hBrushBadge); }
            if (_hBrushBlue != IntPtr.Zero) { Gdi32.DeleteObject(_hBrushBlue); }
            if (_hFont != IntPtr.Zero) { Gdi32.DeleteObject(_hFont); }

            // 关键：必须先将位图从设备上下文中换出（还原 hOldBmp），DeleteObject 才会真正释放底层物理显存！
            if (_hdcOrig != IntPtr.Zero && _hOldBmpOrig != IntPtr.Zero) Gdi32.SelectObject(_hdcOrig, _hOldBmpOrig);
            if (_hdcDark != IntPtr.Zero && _hOldBmpDark != IntPtr.Zero) Gdi32.SelectObject(_hdcDark, _hOldBmpDark);
            if (_hdcBack != IntPtr.Zero && _hOldBmpBack != IntPtr.Zero) Gdi32.SelectObject(_hdcBack, _hOldBmpBack);

            if (_hBmpOrig != IntPtr.Zero) { Gdi32.DeleteObject(_hBmpOrig); _hBmpOrig = IntPtr.Zero; }
            if (_hBmpDark != IntPtr.Zero) { Gdi32.DeleteObject(_hBmpDark); _hBmpDark = IntPtr.Zero; }
            if (_hBmpBack != IntPtr.Zero) { Gdi32.DeleteObject(_hBmpBack); _hBmpBack = IntPtr.Zero; }

            if (_hdcOrig != IntPtr.Zero) { Gdi32.DeleteDC(_hdcOrig); _hdcOrig = IntPtr.Zero; }
            if (_hdcDark != IntPtr.Zero) { Gdi32.DeleteDC(_hdcDark); _hdcDark = IntPtr.Zero; }
            if (_hdcBack != IntPtr.Zero) { Gdi32.DeleteDC(_hdcBack); _hdcBack = IntPtr.Zero; }

            if (_hdcScreen != IntPtr.Zero) { User32.ReleaseDC(IntPtr.Zero, _hdcScreen); _hdcScreen = IntPtr.Zero; }
        }
    }
}
