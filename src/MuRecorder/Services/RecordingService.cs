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
    private bool _disposed;

    /// <summary>recorder_core.dll 是否可用（不可用时录屏入口整体禁用）。</summary>
    public static bool IsAvailable => s_dllAvailable;

    /// <summary>检查指定编码器是否具有硬件加速支持（通过 MFTEnumEx 探测硬件 MFT）。</summary>
    public static bool HasHardwareEncoder(EncoderPreference encoder)
    {
        if (!s_dllAvailable) return false;
        try
        {
            return RecorderNative.recorder_has_hardware_encoder((uint)encoder) != 0;
        }
        catch
        {
            return false;
        }
    }

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
        try
        {
            RecorderSafeHandle handle;
            int rc;
            unsafe
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
                };

                rc = RecorderNative.recorder_create(in cfg, out handle);
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
        }
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
        }
        if (handle is null)
        {
            return;
        }

        int rc = RecorderNative.recorder_stop(handle);
        if (rc != (int)MuRecorder.Interop.RecorderResult.Ok)
        {
            var ex = NewException(rc, handle);
            lock (_gate)
            {
                _isRecording = false;
            }
            throw ex;
        }

        lock (_gate)
        {
            _isRecording = false;
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

    /// <summary>探测 recorder_core.dll 是否可加载（缺失时禁用录屏入口）。</summary>
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

// 说明：recorder_core.dll 与 muclip_core.dll 一致位于输出根目录，运行期默认探测即可命中，
// 无需 NativeLibrary.SetDllImportResolver。若未来改为 RID 特定布局（runtimes/win-x64/native/），
// 在 Program.cs 挂 resolver 指向该相对路径，并在解析失败时禁用录屏入口（Avalonia 跨平台场景）。
