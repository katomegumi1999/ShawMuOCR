using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MuRecorder.Audio;

public enum AudioDeviceFlow
{
    /// <summary>音频输出/渲染端点（扬声器、耳机，用于系统声音 Loopback 录制）。</summary>
    Render = 0,
    /// <summary>音频输入/捕获端点（麦克风、线路输入，用于人声讲解）。</summary>
    Capture = 1,
}

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    AudioDeviceFlow Flow,
    bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (系统默认)" : Name;
}

/// <summary>
/// Windows Core Audio (MMDevice) 音频端点枚举器。
/// 采用纯 Win32 / COM VTable 原生调用，完全兼容 .NET Native AOT，无 ComImport 与反射依赖。
/// 安全枚举系统扬声器与麦克风设备，标记录音/播放默认设备。
/// </summary>
public static unsafe partial class AudioDeviceEnumerator
{
    private const uint CLSCTX_ALL = 0x17;
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int STGM_READ = 0x00000000;
    private const ushort VT_LPWSTR = 31;
    private const uint COINIT_APARTMENTTHREADED = 0x2;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    // PKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [LibraryImport("ole32.dll")]
    private static partial int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>枚举所有活跃的音频输出设备（扬声器/耳机）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices() =>
        EnumerateEndpoints(AudioDeviceFlow.Render);

    /// <summary>枚举所有活跃的音频输入设备（麦克风）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices() =>
        EnumerateEndpoints(AudioDeviceFlow.Capture);

    /// <summary>枚举所有活跃的音频端点（先输出后输入）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateAllDevices()
    {
        var all = new List<AudioDeviceInfo>();
        all.AddRange(EnumerateRenderDevices());
        all.AddRange(EnumerateCaptureDevices());
        return all;
    }

    private static IReadOnlyList<AudioDeviceInfo> EnumerateEndpoints(AudioDeviceFlow flow)
    {
        var result = new List<AudioDeviceInfo>();

        // 确保调用线程具备 COM 环境（支持 STA/MTA 重入）
        int initHr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        bool shouldUninit = initHr == 0 /* S_OK */ || initHr == 1 /* S_FALSE */;

        try
        {
            int hr = CoCreateInstance(in CLSID_MMDeviceEnumerator, IntPtr.Zero, CLSCTX_ALL, in IID_IMMDeviceEnumerator, out IntPtr pEnumerator);
            if (hr != 0 || pEnumerator == IntPtr.Zero)
            {
                return result;
            }

            try
            {
                var enumVtbl = *(IntPtr**)pEnumerator;

                // 1. 获取系统默认端点 ID
                string? defaultId = null;
                var getDefaultEndpoint = (delegate* unmanaged[Stdcall]<IntPtr, int, int, out IntPtr, int>)enumVtbl[4];
                if (getDefaultEndpoint(pEnumerator, (int)flow, 0 /* eConsole */, out IntPtr pDefaultDev) == 0 && pDefaultDev != IntPtr.Zero)
                {
                    try
                    {
                        var devVtbl = *(IntPtr**)pDefaultDev;
                        var getId = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)devVtbl[5];
                        if (getId(pDefaultDev, out IntPtr pstrId) == 0 && pstrId != IntPtr.Zero)
                        {
                            defaultId = Marshal.PtrToStringUni(pstrId);
                            Marshal.FreeCoTaskMem(pstrId);
                        }
                    }
                    finally
                    {
                        Marshal.Release(pDefaultDev);
                    }
                }

                // 2. 枚举所有活跃端点集合
                var enumEndpoints = (delegate* unmanaged[Stdcall]<IntPtr, int, int, out IntPtr, int>)enumVtbl[3];
                if (enumEndpoints(pEnumerator, (int)flow, DEVICE_STATE_ACTIVE, out IntPtr pCollection) == 0 && pCollection != IntPtr.Zero)
                {
                    try
                    {
                        var collVtbl = *(IntPtr**)pCollection;
                        var getCount = (delegate* unmanaged[Stdcall]<IntPtr, out uint, int>)collVtbl[3];
                        var getItem = (delegate* unmanaged[Stdcall]<IntPtr, uint, out IntPtr, int>)collVtbl[4];

                        if (getCount(pCollection, out uint count) == 0)
                        {
                            for (uint i = 0; i < count; i++)
                            {
                                if (getItem(pCollection, i, out IntPtr pDevice) == 0 && pDevice != IntPtr.Zero)
                                {
                                    try
                                    {
                                        var devVtbl = *(IntPtr**)pDevice;
                                        var getId = (delegate* unmanaged[Stdcall]<IntPtr, out IntPtr, int>)devVtbl[5];
                                        if (getId(pDevice, out IntPtr pstrId) == 0 && pstrId != IntPtr.Zero)
                                        {
                                            string devId = Marshal.PtrToStringUni(pstrId) ?? string.Empty;
                                            Marshal.FreeCoTaskMem(pstrId);

                                            if (!string.IsNullOrEmpty(devId))
                                            {
                                                string name = GetDeviceFriendlyName(pDevice) ?? (flow == AudioDeviceFlow.Render ? $"扬声器设备 {i + 1}" : $"麦克风设备 {i + 1}");
                                                bool isDefault = string.Equals(devId, defaultId, StringComparison.OrdinalIgnoreCase);
                                                result.Add(new AudioDeviceInfo(devId, name, flow, isDefault));
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.Release(pDevice);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(pCollection);
                    }
                }
            }
            finally
            {
                Marshal.Release(pEnumerator);
            }
        }
        catch
        {
            // 在无声卡或特权受限环境下安全降级，不阻断主流程
        }
        finally
        {
            if (shouldUninit)
            {
                CoUninitialize();
            }
        }

        // 将默认设备排在最前
        result.Sort((a, b) => b.IsDefault.CompareTo(a.IsDefault));
        return result;
    }

    private static string? GetDeviceFriendlyName(IntPtr pDevice)
    {
        var devVtbl = *(IntPtr**)pDevice;
        var openStore = (delegate* unmanaged[Stdcall]<IntPtr, int, out IntPtr, int>)devVtbl[4];
        if (openStore(pDevice, STGM_READ, out IntPtr pStore) == 0 && pStore != IntPtr.Zero)
        {
            try
            {
                var storeVtbl = *(IntPtr**)pStore;
                var getValue = (delegate* unmanaged[Stdcall]<IntPtr, in PROPERTYKEY, out PROPVARIANT, int>)storeVtbl[5];
                var key = PKEY_Device_FriendlyName;
                if (getValue(pStore, in key, out PROPVARIANT pv) == 0)
                {
                    try
                    {
                        if (pv.vt == VT_LPWSTR && pv.pwszVal != IntPtr.Zero)
                        {
                            return Marshal.PtrToStringUni(pv.pwszVal);
                        }
                    }
                    finally
                    {
                        PropVariantClear(ref pv);
                    }
                }
            }
            finally
            {
                Marshal.Release(pStore);
            }
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)]
        public ushort vt;
        [FieldOffset(2)]
        public ushort wReserved1;
        [FieldOffset(4)]
        public ushort wReserved2;
        [FieldOffset(6)]
        public ushort wReserved3;
        [FieldOffset(8)]
        public IntPtr pwszVal;
    }
}
