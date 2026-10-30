namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 批量 OCR 转换分块配置
/// </summary>
public sealed class BatchSettings
{
    public bool ExportTxt { get; set; } = true;
    public bool ExportXlsx { get; set; } = true;
    public bool ExportDocx { get; set; } = false;
    public bool SplitColumnsByBlocks { get; set; } = true;
    public int ImageRotationIndex { get; set; } = 0;
    public bool IsSuffixEnabled { get; set; } = true;
    public string SuffixText { get; set; } = "_ocr";
    public bool UseOriginalFolder { get; set; } = true;
    public string CustomOutputDir { get; set; } = string.Empty;
    public int MaxCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int GpuStreams { get; set; } = 1;
    public int CpuLimitPercent { get; set; } = 50;

    /// <summary>
    /// 推理设备调度模式 ("Auto", "GPU", "CPU")
    /// </summary>
    public string SchedulerMode { get; set; } = "Auto";

    public BatchSettings Clone() => new()
    {
        ExportTxt = ExportTxt,
        ExportXlsx = ExportXlsx,
        ExportDocx = ExportDocx,
        SplitColumnsByBlocks = SplitColumnsByBlocks,
        ImageRotationIndex = ImageRotationIndex,
        IsSuffixEnabled = IsSuffixEnabled,
        SuffixText = SuffixText,
        UseOriginalFolder = UseOriginalFolder,
        CustomOutputDir = CustomOutputDir,
        MaxCores = MaxCores,
        GpuStreams = GpuStreams,
        CpuLimitPercent = CpuLimitPercent,
        SchedulerMode = SchedulerMode
    };
}
