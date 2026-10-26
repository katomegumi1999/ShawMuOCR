using System.Runtime.InteropServices;

namespace MuRecorder.Interop;

/// <summary>
/// recorder_core.dll 的 C ABI 结果码。数值与 Rust 侧 RecorderResult / recorder.h 逐项对齐，
/// 修改任一侧必须同步（Rust 侧 error.rs 有对应回归测试）。
/// </summary>
public enum RecorderResult
{
    Ok = 0,
    Invalid = 1,
    NoEncoder = 2,
    DeviceLost = 3,
    Io = 4,
    State = 5,
    Panic = 6,
}
