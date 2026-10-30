using System.Runtime.InteropServices;

namespace MuRecorder.Interop;

/// <summary>
/// murecorder_core.dll 的 C ABI 绑定（[LibraryImport] 源生成，Native AOT 安全）。
/// 函数签名与 Rust 侧 src/lib.rs 导出逐一对应；改任一侧必须同步。
/// </summary>
internal static partial class RecorderNative
{
    private const string Lib = "murecorder_core";

    /// <summary>只分配、不启动。cfg 为值传递指针；handle 成功时非空。</summary>
    [LibraryImport(Lib, EntryPoint = "recorder_create")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial int recorder_create(in RecorderConfig cfg, out RecorderSafeHandle handle);

    [LibraryImport(Lib, EntryPoint = "recorder_start")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int recorder_start(RecorderSafeHandle handle);

    /// <summary>幂等；阻塞到线程退出（可能几十到几百毫秒）。</summary>
    [LibraryImport(Lib, EntryPoint = "recorder_stop")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int recorder_stop(RecorderSafeHandle handle);

    /// <summary>无锁快照，可在 UI 线程直接调。</summary>
    [LibraryImport(Lib, EntryPoint = "recorder_get_stats")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial int recorder_get_stats(RecorderSafeHandle handle, out RecorderStats stats);

    [LibraryImport(Lib, EntryPoint = "recorder_get_last_error")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial int recorder_get_last_error(RecorderSafeHandle handle, byte* buf, uint len);

    /// <summary>由 <see cref="RecorderSafeHandle.ReleaseHandle"/> 调用；内部先 stop 再释放。</summary>
    [LibraryImport(Lib, EntryPoint = "recorder_destroy")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial void recorder_destroy(IntPtr handle);

    [LibraryImport(Lib, EntryPoint = "recorder_version")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static unsafe partial byte* recorder_version();

    [LibraryImport(Lib, EntryPoint = "recorder_has_hardware_encoder")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int recorder_has_hardware_encoder(uint encoder);

    [LibraryImport(Lib, EntryPoint = "recorder_extract_range", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    internal static partial int recorder_extract_range(
        string srcPath,
        string dstPath,
        ulong startUs,
        ulong endUs,
        uint fps,
        out ulong outDurationUs);
}
