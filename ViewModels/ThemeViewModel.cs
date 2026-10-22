using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShawMuOCR.Services;

namespace ShawMuOCR.ViewModels;

public partial class ThemeViewModel : ViewModelBase
{
    private readonly IThemeService _themeService;
    public Func<Task<string?>>? RequestPickImageFunc { get; set; }

    public IReadOnlyList<ColorPaletteItem> StandardPalettePresets => ColorPaletteHelper.StandardPresets;

    [ObservableProperty]
    private bool _hasBackgroundImage;

    [ObservableProperty]
    private string _backgroundInfoText = "未设置（使用系统默认纯色背景）";

    [ObservableProperty]
    private double _blurRadius;

    [ObservableProperty]
    private double _componentOpacityPercent;

    [ObservableProperty]
    private double _buttonOpacityPercent;

    [ObservableProperty]
    private string? _backgroundColorHex;

    [ObservableProperty]
    private Color _selectedBackgroundColor = Color.FromRgb(243, 244, 246);

    [ObservableProperty]
    private IBrush? _backgroundColorPreviewBrush;

    [ObservableProperty]
    private bool _hasCustomBackgroundColor;

    [ObservableProperty]
    private string? _cardColorHex;

    [ObservableProperty]
    private Color _selectedCardColor = Colors.White;

    [ObservableProperty]
    private IBrush? _cardColorPreviewBrush;

    [ObservableProperty]
    private bool _hasCustomCardColor;

    [ObservableProperty]
    private string? _textColorHex;

    [ObservableProperty]
    private Color _selectedTextColor = Color.FromRgb(30, 41, 59);

    [ObservableProperty]
    private IBrush? _textColorPreviewBrush;

    [ObservableProperty]
    private bool _hasCustomTextColor;

    [ObservableProperty]
    private string? _floatingWindowBackgroundColorHex;

    [ObservableProperty]
    private Color _selectedFloatingWindowBackgroundColor = Colors.White;

    [ObservableProperty]
    private IBrush? _floatingWindowBackgroundColorPreviewBrush;

    [ObservableProperty]
    private bool _hasCustomFloatingWindowBackgroundColor;

    [ObservableProperty]
    private string? _floatingWindowTextColorHex;

    [ObservableProperty]
    private Color _selectedFloatingWindowTextColor = Color.FromRgb(32, 32, 32);

    [ObservableProperty]
    private IBrush? _floatingWindowTextColorPreviewBrush;

    [ObservableProperty]
    private bool _hasCustomFloatingWindowTextColor;

    [ObservableProperty]
    private string _statusMessage = "就绪：可自定义个性背景图片、纯色背景底色、卡片组件色彩与随动浮窗独立配色";

    private bool _isSyncingColor;

    public ThemeViewModel(IThemeService themeService)
    {
        _themeService = themeService ?? throw new ArgumentNullException(nameof(themeService));

        _blurRadius = _themeService.BlurRadius;
        _componentOpacityPercent = Math.Round(_themeService.ComponentOpacity * 100);
        _buttonOpacityPercent = Math.Round(_themeService.ButtonOpacity * 100);
        _hasBackgroundImage = _themeService.HasBackgroundImage;
        _backgroundColorHex = _themeService.BackgroundColorHex;
        _cardColorHex = _themeService.CardColorHex;
        _textColorHex = _themeService.TextColorHex;
        _floatingWindowBackgroundColorHex = _themeService.FloatingWindowBackgroundColorHex;
        _floatingWindowTextColorHex = _themeService.FloatingWindowTextColorHex;

        if (ColorPaletteHelper.TryParseColor(_backgroundColorHex, out var bgParsed))
        {
            _selectedBackgroundColor = bgParsed;
        }
        else
        {
            _selectedBackgroundColor = Color.FromRgb(243, 244, 246);
        }

        if (ColorPaletteHelper.TryParseColor(_cardColorHex, out var cardParsed))
        {
            _selectedCardColor = cardParsed;
        }
        else
        {
            _selectedCardColor = Colors.White;
        }

        if (ColorPaletteHelper.TryParseColor(_textColorHex, out var textParsed))
        {
            _selectedTextColor = textParsed;
        }
        else
        {
            _selectedTextColor = Color.FromRgb(30, 41, 59);
        }

        if (ColorPaletteHelper.TryParseColor(_floatingWindowBackgroundColorHex, out var fwBgParsed))
        {
            _selectedFloatingWindowBackgroundColor = fwBgParsed;
        }
        else
        {
            _selectedFloatingWindowBackgroundColor = Colors.White;
        }

        if (ColorPaletteHelper.TryParseColor(_floatingWindowTextColorHex, out var fwTextParsed))
        {
            _selectedFloatingWindowTextColor = fwTextParsed;
        }
        else
        {
            _selectedFloatingWindowTextColor = Color.FromRgb(32, 32, 32);
        }

        UpdateBackgroundInfoText();
        UpdateBackgroundColorPreview();
        UpdateCardColorPreview();
        UpdateTextColorPreview();
        UpdateFloatingWindowBackgroundColorPreview();
        UpdateFloatingWindowTextColorPreview();

        _themeService.ThemeVisualChanged += OnThemeVisualChanged;
    }

    private void OnThemeVisualChanged()
    {
        HasBackgroundImage = _themeService.HasBackgroundImage;
        BackgroundColorHex = _themeService.BackgroundColorHex;
        CardColorHex = _themeService.CardColorHex;
        TextColorHex = _themeService.TextColorHex;
        UpdateBackgroundInfoText();
        UpdateBackgroundColorPreview();
        UpdateCardColorPreview();
        UpdateTextColorPreview();
    }

    private void UpdateBackgroundInfoText()
    {
        if (_themeService.HasBackgroundImage)
        {
            BackgroundInfoText = "已启用自定义背景图（已自动等比优化压缩，存储于应用数据目录）";
        }
        else if (_themeService.HasBackgroundColor)
        {
            BackgroundInfoText = $"已启用自定义纯色背景 ({_themeService.BackgroundColorHex})，半透明卡片呈现通透磨砂底色";
        }
        else
        {
            BackgroundInfoText = "未设置（使用默认纯净浅色/深色主题背景）";
        }
    }

    partial void OnBlurRadiusChanged(double value)
    {
        _themeService.SetBlurRadius(value);
    }

    partial void OnComponentOpacityPercentChanged(double value)
    {
        _themeService.SetComponentOpacity(value / 100.0);
    }

    partial void OnButtonOpacityPercentChanged(double value)
    {
        _themeService.SetButtonOpacity(value / 100.0);
    }

    partial void OnSelectedBackgroundColorChanged(Color value)
    {
        if (_isSyncingColor) return;
        _isSyncingColor = true;
        try
        {
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (!string.Equals(BackgroundColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                BackgroundColorHex = hex;
            }
        }
        finally
        {
            _isSyncingColor = false;
        }
    }

    partial void OnBackgroundColorHexChanged(string? value)
    {
        _themeService.SetBackgroundColor(value);
        UpdateBackgroundColorPreview();
        UpdateBackgroundInfoText();

        if (!_isSyncingColor)
        {
            _isSyncingColor = true;
            try
            {
                if (ColorPaletteHelper.TryParseColor(value, out var parsed))
                {
                    SelectedBackgroundColor = parsed;
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    SelectedBackgroundColor = Color.FromRgb(243, 244, 246);
                }
            }
            finally
            {
                _isSyncingColor = false;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = "已恢复系统默认背景底色 (#F3F4F6)";
        }
        else
        {
            StatusMessage = $"✓ 纯色背景底色已设置为 {value}，半透明组件已显现通透效果";
        }
    }

    private void UpdateBackgroundColorPreview()
    {
        HasCustomBackgroundColor = !string.IsNullOrWhiteSpace(BackgroundColorHex);
        if (HasCustomBackgroundColor && ColorPaletteHelper.TryParseColor(BackgroundColorHex, out var c))
        {
            BackgroundColorPreviewBrush = new SolidColorBrush(c);
        }
        else
        {
            // 默认柔和微浅灰底色预览
            BackgroundColorPreviewBrush = new SolidColorBrush(Color.FromRgb(243, 244, 246));
        }
    }

    [RelayCommand]
    public void SelectBackgroundColor(string? hex)
    {
        BackgroundColorHex = hex;
    }

    [RelayCommand]
    public void ResetBackgroundColor()
    {
        BackgroundColorHex = null;
        SelectedBackgroundColor = Color.FromRgb(243, 244, 246);
    }

    partial void OnSelectedCardColorChanged(Color value)
    {
        if (_isSyncingColor) return;
        _isSyncingColor = true;
        try
        {
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (!string.Equals(CardColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                CardColorHex = hex;
            }
        }
        finally
        {
            _isSyncingColor = false;
        }
    }

    partial void OnCardColorHexChanged(string? value)
    {
        _themeService.SetCardColor(value);
        UpdateCardColorPreview();

        if (!_isSyncingColor)
        {
            _isSyncingColor = true;
            try
            {
                if (ColorPaletteHelper.TryParseColor(value, out var parsed))
                {
                    SelectedCardColor = parsed;
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    SelectedCardColor = Colors.White;
                }
            }
            finally
            {
                _isSyncingColor = false;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = "已恢复系统默认卡片与组件颜色 (#FFFFFF)";
        }
        else
        {
            StatusMessage = $"✓ 卡片与组件底色已设置为 {value}";
        }
    }

    private void UpdateCardColorPreview()
    {
        HasCustomCardColor = !string.IsNullOrWhiteSpace(CardColorHex);
        if (HasCustomCardColor && ColorPaletteHelper.TryParseColor(CardColorHex, out var c))
        {
            CardColorPreviewBrush = new SolidColorBrush(c);
        }
        else
        {
            CardColorPreviewBrush = new SolidColorBrush(Colors.White);
        }
    }

    [RelayCommand]
    public void SelectCardColor(string? hex)
    {
        CardColorHex = hex;
    }

    [RelayCommand]
    public void ResetCardColor()
    {
        CardColorHex = null;
        SelectedCardColor = Colors.White;
    }

    partial void OnSelectedTextColorChanged(Color value)
    {
        if (_isSyncingColor) return;
        _isSyncingColor = true;
        try
        {
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (!string.Equals(TextColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                TextColorHex = hex;
            }
        }
        finally
        {
            _isSyncingColor = false;
        }
    }

    partial void OnTextColorHexChanged(string? value)
    {
        _themeService.SetTextColor(value);
        UpdateTextColorPreview();

        if (!_isSyncingColor)
        {
            _isSyncingColor = true;
            try
            {
                if (ColorPaletteHelper.TryParseColor(value, out var parsed))
                {
                    SelectedTextColor = parsed;
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    SelectedTextColor = Color.FromRgb(30, 41, 59);
                }
            }
            finally
            {
                _isSyncingColor = false;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = "已恢复系统默认字体颜色";
        }
        else
        {
            StatusMessage = $"✓ 全局字体颜色已设置为 {value}";
        }
    }

    private void UpdateTextColorPreview()
    {
        HasCustomTextColor = !string.IsNullOrWhiteSpace(TextColorHex);
        if (HasCustomTextColor && ColorPaletteHelper.TryParseColor(TextColorHex, out var c))
        {
            TextColorPreviewBrush = new SolidColorBrush(c);
        }
        else
        {
            // 默认深灰文本色预览
            TextColorPreviewBrush = new SolidColorBrush(Color.FromRgb(30, 41, 59));
        }
    }

    [RelayCommand]
    public void SelectTextColor(string? hex)
    {
        TextColorHex = hex;
    }

    [RelayCommand]
    public void ResetTextColor()
    {
        TextColorHex = null;
        SelectedTextColor = Color.FromRgb(30, 41, 59);
    }

    partial void OnSelectedFloatingWindowBackgroundColorChanged(Color value)
    {
        if (_isSyncingColor) return;
        _isSyncingColor = true;
        try
        {
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (!string.Equals(FloatingWindowBackgroundColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                FloatingWindowBackgroundColorHex = hex;
            }
        }
        finally
        {
            _isSyncingColor = false;
        }
    }

    partial void OnFloatingWindowBackgroundColorHexChanged(string? value)
    {
        _themeService.SetFloatingWindowBackgroundColor(value);
        UpdateFloatingWindowBackgroundColorPreview();

        if (!_isSyncingColor)
        {
            _isSyncingColor = true;
            try
            {
                if (ColorPaletteHelper.TryParseColor(value, out var parsed))
                {
                    SelectedFloatingWindowBackgroundColor = parsed;
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    SelectedFloatingWindowBackgroundColor = Colors.White;
                }
            }
            finally
            {
                _isSyncingColor = false;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = "随动浮窗已恢复系统默认主题背景色";
        }
        else
        {
            StatusMessage = $"✓ 随动浮窗背景色已设置为 {value}";
        }
    }

    private void UpdateFloatingWindowBackgroundColorPreview()
    {
        HasCustomFloatingWindowBackgroundColor = !string.IsNullOrWhiteSpace(FloatingWindowBackgroundColorHex);
        if (HasCustomFloatingWindowBackgroundColor && ColorPaletteHelper.TryParseColor(FloatingWindowBackgroundColorHex, out var c))
        {
            FloatingWindowBackgroundColorPreviewBrush = new SolidColorBrush(c);
        }
        else
        {
            FloatingWindowBackgroundColorPreviewBrush = new SolidColorBrush(Colors.White);
        }
    }

    [RelayCommand]
    public void SelectFloatingWindowBackgroundColor(string? hex)
    {
        FloatingWindowBackgroundColorHex = hex;
    }

    [RelayCommand]
    public void ResetFloatingWindowBackgroundColor()
    {
        FloatingWindowBackgroundColorHex = null;
        SelectedFloatingWindowBackgroundColor = Colors.White;
    }

    partial void OnSelectedFloatingWindowTextColorChanged(Color value)
    {
        if (_isSyncingColor) return;
        _isSyncingColor = true;
        try
        {
            string hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
            if (!string.Equals(FloatingWindowTextColorHex, hex, StringComparison.OrdinalIgnoreCase))
            {
                FloatingWindowTextColorHex = hex;
            }
        }
        finally
        {
            _isSyncingColor = false;
        }
    }

    partial void OnFloatingWindowTextColorHexChanged(string? value)
    {
        _themeService.SetFloatingWindowTextColor(value);
        UpdateFloatingWindowTextColorPreview();

        if (!_isSyncingColor)
        {
            _isSyncingColor = true;
            try
            {
                if (ColorPaletteHelper.TryParseColor(value, out var parsed))
                {
                    SelectedFloatingWindowTextColor = parsed;
                }
                else if (string.IsNullOrWhiteSpace(value))
                {
                    SelectedFloatingWindowTextColor = Color.FromRgb(32, 32, 32);
                }
            }
            finally
            {
                _isSyncingColor = false;
            }
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            StatusMessage = "随动浮窗已恢复系统默认文字颜色";
        }
        else
        {
            StatusMessage = $"✓ 随动浮窗文字颜色已设置为 {value}";
        }
    }

    private void UpdateFloatingWindowTextColorPreview()
    {
        HasCustomFloatingWindowTextColor = !string.IsNullOrWhiteSpace(FloatingWindowTextColorHex);
        if (HasCustomFloatingWindowTextColor && ColorPaletteHelper.TryParseColor(FloatingWindowTextColorHex, out var c))
        {
            FloatingWindowTextColorPreviewBrush = new SolidColorBrush(c);
        }
        else
        {
            FloatingWindowTextColorPreviewBrush = new SolidColorBrush(Color.FromRgb(32, 32, 32));
        }
    }

    [RelayCommand]
    public void SelectFloatingWindowTextColor(string? hex)
    {
        FloatingWindowTextColorHex = hex;
    }

    [RelayCommand]
    public void ResetFloatingWindowTextColor()
    {
        FloatingWindowTextColorHex = null;
        SelectedFloatingWindowTextColor = Color.FromRgb(32, 32, 32);
    }

    [RelayCommand]
    public async Task PickBackgroundImageAsync()
    {
        string? selectedFile = null;
        if (RequestPickImageFunc != null)
        {
            selectedFile = await RequestPickImageFunc.Invoke();
        }

        if (string.IsNullOrEmpty(selectedFile) || !File.Exists(selectedFile))
        {
            return;
        }

        StatusMessage = "正在处理并压缩背景图片...";
        bool success = await Task.Run(() => _themeService.ApplyNewBackgroundImage(selectedFile));
        if (success)
        {
            StatusMessage = "✓ 背景图片应用成功，已自动优化压缩并生成磨砂毛玻璃图层";
        }
        else
        {
            StatusMessage = "⚠ 背景图片应用失败，请检查图片格式是否受支持";
        }
    }

    [RelayCommand]
    public void RemoveBackgroundImage()
    {
        _themeService.RemoveBackgroundImage();
        StatusMessage = "已移除自定义背景图片并清理缓存";
    }

    [RelayCommand]
    public void ResetToDefaults()
    {
        _themeService.ResetToDefaults();
        BlurRadius = _themeService.BlurRadius;
        ComponentOpacityPercent = Math.Round(_themeService.ComponentOpacity * 100);
        ButtonOpacityPercent = Math.Round(_themeService.ButtonOpacity * 100);
        BackgroundColorHex = null;
        CardColorHex = null;
        TextColorHex = null;
        FloatingWindowBackgroundColorHex = null;
        FloatingWindowTextColorHex = null;
        UpdateBackgroundColorPreview();
        UpdateCardColorPreview();
        UpdateTextColorPreview();
        UpdateFloatingWindowBackgroundColorPreview();
        UpdateFloatingWindowTextColorPreview();
        UpdateBackgroundInfoText();
        StatusMessage = "已恢复所有个性化主题配色与随动浮窗参数为默认值";
    }
}
