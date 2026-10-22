using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Views;

namespace ShawMuOCR.ViewModels;

public partial class TranslateViewModel : ViewModelBase
{
    private readonly ITranslationService _translationService;
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly IOcrCoordinator? _coordinator;

    public IReadOnlyList<LanguageItem> SourceLanguages { get; } = TranslationLanguages.All;
    public IReadOnlyList<LanguageItem> TargetLanguages { get; } = TranslationLanguages.AllTargets;

    public IReadOnlyList<TranslationProviderItem> AvailableProviders { get; } = TranslationProviders.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceText))]
    [NotifyPropertyChangedFor(nameof(SourceCharCount))]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslatedText))]
    [NotifyPropertyChangedFor(nameof(ResultCharCount))]
    private string _translatedText = string.Empty;

    [ObservableProperty]
    private LanguageItem _selectedSourceLang;

    [ObservableProperty]
    private LanguageItem _selectedTargetLang;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProvider))]
    private TranslationProviderItem _selectedProviderItem;

    public string SelectedProvider
    {
        get => SelectedProviderItem?.Id ?? "Baidu";
        set
        {
            var item = AvailableProviders.FirstOrDefault(p => string.Equals(p.Id, value, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                SelectedProviderItem = item;
            }
        }
    }

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private string _elapsedText = string.Empty;

    [ObservableProperty]
    private string _quotaSummaryText = string.Empty;

    [ObservableProperty]
    private double _quotaProgressValue;

    public bool HasSourceText => !string.IsNullOrWhiteSpace(SourceText);
    public bool HasTranslatedText => !string.IsNullOrWhiteSpace(TranslatedText);
    public int SourceCharCount => SourceText?.Length ?? 0;
    public int ResultCharCount => TranslatedText?.Length ?? 0;

    public TranslateViewModel(
        ITranslationService translationService,
        ISettingsService settingsService,
        IClipboardService clipboardService,
        IOcrCoordinator? coordinator = null)
    {
        _translationService = translationService;
        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _coordinator = coordinator;

        var s = _settingsService.Current;
        _selectedProviderItem = AvailableProviders.FirstOrDefault(p => string.Equals(p.Id, s.TranslateProvider, StringComparison.OrdinalIgnoreCase)) ?? AvailableProviders[0];
        _selectedSourceLang = SourceLanguages.FirstOrDefault(x => x.Code == s.TranslateSourceLang) ?? SourceLanguages[0];
        _selectedTargetLang = TargetLanguages.FirstOrDefault(x => x.Code == s.TranslateTargetLang) ?? TargetLanguages[0];

        UpdateQuotaInfo();

        _translationService.QuotaUpdated += OnQuotaUpdated;
    }

    private void OnQuotaUpdated()
    {
        Dispatcher.UIThread.Post(UpdateQuotaInfo);
    }

    partial void OnSelectedProviderItemChanged(TranslationProviderItem value)
    {
        UpdateQuotaInfo();
    }

    private void UpdateQuotaInfo()
    {
        var quota = _translationService.GetQuotaInfo(SelectedProvider);
        QuotaProgressValue = quota.Percentage;
        if (quota.MonthlyLimit > 0)
        {
            QuotaSummaryText = $"本月已用: {quota.UsedThisMonth:N0} / {quota.MonthlyLimit:N0} {quota.UnitName} ({quota.Percentage:F1}%)";
        }
        else
        {
            QuotaSummaryText = $"本月已用: {quota.UsedThisMonth:N0} {quota.UnitName}";
        }
    }

    [RelayCommand]
    public async Task TranslateAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceText))
        {
            StatusMessage = "请输入待翻译的文本";
            return;
        }

        IsTranslating = true;
        StatusMessage = "正在请求翻译...";

        try
        {
            var sw = Stopwatch.StartNew();
            var result = await _translationService.TranslateWithProviderAsync(
                SelectedProvider,
                SourceText,
                SelectedSourceLang.Code,
                SelectedTargetLang.Code);
            sw.Stop();

            if (result.IsSuccess)
            {
                TranslatedText = result.TranslatedText;
                ElapsedText = $"耗时: {sw.ElapsedMilliseconds} ms";
                StatusMessage = "✓ 翻译完成";
                UpdateQuotaInfo();
            }
            else
            {
                StatusMessage = $"✕ 翻译失败: {result.ErrorMessage}";
                ToastNotificationWindow.Show(result.ErrorMessage ?? "翻译失败", TimeSpan.FromMilliseconds(50), "翻译提示");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"✕ 发生异常: {ex.Message}";
        }
        finally
        {
            IsTranslating = false;
        }
    }

    [RelayCommand]
    public void SwapLanguages()
    {
        if (SelectedSourceLang.Code == "auto")
        {
            // 若源语言为自动检测，按目标语言直接翻转（如目标为中文则切换为中文->英语；若非中文则切换为该语言->中文）
            var oldTgtCode = SelectedTargetLang.Code;
            var newSrc = SourceLanguages.FirstOrDefault(l => l.Code == oldTgtCode)
                         ?? SourceLanguages.FirstOrDefault(l => l.Code == "zh")
                         ?? SourceLanguages[0];

            string newTgtCode = (oldTgtCode == "zh" || oldTgtCode == "zh-CN") ? "en" : "zh";
            var newTgt = TargetLanguages.FirstOrDefault(l => l.Code == newTgtCode)
                         ?? TargetLanguages.FirstOrDefault(l => l.Code != newSrc.Code)
                         ?? TargetLanguages[0];

            SelectedSourceLang = newSrc;
            SelectedTargetLang = newTgt;
        }
        else
        {
            var oldSrcCode = SelectedSourceLang.Code;
            var oldTgtCode = SelectedTargetLang.Code;

            var newSrc = SourceLanguages.FirstOrDefault(l => l.Code == oldTgtCode);
            var newTgt = TargetLanguages.FirstOrDefault(l => l.Code == oldSrcCode);

            if (newSrc != null && newTgt != null)
            {
                SelectedSourceLang = newSrc;
                SelectedTargetLang = newTgt;
            }
        }

        // 互换原文与译文
        if (!string.IsNullOrWhiteSpace(TranslatedText))
        {
            var tmp = SourceText;
            SourceText = TranslatedText;
            TranslatedText = tmp;
        }
    }

    [RelayCommand]
    public void ClearSource()
    {
        SourceText = string.Empty;
        StatusMessage = "已清空原文";
    }

    [RelayCommand]
    public async Task PasteSourceAsync()
    {
        var text = await _clipboardService.GetTextAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            SourceText = text;
            StatusMessage = "已粘贴剪贴板文字";
        }
    }

    [RelayCommand]
    public async Task CopyResultAsync()
    {
        if (!string.IsNullOrWhiteSpace(TranslatedText))
        {
            await _clipboardService.SetTextAsync(TranslatedText);
            StatusMessage = "✓ 译文已复制到剪贴板";
            ToastNotificationWindow.Show("译文已复制到系统剪贴板", TimeSpan.FromMilliseconds(50), "复制成功");
        }
    }

    [RelayCommand]
    public async Task StartSelectionTranslateAsync()
    {
        if (_coordinator != null)
        {
            await _coordinator.StartTranslateSelectionAsync();
        }
    }
}
