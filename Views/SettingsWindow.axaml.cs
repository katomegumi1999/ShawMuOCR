using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public SettingsWindow(SettingsViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        viewModel.RequestOpenAbout += OnRequestOpenAbout;
        viewModel.RequestPickModelFolder += OnRequestPickModelFolder;
    }

    private async Task<string?> OnRequestPickModelFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 PaddleOCR ONNX 模型的文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            return folders[0].TryGetLocalPath();
        }

        return null;
    }

    private void OnRequestOpenAbout(AboutViewModel aboutVm)
    {
        var aboutWindow = new AboutWindow(aboutVm);
        aboutWindow.ShowDialog(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (DataContext is SettingsViewModel vm)
        {
            vm.CancelRecording();
            vm.RequestPickModelFolder -= OnRequestPickModelFolder;
        }
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || !vm.IsRecordingAnyHotkey)
        {
            return;
        }

        // 1. 按 Esc 键：取消录制
        if (e.Key == Key.Escape)
        {
            vm.CancelRecording();
            e.Handled = true;
            return;
        }

        // 2. 无修饰键按下 Backspace 或 Delete：清空当前正在录制的快捷键 (禁用)
        if (e.KeyModifiers == KeyModifiers.None && (e.Key is Key.Back or Key.Delete))
        {
            if (vm.IsRecordingHotkey)
            {
                vm.ClearHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingCaptureImageHotkey)
            {
                vm.ClearCaptureImageHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingAnnotateHotkey)
            {
                vm.ClearAnnotateHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingLongScreenshotHotkey)
            {
                vm.ClearLongScreenshotHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingRecordingHotkey)
            {
                vm.ClearRecordingHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingTranslateHotkey)
            {
                vm.ClearTranslateHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingClipboardPanelHotkey)
            {
                vm.ClearClipboardPanelHotkeyCommand.Execute(null);
            }
            else if (vm.IsRecordingPhrasesPanelHotkey)
            {
                vm.ClearPhrasesPanelHotkeyCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }

        // 3. 忽略单纯修饰键按下（等待主键输入）
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
            if (vm.IsRecordingHotkey)
            {
                vm.OnHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingCaptureImageHotkey)
            {
                vm.OnCaptureImageHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingAnnotateHotkey)
            {
                vm.OnAnnotateHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingLongScreenshotHotkey)
            {
                vm.OnLongScreenshotHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingRecordingHotkey)
            {
                vm.OnRecordingHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingTranslateHotkey)
            {
                vm.OnTranslateHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingClipboardPanelHotkey)
            {
                vm.OnClipboardPanelHotkeyRecorded(gesture);
            }
            else if (vm.IsRecordingPhrasesPanelHotkey)
            {
                vm.OnPhrasesPanelHotkeyRecorded(gesture);
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
}
