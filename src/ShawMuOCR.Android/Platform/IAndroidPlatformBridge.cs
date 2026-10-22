using System.Threading.Tasks;
using AndroidUri = global::Android.Net.Uri;

namespace ShawMuOCR.Android.Platform;

public interface IAndroidPlatformBridge
{
    /// <summary>
    /// 调用 SAF 文件选择器（支持图片与 PDF），返回选取的资源 Uri
    /// </summary>
    Task<AndroidUri?> PickDocumentAsync();

    /// <summary>
    /// 调用系统相机进行拍照，并在纯内存中直接获取 ImageBuffer
    /// </summary>
    Task<ImageBuffer?> CapturePhotoAsync();

    /// <summary>
    /// 获取 SAF Uri 的友好显示文件名
    /// </summary>
    string GetFileName(AndroidUri uri);

    /// <summary>
    /// 判断 SAF Uri 是否为 PDF 文档
    /// </summary>
    bool IsPdf(AndroidUri uri);

    /// <summary>
    /// 复制文本到系统剪贴板
    /// </summary>
    void CopyToClipboard(string text);
}
