using System;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Clipboard;

public interface IClipboardService
{
    Task<bool> SetTextAsync(string text);
    Task<string?> GetTextAsync();
    Task<bool> SetImageAsync(OcrImage image);

    bool IsListening => false;

    event Action<string>? ExternalTextChanged
    {
        add { }
        remove { }
    }

    event Action? HotkeyTriggered
    {
        add { }
        remove { }
    }

    bool SetText(string text, bool suppressNotification = false) => SetTextAsync(text).GetAwaiter().GetResult();
    string? GetText() => GetTextAsync().GetAwaiter().GetResult();
    void StartListening() { }
    void StopListening() { }
    bool RegisterHotkey(int id, uint modifiers, uint vk) => false;
}
