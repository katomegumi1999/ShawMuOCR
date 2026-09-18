using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Capture;

public interface IScreenCaptureService
{
    /// <summary>
    /// 获取所有连接显示器构成的整个虚拟桌面物理坐标范围
    /// </summary>
    ScreenRect GetVirtualScreenBounds();

    /// <summary>
    /// 截取整个多显示器物理虚拟桌面至内存
    /// </summary>
    OcrImage CaptureVirtualScreen();

    /// <summary>
    /// 截取指定物理矩形区域至内存
    /// </summary>
    OcrImage CaptureRegion(ScreenRect rect);
}
