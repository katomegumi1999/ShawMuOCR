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
/// 安全枚举系统扬声器与麦克风设备，支持标记录音/播放默认设备。
/// </summary>
public static class AudioDeviceEnumerator
{
    private const int DEVICE_STATE_ACTIVE = 0x00000001;
    private const int STGM_READ = 0x00000000;
    private const ushort VT_LPWSTR = 31;

    // PKEY_Device_FriendlyName: {A45C254E-DF1C-4EFD-8020-67D146A850E0}, 14
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid = 14
    };

    /// <summary>枚举所有活跃的音频输出设备（扬声器/耳机）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateRenderDevices() =>
        EnumerateEndpoints(AudioDeviceFlow.Render);

    /// <summary>枚举所有活跃的音频输入设备（麦克风）。</summary>
    public static IReadOnlyList<AudioDeviceInfo> EnumerateCaptureDevices() =>
        EnumerateEndpoints(AudioDeviceFlow.Capture);

    private static IReadOnlyList<AudioDeviceInfo> EnumerateEndpoints(AudioDeviceFlow flow)
    {
        var result = new List<AudioDeviceInfo>();

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return result;

            var comObj = Activator.CreateInstance(enumeratorType);
            if (comObj is not IMMDeviceEnumerator enumerator) return result;

            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint((int)flow, 0 /* eConsole */, out var defaultDev) == 0 && defaultDev != null)
            {
                try
                {
                    if (defaultDev.GetId(out var defId) == 0)
                    {
                        defaultId = defId;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(defaultDev);
                }
            }

            if (enumerator.EnumAudioEndpoints((int)flow, DEVICE_STATE_ACTIVE, out var collection) == 0 && collection != null)
            {
                try
                {
                    if (collection.GetCount(out uint count) == 0)
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            if (collection.Item(i, out var device) == 0 && device != null)
                            {
                                try
                                {
                                    if (device.GetId(out var devId) == 0 && !string.IsNullOrEmpty(devId))
                                    {
                                        string name = GetDeviceFriendlyName(device) ?? $"音频设备 {i + 1}";
                                        bool isDefault = string.Equals(devId, defaultId, StringComparison.OrdinalIgnoreCase);
                                        result.Add(new AudioDeviceInfo(devId, name, flow, isDefault));
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(device);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(collection);
                }
            }

            Marshal.ReleaseComObject(enumerator);
        }
        catch
        {
            // 在无声卡或特权受限环境下安全降级，不阻断主流程
        }

        // 将默认设备排在最前
        result.Sort((a, b) => b.IsDefault.CompareTo(a.IsDefault));
        return result;
    }

    private static string? GetDeviceFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) == 0 && store != null)
        {
            try
            {
                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out var pv) == 0)
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
                Marshal.ReleaseComObject(store);
            }
        }
        return null;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

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

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint pcDevices);

        [PreserveSig]
        int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PROPERTYKEY pkey);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);

        [PreserveSig]
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);

        [PreserveSig]
        int Commit();
    }
}
