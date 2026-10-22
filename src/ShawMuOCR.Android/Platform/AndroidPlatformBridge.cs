using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using AndroidUri = global::Android.Net.Uri;
using AndroidContext = global::Android.Content.Context;

namespace ShawMuOCR.Android.Platform;

/// <summary>
/// 解耦的 Android 原生能力桥接器单例
/// 避免 App 初始化阶段因 MainActivity 尚未创建而导致闪退崩溃
/// </summary>
public sealed class AndroidPlatformBridge : IAndroidPlatformBridge
{
    private static readonly AndroidPlatformBridge s_instance = new();
    public static AndroidPlatformBridge Instance => s_instance;

    private WeakReference<MainActivity>? _activeActivityRef;
    private AndroidContext? _applicationContext;

    private AndroidPlatformBridge() { }

    public void Initialize(AndroidContext appContext)
    {
        _applicationContext = appContext ?? throw new ArgumentNullException(nameof(appContext));
    }

    public void SetActivity(MainActivity activity)
    {
        _activeActivityRef = new WeakReference<MainActivity>(activity);
        if (_applicationContext == null && activity != null)
        {
            _applicationContext = activity.ApplicationContext;
        }
    }

    public void ClearActivity(MainActivity activity)
    {
        if (_activeActivityRef != null && _activeActivityRef.TryGetTarget(out var current) && ReferenceEquals(current, activity))
        {
            _activeActivityRef = null;
        }
    }

    public MainActivity? GetActiveActivity()
    {
        if (_activeActivityRef != null && _activeActivityRef.TryGetTarget(out var activity))
        {
            return activity;
        }
        return null;
    }

    public AndroidContext GetActiveContext()
    {
        return (AndroidContext?)GetActiveActivity() ?? _applicationContext ?? Application.Context;
    }

    public Task<AndroidUri?> PickDocumentAsync()
    {
        var act = GetActiveActivity() ?? throw new InvalidOperationException("当前无前台活跃的 Activity，无法拉起文件选择器");
        return act.PickDocumentInternalAsync();
    }

    public Task<ImageBuffer?> CapturePhotoAsync()
    {
        var act = GetActiveActivity() ?? throw new InvalidOperationException("当前无前台活跃的 Activity，无法拉起相机");
        return act.CapturePhotoInternalAsync();
    }

    public string GetFileName(AndroidUri uri)
    {
        var act = GetActiveActivity();
        if (act != null)
        {
            return act.GetFileNameInternal(uri);
        }
        return uri.LastPathSegment ?? "未知文件";
    }

    public bool IsPdf(AndroidUri uri)
    {
        var act = GetActiveActivity();
        if (act != null)
        {
            return act.IsPdfInternal(uri);
        }
        string fileName = GetFileName(uri);
        return fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public void CopyToClipboard(string text)
    {
        var act = GetActiveActivity();
        if (act != null)
        {
            act.CopyToClipboardInternal(text);
        }
        else
        {
            var clipboard = (ClipboardManager?)GetActiveContext().GetSystemService(AndroidContext.ClipboardService);
            var clip = ClipData.NewPlainText("ShawMuOCR", text);
            if (clipboard != null && clip != null)
            {
                clipboard.PrimaryClip = clip;
            }
        }
    }
}
