using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MuClip.Interop;
using MuClip.Models;
using MuClip.Services;
using MuClip.ViewModels;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;

namespace ShawMuOCR.Views.Clipboard;

public unsafe partial class ClipboardQuickPanelWindow : Window
{
    private static ClipboardQuickPanelWindow? s_currentInstance;
    private IntPtr _hwnd = IntPtr.Zero;
    private readonly WindowsInputTarget _inputTarget = new();
    private bool _isActivatedMode = false;
    private bool _isSubclassed = false;
    private bool _hasBeenActivated;
    private long _activatedTimestamp;
    private bool _isWindowDragging;

    // 全局低级钩子（用于在 No-Activate 模式下检测外部点击与 Esc 关闭）
    private static IntPtr s_mouseHook = IntPtr.Zero;
    private static IntPtr s_keyboardHook = IntPtr.Zero;

    // 平滑动量阻尼滚轮引擎
    private readonly DispatcherTimer _smoothScrollTimer;
    private double _targetScrollY;

    // 列表长按拖拽重排相关状态
    private readonly DispatcherTimer _longPressTimer;
    private MuClipItem? _pressedItem;
    private Control? _pressedControl;
    private Point _pressStartPoint;
    private bool _isItemDragActive;

    // 缓存控件引用
    private Panel? _contentPanel;
    private ListBox? _itemsListBox;
    private Border? _dragPreviewCard;
    private TranslateTransform? _dragPreviewTransform;
    private TextBlock? _dragPreviewText;
    private TextBlock? _dragPreviewSubText;
    private TextBox? _searchBox;
    private TextBox? _editingTextBox;

    public ClipboardQuickPanelViewModel? ViewModel => DataContext as ClipboardQuickPanelViewModel;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_NCDESTROY = 0x0082;
    private const int MA_ACTIVATE = 1;
    private const int MA_NOACTIVATE = 3;

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static partial IntPtr WindowFromPoint(POINT Point);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial IntPtr SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, UIntPtr, IntPtr, IntPtr> lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("comctl32.dll", EntryPoint = "SetWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(IntPtr hWnd, delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [LibraryImport("comctl32.dll", EntryPoint = "DefSubclassProc")]
    private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam);

    [LibraryImport("comctl32.dll", EntryPoint = "RemoveWindowSubclass", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(IntPtr hWnd, delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, UIntPtr, UIntPtr, IntPtr> pfnSubclass, UIntPtr uIdSubclass);

    public ClipboardQuickPanelWindow()
    {
        InitializeComponent();

        _smoothScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10) // 100 FPS 高刷阻尼
        };
        _smoothScrollTimer.Tick += OnSmoothScrollTimerTick;

        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        KeyDown += OnWindowKeyDown;
        PointerMoved += OnWindowPointerMoved;
        PointerReleased += OnWindowPointerReleased;
        PointerCaptureLost += OnWindowPointerCaptureLost;
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;

        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _longPressTimer.Tick += OnLongPressTimerTick;

        _contentPanel = this.FindControl<Panel>("ContentPanel");
        _itemsListBox = this.FindControl<ListBox>("ItemsListBox");
        if (_itemsListBox != null)
        {
            _itemsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnListBoxScrollChanged);
            _itemsListBox.AddHandler(InputElement.PointerWheelChangedEvent, OnListBoxPointerWheelChanged, RoutingStrategies.Tunnel);
        }
        _dragPreviewCard = this.FindControl<Border>("DragPreviewCard");
        _dragPreviewTransform = (_dragPreviewCard?.RenderTransform as TranslateTransform) ?? new TranslateTransform();
        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.RenderTransform = _dragPreviewTransform;
        }
        _dragPreviewText = this.FindControl<TextBlock>("DragPreviewText");
        _dragPreviewSubText = this.FindControl<TextBlock>("DragPreviewSubText");

        // 搜索框：挂载点击与获焦监听，触发激活降级
        _searchBox = this.FindControl<TextBox>("SearchBox");
        if (_searchBox != null)
        {
            _searchBox.AddHandler(InputElement.PointerPressedEvent, (s, e) => TransitionToActivatedMode(), RoutingStrategies.Tunnel);
            _searchBox.GotFocus += (s, e) => TransitionToActivatedMode();
        }

        // 编辑文本框：挂载点击监听
        _editingTextBox = this.FindControl<TextBox>("EditingTextBox");
        if (_editingTextBox != null)
        {
            _editingTextBox.AddHandler(InputElement.PointerPressedEvent, (s, e) => TransitionToActivatedMode(), RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipboardQuickPanelViewModel.IsEditing))
        {
            if (ViewModel?.IsEditing == true)
            {
                TransitionToActivatedMode();
                Dispatcher.UIThread.Post(() =>
                {
                    _editingTextBox?.Focus();
                }, DispatcherPriority.Input);
            }
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        var handle = TryGetPlatformHandle()?.Handle;
        if (handle.HasValue && handle.Value != IntPtr.Zero)
        {
            _hwnd = handle.Value;
            ApplyNativeRoundedCorners();
            ApplyNoActivateStyle();
            SubclassWindow();
            InstallHooks();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _smoothScrollTimer.Stop();
        _longPressTimer.Stop();
        UninstallHooks();
        UnsubclassWindow();
        if (s_currentInstance == this)
        {
            s_currentInstance = null;
        }
    }

    private void ApplyNoActivateStyle()
    {
        if (_hwnd == IntPtr.Zero) return;
        try
        {
            long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
            exStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch
        {
            // 容错处理
        }
    }

    /// <summary>
    /// 当用户点击输入框或进入编辑模式时，优雅退化为激活模式，获取真实键盘焦点
    /// </summary>
    public void TransitionToActivatedMode()
    {
        if (_isActivatedMode) return;
        _isActivatedMode = true;

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                long exStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
                exStyle &= ~WS_EX_NOACTIVATE;
                SetWindowLongPtr(_hwnd, GWL_EXSTYLE, (IntPtr)exStyle);
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                SetForegroundWindow(_hwnd);
            }

            Activate();
            _hasBeenActivated = true;
            _activatedTimestamp = Environment.TickCount64;

            if (ViewModel?.IsEditing == true)
            {
                _editingTextBox?.Focus();
            }
            else
            {
                _searchBox?.Focus();
            }
        }
        catch
        {
            // 容错处理
        }
    }

    private void SubclassWindow()
    {
        if (_isSubclassed || _hwnd == IntPtr.Zero) return;
        if (SetWindowSubclass(_hwnd, &SubclassProc, (UIntPtr)1, UIntPtr.Zero))
        {
            _isSubclassed = true;
        }
    }

    private void UnsubclassWindow()
    {
        if (!_isSubclassed || _hwnd == IntPtr.Zero) return;
        RemoveWindowSubclass(_hwnd, &SubclassProc, (UIntPtr)1);
        _isSubclassed = false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr SubclassProc(IntPtr hWnd, uint uMsg, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_MOUSEACTIVATE)
        {
            // 若当前仍处于默认的未激活浮动模式，拦截激活请求，返回 MA_NOACTIVATE (3)
            // 使得系统不转移前台焦点，鼠标点击直接穿透给 Avalonia 正常分发处理
            if (s_currentInstance != null && !s_currentInstance._isActivatedMode)
            {
                return (IntPtr)MA_NOACTIVATE;
            }
        }
        else if (uMsg == WM_NCDESTROY)
        {
            RemoveWindowSubclass(hWnd, &SubclassProc, uIdSubclass);
            if (s_currentInstance != null)
            {
                s_currentInstance._isSubclassed = false;
            }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private static void InstallHooks()
    {
        UninstallHooks();
        IntPtr hMod = GetModuleHandleW(null);
        s_mouseHook = SetWindowsHookExW(WH_MOUSE_LL, &MouseHookProc, hMod, 0);
        s_keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, &KeyboardHookProc, hMod, 0);
    }

    private static void UninstallHooks()
    {
        if (s_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(s_mouseHook);
            s_mouseHook = IntPtr.Zero;
        }
        if (s_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(s_keyboardHook);
            s_keyboardHook = IntPtr.Zero;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MouseHookProc(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && s_currentInstance != null && s_currentInstance.IsVisible && s_currentInstance._hwnd != IntPtr.Zero)
        {
            uint msg = (uint)wParam;
            if (msg is 0x0201 /* WM_LBUTTONDOWN */ or 0x0204 /* WM_RBUTTONDOWN */ or 0x00A1 /* WM_NCLBUTTONDOWN */ or 0x00A4 /* WM_NCRBUTTONDOWN */)
            {
                MSLLHOOKSTRUCT* pMouse = (MSLLHOOKSTRUCT*)lParam;
                if (pMouse != null)
                {
                    IntPtr clickedHwnd = WindowFromPoint(pMouse->pt);
                    uint clickedPid = 0;
                    if (clickedHwnd != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(clickedHwnd, out clickedPid);
                    }

                    uint currentPid = (uint)Environment.ProcessId;
                    bool isOurProcess = (clickedPid == currentPid && clickedPid != 0);

                    bool insideMainRect = false;
                    if (GetWindowRect(s_currentInstance._hwnd, out RECT rect))
                    {
                        insideMainRect = pMouse->pt.x >= rect.left && pMouse->pt.x <= rect.right &&
                                         pMouse->pt.y >= rect.top && pMouse->pt.y <= rect.bottom;
                    }

                    // 只有点击到其他外部应用且不在本窗口内时才自动关闭面板
                    if (!isOurProcess && !insideMainRect && !s_currentInstance._isWindowDragging)
                    {
                        var inst = s_currentInstance;
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                inst?.Close();
                            }
                            catch
                            {
                                // 忽略关闭异常
                            }
                        });
                    }
                }
            }
        }

        return CallNextHookEx(s_mouseHook, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr KeyboardHookProc(int nCode, UIntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && s_currentInstance != null && s_currentInstance.IsVisible)
        {
            uint msg = (uint)wParam;
            if (msg is 0x0100 /* WM_KEYDOWN */ or 0x0104 /* WM_SYSKEYDOWN */)
            {
                KBDLLHOOKSTRUCT* pKey = (KBDLLHOOKSTRUCT*)lParam;
                if (pKey != null && pKey->vkCode == 0x1B /* VK_ESCAPE */)
                {
                    if (!s_currentInstance._isActivatedMode)
                    {
                        var inst = s_currentInstance;
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                inst?.Close();
                            }
                            catch
                            {
                                // 忽略关闭异常
                            }
                        });
                        // 拦截 Esc 按键，避免 Esc 穿透到底层目标输入框（如取消资源管理器的重命名）
                        return (IntPtr)1;
                    }
                }
            }
        }

        return CallNextHookEx(s_keyboardHook, nCode, wParam, lParam);
    }

    private void ApplyNativeRoundedCorners()
    {
        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                int cornerPreference = DWMWCP_ROUND;
                DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
            }
        }
        catch
        {
            // 旧版 Windows (如 Win10) 或无 DWM 支持时静默降级为直角
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        _hasBeenActivated = true;
        _activatedTimestamp = Environment.TickCount64;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public static void CloseInstance()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (s_currentInstance != null)
            {
                try
                {
                    s_currentInstance.Close();
                }
                catch { }
                s_currentInstance = null;
            }
        });
    }

    public static void ShowInstance(IMuClipService service, string? backgroundColor = null, int initialTabIndex = 0)
    {
        ShowInstance(service, settingsService: null, backgroundColor: backgroundColor, initialTabIndex: initialTabIndex);
    }

    public static void ShowInstance(IMuClipService service, ISettingsService? settingsService, string? backgroundColor = null, int initialTabIndex = 0)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (s_currentInstance != null && s_currentInstance.IsVisible)
            {
                if (s_currentInstance.ViewModel?.SelectedTabIndex == initialTabIndex)
                {
                    s_currentInstance.Close();
                    s_currentInstance = null;
                    return;
                }
                else
                {
                    s_currentInstance.ViewModel?.SwitchTab(initialTabIndex);
                    return;
                }
            }

            var vm = new ClipboardQuickPanelViewModel(service)
            {
                SelectedTabIndex = initialTabIndex
            };

            var win = new ClipboardQuickPanelWindow
            {
                DataContext = vm
            };

            // 1. 固化捕获当前的前台窗口句柄 (此时目标输入窗口仍是绝对的前台焦点窗口)
            win._inputTarget.Capture();

            // 2. 挂载粘贴回调，由窗口根据当前激活状态分流 DirectPaste 或 RestoreAndPaste
            vm.RequestPaste += win.ExecutePaste;

            if (settingsService != null)
            {
                win.ApplyCustomThemeColors(settingsService);
            }
            else if (!string.IsNullOrWhiteSpace(backgroundColor))
            {
                win.ApplyCustomBackground(backgroundColor);
            }

            vm.RequestClose += () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        win.Close();
                    }
                    catch
                    {
                        // 忽略关闭异常
                    }
                });
            };

            s_currentInstance = win;
            win.Closed += (s, e) =>
            {
                if (s_currentInstance == win)
                {
                    s_currentInstance = null;
                }
            };

            win.PositionAtCaretOrCursor();
            win.Show();
            // 关键：不调用 win.Activate() 与 searchBox.Focus()，默认保持 No-Activate 悬浮状态！
        });
    }

    /// <summary>
    /// 根据当前是否处于激活模式，分流就地无缝粘贴或焦点还原粘贴
    /// </summary>
    public void ExecutePaste()
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool wasActivated = _isActivatedMode;
            try
            {
                Close();
            }
            catch
            {
                // 忽略关闭异常
            }

            if (!wasActivated)
            {
                // 默认 No-Activate 模式：原窗口从始至终未失活，无焦点切换损耗，直接就地粘贴
                WindowsInputTarget.DirectPasteWithoutActivating(20);
            }
            else
            {
                // 曾退化为激活模式（如搜索/编辑）：恢复原目标窗口焦点并就地粘贴
                _inputTarget.RestoreAndPaste(35);
            }
        });
    }

    public void ApplyCustomBackground(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var col))
        {
            var brush = new SolidColorBrush(col);
            Background = brush;
            var border = this.FindControl<Border>("RootCardBorder");
            if (border != null)
            {
                border.Background = brush;
            }
        }
    }

    public void ApplyCustomThemeColors(ISettingsService? settingsService)
    {
        if (settingsService?.Current == null) return;

        var s = settingsService.Current;
        string? bgHex = s.ThemeFloatingWindowBackgroundColorHex ?? s.ClipboardPanelBackground;
        string? textHex = s.ThemeFloatingWindowTextColorHex;

        if (!string.IsNullOrWhiteSpace(bgHex) && ColorPaletteHelper.TryParseColor(bgHex, out var bg))
        {
            double brightness = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            bool isDark = brightness < 0.5;

            Color cardBg = isDark
                ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 15), (byte)Math.Min(255, bg.G + 15), (byte)Math.Min(255, bg.B + 15))
                : Color.FromArgb(255, (byte)Math.Max(0, bg.R + 8), (byte)Math.Max(0, bg.G + 8), (byte)Math.Max(0, bg.B + 8));

            Color border = isDark
                ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 35), (byte)Math.Min(255, bg.G + 35), (byte)Math.Min(255, bg.B + 35))
                : Color.FromArgb(255, (byte)Math.Max(0, bg.R - 25), (byte)Math.Max(0, bg.G - 25), (byte)Math.Max(0, bg.B - 25));

            Color textPrimary = !string.IsNullOrWhiteSpace(textHex) && ColorPaletteHelper.TryParseColor(textHex, out var customText)
                ? customText
                : (isDark ? Color.FromRgb(237, 237, 237) : Color.FromRgb(32, 32, 32));

            Color textSecondary = Color.FromArgb((byte)(textPrimary.A * 0.72), textPrimary.R, textPrimary.G, textPrimary.B);
            Color textTertiary = Color.FromArgb((byte)(textPrimary.A * 0.52), textPrimary.R, textPrimary.G, textPrimary.B);

            Resources["QuickPanelBackgroundBrush"] = new SolidColorBrush(bg);
            Resources["QuickPanelCardBrush"] = new SolidColorBrush(cardBg);
            Resources["QuickPanelBorderBrush"] = new SolidColorBrush(border);
            Resources["QuickPanelTextPrimary"] = new SolidColorBrush(textPrimary);
            Resources["QuickPanelTextSecondary"] = new SolidColorBrush(textSecondary);
            Resources["QuickPanelTextTertiary"] = new SolidColorBrush(textTertiary);
            Resources["TextControlForeground"] = new SolidColorBrush(textPrimary);
            Resources["TextControlCaretBrush"] = new SolidColorBrush(textPrimary);
            Resources["ButtonForeground"] = new SolidColorBrush(textPrimary);

            Background = new SolidColorBrush(bg);
            var rootBorder = this.FindControl<Border>("RootCardBorder");
            if (rootBorder != null)
            {
                rootBorder.Background = new SolidColorBrush(bg);
                rootBorder.BorderBrush = new SolidColorBrush(border);
            }
        }
    }

    private void PositionAtCaretOrCursor()
    {
        var (screenX, screenY, isCaret) = Win32CaretHelper.GetCaretOrCursorScreenPosition();

        // 默认窗口 DIP 尺寸（紧凑实体型 368 x 440）
        double dipW = Width > 0 ? Width : 368;
        double dipH = Height > 0 ? Height : 440;

        // 获取所在显示器工作区边界
        var screen = Screens.ScreenFromPoint(new PixelPoint(screenX, screenY)) ?? Screens.Primary;
        if (screen != null)
        {
            double scale = screen.Scaling;
            var bounds = screen.WorkingArea;

            // 将 DIP 逻辑像素换算为屏幕物理像素尺寸，确保在高 DPI 下不溢出边界
            int physicalW = (int)Math.Ceiling(dipW * scale);
            int physicalH = (int)Math.Ceiling(dipH * scale);

            int marginPx = (int)(10 * scale);

            int targetX = screenX;
            int targetY = screenY;

            // 1. 水平方向边界防遮挡与调整
            if (targetX + physicalW > bounds.X + bounds.Width - marginPx)
            {
                targetX = bounds.X + bounds.Width - physicalW - marginPx;
            }
            if (targetX < bounds.X + marginPx)
            {
                targetX = bounds.X + marginPx;
            }

            // 2. 垂直方向边界防遮挡与调整
            if (targetY + physicalH > bounds.Y + bounds.Height - marginPx)
            {
                if (isCaret)
                {
                    // 若有键入光标，向上翻转到光标上方弹出
                    int caretH = (int)(22 * scale);
                    targetY = screenY - physicalH - caretH;
                }
                else
                {
                    // 鼠标触发，停靠在工作区底部上方
                    targetY = bounds.Y + bounds.Height - physicalH - marginPx;
                }
            }

            // 检查翻转后是否超出屏幕顶部
            if (targetY < bounds.Y + marginPx)
            {
                targetY = bounds.Y + marginPx;
            }

            Position = new PixelPoint(targetX, targetY);
        }
        else
        {
            Position = new PixelPoint(screenX, screenY);
        }
    }

    private void OnTopBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 若点击来自 TextBox、Button 等可交互控件，由控件自身处理输入与点击，不触发窗口拖拽
        if (e.Source is Visual visual && (visual.FindAncestorOfType<TextBox>() != null || visual.FindAncestorOfType<Button>() != null))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isWindowDragging = true;
            try
            {
                BeginMoveDrag(e);
            }
            finally
            {
                _isWindowDragging = false;
            }
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _smoothScrollTimer.Stop();
        if (_isWindowDragging || _isItemDragActive) return;

        // 只有在已进入激活模式且经过至少 200ms 防抖后失去焦点才关闭，未激活模式由 LowLevel MouseHook 统一处理外部点击
        if (_isActivatedMode && _hasBeenActivated && (Environment.TickCount64 - _activatedTimestamp > 200))
        {
            Close();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_isItemDragActive)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _longPressTimer.Stop();
                EndItemDrag();
                return;
            }
        }

        if (ViewModel != null && ViewModel.IsEditing)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ViewModel.CancelEdit();
                return;
            }
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                ViewModel.SaveEdit();
                return;
            }
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _smoothScrollTimer.Stop();
            Close();
            return;
        }

        // Ctrl+1 切换剪贴板，Ctrl+2 切换常用语
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key is Key.D1 or Key.NumPad1)
            {
                e.Handled = true;
                ViewModel?.SwitchTab(0);
                return;
            }
            if (e.Key is Key.D2 or Key.NumPad2)
            {
                e.Handled = true;
                ViewModel?.SwitchTab(1);
                return;
            }
        }

        // Enter 触发当前选中项就地粘贴
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ViewModel?.SelectCurrent();
            return;
        }

        // Alt + 1..9 快速选取第 1..9 项
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                e.Handled = true;
                int num = e.Key - Key.D1 + 1;
                ViewModel?.QuickSelectNumber(num);
                return;
            }
        }
    }

    private void OnListBoxPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_itemsListBox == null) return;
        var sv = _itemsListBox.FindDescendantOfType<ScrollViewer>();
        if (sv == null) return;

        // 拦截默认生硬阶跃滚轮行为
        e.Handled = true;

        // 每格滚轮产生 72px 阻尼位移（约 1.5 行卡片高度，手感适中顺滑）
        double scrollDelta = -e.Delta.Y * 72.0;
        double maxScroll = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

        if (!_smoothScrollTimer.IsEnabled)
        {
            _targetScrollY = sv.Offset.Y;
        }

        _targetScrollY = Math.Clamp(_targetScrollY + scrollDelta, 0, maxScroll);
        _smoothScrollTimer.Start();
    }

    private void OnSmoothScrollTimerTick(object? sender, EventArgs e)
    {
        if (_itemsListBox == null)
        {
            _smoothScrollTimer.Stop();
            return;
        }

        var sv = _itemsListBox.FindDescendantOfType<ScrollViewer>();
        if (sv == null)
        {
            _smoothScrollTimer.Stop();
            return;
        }

        double currentY = sv.Offset.Y;
        double diff = _targetScrollY - currentY;

        // 距离微小（< 0.5px）时直接吸附目标位置并停止定时器，CPU/GPU 占用立即归零（0.00%）
        if (Math.Abs(diff) < 0.5)
        {
            sv.Offset = new Vector(sv.Offset.X, _targetScrollY);
            _smoothScrollTimer.Stop();
        }
        else
        {
            // 指数衰减插值 (Lerp Ease-Out 衰减系数 0.22)，实现类似 Slint/macOS 的高刷动量平滑阻尼感
            double nextY = currentY + (diff * 0.22);
            sv.Offset = new Vector(sv.Offset.X, nextY);
        }
    }

    private void OnListBoxScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer sv)
        {
            // 非阻尼动画驱动的滚动（如键盘上下键或拖拽滚动条）时，同步目标位置
            if (!_smoothScrollTimer.IsEnabled)
            {
                _targetScrollY = sv.Offset.Y;
            }

            // 当滚动条距离底部不足 150 像素（约 3 条数据高度）时，自动触发下一页预加载
            if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 150)
            {
                ViewModel?.LoadNextPage();
            }
        }
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_pressedItem != null && !_isItemDragActive)
        {
            BeginItemDrag(_pressedItem);
        }
    }

    private void BeginItemDrag(MuClipItem item)
    {
        _isItemDragActive = true;
        item.IsDragging = true;

        if (_dragPreviewText != null)
        {
            _dragPreviewText.Text = item.Preview;
        }
        if (_dragPreviewSubText != null)
        {
            _dragPreviewSubText.Text = item.TimeAndCharCount;
        }

        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.IsVisible = true;
            UpdateDragPreviewPosition();
        }
    }

    private void UpdateDragPreviewPosition(Point? currentWindowPt = null)
    {
        if (_dragPreviewCard == null || _dragPreviewTransform == null || _contentPanel == null)
            return;

        double cardH = _dragPreviewCard.Bounds.Height > 0 ? _dragPreviewCard.Bounds.Height : 48;
        double minY = 76;
        double maxY = _contentPanel.Bounds.Height > 0 ? Math.Max(minY, _contentPanel.Bounds.Height - cardH - 6) : 380;

        if (currentWindowPt.HasValue)
        {
            var panelPt = currentWindowPt.Value;
            var trans = this.TranslatePoint(panelPt, _contentPanel);
            if (trans.HasValue)
            {
                panelPt = trans.Value;
            }

            double targetY = Math.Clamp(panelPt.Y - (cardH / 2), minY, maxY);
            _dragPreviewTransform.Y = targetY;

            double centerX = _contentPanel.Bounds.Width > 0 ? _contentPanel.Bounds.Width / 2 : 174;
            double tiltX = Math.Clamp((panelPt.X - centerX) * 0.12, -8, 8);
            _dragPreviewTransform.X = tiltX;
        }
        else if (_pressedControl != null)
        {
            var itemPt = _pressedControl.TranslatePoint(new Point(0, 0), _contentPanel);
            if (itemPt.HasValue)
            {
                _dragPreviewTransform.Y = Math.Clamp(itemPt.Value.Y, minY, maxY);
                _dragPreviewTransform.X = 0;
            }
        }
    }

    private void EndItemDrag()
    {
        _isItemDragActive = false;
        if (_pressedItem != null)
        {
            _pressedItem.IsDragging = false;
        }
        if (_dragPreviewCard != null)
        {
            _dragPreviewCard.IsVisible = false;
        }
        if (_dragPreviewTransform != null)
        {
            _dragPreviewTransform.X = 0;
            _dragPreviewTransform.Y = 0;
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 如果点击发生在操作按钮上，由按钮处理，不触发拖拽
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null)
        {
            return;
        }

        if (sender is Control ctrl && ctrl.DataContext is MuClipItem item)
        {
            _pressedItem = item;
            _pressedControl = ctrl;
            _pressStartPoint = e.GetPosition(this);
            _isItemDragActive = false;

            if (ViewModel != null)
            {
                ViewModel.SelectedItem = item;
            }

            _longPressTimer.Stop();
            _longPressTimer.Start();

            // 关键：将捕获绑定在 Window 自身，彻底杜绝因为列表项在 items.Move 发生视觉树变动时丢失鼠标捕获！
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem == null) return;

        var currentPoint = e.GetPosition(this);
        double deltaY = Math.Abs(currentPoint.Y - _pressStartPoint.Y);
        double deltaX = Math.Abs(currentPoint.X - _pressStartPoint.X);

        if (!_isItemDragActive)
        {
            // 鼠标位移超过 8px 判定为主动拖拽重排模式，立即开启
            if (deltaY > 8 || deltaX > 8)
            {
                _longPressTimer.Stop();
                BeginItemDrag(_pressedItem);
            }
        }

        if (_isItemDragActive)
        {
            // 1. 更新悬浮预览卡片位置
            UpdateDragPreviewPosition(currentPoint);

            // 2. 拖拽到列表边缘时自动平滑滚动
            if (_itemsListBox != null)
            {
                var scrollViewer = _itemsListBox.FindDescendantOfType<ScrollViewer>();
                if (scrollViewer != null)
                {
                    var listPt = e.GetPosition(_itemsListBox);
                    if (listPt.Y < 24)
                    {
                        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Max(0, scrollViewer.Offset.Y - 8));
                    }
                    else if (listPt.Y > _itemsListBox.Bounds.Height - 24)
                    {
                        scrollViewer.Offset = new Vector(scrollViewer.Offset.X, scrollViewer.Offset.Y + 8);
                    }
                }
            }

            // 3. 队列位置让位重排（基于目标项中线判定，防止上下跳动与抖动）
            var hit = this.InputHitTest(currentPoint) as Visual;
            var targetListBoxItem = hit?.FindAncestorOfType<ListBoxItem>();
            if (targetListBoxItem != null && targetListBoxItem.DataContext is MuClipItem targetItem && targetItem != _pressedItem)
            {
                if (ViewModel?.DisplayItems is { } items)
                {
                    int oldIdx = items.IndexOf(_pressedItem);
                    int newIdx = items.IndexOf(targetItem);
                    if (oldIdx >= 0 && newIdx >= 0 && oldIdx != newIdx)
                    {
                        var ptInItem = e.GetPosition(targetListBoxItem);
                        double itemH = targetListBoxItem.Bounds.Height;
                        if (itemH > 0)
                        {
                            bool canSwap = false;
                            // 向下拖动时：光标穿过目标项前半部（> 35% 高度）才让位
                            if (newIdx > oldIdx && ptInItem.Y > itemH * 0.35)
                            {
                                canSwap = true;
                            }
                            // 向上拖动时：光标穿过目标项后半部（< 65% 高度）才让位
                            else if (newIdx < oldIdx && ptInItem.Y < itemH * 0.65)
                            {
                                canSwap = true;
                            }

                            if (canSwap)
                            {
                                items.Move(oldIdx, newIdx);
                                // 注意：此处绝对不调用 ReorderPhrases / ApplyFilter，只更新内存队列，保证拖拽流畅且无中断！
                            }
                        }
                    }
                }
            }
        }
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedItem == null && !_isItemDragActive) return;

        _longPressTimer.Stop();

        var item = _pressedItem;
        bool wasDragging = _isItemDragActive;

        EndItemDrag();

        _pressedItem = null;
        _pressedControl = null;

        try
        {
            if (e.Pointer.Captured != null)
            {
                e.Pointer.Capture(null);
            }
        }
        catch
        {
            // 防御性捕获
        }

        if (item != null)
        {
            if (wasDragging)
            {
                e.Handled = true;
                // 拖拽完成：统一持久化新排序
                ViewModel?.CommitItemsReordered();
            }
            else
            {
                // 普通轻击：直接就地粘贴（通过 Dispatcher 异步执行，确保当前鼠标事件在 Avalonia 事件总线中完整派发完毕）
                e.Handled = true;
                Dispatcher.UIThread.Post(() =>
                {
                    ViewModel?.SelectAndPaste(item);
                });
            }
        }
    }

    private void OnWindowPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_pressedItem == null && !_isItemDragActive) return;

        _longPressTimer.Stop();
        if (_isItemDragActive)
        {
            ViewModel?.CommitItemsReordered();
        }
        EndItemDrag();
        _pressedItem = null;
        _pressedControl = null;
    }
}
