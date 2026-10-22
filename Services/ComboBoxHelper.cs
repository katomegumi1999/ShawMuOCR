using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ShawMuOCR.Services;

/// <summary>
/// ComboBox 滚轮行为辅助类：
/// 解决鼠标指针悬停在 ComboBox 上时滚动滚轮意外改变选中项的问题。
/// 当下拉框未展开时，阻止 ComboBox 改变选中项，并将滚轮平滑传递给外层 ScrollViewer。
/// </summary>
public static class ComboBoxHelper
{
    public static readonly AttachedProperty<bool> DisableWheelChangeProperty =
        AvaloniaProperty.RegisterAttached<ComboBox, bool>(
            "DisableWheelChange",
            typeof(ComboBoxHelper),
            defaultValue: false);

    public static bool GetDisableWheelChange(AvaloniaObject element) =>
        element.GetValue(DisableWheelChangeProperty);

    public static void SetDisableWheelChange(AvaloniaObject element, bool value) =>
        element.SetValue(DisableWheelChangeProperty, value);

    static ComboBoxHelper()
    {
        DisableWheelChangeProperty.Changed.AddClassHandler<ComboBox>((comboBox, args) =>
        {
            comboBox.RemoveHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged);
            if (args.NewValue is true)
            {
                comboBox.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel);
            }
        });
    }

    private static void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is ComboBox comboBox && !comboBox.IsDropDownOpen)
        {
            // 标记已处理，阻止 ComboBox 内部触发前后项切换
            e.Handled = true;

            // 联动向上寻找父级 ScrollViewer 并执行平滑滚动，保持页面浏览连贯性
            var scrollViewer = comboBox.FindAncestorOfType<ScrollViewer>();
            if (scrollViewer != null)
            {
                const double scrollStep = 50.0;
                double maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                double newY = Math.Clamp(scrollViewer.Offset.Y - (e.Delta.Y * scrollStep), 0, maxOffsetY);
                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, newY);
            }
        }
    }
}
