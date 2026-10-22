using System;
using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using ShawMuOCR.Android.Platform;

namespace ShawMuOCR.Android;

[Application(
    Label = "小沐 OCR",
    Icon = "@android:drawable/sym_def_app_icon",
    Theme = "@style/MainTheme.NoActionBar",
    HardwareAccelerated = true,
    LargeHeap = true)]
public class MainApplication : AvaloniaAndroidApplication<App>
{
    public MainApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer)
    {
    }

    public override void OnCreate()
    {
        try
        {
            AndroidCrashLogger.Initialize(this);
            AndroidCrashLogger.Log("[LIFECYCLE] MainApplication.OnCreate 开始执行");
            AndroidPlatformBridge.Instance.Initialize(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainApplication] 日志初始化异常: {ex}");
        }

        base.OnCreate();
        AndroidCrashLogger.Log("[LIFECYCLE] MainApplication.OnCreate 执行完毕");
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AndroidCrashLogger.Log("[LIFECYCLE] CustomizeAppBuilder 配置字体与样式");
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
