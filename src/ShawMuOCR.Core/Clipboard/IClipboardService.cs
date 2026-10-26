using System;
using System.Threading.Tasks;
using ShawMuOCR.Core.OCR;

namespace ShawMuOCR.Core.Clipboard;

public interface IClipboardService
{
    Task<bool> SetTextAsync(string text);

    /// <summary>
    /// 写入「纯文本 + HTML」双格式：富文本目标（Excel / WPS / Word）优先用 HTML，
    /// 纯文本目标（聊天窗口、搜索框、记事本）只读到 <paramref name="text"/>。
    /// 默认实现退化为纯文本写入，便于测试替身保持最小实现。
    /// </summary>
    Task<bool> SetTextWithHtmlAsync(string text, string? html) => SetTextAsync(text);

    /// <summary>
    /// 最近一次 <see cref="SetTextWithHtmlAsync"/> 是否真的把 HTML 格式写进了剪贴板。
    /// 用于区分两种截然不同的「粘到 Excel 还是一格」成因：
    /// - 为 false：写入侧就失败了（剪贴板被占用等），此时只有纯文本可用；
    /// - 为 true 却仍粘成一格：说明目标程序不认 CF_HTML，问题不在写入侧。
    /// 默认实现恒为 false，测试替身无需关心。
    /// </summary>
    bool LastWriteIncludedHtml => false;

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
