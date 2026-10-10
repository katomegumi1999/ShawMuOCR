using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MuClip.Services;
using MuClip.ViewModels;
using MuClip.Views;

namespace MuClip;

public partial class App : Application
{
    private MuClipService? _service;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dbPath = Path.Combine(appData, "MuClip", "muclip.db");

            string randomPin = MuClipService.GenerateRandomPin();
            _service = new MuClipService();
            _service.Initialize(dbPath, startSync: true, syncPort: 42424, pairingPin: randomPin);

            _service.QuickPanelHotkeyTriggered += () =>
            {
                ClipboardQuickPanelWindow.ShowInstance(_service);
            };
            _service.RegisterQuickPanelHotkey();

            var vm = new ClipboardViewModel(_service);
            desktop.MainWindow = new ClipboardStandaloneWindow
            {
                DataContext = vm
            };

            desktop.Exit += (s, e) =>
            {
                _service?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
