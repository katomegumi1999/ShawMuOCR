using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MuRecorder.Interop;

namespace MuRecorder.Services;

/// <summary>
/// 录屏门面：UI 唯一入口。线程规则（写进团队规范）：
/// - create / start / stop / destroy 一律在 Task.Run 中调用，绝不在 UI 线程直接调
///   （stop 要 join 原生线程，可能阻塞几十到几百毫秒）；
/// - get_stats 无锁快照，可在 DispatcherTimer 里直接调；
/// - 任何 native 返回非 0 时，立即取 recorder_get_last_error 转 RecordingException。
/// </summary>
public sealed class RecordingService : IAsyncDisposable
{
    private static readonly bool s_dllAvailable = ProbeDll();

    private readonly object _gate = new();
    private RecorderSafeHandle? _handle;
    private bool _isRecording;
    private IntPtr _previewHwnd;
    private bool _disposed;

    /// <summary>murecorder_core.dll 是否可用（不可用时录屏入口整体禁用）。</summary>
    public static bool IsAvailable => s_dllAvailable;

    /// <summary>
    /// 硬件探测结果缓存。原生侧已经缓存了（同一子格式只枚举一次），这里再缓存一层，
    /// 是为了让 UI 线程连跨 P/Invoke 的调用都不必付。
    /// </summary>
    private static readonly ConcurrentDictionary<EncoderPreference, bool> s_hardwareCache = new();

    private static volatile bool s_hardwareProbeReady;
    private static int s_warmStarted;

    /// <summary>三种硬件编码器是否都探测完毕（UI 可据此决定要不要显示「未检测到」）。</summary>
    public static bool HardwareProbeReady => s_hardwareProbeReady;

    /// <summary>
    /// 预热硬件探测。**必须在后台线程调用**：单次 MFTEnumEx 实测 100–430 ms，
    /// 放在 UI 线程会直接卡住窗口。
    /// </summary>
    public static void WarmHardwareProbe()
    {
        if (!s_dllAvailable) return;
        if (Interlocked.Exchange(ref s_warmStarted, 1) != 0) return;
        try
        {
            RecorderNative.recorder_warm_hardware_probe();
            foreach (var enc in new[]
                     {
                         EncoderPreference.GpuAv1, EncoderPreference.GpuHevc, EncoderPreference.GpuH264
                     })
            {
                // 返回 -1 = 问不出来（MF 初始化或枚举报错），**不写缓存**：
                // 写进去就等于把一次瞬时故障固化成"这台机器没有硬件编码器"，而且看不出来。
                var v = RecorderNative.recorder_has_hardware_encoder((uint)enc);
                if (v >= 0) s_hardwareCache[enc] = v != 0;
            }
            s_hardwareProbeReady = true;
        }
        catch
        {
            s_hardwareProbeReady = false;
        }
    }

    /// <summary>
    /// 只读缓存，**绝不阻塞**。返回 null 表示还没探测过（宿主没预热），
    /// 或探测当时出错（问不出来）。
    /// UI 构建选项时用这个，避免把 100–430 ms 的枚举压到页面加载上。
    ///
    /// 这是硬件探测的**唯一入口**。历史上还有一个返回 <c>bool</c> 的
    /// <c>HasHardwareEncoder</c>，已删除：它把「明确没有」和「问不出来」压成同一个
    /// <c>false</c>，谁拿它写「未检测到硬件支持」就会把一次瞬时枚举故障放大成整个会话的误报。
    /// 查询硬件支持一律走这里，三态才够用。
    /// </summary>
    public static bool? TryGetHardwareEncoder(EncoderPreference encoder)
        => s_hardwareCache.TryGetValue(encoder, out var cached) ? cached : null;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _isRecording;
            }
        }
    }

    /// <summary>
    /// 录制停止事件。正常用户停止不触发；仅异常停止（设备丢失/编码失败/IO 错误等）时触发，
    /// 由 <see cref="RecordingPoller"/> 检测到 stats.LastError != 0 时 raise。
    /// </summary>
    public event EventHandler<RecordingStoppedEventArgs>? Stopped;

    /// <summary>开始录制。</summary>
    public Task StartAsync(RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => StartCore(options));
    }

    /// <summary>开始单个或多个窗口的窗口录制（WGC 窗口表面 + 屏幕画布）。</summary>
    public Task StartWindowCaptureAsync(
        System.Collections.Generic.IEnumerable<IntPtr> hwnds,
        string outputPath,
        uint fps = 30,
        uint bitrateKbps = 8000,
        EncoderPreference encoder = EncoderPreference.GpuH264)
    {
        return StartAsync(RecordingOptions.ForWindows(hwnds, outputPath, fps, bitrateKbps, encoder));
    }

    /// <summary>开始单个窗口录制便利入口。</summary>
    public Task StartWindowCaptureAsync(
        IntPtr hwnd,
        string outputPath,
        uint fps = 30,
        uint bitrateKbps = 8000,
        EncoderPreference encoder = EncoderPreference.GpuH264)
    {
        return StartWindowCaptureAsync(new[] { hwnd }, outputPath, fps, bitrateKbps, encoder);
    }

    private void StartCore(RecordingOptions options)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_isRecording)
            {
                throw new RecordingException(MuRecorder.Interop.RecorderResult.State, "已在录制中");
            }
        }

        // 字符串字段：调用期间固定为 UTF-8 原生内存，返回后立即释放（Rust 只读不持有）。
        var outputPathPtr = Marshal.StringToCoTaskMemUTF8(options.OutputPath);
        var audioDevicePtr = options.AudioDeviceId is null
            ? IntPtr.Zero
            : Marshal.StringToCoTaskMemUTF8(options.AudioDeviceId);
        // 麦克风叠加的端点 ID 与主音源是两条独立的字符串，必须各自持有到
        // recorder_create 返回为止：Rust 只读不持有，提前释放就是读悬垂指针。
        var micDevicePtr = options.MicDeviceId is null
            ? IntPtr.Zero
            : Marshal.StringToCoTaskMemUTF8(options.MicDeviceId);
        try
        {
            RecorderSafeHandle handle;
            int rc;
            unsafe
            {
                var srcs = (options.Sources ?? Array.Empty<WindowSourceOption>())
                    .Select(s => new RecorderSource
                    {
                        Hwnd = (ulong)s.Hwnd,
                        Z = s.Z,
                        Flags = s.Flags != 0 ? s.Flags : ((s.ClientAreaOnly ? 1u : 0u) | (s.AlphaBlend ? 2u : 0u)),
                    })
                    .ToArray();

                fixed (RecorderSource* pSources = srcs)
                {
                    var cfg = new RecorderConfig
                {
                    AbiVersion = RecorderAbi.Version,
                    RegionX = options.Region.X,
                    RegionY = options.Region.Y,
                    RegionWidth = (uint)options.Region.Width,
                    RegionHeight = (uint)options.Region.Height,
                    Fps = options.Fps,
                    Encoder = (uint)options.Encoder,
                    BitrateKbps = options.BitrateKbps,
                    CaptureCursor = options.CaptureCursor ? 1u : 0u,
                    OutputPath = (byte*)outputPathPtr,
                    AudioSource = options.AudioSource,
                    AudioDeviceId = audioDevicePtr == IntPtr.Zero ? null : (byte*)audioDevicePtr,
                    AudioWindowHwnd = (ulong)options.AudioWindowHwnd,
                    AudioProcessId = options.AudioProcessId,
                    AudioBitrateKbps = options.AudioBitrateKbps,
                    CursorClickEffect = options.CursorClickEffect ? 1u : 0u,
                    CursorTrailMs = options.CursorTrailMs,
                    CaptureMode = (uint)options.CaptureMode,
                    CanvasW = options.Canvas?.Width ?? 0,
                    CanvasH = options.Canvas?.Height ?? 0,
                    CanvasBackground = options.Canvas?.Background ?? 0,
                    SourceCount = (uint)srcs.Length,
                    Sources = srcs.Length == 0 ? null : pSources,
                    CaptureFlags = options.CaptureFlags,
                    MicGainPercent = options.MicGainPercent,
                    MicDeviceId = micDevicePtr == IntPtr.Zero ? null : (byte*)micDevicePtr,
                };

                rc = RecorderNative.recorder_create(in cfg, out handle);
                }
            }
            if (rc != (int)MuRecorder.Interop.RecorderResult.Ok)
            {
                var ex = NewException(rc, handle);
                handle?.Dispose();
                throw ex;
            }

            rc = RecorderNative.recorder_start(handle);
            if (rc != (int)MuRecorder.Interop.RecorderResult.Ok)
            {
                var ex = NewException(rc, handle);
                handle.Dispose();
                throw ex;
            }

            lock (_gate)
            {
                _handle = handle;
                _isRecording = true;
                if (_previewHwnd != IntPtr.Zero)
                {
                    RecorderNative.recorder_set_preview_hwnd(handle, (ulong)_previewHwnd.ToInt64());
                }
            }
        }
        finally
        {
            if (outputPathPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(outputPathPtr);
            }
            if (audioDevicePtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(audioDevicePtr);
            }
            if (micDevicePtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(micDevicePtr);
            }
        }
    }

    /// <summary>
    /// 从录制中（或已录制完）的文件无损切片提取区间（微秒单位），无需停止录制，零重编码。
    /// </summary>
    /// <summary>设置实时录屏硬件预览的宿主窗口句柄 HWND（传 IntPtr.Zero 暂停预览）。</summary>
    public void SetPreviewHwnd(IntPtr hwnd)
    {
        lock (_gate)
        {
            _previewHwnd = hwnd;
            if (_isRecording && _handle != null && !_handle.IsInvalid && !_handle.IsClosed)
            {
                RecorderNative.recorder_set_preview_hwnd(_handle, (ulong)hwnd.ToInt64());
            }
        }
    }

    public static Task<ulong> ExtractRangeAsync(string srcPath, string dstPath, ulong startUs, ulong endUs, uint fps = 30)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(srcPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dstPath);

        return Task.Run(() =>
        {
            int rc = RecorderNative.recorder_extract_range(srcPath, dstPath, startUs, endUs, fps, out ulong durationUs);
            if (rc != (int)MuRecorder.Interop.RecorderResult.Ok)
            {
                throw new RecordingException((MuRecorder.Interop.RecorderResult)rc, $"切片提取失败 (错误码 {rc})");
            }
            return durationUs;
        });
    }

    /// <summary>停止录制（阻塞到文件落定；幂等）。</summary>
    public Task StopAsync()
    {
        return Task.Run(StopCore);
    }

    private void StopCore()
    {
        RecorderSafeHandle? handle;
        lock (_gate)
        {
            handle = _handle;
            _handle = null;
            _isRecording = false;
        }
        if (handle is null)
        {
            return;
        }

        try
        {
            int rc = RecorderNative.recorder_stop(handle);
            if (rc != (int)MuRecorder.Interop.RecorderResult.Ok)
            {
                var ex = NewException(rc, handle);
                throw ex;
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    /// <summary>
    /// 轻量统计拉取（无锁快照，可直接在 UI 线程调）。未录制时返回 null。
    /// </summary>
    public RecorderStats? PollStats()
    {
        RecorderSafeHandle? handle;
        lock (_gate)
        {
            handle = _handle;
        }
        if (handle is null || handle.IsInvalid)
        {
            return null;
        }

        int rc = RecorderNative.recorder_get_stats(handle, out var stats);
        return rc == (int)MuRecorder.Interop.RecorderResult.Ok ? stats : null;
    }

    /// <summary>
    /// 最近一次原生错误原文（recorder_get_last_error，无错误时为空串）。
    /// 未录制时返回空串。异常停止排查与用户反馈时使用。
    /// </summary>
    public string GetLastErrorMessage()
    {
        RecorderSafeHandle? handle;
        lock (_gate)
        {
            handle = _handle;
        }
        if (handle is null || handle.IsInvalid)
        {
            return string.Empty;
        }
        return GetLastError(handle);
    }

    /// <summary>供 <see cref="RecordingPoller"/> 调用：内部标记停止并触发事件。</summary>
    internal void RaiseStopped(MuRecorder.Interop.RecorderResult result, string message, string? filePath)
    {
        string native;
        lock (_gate)
        {
            if (!_isRecording)
            {
                return;
            }
            _isRecording = false;
            native = _handle is { IsInvalid: false } h ? GetLastError(h) : string.Empty;
        }
        var detail = string.IsNullOrWhiteSpace(native) ? message : $"{message}：{native}";
        Stopped?.Invoke(this, new RecordingStoppedEventArgs(result, detail, filePath));
    }

    private RecordingException NewException(int rc, RecorderSafeHandle? handle)
    {
        var native = handle is not null && !handle.IsInvalid ? GetLastError(handle) : string.Empty;
        return new RecordingException((MuRecorder.Interop.RecorderResult)rc, native);
    }

    private static string GetLastError(RecorderSafeHandle handle)
    {
        Span<byte> buf = stackalloc byte[512];
        unsafe
        {
            fixed (byte* p = buf)
            {
                RecorderNative.recorder_get_last_error(handle, p, (uint)buf.Length);
            }
        }
        int end = buf.IndexOf((byte)0);
        if (end <= 0)
        {
            return string.Empty;
        }
        return System.Text.Encoding.UTF8.GetString(buf[..end]);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RecordingService));
        }
    }

    /// <summary>探测 murecorder_core.dll 是否可加载（缺失时禁用录屏入口）。</summary>
    private static bool ProbeDll()
    {
        try
        {
            // recorder_version 是零依赖探针：不创建会话、不动 GPU。
            unsafe
            {
                byte* v = RecorderNative.recorder_version();
                return v != null;
            }
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RecorderSafeHandle? handle;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            handle = _handle;
            _handle = null;
        }

        if (handle is null)
        {
            return;
        }

        // destroy 内部先 stop 再 join；放线程池避免 UI 卡顿。
        await Task.Run(() => handle.Dispose()).ConfigureAwait(false);
        GC.KeepAlive(this);
    }
}

/// <summary>录制停止事件参数。</summary>
public sealed class RecordingStoppedEventArgs : EventArgs
{
    public MuRecorder.Interop.RecorderResult Result { get; }

    public string Message { get; }

    public string? FilePath { get; }

    public RecordingStoppedEventArgs(MuRecorder.Interop.RecorderResult result, string message, string? filePath)
    {
        Result = result;
        Message = message;
        FilePath = filePath;
    }
}

// 说明：murecorder_core.dll 与 muclip_core.dll 一致位于输出根目录，运行期默认探测即可命中，
// 无需 NativeLibrary.SetDllImportResolver。若未来改为 RID 特定布局（runtimes/win-x64/native/），
// 在 Program.cs 挂 resolver 指向该相对路径，并在解析失败时禁用录屏入口（Avalonia 跨平台场景）。
