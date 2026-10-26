using CommunityToolkit.Mvvm.ComponentModel;
using MuRecorder.Windows;

namespace MuRecorder.ViewModels;

/// <summary>
/// 支持多选复选框绑定的窗口项包装。
/// </summary>
public partial class SelectableWindowItem : ObservableObject
{
    private readonly Action? _onSelectionChanged;

    public WindowInfo Window { get; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged?.Invoke();
    }

    public SelectableWindowItem(WindowInfo window, bool isSelected = false, Action? onSelectionChanged = null)
    {
        Window = window;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public override string ToString() => Window.ToString();
}
