using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views;

public partial class MainWindow : Window
{
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        DataContext = viewModel;

        Closing += OnClosing;

        // 窗口内全局键盘拦截：使用 Tunnel 策略与 handledEventsToo 确保在任何焦点状态下都能第一时间捕获
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // 1. 若当前处于系统设置页且正在录制快捷键，则优先处理快捷键录制
        if (vm.CurrentPage == AppNavPage.Settings && vm.SettingsVm.IsRecordingAnyHotkey)
        {
            HandleSettingsHotkeyRecording(vm.SettingsVm, e);
            return;
        }

        // 2. 窗口内快捷键触发：检查是否按下了截图 OCR、截图复制或截图标注
        var mod = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mod |= HotkeyModifiers.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mod |= HotkeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mod |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) mod |= HotkeyModifiers.Win;

        int vk = ConvertAvaloniaKeyToVk(e.Key);
        if (vk == 0) return;

        // 截图 OCR 快捷键
        if (HotkeyGesture.TryParse(vm.CurrentHotkey, out var ocrGesture) &&
            mod == ocrGesture.Modifiers && vk == ocrGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartCaptureCommand.Execute(null);
            return;
        }

        // 截图复制快捷键
        if (HotkeyGesture.TryParse(vm.CurrentCaptureImageHotkey, out var capGesture) &&
            mod == capGesture.Modifiers && vk == capGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartCaptureImageCommand.Execute(null);
            return;
        }

        // 截图标注快捷键
        if (HotkeyGesture.TryParse(vm.CurrentAnnotateHotkey, out var annGesture) &&
            mod == annGesture.Modifiers && vk == annGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartAnnotateCommand.Execute(null);
            return;
        }

        // 选区翻译快捷键
        if (HotkeyGesture.TryParse(vm.CurrentTranslateHotkey, out var transGesture) &&
            mod == transGesture.Modifiers && vk == transGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartTranslateSelectionCommand.Execute(null);
            return;
        }
    }

    private static void HandleSettingsHotkeyRecording(SettingsViewModel settingsVm, KeyEventArgs e)
    {
        // 按 Esc 键：取消录制
        if (e.Key == Key.Escape)
        {
            settingsVm.CancelRecording();
            e.Handled = true;
            return;
        }

        // 无修饰键按下 Backspace 或 Delete：清空当前正在录制的快捷键 (禁用)
        if (e.KeyModifiers == KeyModifiers.None && (e.Key is Key.Back or Key.Delete))
        {
            if (settingsVm.IsRecordingHotkey)
            {
                settingsVm.ClearHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingCaptureImageHotkey)
            {
                settingsVm.ClearCaptureImageHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingAnnotateHotkey)
            {
                settingsVm.ClearAnnotateHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingTranslateHotkey)
            {
                settingsVm.ClearTranslateHotkeyCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // 忽略单纯修饰键按下（等待主键输入）
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or
            Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or
            Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifiers.Win;

        int vk = ConvertAvaloniaKeyToVk(e.Key);
        if (vk != 0)
        {
            var gesture = new HotkeyGesture(modifiers, vk);
            if (settingsVm.IsRecordingHotkey)
            {
                settingsVm.OnHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingCaptureImageHotkey)
            {
                settingsVm.OnCaptureImageHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingAnnotateHotkey)
            {
                settingsVm.OnAnnotateHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingTranslateHotkey)
            {
                settingsVm.OnTranslateHotkeyRecorded(gesture);
            }
            e.Handled = true;
        }
    }

    private static int ConvertAvaloniaKeyToVk(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return 0x41 + (key - Key.A);
        }
        if (key >= Key.D0 && key <= Key.D9)
        {
            return 0x30 + (key - Key.D0);
        }
        if (key >= Key.F1 && key <= Key.F24)
        {
            return 0x70 + (key - Key.F1);
        }
        if (key >= Key.NumPad0 && key <= Key.NumPad9)
        {
            return 0x60 + (key - Key.NumPad0);
        }

        return key switch
        {
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.Escape => 0x1B,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.Oem1 => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.Oem2 => 0xBF,
            Key.Oem3 => 0xC0,
            Key.Oem4 => 0xDB,
            Key.Oem5 => 0xDC,
            Key.Oem6 => 0xDD,
            Key.Oem7 => 0xDE,
            _ => 0
        };
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // 遵循规划文档第十八节：关闭主窗口时隐藏至托盘，不退出程序
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ExplicitExit()
    {
        _isExplicitExit = true;
        Close();
    }
}