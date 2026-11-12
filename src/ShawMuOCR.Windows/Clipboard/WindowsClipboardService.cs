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

    // 两把锁，职责必须分清，否则历史上「读取卡住 → 界面卡死」那条链会重现：
    //
    // · s_clipboardLock —— 只保护「OpenClipboard → 操作 → CloseClipboard」这段 OS 独占窗口。
    //   ⚠️ 这段里的 GetClipboardData / EmptyClipboard 都可能**无限期阻塞**：
    //   GetClipboardData 会同步向延迟渲染的所有者发 WM_RENDERFORMAT 并等它回应，
    //   EmptyClipboard 会 SendMessage(WM_DESTROYCLIPBOARD) 通知上一个所有者。
    //   对方要是卡死（不泵消息），持锁的线程就一直出不来。
    //   所以**写入方一律用 Monitor.TryEnter 带超时进入**（见 TryEnterClipboardIo），
    //   宁可这次写不进去，也绝不把 UI 线程挂在这把锁上。
    //
    // · s_stateLock —— 只保护下面那几个纯内存状态字段，持有时间是纳秒级，永远不碰剪贴板 API。
    //   监听线程、延迟读取、生命周期管理全部只用它，因此绝不会被卡住的 I/O 拖住。
    //
    // 加锁顺序：只允许「先 s_clipboardLock，再 s_stateLock」，禁止反向。
    private static readonly object s_clipboardLock = new();
    private static readonly object s_stateLock = new();

    // 等这把 I/O 锁的上限。锁空闲时零等待；只有真的和别的线程撞上才吃这 20ms。
    // 之所以敢设这么短：持有 I/O 锁的线程要么在微秒级完成，要么已经卡死在里面——
    // 前者 20ms 足够等到，后者等 20ms 和等 20 秒结果一样。
    private const int ClipboardIoTimeoutMs = 20;

    /// <summary>进入剪贴板 I/O 临界区；返回 true 时调用方必须 Monitor.Exit(s_clipboardLock)。</summary>
    private static bool TryEnterClipboardIo() => Monitor.TryEnter(s_clipboardLock, ClipboardIoTimeoutMs);

    private static uint s_suppressedSequenceNumber;
    private static string? s_suppressedText;
    private static long s_suppressedUntilTick;
    private static uint s_lastHandledSequenceNumber;
    private static string? s_lastHandledText;
    private static long s_lastHandledTick;
    private static WindowsClipboardService? s_activeListener;
    private static uint s_htmlClipboardFormat;

    private Thread? _listenerThread;
    private IntPtr _hwnd;
    private ManualResetEventSlim? _startedEvent;
    private bool _disposed;

    public bool IsListening => _hwnd != IntPtr.Zero && _listenerThread is { IsAlive: true };

    /// <inheritdoc />
    public bool LastWriteIncludedHtml { get; private set; }

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

    private static IntPtr GetPreferredOwnerHwnd()
    {
        var hwnd = s_activeListener?._hwnd ?? IntPtr.Zero;
        return hwnd != IntPtr.Zero ? hwnd : IntPtr.Zero;
    }

    // ═════════════ 写入剪贴板：只做三件事 ═════════════
    //
    // Windows 剪贴板天生就是「谁最后写谁赢」，这正是我们要的语义，不需要任何机制去维护。
    // 唯一能破坏它的是**我们自己在别人写完之后还在继续写**。所以只做三件事：
    //
    // 1. 数据在锁外准备好，锁内只做「清空 + 交付」，剪贴板不留空窗
    //    （空窗期间粘贴方会读到「有剪贴板没内容」：要么卡住输入框，要么退回旧缓存）。
    // 2. 撞车就重试，但合计只等 120ms 就让位 —— 占着剪贴板的人很可能正在写更新的内容。
    // 3. 重试期间只要剪贴板被别人改过，立刻收手 —— 那份内容比我们的晚，本来就该留着。
    //
    // 除此之外什么都不做：写好一次就结束，不回头核验、不抢回、不和远程工具拉锯。
    //
    // ⚠️ 规则 2 是地基。早期异步路径重试 8 次约 1.08 秒，等于让一个「先发起」的写入
    // 若无其事地在后面落地，把用户新复制的内容盖回去 —— 「OCR 之后去远程复制，粘出来
    // 还是 OCR 文本」就是这么来的。重试窗口必须压到人的动作间隔以下。
    private const int MaxCommitAttempts = 4;

    // 第 2 / 3 / 4 次尝试前分别等 20 / 40 / 60 ms，合计 120 ms。
    private static int RetryDelayMs(int attempt) => 20 * attempt;

    private enum WriteOutcome
    {
        /// <summary>已交付给系统，内存所有权已转移给剪贴板。</summary>
        Success,

        /// <summary>剪贴板被别人占着，可以再试一次。</summary>
        Contended,

        /// <summary>期间已有更新的内容落地，本次作废。**绝不能再重试**。</summary>
        Superseded,
    }

    // 一次逻辑写入的可变状态。重试之间要共享，所以不能是 ref 局部变量（ref 跨不过 await），
    // 必须是一个能被闭包捕获的对象。
    private sealed class WriteSession
    {
        /// <summary>发起写入那一刻的剪贴板序列号；我们自己「清空后写失败」时会刷新。</summary>
        public uint SequenceAtIssue;
    }

    // 单次写入的等待上限。超过就认为对端进程不响应，放弃等待并把结果当作撞车处理。
    // 有它兜底，即使对方永久卡死，调用方也能在 500ms 内拿到「没写进去」的结论，而不是永远挂着。
    private static readonly TimeSpan WriteAbandonTimeout = TimeSpan.FromMilliseconds(500);

    private readonly struct CommitResult
    {
        public CommitResult(WriteOutcome outcome, bool htmlWritten = false, bool abandoned = false)
        {
            Outcome = outcome;
            HtmlWritten = htmlWritten;
            Abandoned = abandoned;
        }

        public WriteOutcome Outcome { get; }
        public bool HtmlWritten { get; }

        /// <summary>
        /// true 表示提交任务已超时、但**仍在后台运行**，相关 HGLOBAL 的所有权不明。
        /// 调用方看到它必须直接放弃并且**绝不释放句柄** —— 释放会造成 use-after-free。
        /// </summary>
        public bool Abandoned { get; }
    }

    /// <summary>
    /// 把一次剪贴板写入丢到线程池执行 —— 「UI 线程绝不执行剪贴板 I/O」的落点。
    /// <para>
    /// EmptyClipboard / SetClipboardData 都可能因为对端进程不泵消息而无限期阻塞
    /// （见 s_clipboardLock 的注释）。它们跑在 UI 线程上就是界面卡死；
    /// 放到池线程后 UI 只是 await，消息泵照转。
    /// </para>
    /// 超时后返回 <see cref="CommitResult.Abandoned"/>，由调用方决定不释放句柄：
    /// 那个任务可能还在用，泄漏一个几百字节的 HGLOBAL 远比崩溃安全。
    /// </summary>
    private static async Task<CommitResult> RunCommitAsync(Func<CommitResult> commit)
    {
        try
        {
            return await Task.Run(commit).WaitAsync(WriteAbandonTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new CommitResult(WriteOutcome.Contended, abandoned: true);
        }
    }

    public Task<bool> SetTextAsync(string text) => SetTextAsync(text, suppressNotification: false);

    public async Task<bool> SetTextAsync(string text, bool suppressNotification)
    {
        text ??= string.Empty;

        // 只记一次、只记在发起这一刻：它代表「我的内容产生于这个时点」，重试不会让内容变新。
        var session = new WriteSession { SequenceAtIssue = User32.GetClipboardSequenceNumber() };

        IntPtr hText = AllocUnicodeTextBlock(text);
        if (hText == IntPtr.Zero)
        {
            return false;
        }

        for (int attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelayMs(attempt)).ConfigureAwait(false);
            }

            CommitResult result = await RunCommitAsync(
                () => CommitTextBlock(session, text, hText, suppressNotification)).ConfigureAwait(false);

            if (result.Abandoned)
            {
                // 池任务仍在运行（多半卡在对端进程里），句柄可能正被它使用：
                // 这里绝不能 GlobalFree，宁可泄漏也不能 use-after-free。
                return false;
            }

            if (result.Outcome == WriteOutcome.Success)
            {
                return true;
            }
            if (result.Outcome == WriteOutcome.Superseded)
            {
                Kernel32.GlobalFree(hText);
                return false;
            }
        }

        Kernel32.GlobalFree(hText);
        return false;
    }

    // ⚠️ 同步版只给**非 UI 线程**用（目前唯一调用方是收到远程剪贴板后的 Sync 线程）。
    // UI 线程一律走 SetTextAsync —— 同步版会在调用线程上直接执行 EmptyClipboard，
    // 对端卡死时就是界面卡死，那是历史上最难查的一类故障。
    //
    // 这里曾做过「写后 220ms 回验、内容不符就写回」来对抗远程工具的延迟旧包覆写，已整体删除：
    // 它必须猜「这次变化是迟到的旧包，还是用户真的复制了新东西」，永远猜不准，
    // 而且会在 1 秒多的窗口里反复抢剪贴板锁，反而制造出「粘出来还是旧内容」。
    public bool SetText(string text, bool suppressNotification = false)
    {
        text ??= string.Empty;

        var session = new WriteSession { SequenceAtIssue = User32.GetClipboardSequenceNumber() };

        IntPtr hText = AllocUnicodeTextBlock(text);
        if (hText == IntPtr.Zero)
        {
            return false;
        }

        for (int attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(RetryDelayMs(attempt));
            }

            WriteOutcome outcome = CommitTextBlock(session, text, hText, suppressNotification).Outcome;
            if (outcome == WriteOutcome.Success)
            {
                return true;
            }
            if (outcome == WriteOutcome.Superseded)
            {
                // 剪贴板里已经是比我们更新的内容，这就是正确终态
                Kernel32.GlobalFree(hText);
                return false;
            }
        }

        Kernel32.GlobalFree(hText);
        return false;
    }

    /// <summary>
    /// 同时写入纯文本 (CF_UNICODETEXT) 与 HTML (CF_HTML) 两种格式：
    /// - Excel / WPS / Word / 腾讯文档等富文本目标会优先采用 HTML，把内容还原为真正的表格；
    /// - 聊天窗口、搜索框、记事本等纯文本目标只会读到 <paramref name="text"/>，不受 HTML 影响。
    /// HTML 写入失败时自动退化为纯文本写入，保证剪贴板始终可用。
    ///
    /// 返回 false 有两种可能，调用方不必区分、也不该重试：
    /// 1. 剪贴板一直被其他程序占着（真写不进去）；
    /// 2. 期间已有更新的内容落地，我们主动让位 —— 此时剪贴板里躺着的是**比这次更新**的东西，
    ///    这正是期望终态。
    /// </summary>
    public async Task<bool> SetTextWithHtmlAsync(string text, string? html)
    {
        text ??= string.Empty;
        LastWriteIncludedHtml = false;

        if (string.IsNullOrWhiteSpace(html))
        {
            return await SetTextAsync(text).ConfigureAwait(false);
        }

        uint htmlFormat = EnsureHtmlFormat();
        byte[] htmlBytes = CfHtmlBuilder.WrapToUtf8Bytes(html);

        var session = new WriteSession { SequenceAtIssue = User32.GetClipboardSequenceNumber() };

        IntPtr hText = AllocUnicodeTextBlock(text);
        if (hText == IntPtr.Zero)
        {
            return false;
        }

        // HTML 块允许分配失败或格式未注册：此时只写纯文本，仍算成功（只是 LastWriteIncludedHtml=false）。
        // 注意不要退回到「失败后再调一遍 SetTextAsync」的老兜底：那是另一次全新写入，
        // 会把本已过时的内容重新当最新写进去。
        IntPtr hHtml = htmlFormat != 0 ? AllocRawBytesBlock(htmlBytes) : IntPtr.Zero;

        for (int attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelayMs(attempt)).ConfigureAwait(false);
            }

            CommitResult result = await RunCommitAsync(
                () => CommitTextAndHtmlBlock(session, hText, hHtml, htmlFormat)).ConfigureAwait(false);

            if (result.Abandoned)
            {
                // 池任务仍在运行（多半卡在对端进程里），句柄可能正被它使用：
                // 这里绝不能 GlobalFree，宁可泄漏也不能 use-after-free。
                return false;
            }

            if (result.Outcome == WriteOutcome.Success)
            {
                LastWriteIncludedHtml = result.HtmlWritten;
                return true;
            }

            if (result.Outcome == WriteOutcome.Superseded)
            {
                Kernel32.GlobalFree(hText);
                if (hHtml != IntPtr.Zero) { Kernel32.GlobalFree(hHtml); }
                return false;
            }
        }

        Kernel32.GlobalFree(hText);
        if (hHtml != IntPtr.Zero) { Kernel32.GlobalFree(hHtml); }
        return false;
    }

    public Task<string?> GetTextAsync()
    {
        // 必须是真异步。GetText 内部的 GetClipboardData 可能卡住（见 s_clipboardLock 的注释），
        // 而 Task.FromResult(GetText()) 会**在调用线程上**先跑完再包装成 Task ——
        // UI 线程 await 它就等于界面直接卡死。丢到线程池，最坏只卡住一个后台线程。
        return Task.Run(GetText);
    }

    // 读路径同样压短：每次 WM_CLIPBOARDUPDATE 和每次收到远程剪贴板都会走到这里，
    // 原来的 8 次重试要反复抢锁约 0.9 秒，正是与 UU远程 / rdpclip 互相顶住的窗口。
    // 读不到就返回 null，下一轮更新自然会再来一次，不值得死磕。
    public string? GetText()
    {
        for (int attempt = 0; attempt < 3; attempt++)
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

        var session = new WriteSession { SequenceAtIssue = User32.GetClipboardSequenceNumber() };

        // DIB 只构造一次：4K 截图逐像素填充要几毫秒，没理由每次重试都重做。
        // 重试只重试「进剪贴板」这一步，也就是真正可能被别的进程占住的那一步。
        IntPtr hDib = AllocDibBlock(image);
        if (hDib == IntPtr.Zero)
        {
            return false;
        }

        for (int attempt = 0; attempt < MaxCommitAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RetryDelayMs(attempt)).ConfigureAwait(false);
            }

            CommitResult result = await RunCommitAsync(
                () => TryCommitDibBlock(session, hDib)).ConfigureAwait(false);

            if (result.Abandoned)
            {
                // 池任务仍在运行（多半卡在对端进程里），句柄可能正被它使用：
                // 这里绝不能 GlobalFree，宁可泄漏也不能 use-after-free。
                return false;
            }

            if (result.Outcome == WriteOutcome.Success)
            {
                return true; // 内存已归系统所有，不能再释放
            }
            if (result.Outcome == WriteOutcome.Superseded)
            {
                Kernel32.GlobalFree(hDib);
                return false;
            }
        }

        Kernel32.GlobalFree(hDib); // 一次都没交付成功，自己回收
        return false;
    }

    public void StartListening()
    {
        lock (s_stateLock)
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
        lock (s_stateLock)
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

        lock (s_stateLock)
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

        // 异步非阻塞防抖读取：绝不在消息循环中立即同步抢占 OpenClipboard！
        // 留出 35ms 缓冲，让外部写入方（如 UU 远程、Office 等）完全写完并安全 CloseClipboard，
        // 彻底杜绝与远程桌面同步程序的锁碰撞（解决用户在远程复制时无法同步回主控电脑的根因）
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(35);
            ProcessDelayedClipboardUpdate(seq);
        });
    }

    private void ProcessDelayedClipboardUpdate(uint seq)
    {
        string? text = GetText();
        if (string.IsNullOrWhiteSpace(text)) return;

        long nowTick = Environment.TickCount64;

        lock (s_stateLock)
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

    /// <summary>
    /// 把已备好的文本块交付给剪贴板（规则 1：锁内只有「清空 + 交付」）。
    /// <paramref name="session.SequenceAtIssue"/> 会在「我们自己清空了却写失败」时刷新，
    /// 否则下一次重试会把自己造成的变化误判成「别人写了更新的内容」。
    /// </summary>
    private static CommitResult CommitTextBlock(
        WriteSession session,
        string text,
        IntPtr hText,
        bool suppressNotification)
    {
        if (!TryEnterClipboardIo())
        {
            // 有别的线程正卡在剪贴板 I/O 里（对方进程不泵消息时，CloseClipboard/SetClipboardData
            // 之外的调用同样会阻塞）。宁可这次写不进去，也不能把调用线程挂在锁上。
            return new CommitResult(WriteOutcome.Contended);
        }

        try
        {
            // 规则 3：发起之后剪贴板被改过 → 那份内容比我们的晚，让位
            if (session.SequenceAtIssue != User32.GetClipboardSequenceNumber())
            {
                return new CommitResult(WriteOutcome.Superseded);
            }

            if (!User32.OpenClipboard(GetPreferredOwnerHwnd()))
            {
                return new CommitResult(WriteOutcome.Contended); // 句柄留给下一次重试用
            }

            bool ok = false;
            try
            {
                // 从空到满只有这两条语句，中间不允许插入任何东西
                //（空窗为什么会卡死粘贴方，见 AllocUnicodeTextBlock 的注释）
                User32.EmptyClipboard();
                ok = User32.SetClipboardData(User32.CF_UNICODETEXT, hText) != IntPtr.Zero;

                if (!ok)
                {
                    // 这次变化是我们自己 EmptyClipboard 造成的，不能让它冒充「外部更新」
                    session.SequenceAtIssue = User32.GetClipboardSequenceNumber();
                    return new CommitResult(WriteOutcome.Contended);
                }

                return new CommitResult(WriteOutcome.Success);
            }
            finally
            {
                User32.CloseClipboard();

                // 自写回显抑制只在真正写成功后才安装：写失败却挂上抑制，
                // 会把之后 600ms 内内容相同的合法外部复制一并吞掉。
                if (ok && suppressNotification)
                {
                    // 嵌套：I/O 锁 → 状态锁，符合既定顺序（无反向路径）
                    lock (s_stateLock)
                    {
                        s_suppressedText = text;
                        s_suppressedUntilTick = Environment.TickCount64 + 600;
                        s_suppressedSequenceNumber = User32.GetClipboardSequenceNumber();
                        s_lastHandledText = text;
                        s_lastHandledTick = Environment.TickCount64;
                    }
                }
            }
        }
        finally
        {
            Monitor.Exit(s_clipboardLock);
        }
    }

    private static uint EnsureHtmlFormat()
    {
        uint format = s_htmlClipboardFormat;
        if (format != 0)
        {
            return format;
        }

        format = User32.RegisterClipboardFormat("HTML Format");
        if (format != 0)
        {
            s_htmlClipboardFormat = format;
        }
        return format;
    }

    /// <summary>
    /// 一次原子写入：先放纯文本，再放 HTML。<paramref name="hHtml"/> 为 0（或格式未注册）时退化为纯文本写入。
    /// HTML 写入失败不会让整次写入失败，只让 <see cref="CommitResult.HtmlWritten"/> 回 false，
    /// 供调用方如实报告，避免「界面显示已分列、粘出来却是一格」的假成功。
    /// </summary>
    private static CommitResult CommitTextAndHtmlBlock(
        WriteSession session,
        IntPtr hText,
        IntPtr hHtml,
        uint htmlFormat)
    {
        if (!TryEnterClipboardIo())
        {
            return new CommitResult(WriteOutcome.Contended);
        }

        try
        {
            // 规则 3：发起之后剪贴板被改过 → 那份内容比我们的晚，让位
            if (session.SequenceAtIssue != User32.GetClipboardSequenceNumber())
            {
                return new CommitResult(WriteOutcome.Superseded);
            }

            if (!User32.OpenClipboard(GetPreferredOwnerHwnd()))
            {
                return new CommitResult(WriteOutcome.Contended);
            }

            try
            {
                User32.EmptyClipboard();

                if (User32.SetClipboardData(User32.CF_UNICODETEXT, hText) == IntPtr.Zero)
                {
                    session.SequenceAtIssue = User32.GetClipboardSequenceNumber();
                    return new CommitResult(WriteOutcome.Contended);
                }

                // 文本已经落地。到这里 HTML 失败也不该重试——
                // 重试会把文本再写一遍，白白再开一次空窗，得不偿失。
                bool htmlWritten = false;
                if (hHtml != IntPtr.Zero && htmlFormat != 0)
                {
                    htmlWritten = User32.SetClipboardData(htmlFormat, hHtml) != IntPtr.Zero;
                    if (!htmlWritten)
                    {
                        Kernel32.GlobalFree(hHtml);
                    }
                }

                return new CommitResult(WriteOutcome.Success, htmlWritten);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            Monitor.Exit(s_clipboardLock);
        }
    }

    /// <summary>
    /// 预分配并填好一个 UTF-16 文本 HGLOBAL，**不接触剪贴板**。
    ///
    /// ⚠️ 为什么分配必须在这里（锁外），而不是在 OpenClipboard 之后：
    /// 旧写法是 OpenClipboard → EmptyClipboard → GlobalAlloc/GlobalLock/拷贝 → SetClipboardData，
    /// 也就是从 EmptyClipboard 到 SetClipboardData 之间**剪贴板是空的**，而这期间还在分配内存、
    /// 还在一字节一字节地拷文本。这段时间里任何程序按 Ctrl+V：
    ///   · 读到「剪贴板可用但没有目标格式」→ 目标程序往往在 UI 线程上重试/等待，输入框就卡住；
    ///   · 或者退回它自己缓存的上一份内容 → 粘贴出来的是旧东西。
    /// 空窗长度随内容大小增长，于是表现为「粘贴时输入框卡死 + 粘出来不是刚复制的」。
    /// 现在分配与拷贝全部前置到锁外，锁内只剩 EmptyClipboard + SetClipboardData 两条语句。
    ///
    /// 返回句柄的所有权归调用方：交给 <see cref="CommitTextBlock"/> /
    /// <see cref="CommitTextAndHtmlBlock"/> 成功后由系统接管，放弃写入时必须自己 GlobalFree。
    /// </summary>
    private static unsafe IntPtr AllocUnicodeTextBlock(string text)
    {
        int byteCount = (text.Length + 1) * sizeof(char);
        IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)byteCount);
        if (hGlobal == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
        if (targetPtr == IntPtr.Zero)
        {
            Kernel32.GlobalFree(hGlobal);
            return IntPtr.Zero;
        }

        try
        {
            if (text.Length > 0)
            {
                fixed (char* srcChars = text)
                {
                    Buffer.MemoryCopy(srcChars, (void*)targetPtr, byteCount, text.Length * sizeof(char));
                }
            }
            Marshal.WriteInt16(targetPtr, text.Length * sizeof(char), 0);
        }
        finally
        {
            Kernel32.GlobalUnlock(hGlobal);
        }

        return hGlobal;
    }

    /// <summary>
    /// 预分配并填好一个原始字节 HGLOBAL（HTML Format 等），**不接触剪贴板**。
    /// 句柄所有权约定同 <see cref="AllocUnicodeTextBlock"/>。
    /// </summary>
    private static unsafe IntPtr AllocRawBytesBlock(byte[] payload)
    {
        IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)payload.Length);
        if (hGlobal == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
        if (targetPtr == IntPtr.Zero)
        {
            Kernel32.GlobalFree(hGlobal);
            return IntPtr.Zero;
        }

        try
        {
            Marshal.Copy(payload, 0, targetPtr, payload.Length);
        }
        finally
        {
            Kernel32.GlobalUnlock(hGlobal);
        }

        return hGlobal;
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

            if (!User32.OpenClipboard(GetPreferredOwnerHwnd()))
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

    /// <summary>
    /// 构造一个完整的 CF_DIB 位图块（BITMAPINFOHEADER + 自底向上 32bpp 像素），**不接触剪贴板**。
    /// 句柄所有权归调用方：交给 <see cref="TryCommitDibBlock"/> 成功即归系统，否则须自行 GlobalFree。
    /// </summary>
    private static unsafe IntPtr AllocDibBlock(OcrImage image)
    {
        int width = image.Width;
        int height = image.Height;
        if (width <= 0 || height <= 0) return IntPtr.Zero;

        int stride = width * 4;
        int headerSize = Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>();
        int totalSize = headerSize + stride * height;

        // 图像路径是空窗最长的一条：4K 截图有 830 万个像素要逐点搬进 DIB。
        // 旧实现在 EmptyClipboard 之后才开始分配和逐像素拷贝，等于让剪贴板空着几毫秒到几十毫秒——
        // 这期间粘贴方就会卡住或拿到旧内容。所以整段填充必须前置到锁外。
        IntPtr hGlobal = Kernel32.GlobalAlloc(Kernel32.GMEM_MOVEABLE, (nuint)totalSize);
        if (hGlobal == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr targetPtr = Kernel32.GlobalLock(hGlobal);
        if (targetPtr == IntPtr.Zero)
        {
            Kernel32.GlobalFree(hGlobal);
            return IntPtr.Zero;
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

        return hGlobal;
    }

    /// <summary>
    /// 把已备好的 DIB 块交付给剪贴板。成功即由系统接管内存（调用方不得再 GlobalFree）。
    /// ⚠️ 只允许在这里做「清空 + 交付」，不允许再插入任何分配/拷贝/IO。
    /// </summary>
    private static CommitResult TryCommitDibBlock(WriteSession session, IntPtr hDib)
    {
        if (!TryEnterClipboardIo())
        {
            return new CommitResult(WriteOutcome.Contended);
        }

        try
        {
            // 规则 3：发起之后剪贴板被改过 → 那份内容比我们的晚，让位
            if (session.SequenceAtIssue != User32.GetClipboardSequenceNumber())
            {
                return new CommitResult(WriteOutcome.Superseded);
            }

            if (!User32.OpenClipboard(GetPreferredOwnerHwnd()))
            {
                return new CommitResult(WriteOutcome.Contended);
            }

            try
            {
                User32.EmptyClipboard();

                if (User32.SetClipboardData(User32.CF_DIB, hDib) == IntPtr.Zero)
                {
                    session.SequenceAtIssue = User32.GetClipboardSequenceNumber();
                    return new CommitResult(WriteOutcome.Contended);
                }

                return new CommitResult(WriteOutcome.Success);
            }
            finally
            {
                User32.CloseClipboard();
            }
        }
        finally
        {
            Monitor.Exit(s_clipboardLock);
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
