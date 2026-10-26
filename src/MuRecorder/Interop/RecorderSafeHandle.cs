using System.Runtime.InteropServices;

namespace MuRecorder.Interop;

/// <summary>
/// RecorderHandle 的安全包装：ReleaseHandle 调 recorder_destroy（内部先 stop 再 join），
/// 保证 using / 终结器 / 异常路径都不会泄漏原生线程与文件句柄。
/// </summary>
public sealed class RecorderSafeHandle : SafeHandle
{
    public RecorderSafeHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public RecorderSafeHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    /// <summary>
    /// recorder_destroy 幂等且内部先 stop；此处只保证不重复释放。
    /// </summary>
    protected override bool ReleaseHandle()
    {
        RecorderNative.recorder_destroy(handle);
        return true;
    }
}
