using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;
using ShawMuOCR.Services;
using MuClip.Services;

namespace ShawMuOCR.Views;

public enum FloatingWindowMode
{
    Translation,
    OcrPopup,
    Note
}

/// <summary>
/// 随动翻译浮窗 / OCR 窗格 / 随手便笺（纯 UI 视图层）：
/// 彻底与数据和后台业务解耦，只负责渲染展示与收集基础交互（如点击重试、复制、切换置顶等）。
/// 所有 OCR 识别、网络翻译、耗时统计与数据流水线均由协调服务统筹推进，并直接推送至本视图。
/// </summary>
public partial class TranslateFloatingWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private readonly ISettingsService? _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly ITranslationService? _translationService;
    private readonly IMuClipService? _muClipService;
    private readonly FloatingWindowMode _mode;

    private string _currentOcrText = string.Empty;
    private string _currentResultText = string.Empty;
    private string _originalResultText = string.Empty;
    private bool _isInitializingProvider = false;
    private bool _isInitializingLanguages = false;

    /// <summary>
    /// 当用户点击浮窗内的"重试"按钮时触发
    /// </summary>
    public event Action? RetryRequested;

    /// <summary>
    /// 当用户点击浮窗内的"新建"按钮时触发（发起新的 OCR 窗格 / 选区识别）
    /// </summary>
    public event Action? NewSessionRequested;

    public TranslateFloatingWindow()
    {
        InitializeComponent();
        _settingsService = null!;
        _clipboardService = null!;
        _translationService = null!;
        _muClipService = null;
        _mode = FloatingWindowMode.Translation;
    }

    public TranslateFloatingWindow(
        ISettingsService settingsService,
        IClipboardService clipboardService,
        ITranslationService? translationService,
        FloatingWindowMode mode = FloatingWindowMode.Translation,
        IMuClipService? muClipService = null)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _translationService = translationService;
        _mode = mode;
        _muClipService = muClipService;

        ApplyMode();
        ApplyCustomThemeColors();

        if (_mode == FloatingWindowMode.Translation)
        {
            InitializeProviderComboBox();
            InitializeLanguageComboBoxes();
            UpdateQuotaDisplay();
        }
        UpdateSourceTooltip(string.Empty);

        KeyDown += OnWindowKeyDown;

        if (_settingsService != null)
        {
            _settingsService.SettingsChanged += OnSettingsChanged;
            Closed += (_, _) => _settingsService.SettingsChanged -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(AppSettings _)
    {
        Dispatcher.UIThread.Post(ApplyCustomThemeColors);
    }

    private void ApplyCustomThemeColors()
    {
        if (_settingsService?.Current == null) return;

        var s = _settingsService.Current;
        string? bgHex = s.ThemeFloatingWindowBackgroundColorHex;
        string? textHex = s.ThemeFloatingWindowTextColorHex;

        if (!string.IsNullOrWhiteSpace(bgHex) && ColorPaletteHelper.TryParseColor(bgHex, out var bg))
        {
            double brightness = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
            bool isDark = brightness < 0.5;

            Color headerBg = isDark
                ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 12), (byte)Math.Min(255, bg.G + 12), (byte)Math.Min(255, bg.B + 12))
                : Color.FromArgb(255, (byte)Math.Max(0, bg.R - 10), (byte)Math.Max(0, bg.G - 10), (byte)Math.Max(0, bg.B - 10));

            Color border = isDark
                ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 35), (byte)Math.Min(255, bg.G + 35), (byte)Math.Min(255, bg.B + 35))
                : Color.FromArgb(255, (byte)Math.Max(0, bg.R - 25), (byte)Math.Max(0, bg.G - 25), (byte)Math.Max(0, bg.B - 25));

            Color divider = isDark
                ? Color.FromArgb(255, (byte)Math.Min(255, bg.R + 22), (byte)Math.Min(255, bg.G + 22), (byte)Math.Min(255, bg.B + 22))
                : Color.FromArgb(255, (byte)Math.Max(0, bg.R - 18), (byte)Math.Max(0, bg.G - 18), (byte)Math.Max(0, bg.B - 18));

            Color hover = isDark ? Color.FromArgb(32, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
            Color pressed = isDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0);

            Color textPrimary;
            if (!string.IsNullOrWhiteSpace(textHex) && ColorPaletteHelper.TryParseColor(textHex, out var customText))
            {
                textPrimary = customText;
            }
            else
            {
                textPrimary = isDark ? Color.FromRgb(237, 237, 237) : Color.FromRgb(32, 32, 32);
            }

            Color textSecondary = Color.FromArgb((byte)(textPrimary.A * 0.72), textPrimary.R, textPrimary.G, textPrimary.B);
            Color textTertiary = Color.FromArgb((byte)(textPrimary.A * 0.52), textPrimary.R, textPrimary.G, textPrimary.B);

            Resources["FloatingWindowBackground"] = new SolidColorBrush(bg);
            Resources["FloatingWindowHeaderBg"] = new SolidColorBrush(headerBg);
            Resources["FloatingWindowCardBg"] = new SolidColorBrush(bg);
            Resources["FloatingWindowBorder"] = new SolidColorBrush(border);
            Resources["FloatingWindowDivider"] = new SolidColorBrush(divider);
            Resources["FloatingWindowHoverBrush"] = new SolidColorBrush(hover);
            Resources["FloatingWindowPressedBrush"] = new SolidColorBrush(pressed);
            Resources["FloatingWindowTextPrimary"] = new SolidColorBrush(textPrimary);
            Resources["FloatingWindowTextSecondary"] = new SolidColorBrush(textSecondary);
            Resources["FloatingWindowTextTertiary"] = new SolidColorBrush(textTertiary);
            Resources["TextControlForeground"] = new SolidColorBrush(textPrimary);
            Resources["TextControlCaretBrush"] = new SolidColorBrush(textPrimary);
            Resources["ButtonForeground"] = new SolidColorBrush(textPrimary);

            if (RootCardBorder != null)
            {
                RootCardBorder.Background = new SolidColorBrush(bg);
                RootCardBorder.BorderBrush = new SolidColorBrush(border);
            }
        }
        else
        {
            string[] keysToClear =
            [
                "FloatingWindowBackground",
                "FloatingWindowHeaderBg",
                "FloatingWindowCardBg",
                "FloatingWindowBorder",
                "FloatingWindowDivider",
                "FloatingWindowHoverBrush",
                "FloatingWindowPressedBrush",
                "FloatingWindowTextPrimary",
                "FloatingWindowTextSecondary",
                "FloatingWindowTextTertiary",
                "TextControlForeground",
                "TextControlCaretBrush",
                "ButtonForeground"
            ];
            foreach (var key in keysToClear)
            {
                Resources.Remove(key);
            }

            if (!string.IsNullOrWhiteSpace(textHex) && ColorPaletteHelper.TryParseColor(textHex, out var customText))
            {
                Color textSecondary = Color.FromArgb((byte)(customText.A * 0.72), customText.R, customText.G, customText.B);
                Color textTertiary = Color.FromArgb((byte)(customText.A * 0.52), customText.R, customText.G, customText.B);

                Resources["FloatingWindowTextPrimary"] = new SolidColorBrush(customText);
                Resources["FloatingWindowTextSecondary"] = new SolidColorBrush(textSecondary);
                Resources["FloatingWindowTextTertiary"] = new SolidColorBrush(textTertiary);
                Resources["TextControlForeground"] = new SolidColorBrush(customText);
                Resources["TextControlCaretBrush"] = new SolidColorBrush(customText);
                Resources["ButtonForeground"] = new SolidColorBrush(customText);
            }

            if (RootCardBorder != null)
            {
                RootCardBorder.ClearValue(Border.BackgroundProperty);
                RootCardBorder.ClearValue(Border.BorderBrushProperty);
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_mode == FloatingWindowMode.Note)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ResultTextBox.Focus();
            }, DispatcherPriority.Input);
        }

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle.HasValue && handle.Value != IntPtr.Zero)
        {
            try
            {
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(handle.Value, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
            }
            catch
            {
                // 容错处理（Win10 及更早系统自动忽略）
            }
        }
    }

    private void ApplyMode()
    {
        switch (_mode)
        {
            case FloatingWindowMode.Translation:
                Title = "选区翻译 - 小沐 OCR";
                TitleIcon.Text = "\U0001f310";
                TitleText.Text = "选区翻译";
                TitleElapsedBadge.IsVisible = false;
                TitleElapsedBadge.Text = string.Empty;
                ToolbarBorder.IsVisible = true;
                StatusStackPanel.IsVisible = true;
                QuotaRow.IsVisible = true;
                RetryButton.IsVisible = true;
                RestoreButton.IsVisible = true;
                NewSessionButton.IsVisible = true;
                CopySourceButton.IsVisible = true;
                ClearNoteButton.IsVisible = false;
                SavePhraseButton.IsVisible = false;
                CopyResultText.Text = "复制译文";
                break;
            case FloatingWindowMode.OcrPopup:
                Title = "OCR 窗格 - 小沐 OCR";
                TitleIcon.Text = "\U0001f4dd";
                TitleText.Text = "OCR 窗格";
                TitleElapsedBadge.IsVisible = true;
                TitleElapsedBadge.Text = string.Empty;
                ToolbarBorder.IsVisible = false;
                StatusStackPanel.IsVisible = false;
                QuotaRow.IsVisible = false;
                RetryButton.IsVisible = false;
                RestoreButton.IsVisible = true;
                NewSessionButton.IsVisible = true;
                CopySourceButton.IsVisible = false;
                ClearNoteButton.IsVisible = false;
                SavePhraseButton.IsVisible = false;
                CopyResultText.Text = "复制结果";
                break;
            case FloatingWindowMode.Note:
                Title = "随手便笺 - 小沐 OCR";
                TitleIcon.Text = "\U0001f4dd";
                TitleText.Text = "随手便笺";
                TitleElapsedBadge.IsVisible = false;
                TitleElapsedBadge.Text = string.Empty;
                ToolbarBorder.IsVisible = false;
                StatusStackPanel.IsVisible = false;
                LoadingPanel.IsVisible = false;
                ErrorPanel.IsVisible = false;
                ResultTextBox.IsVisible = true;
                ResultTextBox.PlaceholderText = "在此输入便笺内容，支持自由编辑、换行与快捷保存...";
                RetryButton.IsVisible = false;
                RestoreButton.IsVisible = false;
                NewSessionButton.IsVisible = false;
                CopySourceButton.IsVisible = false;
                ClearNoteButton.IsVisible = true;
                SavePhraseButton.IsVisible = true;
                CopyResultText.Text = "复制";
                break;
        }
    }

    #region 公共数据推送接口 (UI 只负责展示)

    /// <summary>
    /// 根据用户框选的屏幕物理区域与鼠标起始按下坐标计算浮窗初始物理位置，
    /// 以用户起始鼠标位置为准展开，在多屏混合 DPI 场景下精准对齐、屏幕边缘停靠不翻转、绝不溢出屏幕边界。
    /// </summary>
    public void InitializePosition(ScreenRect sel, ScreenPoint? startPoint = null)
    {
        double dipW = Width > 0 ? Width : 380;
        double dipH = Height > 0 ? Height : 440;

        // 以用户起始鼠标位置为准；若未传入起始点则使用选区左上角
        int refX = startPoint?.X ?? sel.X;
        int refY = startPoint?.Y ?? sel.Y;

        var screen = Screens.ScreenFromPoint(new PixelPoint(refX, refY)) ?? Screens.Primary;

        if (screen != null)
        {
            double scale = screen.Scaling;
            var bounds = screen.WorkingArea; // bounds 为真实屏幕物理像素

            int physicalW = (int)Math.Ceiling(dipW * scale);
            int physicalH = (int)Math.Ceiling(dipH * scale);
            int marginPx = (int)(10 * scale);

            // 以用户鼠标起始按下位置为准展开
            int targetX = refX;
            int targetY = refY;

            // 1. 水平方向边界防出界调整（停靠在屏幕边缘，不自动翻转）
            if (targetX + physicalW > bounds.X + bounds.Width - marginPx)
            {
                targetX = bounds.X + bounds.Width - physicalW - marginPx;
            }
            if (targetX < bounds.X + marginPx)
            {
                targetX = bounds.X + marginPx;
            }

            // 2. 垂直方向边界防出界调整（停靠在屏幕边缘，不自动翻转）
            if (targetY + physicalH > bounds.Y + bounds.Height - marginPx)
            {
                targetY = bounds.Y + bounds.Height - physicalH - marginPx;
            }
            if (targetY < bounds.Y + marginPx)
            {
                targetY = bounds.Y + marginPx;
            }

            Position = new PixelPoint(targetX, targetY);
        }
        else
        {
            Position = new PixelPoint(refX, refY);
        }
    }

    /// <summary>
    /// 便笺模式初始位置：在屏幕中上方舒适展开（更符合视线与输入习惯）
    /// </summary>
    public void InitializeCenterPosition()
    {
        double dipW = Width > 0 ? Width : 380;
        double dipH = Height > 0 ? Height : 440;

        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen != null)
        {
            double scale = screen.Scaling;
            var bounds = screen.WorkingArea;
            int physicalW = (int)Math.Ceiling(dipW * scale);
            int physicalH = (int)Math.Ceiling(dipH * scale);

            int targetX = bounds.X + (bounds.Width - physicalW) / 2;
            int targetY = bounds.Y + (bounds.Height - physicalH) / 3;

            Position = new PixelPoint(targetX, targetY);
        }
    }

    /// <summary>
    /// 将窗口激活置前，保留用户自行调整好的窗口位置不移动
    /// </summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
    }

    /// <summary>
    /// 展示加载中状态与提示文字
    /// </summary>
    public void ShowLoading(string statusText)
    {
        LoadingPanel.IsVisible = true;
        ResultTextBox.IsVisible = false;
        ErrorPanel.IsVisible = false;
        LoadingStatusText.Text = statusText;
        ElapsedText.Text = "处理中...";

        if (_mode == FloatingWindowMode.OcrPopup)
        {
            TitleElapsedBadge.IsVisible = true;
            TitleElapsedBadge.Text = "· 识别中...";
            ToolTip.SetTip(TitleElapsedBadge, null);
        }
    }

    /// <summary>
    /// 推送并展示 OCR 原文数据（更新 Hover 浮动卡片）
    /// </summary>
    public void SetSourceText(string ocrText)
    {
        _currentOcrText = ocrText ?? string.Empty;
        UpdateSourceTooltip(_currentOcrText);
    }

    /// <summary>
    /// 推送并展示翻译成功结果与耗时额度数据
    /// </summary>
    public void SetSuccessResult(string resultText, string elapsedText, string quotaText)
    {
        _currentResultText = resultText ?? string.Empty;
        _originalResultText = _currentResultText;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;

        ResultTextBox.Text = _currentResultText;
        ResultTextBox.IsVisible = true;

        ElapsedText.Text = elapsedText;
        if (_mode == FloatingWindowMode.Translation)
        {
            QuotaInfoText.Text = quotaText;
        }
        else if (_mode == FloatingWindowMode.OcrPopup)
        {
            TitleElapsedBadge.IsVisible = true;
            TitleElapsedBadge.Text = string.IsNullOrWhiteSpace(elapsedText) ? string.Empty : $"· {elapsedText}";
            ToolTip.SetTip(TitleElapsedBadge, $"耗时统计: {elapsedText}");
        }
    }

    /// <summary>
    /// 推送并展示错误信息
    /// </summary>
    public void SetError(string errorMsg, string elapsedText)
    {
        LoadingPanel.IsVisible = false;
        ResultTextBox.IsVisible = false;
        ErrorPanel.IsVisible = true;

        ErrorTextBlock.Text = errorMsg;
        ElapsedText.Text = elapsedText;

        if (_mode == FloatingWindowMode.OcrPopup)
        {
            TitleElapsedBadge.IsVisible = true;
            TitleElapsedBadge.Text = string.IsNullOrWhiteSpace(elapsedText) ? string.Empty : $"· {elapsedText}";
            ToolTip.SetTip(TitleElapsedBadge, $"异常终止耗时: {elapsedText}");
        }
    }

    /// <summary>
    /// 更新配额信息展示
    /// </summary>
    public void UpdateQuotaDisplay()
    {
        if (_translationService == null || _settingsService == null) return;
        var quota = _translationService.GetQuotaInfo(_settingsService.Current.TranslateProvider);
        if (quota.MonthlyLimit > 0)
        {
            QuotaInfoText.Text = $"{quota.UsedThisMonth:N0} / {quota.MonthlyLimit:N0} {quota.UnitName} ({quota.Percentage:F1}%)";
        }
        else
        {
            QuotaInfoText.Text = $"{quota.UsedThisMonth:N0} {quota.UnitName}";
        }
    }

    /// <summary>
    /// 更新源语言与目标语言方向（同步下拉列表选中项）
    /// </summary>
    public void UpdateLanguageDirectionText()
    {
        if (_settingsService == null || SourceLangComboBox == null || TargetLangComboBox == null) return;
        var s = _settingsService.Current;

        _isInitializingLanguages = true;
        try
        {
            if (SourceLangComboBox.ItemsSource is IEnumerable<ComboBoxItem> srcItems)
            {
                var match = srcItems.FirstOrDefault(i => string.Equals(i.Tag as string, s.TranslateSourceLang, StringComparison.OrdinalIgnoreCase));
                if (match != null) SourceLangComboBox.SelectedItem = match;
            }
            if (TargetLangComboBox.ItemsSource is IEnumerable<ComboBoxItem> tgtItems)
            {
                var match = tgtItems.FirstOrDefault(i => string.Equals(i.Tag as string, s.TranslateTargetLang, StringComparison.OrdinalIgnoreCase));
                if (match != null) TargetLangComboBox.SelectedItem = match;
            }
        }
        finally
        {
            _isInitializingLanguages = false;
        }
    }

    #endregion

    #region 私有交互与渲染逻辑

    private void InitializeLanguageComboBoxes()
    {
        if (_settingsService == null) return;
        _isInitializingLanguages = true;
        try
        {
            var s = _settingsService.Current;

            // 源语言列表 (支持 auto 自动检测及各类常用语言)
            var srcItems = new List<ComboBoxItem>();
            foreach (var lang in TranslationLanguages.SupportedSourceLanguages)
            {
                string display = lang.Code == "auto" ? "自动" : lang.DisplayName.Split(' ')[0];
                var item = new ComboBoxItem { Tag = lang.Code, Content = display };
                ToolTip.SetTip(item, lang.DisplayName);
                srcItems.Add(item);
            }
            SourceLangComboBox.ItemsSource = srcItems;
            var selectedSrc = srcItems.FirstOrDefault(i => string.Equals(i.Tag as string, s.TranslateSourceLang, StringComparison.OrdinalIgnoreCase)) ?? srcItems[0];
            SourceLangComboBox.SelectedItem = selectedSrc;

            // 目标语言列表 (不包含 auto)
            var tgtItems = new List<ComboBoxItem>();
            foreach (var lang in TranslationLanguages.SupportedTargetLanguages)
            {
                string display = lang.DisplayName.Split(' ')[0];
                var item = new ComboBoxItem { Tag = lang.Code, Content = display };
                ToolTip.SetTip(item, lang.DisplayName);
                tgtItems.Add(item);
            }
            TargetLangComboBox.ItemsSource = tgtItems;
            var selectedTgt = tgtItems.FirstOrDefault(i => string.Equals(i.Tag as string, s.TranslateTargetLang, StringComparison.OrdinalIgnoreCase)) ?? tgtItems[0];
            TargetLangComboBox.SelectedItem = selectedTgt;
        }
        finally
        {
            _isInitializingLanguages = false;
        }
    }

    private void OnSourceLangSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingLanguages || _settingsService == null) return;

        if (SourceLangComboBox.SelectedItem is ComboBoxItem item && item.Tag is string newSrc)
        {
            var s = _settingsService.Current;
            if (!string.Equals(s.TranslateSourceLang, newSrc, StringComparison.OrdinalIgnoreCase))
            {
                s.TranslateSourceLang = newSrc;
                _settingsService.Save(s);

                // 切换语言后，若已有 OCR 识别原文则自动触发重试翻译
                if (!string.IsNullOrWhiteSpace(_currentOcrText))
                {
                    RetryRequested?.Invoke();
                }
            }
        }
    }

    private void OnTargetLangSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingLanguages || _settingsService == null) return;

        if (TargetLangComboBox.SelectedItem is ComboBoxItem item && item.Tag is string newTgt)
        {
            var s = _settingsService.Current;
            if (!string.Equals(s.TranslateTargetLang, newTgt, StringComparison.OrdinalIgnoreCase))
            {
                s.TranslateTargetLang = newTgt;
                _settingsService.Save(s);

                // 切换语言后，若已有 OCR 识别原文则自动触发重试翻译
                if (!string.IsNullOrWhiteSpace(_currentOcrText))
                {
                    RetryRequested?.Invoke();
                }
            }
        }
    }

    private void OnSwapLangClick(object? sender, RoutedEventArgs e)
    {
        if (_settingsService == null) return;
        var s = _settingsService.Current;

        string oldSrc = s.TranslateSourceLang;
        string oldTgt = s.TranslateTargetLang;

        string newSrc;
        string newTgt;

        if (string.Equals(oldSrc, "auto", StringComparison.OrdinalIgnoreCase))
        {
            // 若源语言为自动检测，按目标语言直接翻转（如目标为中文则直接切换为中文->英语；若非中文则切换为该语言->中文）
            newSrc = oldTgt;
            newTgt = (oldTgt == "zh" || oldTgt == "zh-CN") ? "en" : "zh";
        }
        else
        {
            newSrc = oldTgt;
            newTgt = oldSrc;
        }

        s.TranslateSourceLang = newSrc;
        s.TranslateTargetLang = newTgt;
        _settingsService.Save(s);

        UpdateLanguageDirectionText();

        if (!string.IsNullOrWhiteSpace(_currentOcrText))
        {
            RetryRequested?.Invoke();
        }
    }

    private void InitializeProviderComboBox()
    {
        if (_settingsService == null) return;
        _isInitializingProvider = true;
        try
        {
            var s = _settingsService.Current;
            var items = new List<ComboBoxItem>();

            var baiduItem = new ComboBoxItem { Tag = "Baidu", Content = "百度翻译" };
            var googleItem = new ComboBoxItem { Tag = "Google", Content = "谷歌翻译" };

            var openAiItem = new ComboBoxItem { Tag = "OpenAI", Content = "OpenAI" };
            string openAiTip = string.IsNullOrWhiteSpace(s.OpenAiModel)
                ? "OpenAI 兼容接口"
                : $"OpenAI ({s.OpenAiModel})";
            ToolTip.SetTip(openAiItem, openAiTip);

            items.Add(baiduItem);
            items.Add(googleItem);
            items.Add(openAiItem);

            var volcengineItem = new ComboBoxItem { Tag = "Volcengine", Content = "火山引擎" };
            var aliyunItem = new ComboBoxItem { Tag = "Aliyun", Content = "阿里云翻译" };
            items.Add(volcengineItem);
            items.Add(aliyunItem);

            ProviderComboBox.ItemsSource = items;

            var selected = items.FirstOrDefault(i => string.Equals(i.Tag as string, s.TranslateProvider, StringComparison.OrdinalIgnoreCase)) ?? baiduItem;
            ProviderComboBox.SelectedItem = selected;
        }
        finally
        {
            _isInitializingProvider = false;
        }
    }

    private void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingProvider || _settingsService == null) return;

        if (ProviderComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string newProvider)
        {
            var s = _settingsService.Current;
            if (!string.Equals(s.TranslateProvider, newProvider, StringComparison.OrdinalIgnoreCase))
            {
                s.TranslateProvider = newProvider;
                _settingsService.Save(s);

                UpdateLanguageDirectionText();
                UpdateQuotaDisplay();

                // 用户手动切换模型时，若已有 OCR 识别内容可点击重试
            }
        }
    }

    private void UpdateSourceTooltip(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ToolTip.SetTip(SourceHoverButton, "正在等待 OCR 识别结果...");
            return;
        }

        var content = new Border
        {
            MaxWidth = 340,
            MaxHeight = 220,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = this.TryFindResource("FloatingWindowBackground", out var bg) && bg is IBrush brushBg ? brushBg : Brushes.DimGray,
            BorderBrush = this.TryFindResource("FloatingWindowBorder", out var border) && border is IBrush brushBorder ? brushBorder : Brushes.Gray,
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 4, Blur = 16, Spread = 0, Color = Color.Parse("#40000000") })
        };

        var sp = new StackPanel { Spacing = 6 };
        var headerDock = new DockPanel { LastChildFill = true };
        headerDock.Children.Add(new TextBlock
        {
            Text = "📄 OCR 提取原文",
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Foreground = this.TryFindResource("FloatingWindowTextSecondary", out var fg) && fg is IBrush brushFg ? brushFg : Brushes.LightGray
        });
        var countText = new TextBlock
        {
            Text = $"{text.Length} 字符",
            FontSize = 11,
            Foreground = this.TryFindResource("FloatingWindowTextTertiary", out var tg) && tg is IBrush brushTg ? brushTg : Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(countText, Dock.Right);
        headerDock.Children.Add(countText);

        var sep = new Separator
        {
            Height = 1,
            Background = this.TryFindResource("FloatingWindowDivider", out var dv) && dv is IBrush brushDv ? brushDv : Brushes.Gray,
            Margin = new Thickness(0, 2)
        };

        var scroll = new ScrollViewer
        {
            MaxHeight = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                LineHeight = 18,
                Foreground = this.TryFindResource("FloatingWindowTextPrimary", out var tp) && tp is IBrush brushTp ? brushTp : Brushes.Gray
            }
        };

        var footerTip = new TextBlock
        {
            Text = "💡 提示: 点击按钮可直接复制原文",
            FontSize = 10,
            Foreground = this.TryFindResource("FloatingWindowTextTertiary", out var ft) && ft is IBrush brushFt ? brushFt : Brushes.Gray,
            Margin = new Thickness(0, 2, 0, 0)
        };

        sp.Children.Add(headerDock);
        sp.Children.Add(sep);
        sp.Children.Add(scroll);
        sp.Children.Add(footerTip);
        content.Child = sp;

        ToolTip.SetTip(SourceHoverButton, content);
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnPinClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinIcon.Opacity = Topmost ? 1.0 : 0.4;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke();
    }

    private async void OnCopySourceClick(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_currentOcrText))
        {
            await _clipboardService.SetTextAsync(_currentOcrText);
            ToastNotificationWindow.Show("原文已复制到剪贴板", TimeSpan.FromMilliseconds(50), "复制成功");
        }
        else
        {
            ToastNotificationWindow.Show("暂无 OCR 原文可复制", TimeSpan.FromMilliseconds(50), "提示");
        }
    }

    private async void OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        string text = ResultTextBox.Text ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            await _clipboardService.SetTextAsync(text);
            string label = _mode switch
            {
                FloatingWindowMode.Translation => "译文",
                FloatingWindowMode.Note => "便笺内容",
                _ => "识别结果"
            };
            ToastNotificationWindow.Show($"{label}已复制到剪贴板", TimeSpan.FromMilliseconds(50), "复制成功");
        }
        else if (_mode == FloatingWindowMode.Note)
        {
            ToastNotificationWindow.Show("便笺内容为空", TimeSpan.FromMilliseconds(50), "提示");
        }
    }

    private void OnClearNoteClick(object? sender, RoutedEventArgs e)
    {
        ResultTextBox.Text = string.Empty;
        ResultTextBox.Focus();
        ToastNotificationWindow.Show("便笺内容已清空", TimeSpan.FromMilliseconds(50), "清空");
    }

    private void OnSavePhraseClick(object? sender, RoutedEventArgs e)
    {
        string text = ResultTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            ToastNotificationWindow.Show("便笺内容为空，无法保存", TimeSpan.FromMilliseconds(50), "提示");
            return;
        }

        if (_muClipService != null)
        {
            _muClipService.AddPhrase(text);
            ToastNotificationWindow.Show("已成功保存至常用语", TimeSpan.FromMilliseconds(50), "常用语");
        }
        else
        {
            ToastNotificationWindow.Show("未启用剪贴板模块，无法保存常用语", TimeSpan.FromMilliseconds(50), "提示");
        }
    }

    private void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        ResultTextBox.Text = _originalResultText;
        ResultTextBox.CaretIndex = _originalResultText.Length;
    }

    private void OnNewSessionClick(object? sender, RoutedEventArgs e)
    {
        NewSessionRequested?.Invoke();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    #endregion
}
