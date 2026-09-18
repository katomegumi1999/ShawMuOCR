using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.Clipboard;
using ShawMuOCR.Core.Services;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Core.Translation;

namespace ShawMuOCR.Views;

/// <summary>
/// 随动翻译浮窗（纯 UI 视图层）：
/// 彻底与数据和后台业务解耦，只负责渲染展示与收集基础交互（如点击重试、复制、切换置顶等）。
/// 所有 OCR 识别、网络翻译、耗时统计与数据流水线均由协调服务统筹推进，并直接推送至本视图。
/// </summary>
public partial class TranslateFloatingWindow : Window
{
    private readonly ISettingsService _settingsService;
    private readonly IClipboardService _clipboardService;
    private readonly ITranslationService _translationService;

    private string _currentOcrText = string.Empty;
    private string _currentTranslatedText = string.Empty;
    private bool _isInitializingProvider = false;

    /// <summary>
    /// 当用户点击浮窗内的“重试”按钮时触发
    /// </summary>
    public event Action? RetryRequested;

    public TranslateFloatingWindow()
    {
        InitializeComponent();
        _settingsService = null!;
        _clipboardService = null!;
        _translationService = null!;
    }

    public TranslateFloatingWindow(
        ISettingsService settingsService,
        IClipboardService clipboardService,
        ITranslationService translationService)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _clipboardService = clipboardService;
        _translationService = translationService;

        InitializeProviderComboBox();
        UpdateLanguageDirectionText();
        UpdateQuotaDisplay();
        UpdateSourceTooltip(string.Empty);

        KeyDown += OnWindowKeyDown;
    }

    #region 公共数据推送接口 (UI 只负责展示)

    /// <summary>
    /// 仅首次唤起时计算初始位置靠在选区旁边
    /// </summary>
    public void InitializePosition(ScreenRect sel)
    {
        int windowW = 390;
        int windowH = 430;

        int x = sel.X + sel.Width + 10;
        int y = sel.Y;

        var screens = Screens;
        var screen = screens.ScreenFromPoint(new PixelPoint(sel.X + sel.Width / 2, sel.Y + sel.Height / 2)) ?? screens.Primary;
        if (screen != null)
        {
            var bounds = screen.WorkingArea;
            if (x + windowW > bounds.X + bounds.Width)
            {
                x = sel.X - windowW - 10;
            }
            if (x < bounds.X)
            {
                x = Math.Max(bounds.X + 10, Math.Min(sel.X, bounds.X + bounds.Width - windowW - 10));
                y = sel.Y + sel.Height + 10;
            }
            if (y + windowH > bounds.Y + bounds.Height)
            {
                y = Math.Max(bounds.Y + 10, bounds.Y + bounds.Height - windowH - 10);
            }
        }

        Position = new PixelPoint(x, y);
        BringToFront();
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
        ResultScrollViewer.IsVisible = false;
        ErrorPanel.IsVisible = false;
        LoadingStatusText.Text = statusText;
        ElapsedText.Text = "处理中...";
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
    public void SetSuccessResult(string translatedText, string elapsedText, string quotaText)
    {
        _currentTranslatedText = translatedText ?? string.Empty;
        LoadingPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;

        ResultTextBlock.Text = _currentTranslatedText;
        ResultScrollViewer.IsVisible = true;

        ElapsedText.Text = elapsedText;
        QuotaInfoText.Text = quotaText;
    }

    /// <summary>
    /// 推送并展示错误信息
    /// </summary>
    public void SetError(string errorMsg, string elapsedText)
    {
        LoadingPanel.IsVisible = false;
        ResultScrollViewer.IsVisible = false;
        ErrorPanel.IsVisible = true;

        ErrorTextBlock.Text = errorMsg;
        ElapsedText.Text = elapsedText;
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
    /// 更新源语言与目标语言方向标签
    /// </summary>
    public void UpdateLanguageDirectionText()
    {
        if (_settingsService == null) return;
        var s = _settingsService.Current;
        string srcName = TranslationLanguages.GetName(s.TranslateSourceLang);
        string tgtName = TranslationLanguages.GetName(s.TranslateTargetLang);
        LanguageDirectionText.Text = $"[{srcName} ➔ {tgtName}]";
    }

    #endregion

    #region 私有交互与渲染逻辑

    private void InitializeProviderComboBox()
    {
        _isInitializingProvider = true;
        try
        {
            var s = _settingsService.Current;
            var items = new List<ComboBoxItem>();

            var baiduItem = new ComboBoxItem { Tag = "Baidu", Content = "百度翻译" };
            var googleItem = new ComboBoxItem { Tag = "Google", Content = "谷歌翻译" };

            string openAiLabel = string.IsNullOrWhiteSpace(s.OpenAiModel)
                ? "OpenAI 兼容"
                : $"OpenAI ({s.OpenAiModel})";
            var openAiItem = new ComboBoxItem { Tag = "OpenAI", Content = openAiLabel };

            items.Add(baiduItem);
            items.Add(googleItem);
            items.Add(openAiItem);

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
        if (_isInitializingProvider) return;

        if (ProviderComboBox.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string newProvider)
        {
            var s = _settingsService.Current;
            if (!string.Equals(s.TranslateProvider, newProvider, StringComparison.OrdinalIgnoreCase))
            {
                s.TranslateProvider = newProvider;
                _settingsService.Save(s);

                UpdateLanguageDirectionText();
                UpdateQuotaDisplay();

                // 要求1：用户手动切换模型时不自动重新发起翻译，仅保存设置并刷新界面提示。用户如需重新翻译可主动点击“重试”按钮
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
            Foreground = this.TryFindResource("TextFillColorSecondaryBrush", out var fg) && fg is IBrush brushFg ? brushFg : Brushes.LightGray
        });
        var countText = new TextBlock
        {
            Text = $"{text.Length} 字符",
            FontSize = 11,
            Foreground = this.TryFindResource("TextFillColorTertiaryBrush", out var tg) && tg is IBrush brushTg ? brushTg : Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        DockPanel.SetDock(countText, Dock.Right);
        headerDock.Children.Add(countText);

        var sep = new Separator
        {
            Height = 1,
            Background = this.TryFindResource("DividerStrokeColorDefaultBrush", out var dv) && dv is IBrush brushDv ? brushDv : Brushes.Gray,
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
                LineHeight = 18
            }
        };

        var footerTip = new TextBlock
        {
            Text = "💡 提示: 点击按钮可直接复制原文",
            FontSize = 10,
            Foreground = this.TryFindResource("TextFillColorTertiaryBrush", out var ft) && ft is IBrush brushFt ? brushFt : Brushes.Gray,
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
        if (!string.IsNullOrWhiteSpace(_currentTranslatedText))
        {
            await _clipboardService.SetTextAsync(_currentTranslatedText);
            ToastNotificationWindow.Show("译文已复制到剪贴板", TimeSpan.FromMilliseconds(50), "复制成功");
        }
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
