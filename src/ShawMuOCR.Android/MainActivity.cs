using System;
using System.IO;
using System.Threading.Tasks;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using ShawMuOCR.Android.Platform;

using AndroidUri = global::Android.Net.Uri;

namespace ShawMuOCR.Android;

[Activity(
    Label = "小沐 OCR",
    Theme = "@style/MainTheme.NoActionBar",
    Icon = "@android:drawable/sym_def_app_icon",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : AvaloniaMainActivity, IAndroidPlatformBridge
{
    private const int RequestCodePickFile = 2001;
    private const int RequestCodeCamera = 2002;
    private const int RequestCodeCameraPermission = 3001;

    private TaskCompletionSource<AndroidUri?>? _filePickerTcs;
    private TaskCompletionSource<ImageBuffer?>? _cameraTcs;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidCrashLogger.Log("[LIFECYCLE] MainActivity.OnCreate 开始执行");
        AndroidPlatformBridge.Instance.SetActivity(this);

        try
        {
            base.OnCreate(savedInstanceState);
            AndroidCrashLogger.Log("[LIFECYCLE] MainActivity.OnCreate 基类初始化成功");
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.LogException(ex, "MainActivity.OnCreate base.OnCreate 异常");
            throw;
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidPlatformBridge.Instance.SetActivity(this);
    }

    protected override void OnDestroy()
    {
        AndroidPlatformBridge.Instance.ClearActivity(this);
        base.OnDestroy();
    }

    public Task<AndroidUri?> PickDocumentAsync() => PickDocumentInternalAsync();
    public Task<ImageBuffer?> CapturePhotoAsync() => CapturePhotoInternalAsync();
    public string GetFileName(AndroidUri uri) => GetFileNameInternal(uri);
    public bool IsPdf(AndroidUri uri) => IsPdfInternal(uri);
    public void CopyToClipboard(string text) => CopyToClipboardInternal(text);

    internal Task<AndroidUri?> PickDocumentInternalAsync()
    {
        _filePickerTcs?.TrySetCanceled();
        _filePickerTcs = new TaskCompletionSource<AndroidUri?>();

        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[] { "image/*", "application/pdf" });

        StartActivityForResult(intent, RequestCodePickFile);
        return _filePickerTcs.Task;
    }

    private AndroidUri? _cameraOutputUri;

    internal async Task<ImageBuffer?> CapturePhotoInternalAsync()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.M)
        {
            if (CheckSelfPermission(Manifest.Permission.Camera) != Permission.Granted)
            {
                RequestPermissions(new[] { Manifest.Permission.Camera }, RequestCodeCameraPermission);
            }
        }

        _cameraTcs?.TrySetCanceled();
        _cameraTcs = new TaskCompletionSource<ImageBuffer?>();

        var intent = new Intent(MediaStore.ActionImageCapture);
        try
        {
            // 通过 MediaStore 预分配全高清拍照输出 Uri，彻底告别低分辨率缩略图
            var values = new ContentValues();
            values.Put(MediaStore.Images.Media.InterfaceConsts.Title, $"ShawMuOCR_{DateTime.Now:yyyyMMdd_HHmmss}");
            values.Put(MediaStore.Images.Media.InterfaceConsts.MimeType, "image/jpeg");
            _cameraOutputUri = ContentResolver?.Insert(MediaStore.Images.Media.ExternalContentUri!, values);

            if (_cameraOutputUri != null)
            {
                intent.PutExtra(MediaStore.ExtraOutput, _cameraOutputUri);
            }
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.Log($"[CAMERA] 预分配高清拍照 Uri 失败，将回退: {ex.Message}");
            _cameraOutputUri = null;
        }

        if (intent.ResolveActivity(PackageManager!) != null)
        {
            StartActivityForResult(intent, RequestCodeCamera);
        }
        else
        {
            _cameraTcs.TrySetResult(null);
        }

        return await _cameraTcs.Task;
    }

    internal string GetFileNameInternal(AndroidUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            using var cursor = ContentResolver?.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int nameIndex = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
                if (nameIndex >= 0)
                {
                    string? displayName = cursor.GetString(nameIndex);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        return displayName;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AndroidCrashLogger.Log($"[SAF] 查询文件名异常: {ex.Message}");
        }

        return uri.LastPathSegment ?? "未知文件";
    }

    internal bool IsPdfInternal(AndroidUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        try
        {
            string? mime = ContentResolver?.GetType(uri);
            if (!string.IsNullOrWhiteSpace(mime) && mime.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }

        string fileName = GetFileName(uri);
        return fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
    }

    internal void CopyToClipboardInternal(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        var clip = ClipData.NewPlainText("ShawMuOCR", text);
        if (clipboard != null && clip != null)
        {
            clipboard.PrimaryClip = clip;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == RequestCodePickFile)
        {
            if (resultCode == Result.Ok && data?.Data != null)
            {
                _filePickerTcs?.TrySetResult(data.Data);
            }
            else
            {
                _filePickerTcs?.TrySetResult(null);
            }
        }
        else if (requestCode == RequestCodeCamera)
        {
            var targetUri = _cameraOutputUri;
            _cameraOutputUri = null;

            if (resultCode == Result.Ok)
            {
                try
                {
                    ImageBuffer? buffer = null;

                    // 1. 优先使用预分配的高清原图 Uri
                    if (targetUri != null)
                    {
                        buffer = AndroidImageDecoder.DecodeFromUri(this, targetUri);
                    }

                    // 2. 兜底策略：若未拿到 Uri 则尝试从返回数据获取
                    if (buffer == null && data != null)
                    {
                        if (data.Data != null)
                        {
                            buffer = AndroidImageDecoder.DecodeFromUri(this, data.Data);
                        }
                        else if (data.Extras != null && data.Extras.ContainsKey("data"))
                        {
                            var bmp = data.Extras.Get("data") as Bitmap;
                            if (bmp != null)
                            {
                                buffer = AndroidImageDecoder.ConvertBitmapToBuffer(bmp);
                                bmp.Recycle();
                                bmp.Dispose();
                            }
                        }
                    }

                    _cameraTcs?.TrySetResult(buffer);
                }
                catch (Exception ex)
                {
                    AndroidCrashLogger.LogException(ex, "相机拍照结果解码异常");
                    _cameraTcs?.TrySetException(ex);
                }
                finally
                {
                    // 零污染：读取完毕后立即从媒体库移除该临时抓拍记录，杜绝相册垃圾堆积与内存落盘
                    if (targetUri != null)
                    {
                        try { ContentResolver?.Delete(targetUri, null, null); } catch { }
                    }
                }
            }
            else
            {
                if (targetUri != null)
                {
                    try { ContentResolver?.Delete(targetUri, null, null); } catch { }
                }
                _cameraTcs?.TrySetResult(null);
            }
        }
    }
}
