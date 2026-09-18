namespace ShawMuOCR.Core.Editor;

/// <summary>
/// 截图保存导出配置选项
/// </summary>
public sealed class ImageExportOptions
{
    public ImageSaveFormat Format { get; set; } = ImageSaveFormat.Png;

    /// <summary>
    /// 压缩画质 (1 - 100)
    /// </summary>
    public int Quality { get; set; } = 90;

    /// <summary>
    /// 缩放比例 (例如 1.0 代表 100%)
    /// </summary>
    public float Scale { get; set; } = 1.0f;

    /// <summary>
    /// 自定义导出宽度（像素），0 表示自动按比例计算
    /// </summary>
    public int TargetWidth { get; set; }

    /// <summary>
    /// 自定义导出高度（像素），0 表示自动按比例计算
    /// </summary>
    public int TargetHeight { get; set; }

    public static string GetExtension(ImageSaveFormat format) => format switch
    {
        ImageSaveFormat.Png => ".png",
        ImageSaveFormat.Jpeg => ".jpg",
        ImageSaveFormat.Webp => ".webp",
        ImageSaveFormat.Bmp => ".bmp",
        _ => ".png"
    };

    public static string GetFilterName(ImageSaveFormat format) => format switch
    {
        ImageSaveFormat.Png => "PNG 图像 (*.png)",
        ImageSaveFormat.Jpeg => "JPEG 图像 (*.jpg;*.jpeg)",
        ImageSaveFormat.Webp => "WEBP 图像 (*.webp)",
        ImageSaveFormat.Bmp => "位图文件 (*.bmp)",
        _ => "PNG 图像 (*.png)"
    };
}
