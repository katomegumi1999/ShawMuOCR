using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

/// <summary>
/// 自动复制的写入策略测试：
/// - 识别到表格且开关开启 → 写「纯文本 + HTML」双格式（Excel 拿表格，聊天框拿干净文本）；
/// - 开关关闭或非表格内容 → 只写纯文本，行为与旧版一致。
/// </summary>
public class OcrCoordinatorTableCopyTests
{
    private sealed class FailingScreenCaptureService : IScreenCaptureService
    {
        public ScreenRect GetVirtualScreenBounds() => new(0, 0, 100, 100);
        public OcrImage CaptureVirtualScreen() => new(new SKBitmap(10, 10));
        public OcrImage CaptureRegion(ScreenRect rect) => new(new SKBitmap(10, 10));
    }

    private sealed class StubOcrEngineFactory : IOcrEngineFactory
    {
        private readonly OcrResult _result;

        public StubOcrEngineFactory(OcrResult result) => _result = result;

        public IOcrEngine Create(string modelName) => new StubOcrEngine(modelName, _result);
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        private readonly OcrResult _result;

        public StubOcrEngine(string modelName, OcrResult result)
        {
            ModelName = modelName;
            _result = result;
        }

        public string ModelName { get; }
        public bool IsLoaded => true;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
            => Task.FromResult(_result);

        public void Dispose() { }
    }

    private sealed class RecordingClipboardService : IClipboardService
    {
        public string? LastText { get; private set; }
        public string? LastHtml { get; private set; }
        public int PlainWriteCount { get; private set; }
        public int HtmlWriteCount { get; private set; }

        /// <summary>模拟 HTML 格式是否真正落盘（剪贴板被占用时会失败）。</summary>
        public bool HtmlActuallyWritten { get; set; } = true;

        public bool LastWriteIncludedHtml { get; private set; }

        public Task<bool> SetTextAsync(string text)
        {
            LastText = text;
            PlainWriteCount++;
            return Task.FromResult(true);
        }

        public Task<bool> SetTextWithHtmlAsync(string text, string? html)
        {
            LastText = text;
            LastHtml = html;
            HtmlWriteCount++;
            LastWriteIncludedHtml = HtmlActuallyWritten;
            return Task.FromResult(true);
        }

        public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);

        public Task<bool> SetImageAsync(OcrImage image) => Task.FromResult(true);
    }

    private sealed class StubSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new()
        {
            AutoCopy = true,
            ShowOcrNotification = false,
            SilentMode = true,
            ShowWindowAfterOcr = false
        };

        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(settings);
        }
    }

    private static OcrTextBlock Block(string text, int left, int top, int width = 40, int height = 18)
        => new() { Text = text, Confidence = 0.99f, BoundingBox = new SKRectI(left, top, left + width, top + height) };

    private static OcrResult TableResult() => new()
    {
        Text = "物资名称 数量" + Environment.NewLine + "螺丝 120",
        Blocks = new[]
        {
            Block("物资名称", 10, 10, 60),
            Block("数量", 120, 10, 40),
            Block("螺丝", 10, 45, 40),
            Block("120", 120, 45, 30)
        },
        Elapsed = TimeSpan.FromMilliseconds(12),
        IsSuccess = true
    };

    private static OcrResult PlainResult() => new()
    {
        Text = "这是一段普通段落文字",
        Blocks = new[]
        {
            Block("这是一段普通段落文字", 10, 10, 200),
            Block("第二行也只是普通文字", 10, 45, 200)
        },
        Elapsed = TimeSpan.FromMilliseconds(12),
        IsSuccess = true
    };

    private static async Task<(RecordingClipboardService Clipboard, StubSettingsService Settings, string? Status)> RunAsync(
        OcrResult result,
        bool tableAsCells,
        bool htmlWriteSucceeds = true)
    {
        var clipboard = new RecordingClipboardService { HtmlActuallyWritten = htmlWriteSucceeds };
        var settings = new StubSettingsService();
        settings.Current.AutoCopyTableAsCells = tableAsCells;

        using var image = new OcrImage(new SKBitmap(300, 100));
        using var coordinator = new OcrCoordinator(
            new FailingScreenCaptureService(),
            new StubOcrEngineFactory(result),
            clipboard,
            settings);

        await coordinator.RunOcrAsync(image);
        return (clipboard, settings, coordinator.StatusMessage);
    }

    [Fact]
    public async Task RunOcrAsync_TableWithFeatureEnabled_WritesHtmlPlusPlainText()
    {
        var (clipboard, _, _) = await RunAsync(TableResult(), tableAsCells: true);

        Assert.Equal(1, clipboard.HtmlWriteCount);
        Assert.Equal(0, clipboard.PlainWriteCount);

        // HTML 走表格结构，供 Excel / WPS 粘贴还原单元格
        Assert.NotNull(clipboard.LastHtml);
        Assert.Contains("<table", clipboard.LastHtml);
        Assert.Contains("<td>物资名称</td>", clipboard.LastHtml);

        // 纯文本保持原来的空格分隔版本，粘贴到聊天窗口 / 搜索框不会出现制表符
        Assert.Equal("物资名称 数量" + Environment.NewLine + "螺丝 120", clipboard.LastText);
        Assert.DoesNotContain('\t', clipboard.LastText!);
    }

    [Fact]
    public async Task RunOcrAsync_HtmlWriteSucceeds_StatusClaimsTableSplit()
    {
        var (_, _, status) = await RunAsync(TableResult(), tableAsCells: true, htmlWriteSucceeds: true);

        Assert.NotNull(status);
        Assert.Contains("表格已分列", status);
        Assert.Contains("2 行 × 2 列", status);
    }

    /// <summary>
    /// 剪贴板被占用导致 CF_HTML 未落盘时，不能谎报「表格已分列」——
    /// 否则用户会以为功能生效，粘到 Excel 却发现仍挤在一格，无从判断是软件问题还是目标程序不认。
    /// </summary>
    [Fact]
    public async Task RunOcrAsync_HtmlWriteFails_StatusFallsBackToPlainCopy()
    {
        var (clipboard, _, status) = await RunAsync(TableResult(), tableAsCells: true, htmlWriteSucceeds: false);

        Assert.Equal(1, clipboard.HtmlWriteCount);
        Assert.NotNull(status);
        Assert.DoesNotContain("表格已分列", status);
        Assert.Contains("已自动复制", status);
    }

    [Fact]
    public async Task RunOcrAsync_TableWithFeatureDisabled_WritesPlainTextOnly()
    {
        var (clipboard, _, _) = await RunAsync(TableResult(), tableAsCells: false);

        Assert.Equal(1, clipboard.PlainWriteCount);
        Assert.Equal(0, clipboard.HtmlWriteCount);
        Assert.Null(clipboard.LastHtml);
    }

    [Fact]
    public async Task RunOcrAsync_NonTableContent_WritesPlainTextOnly()
    {
        var (clipboard, _, _) = await RunAsync(PlainResult(), tableAsCells: true);

        Assert.Equal(1, clipboard.PlainWriteCount);
        Assert.Equal(0, clipboard.HtmlWriteCount);
        Assert.Equal("这是一段普通段落文字", clipboard.LastText);
    }
}
