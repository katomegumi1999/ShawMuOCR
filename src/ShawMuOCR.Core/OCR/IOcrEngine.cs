namespace ShawMuOCR.Core.OCR;

public interface IOcrEngine : IDisposable
{
    string ModelName { get; }

    /// <summary>
    /// 该引擎是否占用本机 CPU 做推理。
    /// 批量调度器按 CPU 占空比做补偿休眠，远程 AI 接口的耗时几乎不消耗本机 CPU，
    /// 若仍按耗时补偿会造成"已经等了 3 秒、再空等 3 秒"的纯粹浪费，故由引擎自报。
    /// </summary>
    bool UsesLocalCpu => true;

    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default);

    Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
