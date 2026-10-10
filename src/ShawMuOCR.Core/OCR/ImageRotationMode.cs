namespace ShawMuOCR.Core.OCR;

/// <summary>
/// 图像与 PDF 页面旋转模式
/// </summary>
public enum ImageRotationMode
{
    /// <summary>
    /// 自动校正（根据 PDF /Rotate 属性或 EXIF 朝向自适应转正）
    /// </summary>
    Auto = 0,

    /// <summary>
    /// 保持原样不旋转
    /// </summary>
    None = 1,

    /// <summary>
    /// 顺时针旋转 90 度
    /// </summary>
    Rotate90 = 2,

    /// <summary>
    /// 顺时针旋转 180 度
    /// </summary>
    Rotate180 = 3,

    /// <summary>
    /// 顺时针旋转 270 度（相当于逆时针 90 度）
    /// </summary>
    Rotate270 = 4
}
