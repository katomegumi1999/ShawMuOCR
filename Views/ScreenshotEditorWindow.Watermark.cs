using System;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ShawMuOCR.Core.Editor;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 防伪/版权水印生成与调节 (Watermark) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    private void OnCancelWatermarkClick(object? sender, RoutedEventArgs e)
    {
        WatermarkBtn.Flyout?.Hide();
    }

    private void OnWatermarkTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirmWatermarkClick(sender, e);
            e.Handled = true;
        }
    }

    private void OnWatermarkFontSizeSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (WatermarkAutoFontSizeCheck != null && WatermarkFontSizeText != null)
        {
            if (WatermarkAutoFontSizeCheck.IsChecked == true)
            {
                WatermarkAutoFontSizeCheck.IsChecked = false;
            }
            WatermarkFontSizeText.Text = $"{(int)e.NewValue}px";
        }
    }

    private void OnWatermarkAutoFontSizeCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (WatermarkFontSizeText != null && WatermarkFontSizeSlider != null)
        {
            if (WatermarkAutoFontSizeCheck.IsChecked == true)
            {
                WatermarkFontSizeText.Text = "自动";
                WatermarkFontSizeSlider.Opacity = 0.5;
            }
            else
            {
                WatermarkFontSizeText.Text = $"{(int)WatermarkFontSizeSlider.Value}px";
                WatermarkFontSizeSlider.Opacity = 1.0;
            }
        }
    }

    private void OnConfirmWatermarkClick(object? sender, RoutedEventArgs e)
    {
        WatermarkBtn.Flyout?.Hide();
        string text = WatermarkTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ToastNotificationWindow.Show("请输入有效的水印文字", TimeSpan.FromSeconds(2), "💧 水印内容为空");
            return;
        }

        bool isTiled = WatermarkTileRadio.IsChecked == true;
        float opacity = (float)(WatermarkOpacitySlider.Value / 100.0);
        float fontSize = (WatermarkAutoFontSizeCheck.IsChecked == true) ? 0f : (float)WatermarkFontSizeSlider.Value;
        float density = (float)WatermarkDensitySlider.Value;

        var wm = new WatermarkElement
        {
            Text = text,
            Color = _currentColor,
            FontSize = fontSize,
            Density = density,
            Opacity = opacity,
            IsTiled = isTiled,
            ImageWidth = _doc.OriginalImage.Width,
            ImageHeight = _doc.OriginalImage.Height,
            Rotation = isTiled ? -28f : -30f
        };

        _doc.AddElement(wm);
        RefreshDisplay();
        ToastNotificationWindow.Show("防伪水印已成功覆盖至图片 (可按 Ctrl+Z 撤销)", TimeSpan.FromSeconds(2), "💧 水印添加成功");
    }
}
