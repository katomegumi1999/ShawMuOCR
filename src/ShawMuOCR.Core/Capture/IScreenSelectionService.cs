using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Capture;

public sealed class ScreenSelectionResult
{
    public required OcrImage Image { get; init; }
    public required ScreenRect ScreenBounds { get; init; }
    public ScreenPoint? StartPoint { get; init; }
}

public interface IScreenSelectionService
{
    /// <summary>
    /// 在屏幕上呈现全屏截图遮罩并供用户交互框选，返回用户所选物理区域裁剪后的图像；若用户取消（按 ESC 或右键）则返回 null。
    /// </summary>
    Task<OcrImage?> SelectRegionAsync(OcrImage fullScreenshot, ScreenRect virtualBounds);

    /// <summary>
    /// 包含选区物理屏幕坐标的全功能框选；若取消返回 null。
    /// </summary>
    async Task<ScreenSelectionResult?> SelectRegionExAsync(OcrImage fullScreenshot, ScreenRect virtualBounds)
    {
        var img = await SelectRegionAsync(fullScreenshot, virtualBounds);
        return img != null ? new ScreenSelectionResult { Image = img, ScreenBounds = new ScreenRect(0, 0, img.Width, img.Height) } : null;
    }
}
