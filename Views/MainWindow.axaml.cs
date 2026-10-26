using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShawMuOCR.Core.Hotkey;
using ShawMuOCR.ViewModels;
using ShawMuOCR.Views.Pages;
using ShawMuOCR.Views.Clipboard;

namespace ShawMuOCR.Views;

public partial class MainWindow : Window
{
    private bool _isExplicitExit;
    private readonly MainViewModel? _viewModel;
    private readonly HashSet<AppNavPage> _loadedPages = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage))
            {
                EnsurePageLoaded(_viewModel.CurrentPage);
            }
        };

        Closing += OnClosing;

        // 窗口内全局键盘拦截：使用 Tunnel 策略与 handledEventsToo 确保在任何焦点状态下都能第一时间捕获
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // 全窗口文件/文件夹拖拽支持：使用 Bubble + handledEventsToo 穿透所有子控件（包括 TextBox、侧边栏、标题栏、状态栏等）
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>
    /// 按需动态渲染指定子页面，仅在首次切换到该页面时实例化 View 并装配 DataContext，彻底消除启动阻塞
    /// </summary>
    public void EnsurePageLoaded(AppNavPage page)
    {
        if (_loadedPages.Contains(page) || _viewModel == null) return;
        _loadedPages.Add(page);

        switch (page)
        {
            case AppNavPage.Ocr:
                if (OcrPageHost != null && OcrPageHost.Children.Count == 0)
                    OcrPageHost.Children.Add(new OcrPageView { DataContext = _viewModel });
                break;
            case AppNavPage.Batch:
                if (BatchPageHost != null && BatchPageHost.Children.Count == 0)
                    BatchPageHost.Children.Add(new BatchOcrPageView { DataContext = _viewModel.BatchVm });
                break;
            case AppNavPage.HttpApi:
                if (HttpApiPageHost != null && HttpApiPageHost.Children.Count == 0)
                    HttpApiPageHost.Children.Add(new HttpApiPageView { DataContext = _viewModel.HttpApiVm });
                break;
            case AppNavPage.Capture:
                if (CapturePageHost != null && CapturePageHost.Children.Count == 0)
                    CapturePageHost.Children.Add(new CapturePageView { DataContext = _viewModel });
                break;
            case AppNavPage.Recording:
                if (RecordingPageHost != null && RecordingPageHost.Children.Count == 0)
                    RecordingPageHost.Children.Add(new RecordingPageView { DataContext = _viewModel.RecordingVm });
                break;
            case AppNavPage.Translate:
                if (TranslatePageHost != null && TranslatePageHost.Children.Count == 0)
                    TranslatePageHost.Children.Add(new TranslatePageView { DataContext = _viewModel.TranslateVm });
                break;
            case AppNavPage.Clipboard:
                if (ClipboardPageHost != null && ClipboardPageHost.Children.Count == 0)
                    ClipboardPageHost.Children.Add(new ClipboardView { DataContext = _viewModel.ClipboardVm });
                break;
            case AppNavPage.Theme:
                if (ThemePageHost != null && ThemePageHost.Children.Count == 0)
                    ThemePageHost.Children.Add(new ThemePageView { DataContext = _viewModel.ThemeVm });
                break;
            case AppNavPage.Settings:
                if (SettingsPageHost != null && SettingsPageHost.Children.Count == 0)
                    SettingsPageHost.Children.Add(new SettingsPageView { DataContext = _viewModel.SettingsVm });
                break;
            case AppNavPage.About:
                if (AboutPageHost != null && AboutPageHost.Children.Count == 0)
                    AboutPageHost.Children.Add(new AboutPageView { DataContext = _viewModel.AboutVm });
                break;
            case AppNavPage.Log:
                if (LogPageHost != null && LogPageHost.Children.Count == 0)
                    LogPageHost.Children.Add(new LogPageView { DataContext = _viewModel.LogVm });
                break;
        }
    }

    /// <summary>
    /// 在主窗口完成首帧秒显后，于后台空闲帧平滑逐步预加载其余未呈现的页面，保证后续切换均为 0ms 瞬间响应
    /// </summary>
    public void PreloadRemainingPages()
    {
        foreach (var page in Enum.GetValues<AppNavPage>())
        {
            if (!_loadedPages.Contains(page))
            {
                EnsurePageLoaded(page);
            }
        }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var storageItems = e.DataTransfer.TryGetFiles();
        if (storageItems == null || storageItems.Length == 0) return;

        e.Handled = true;

        var directories = new List<string>();
        var files = new List<string>();

        foreach (var item in storageItems)
        {
            string path = item.Path.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;

            if (Directory.Exists(path))
            {
                directories.Add(path);
            }
            else if (File.Exists(path))
            {
                files.Add(path);
            }
        }

        if (directories.Count == 0 && files.Count == 0) return;

        // 0. 如果当前在剪贴板页面（或文件传输界面）：将文件加入文件传输任务队列，绝不跳转至 OCR
        if (vm.IsClipboardPage)
        {
            if (vm.ClipboardVm != null)
            {
                var allFiles = new List<string>(files);
                foreach (var dir in directories)
                {
                    try
                    {
                        allFiles.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
                    }
                    catch { }
                }

                if (allFiles.Count > 0)
                {
                    vm.ClipboardVm.OpenFileTransferView();
                    vm.ClipboardVm.AddFilesToTransferQueue(allFiles);
                }
            }
            return;
        }

        // 1. 如果当前在批量转换页，或者拖入了文件夹/多个文件：直接加入批量转换队列（若不在批量页且拖入多个/文件夹则自动切到批量页）
        if (vm.CurrentPage == AppNavPage.Batch || directories.Count > 0 || files.Count > 1)
        {
            if (vm.CurrentPage != AppNavPage.Batch)
            {
                vm.NavigateToBatchCommand.Execute(null);
            }

            foreach (var dir in directories)
            {
                vm.BatchVm.AddDirectory(dir);
            }

            if (files.Count > 0)
            {
                vm.BatchVm.AddFiles(files);
            }
            return;
        }

        // 2. 拖入单个文件且当前不在批量转换页：切换到 OCR 识别页并直接识别该文件
        if (files.Count == 1)
        {
            if (vm.CurrentPage != AppNavPage.Ocr)
            {
                vm.NavigateToOcrCommand.Execute(null);
            }

            await vm.RecognizeFileAsync(files[0]);
        }
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

        // 长截屏快捷键
        if (HotkeyGesture.TryParse(vm.CurrentLongScreenshotHotkey, out var longShotGesture) &&
            mod == longShotGesture.Modifiers && vk == longShotGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartLongScreenshotCommand.Execute(null);
            return;
        }

        // 屏幕录制快捷键
        if (HotkeyGesture.TryParse(vm.CurrentRecordingHotkey, out var recGesture) &&
            mod == recGesture.Modifiers && vk == recGesture.VirtualKey)
        {
            e.Handled = true;
            ShawMuOCR.Views.RecordingFloatingWindow.ShowOverlay(vm.RecordingVm);
            vm.RecordingVm.SelectRegionCommand.Execute(null);
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

        // OCR 窗格快捷键
        if (HotkeyGesture.TryParse(vm.CurrentOcrPopupHotkey, out var ocrPopupGesture) &&
            mod == ocrPopupGesture.Modifiers && vk == ocrPopupGesture.VirtualKey)
        {
            e.Handled = true;
            vm.StartOcrPopupCommand.Execute(null);
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
            else if (settingsVm.IsRecordingLongScreenshotHotkey)
            {
                settingsVm.ClearLongScreenshotHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingRecordingHotkey)
            {
                settingsVm.ClearRecordingHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingTranslateHotkey)
            {
                settingsVm.ClearTranslateHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingOcrPopupHotkey)
            {
                settingsVm.ClearOcrPopupHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingQuickNoteHotkey)
            {
                settingsVm.ClearQuickNoteHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingClipboardPanelHotkey)
            {
                settingsVm.ClearClipboardPanelHotkeyCommand.Execute(null);
            }
            else if (settingsVm.IsRecordingPhrasesPanelHotkey)
            {
                settingsVm.ClearPhrasesPanelHotkeyCommand.Execute(null);
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
            else if (settingsVm.IsRecordingLongScreenshotHotkey)
            {
                settingsVm.OnLongScreenshotHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingRecordingHotkey)
            {
                settingsVm.OnRecordingHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingTranslateHotkey)
            {
                settingsVm.OnTranslateHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingOcrPopupHotkey)
            {
                settingsVm.OnOcrPopupHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingQuickNoteHotkey)
            {
                settingsVm.OnQuickNoteHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingClipboardPanelHotkey)
            {
                settingsVm.OnClipboardPanelHotkeyRecorded(gesture);
            }
            else if (settingsVm.IsRecordingPhrasesPanelHotkey)
            {
                settingsVm.OnPhrasesPanelHotkeyRecorded(gesture);
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