namespace MuRecorder.Services;

/// <summary>
/// 录屏异常：native 返回非 0 时抛出，携带错误码与 recorder_get_last_error 的原文。
/// </summary>
public sealed class RecordingException : Exception
{
    public MuRecorder.Interop.RecorderResult Result { get; }

    public RecordingException(MuRecorder.Interop.RecorderResult result, string? nativeMessage)
        : base(BuildMessage(result, nativeMessage))
    {
        Result = result;
    }

    private static string BuildMessage(MuRecorder.Interop.RecorderResult result, string? nativeMessage)
    {
        var label = result switch
        {
            MuRecorder.Interop.RecorderResult.Invalid => "参数无效",
            MuRecorder.Interop.RecorderResult.NoEncoder => "编码器不可用",
            MuRecorder.Interop.RecorderResult.DeviceLost => "设备丢失",
            MuRecorder.Interop.RecorderResult.Io => "文件读写失败",
            MuRecorder.Interop.RecorderResult.State => "状态错误",
            MuRecorder.Interop.RecorderResult.Panic => "内部错误",
            _ => "未知错误",
        };
        return string.IsNullOrWhiteSpace(nativeMessage)
            ? $"录屏失败（{label}）"
            : $"录屏失败（{label}）：{nativeMessage}";
    }
}
