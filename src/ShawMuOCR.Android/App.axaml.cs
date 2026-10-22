using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ShawMuOCR.Android.OCR;
using ShawMuOCR.Android.Platform;
using ShawMuOCR.Android.ViewModels;
using ShawMuOCR.Android.Views;

namespace ShawMuOCR.Android;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        try
        {
            AndroidCrashLogger.Log("[LIFECYCLE] App.Initialize 开始加载 XAML 资源");
            AvaloniaXamlLoader.Load(this);
            AndroidCrashLogger.Log("[LIFECYCLE] App.Initialize XAML 资源加载完成");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "App.Initialize 异常");
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            AndroidCrashLogger.Log("[LIFECYCLE] App.OnFrameworkInitializationCompleted 开始");

            if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                var ocrService = new AndroidOcrService();
                var bridge = AndroidPlatformBridge.Instance;
                var fileOcrVm = new FileOcrViewModel(ocrService, bridge, () => AndroidPlatformBridge.Instance.GetActiveContext());
                var cameraOcrVm = new CameraOcrViewModel(ocrService, bridge);
                var settingsVm = new SettingsViewModel(ocrService);

                var mainVm = new MainViewModel(fileOcrVm, cameraOcrVm, settingsVm);

                singleView.MainView = new MainView
                {
                    DataContext = mainVm
                };

                AndroidCrashLogger.Log("[LIFECYCLE] singleView.MainView 构建完成并绑定 DataContext");
            }

            base.OnFrameworkInitializationCompleted();
            AndroidCrashLogger.Log("[LIFECYCLE] App.OnFrameworkInitializationCompleted 完成");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "App.OnFrameworkInitializationCompleted 异常");
            throw;
        }
    }
}
