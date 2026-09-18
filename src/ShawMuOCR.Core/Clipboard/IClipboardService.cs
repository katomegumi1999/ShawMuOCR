using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Clipboard;

public interface IClipboardService
{
    Task<bool> SetTextAsync(string text);
    Task<string?> GetTextAsync();
    Task<bool> SetImageAsync(OcrImage image);
}
