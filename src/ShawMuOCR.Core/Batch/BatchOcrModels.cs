using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Batch;

/// <summary>
/// 批量处理的文件类型
/// </summary>
public enum BatchFileType
{
    Image,
    Pdf
}

/// <summary>
/// 批量任务项的处理状态
/// </summary>
public enum BatchItemStatus
{
    /// <summary>
    /// 等待处理
    /// </summary>
    Pending,

    /// <summary>
    /// 正在处理
    /// </summary>
    Processing,

    /// <summary>
    /// 处理成功完成
    /// </summary>
    Completed,

    /// <summary>
    /// 处理失败
    /// </summary>
    Failed,

    /// <summary>
    /// 已被用户取消
    /// </summary>
    Cancelled,

    /// <summary>
    /// 已跳过（如文件不存在或重复）
    /// </summary>
    Skipped
}

/// <summary>
/// 批量 OCR 转换配置选项
/// </summary>
public sealed class BatchOcrOptions
{
    /// <summary>
    /// 是否导出纯文本 .txt
    /// </summary>
    public bool ExportTxt { get; set; } = true;

    /// <summary>
    /// 是否导出 Excel 表格 .xlsx
    /// </summary>
    public bool ExportXlsx { get; set; } = true;

    /// <summary>
    /// 是否导出 Word 文档 .docx
    /// </summary>
    public bool ExportDocx { get; set; } = false;

    /// <summary>
    /// 是否根据检测框自动分列填入表格单元格（适用于表格、票据与表单多列排版）
    /// </summary>
    public bool SplitColumnsByBlocks { get; set; } = false;

    /// <summary>
    /// 是否开启追加文件名后缀
    /// </summary>
    public bool IsSuffixEnabled { get; set; } = true;

    /// <summary>
    /// 追加的文件名后缀（如 _ocr）
    /// </summary>
    public string SuffixText { get; set; } = "_ocr";

    /// <summary>
    /// 是否跟随原文件路径导出
    /// </summary>
    public bool UseOriginalFolder { get; set; } = true;

    /// <summary>
    /// 自定义导出目录路径（当 UseOriginalFolder 为 false 时生效）
    /// </summary>
    public string CustomOutputDir { get; set; } = string.Empty;

    /// <summary>
    /// CPU 调度并发核心数量上限
    /// </summary>
    public int MaxCores { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// CPU 利用率上限百分比 (10 ~ 100)
    /// </summary>
    public int CpuLimitPercent { get; set; } = 50;

    /// <summary>
    /// 图像与 PDF 页面旋转模式（默认为 Auto 自动自适应转正）
    /// </summary>
    public ImageRotationMode ImageRotation { get; set; } = ImageRotationMode.Auto;
}
