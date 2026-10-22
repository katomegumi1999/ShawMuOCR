using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShawMuOCR.Core.Editor;

namespace ShawMuOCR.Views;

/// <summary>
/// 截图标注窗口 - 透明自适应动态扩增内联文字输入框 (Text Editing) 模块
/// </summary>
public partial class ScreenshotEditorWindow
{
    private Point _textInsertPoint;
    private TextElement? _editingTextElement;

    private void StartEditingTextElement(TextElement targetText)
    {
        _editingTextElement = targetText;
        _customFontSize = targetText.FontSize;
        if (FontSizeTextBox != null)
        {
            FontSizeTextBox.Text = ((int)Math.Round(targetText.FontSize)).ToString();
        }
        _currentStrokeWidth = targetText.StrokeWidth;
        if (StrokeSlider != null)
        {
            StrokeSlider.Value = targetText.StrokeWidth;
        }
        ApplyColor(targetText.Color);
        _selectedElement = null;
        UpdateSelectionHighlight();

        // 重新渲染底图：跳过正在编辑的文字要素，避免两层文字重影
        RefreshDisplay();

        // 开启内联输入框并加载原文字
        ShowInlineTextBox(new Point(targetText.X * _displayScale - 4, targetText.Y * _displayScale - 3), targetText.Text);
    }

    private void ShowInlineTextBox(Point pt, string initialText = "")
    {
        InlineTextBox.Text = initialText;

        float fontSize = GetCurrentFontSize();
        InlineTextBox.FontSize = Math.Max(12, fontSize * _displayScale);
        InlineTextBox.FontWeight = _currentStrokeWidth <= 2.5f ? FontWeight.Normal : (_currentStrokeWidth <= 6f ? FontWeight.SemiBold : FontWeight.Bold);

        var avaloniaColor = Color.FromArgb(_currentColor.Alpha, _currentColor.Red, _currentColor.Green, _currentColor.Blue);
        InlineTextBox.Foreground = new SolidColorBrush(avaloniaColor);
        InlineTextBox.CaretBrush = new SolidColorBrush(avaloniaColor);

        // 允许文本框定位超出图片边界，确保超出图片边界时仍可自然输入与显示
        double left = pt.X;
        double top = pt.Y;
        _textInsertPoint = new Point(left, top);

        Canvas.SetLeft(InlineTextBox, left);
        Canvas.SetTop(InlineTextBox, top);

        AdjustInlineTextBoxSize();
        InlineTextBox.IsVisible = true;

        ReactivateWindowAndIme();
        InlineTextBox.Focus();
        if (!string.IsNullOrEmpty(initialText))
        {
            InlineTextBox.CaretIndex = initialText.Length;
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            ReactivateWindowAndIme();
            InlineTextBox.Focus();
            if (!string.IsNullOrEmpty(initialText))
            {
                InlineTextBox.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    private void OnInlineTextBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        AdjustInlineTextBoxSize();
    }

    private void AdjustInlineTextBoxSize()
    {
        string text = InlineTextBox.Text ?? string.Empty;
        var lines = text.Split('\n');
        double fontSize = InlineTextBox.FontSize;
        double lineH = fontSize * 1.35;

        double maxLineLen = 0;
        foreach (var line in lines)
        {
            double lineW = 0;
            foreach (char ch in line)
            {
                lineW += (ch > 127) ? fontSize : (fontSize * 0.55);
            }
            if (lineW > maxLineLen) maxLineLen = lineW;
        }

        double w = Math.Max(50, maxLineLen + fontSize + 20);
        double h = Math.Max(fontSize * 1.4, lines.Length * lineH + 12);

        InlineTextBox.Width = w;
        InlineTextBox.Height = h;
    }

    private void CommitInlineText()
    {
        if (!InlineTextBox.IsVisible) return;

        string text = InlineTextBox.Text?.Trim() ?? string.Empty;
        InlineTextBox.IsVisible = false;

        // 如果是重新编辑既有文字要素
        if (_editingTextElement != null)
        {
            var target = _editingTextElement;
            _editingTextElement = null;

            if (string.IsNullOrEmpty(text))
            {
                _doc.RemoveElement(target);
                _selectedElement = null;
            }
            else
            {
                var oldState = target.Clone() as TextElement;
                target.Text = text;
                target.FontSize = GetCurrentFontSize();
                target.Color = _currentColor;
                target.StrokeWidth = _currentStrokeWidth;
                target.IsBold = _currentStrokeWidth > 2.5f;
                if (oldState != null && (oldState.Text != target.Text || Math.Abs(oldState.FontSize - target.FontSize) > 0.1f || oldState.Color != target.Color || Math.Abs(oldState.StrokeWidth - target.StrokeWidth) > 0.1f))
                {
                    _doc.UndoRedo.Execute(new TransformElementCommand(target, oldState, target.Clone()));
                }
                _selectedElement = target;
            }

            RefreshDisplay();
            UpdateSelectionHighlight();
            return;
        }

        if (!string.IsNullOrEmpty(text))
        {
            float imgX = (float)(_textInsertPoint.X / _displayScale);
            float imgY = (float)(_textInsertPoint.Y / _displayScale);

            _doc.AddElement(new TextElement
            {
                X = imgX,
                Y = imgY,
                Text = text,
                Color = _currentColor,
                FontSize = GetCurrentFontSize(),
                StrokeWidth = _currentStrokeWidth,
                IsBold = _currentStrokeWidth > 2.5f
            });

            RefreshDisplay();
        }
    }

    private void OnInlineTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitInlineText();
    }

    private void OnInlineTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        // 按 Enter 直接提交保存（Shift+Enter 允许换行）
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            CommitInlineText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CommitInlineText();
            e.Handled = true;
        }
    }
}
