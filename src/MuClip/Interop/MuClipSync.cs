using System.Runtime.InteropServices;
using System.Text;
using MuClip.Models;

namespace MuClip.Interop;

public class RemoteClipboardEventArgs : EventArgs
{
    public string Content { get; }
    public string ContentType { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }

    public RemoteClipboardEventArgs(string content, string contentType, string deviceId, string deviceName)
    {
        Content = content;
        ContentType = contentType;
        DeviceId = deviceId;
        DeviceName = deviceName;
    }
}

public class IncomingPairRequestEventArgs : EventArgs
{
    public string RequestId { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string PeerIp { get; }

    public IncomingPairRequestEventArgs(string requestId, string deviceId, string deviceName, string peerIp)
    {
        RequestId = requestId;
        DeviceId = deviceId;
        DeviceName = deviceName;
        PeerIp = peerIp;
    }
}

public class PairEstablishedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string PeerIp { get; }
    public string Pin { get; }

    public PairEstablishedEventArgs(string deviceId, string deviceName, string peerIp, string pin)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        PeerIp = peerIp;
        Pin = pin;
    }
}

public class PairRequestEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string DeviceName { get; }
    public string Pin { get; }

    public PairRequestEventArgs(string deviceId, string deviceName, string pin)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Pin = pin;
    }
}

/// <summary>
/// Rust P2P 局域网自发现与端到端加密同步引擎
/// </summary>
public sealed class MuClipSync : IDisposable
{
    private IntPtr _handle;
    private int _disposed;
    private int _activeCalls = 1;
    private readonly MuClipNative.NativeClipboardCallback _callback;
    private readonly GCHandle _callbackHandle;
    private readonly MuClipNative.NativePairCallback _pairCallback;
    private readonly GCHandle _pairCallbackHandle;
    private readonly MuClipNative.NativePairRequestCallback _pairRequestCallback;
    private readonly GCHandle _pairRequestCallbackHandle;
    private readonly MuClipNative.NativePairSuccessCallback _pairSuccessCallback;
    private readonly GCHandle _pairSuccessCallbackHandle;
    private readonly MuClipNative.NativeUnpairCallback _unpairCallback;
    private readonly GCHandle _unpairCallbackHandle;
    private readonly MuClipNative.NativeFileProgressCallback _fileProgressCallback;
    private readonly GCHandle _fileProgressCallbackHandle;
    private readonly MuClipNative.NativeFileCompletedCallback _fileCompletedCallback;
    private readonly GCHandle _fileCompletedCallbackHandle;
    private readonly MuClipNative.NativeFileOfferCallback _fileOfferCallback;
    private readonly GCHandle _fileOfferCallbackHandle;

    public event EventHandler<RemoteClipboardEventArgs>? ClipboardReceived;
    public event EventHandler<IncomingPairRequestEventArgs>? IncomingPairRequestReceived;
    public event EventHandler<PairEstablishedEventArgs>? PairEstablished;
    public event EventHandler<PairRequestEventArgs>? PairRequestReceived;
    public event EventHandler<string>? PeerUnpaired;
    public event EventHandler<FileProgressEvent>? FileProgress;
    public event EventHandler<FileCompletedEvent>? FileCompleted;
    public event EventHandler<FileOfferEvent>? FileOfferReceived;
    public ushort BoundPort { get; private set; }

    public unsafe MuClipSync(
        string deviceId,
        string deviceName,
        string deviceType,
        ushort syncPort,
        ushort discoveryPort,
        string pairingPin
    )
    {
        if (string.IsNullOrWhiteSpace(pairingPin) || pairingPin == "000000")
        {
            throw new ArgumentException("配对凭证尚未初始化或使用了不安全的弱默认配对码 (000000)。", nameof(pairingPin));
        }

        byte[] idBytes = Encoding.UTF8.GetBytes(deviceId + "\0");
        byte[] nameBytes = Encoding.UTF8.GetBytes(deviceName + "\0");
        byte[] typeBytes = Encoding.UTF8.GetBytes(deviceType + "\0");
        byte[] pinBytes = Encoding.UTF8.GetBytes(pairingPin + "\0");

        fixed (byte* pId = idBytes)
        fixed (byte* pName = nameBytes)
        fixed (byte* pType = typeBytes)
        fixed (byte* pPin = pinBytes)
        {
            _handle = MuClipNative.SyncStart(pId, pName, pType, syncPort, discoveryPort, pPin);
        }

        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("启动 MuClip 局域网同步引擎失败！");
        }

        BoundPort = MuClipNative.SyncGetBoundPort(_handle);

        _callback = OnNativeClipboardReceived;
        _callbackHandle = GCHandle.Alloc(_callback);
        IntPtr cbPtr = Marshal.GetFunctionPointerForDelegate(_callback);
        MuClipNative.SyncSetCallback(_handle, cbPtr, IntPtr.Zero);

        _pairCallback = OnNativePairReceived;
        _pairCallbackHandle = GCHandle.Alloc(_pairCallback);
        IntPtr pairCbPtr = Marshal.GetFunctionPointerForDelegate(_pairCallback);
        MuClipNative.SyncSetPairCallback(_handle, pairCbPtr, IntPtr.Zero);

        _pairRequestCallback = OnNativePairRequestReceived;
        _pairRequestCallbackHandle = GCHandle.Alloc(_pairRequestCallback);
        IntPtr pairReqCbPtr = Marshal.GetFunctionPointerForDelegate(_pairRequestCallback);
        MuClipNative.SyncSetPairRequestCallback(_handle, pairReqCbPtr, IntPtr.Zero);

        _pairSuccessCallback = OnNativePairSuccessReceived;
        _pairSuccessCallbackHandle = GCHandle.Alloc(_pairSuccessCallback);
        IntPtr pairSuccessCbPtr = Marshal.GetFunctionPointerForDelegate(_pairSuccessCallback);
        MuClipNative.SyncSetPairSuccessCallback(_handle, pairSuccessCbPtr, IntPtr.Zero);

        _unpairCallback = OnNativePeerUnpaired;
        _unpairCallbackHandle = GCHandle.Alloc(_unpairCallback);
        IntPtr unpairCbPtr = Marshal.GetFunctionPointerForDelegate(_unpairCallback);
        MuClipNative.SyncSetUnpairCallback(_handle, unpairCbPtr, IntPtr.Zero);

        _fileProgressCallback = OnNativeFileProgress;
        _fileProgressCallbackHandle = GCHandle.Alloc(_fileProgressCallback);
        IntPtr fileProgressCbPtr = Marshal.GetFunctionPointerForDelegate(_fileProgressCallback);

        _fileCompletedCallback = OnNativeFileCompleted;
        _fileCompletedCallbackHandle = GCHandle.Alloc(_fileCompletedCallback);
        IntPtr fileCompletedCbPtr = Marshal.GetFunctionPointerForDelegate(_fileCompletedCallback);

        _fileOfferCallback = OnNativeFileOffer;
        _fileOfferCallbackHandle = GCHandle.Alloc(_fileOfferCallback);
        IntPtr fileOfferCbPtr = Marshal.GetFunctionPointerForDelegate(_fileOfferCallback);

        MuClipNative.SyncSetFileCallbacks(
            _handle,
            fileProgressCbPtr,
            fileCompletedCbPtr,
            fileOfferCbPtr,
            IntPtr.Zero
        );
    }

    private void OnNativePeerUnpaired(IntPtr devIdPtr, IntPtr userData)
    {
        string devId = Marshal.PtrToStringUTF8(devIdPtr) ?? string.Empty;
        if (!string.IsNullOrEmpty(devId))
        {
            PeerUnpaired?.Invoke(this, devId);
        }
    }

    private void OnNativePairRequestReceived(IntPtr reqIdPtr, IntPtr devIdPtr, IntPtr devNamePtr, IntPtr peerIpPtr, IntPtr userData)
    {
        string reqId = Marshal.PtrToStringUTF8(reqIdPtr) ?? string.Empty;
        string devId = Marshal.PtrToStringUTF8(devIdPtr) ?? string.Empty;
        string devName = Marshal.PtrToStringUTF8(devNamePtr) ?? string.Empty;
        string peerIp = Marshal.PtrToStringUTF8(peerIpPtr) ?? string.Empty;

        IncomingPairRequestReceived?.Invoke(this, new IncomingPairRequestEventArgs(reqId, devId, devName, peerIp));
    }

    private void OnNativePairSuccessReceived(IntPtr devIdPtr, IntPtr devNamePtr, IntPtr peerIpPtr, IntPtr pinPtr, IntPtr userData)
    {
        string devId = Marshal.PtrToStringUTF8(devIdPtr) ?? string.Empty;
        string devName = Marshal.PtrToStringUTF8(devNamePtr) ?? string.Empty;
        string peerIp = Marshal.PtrToStringUTF8(peerIpPtr) ?? string.Empty;
        string pin = Marshal.PtrToStringUTF8(pinPtr) ?? string.Empty;

        PairEstablished?.Invoke(this, new PairEstablishedEventArgs(devId, devName, peerIp, pin));
    }

    private void OnNativePairReceived(IntPtr devIdPtr, IntPtr devNamePtr, IntPtr pinPtr, IntPtr userData)
    {
        string devId = Marshal.PtrToStringUTF8(devIdPtr) ?? string.Empty;
        string devName = Marshal.PtrToStringUTF8(devNamePtr) ?? string.Empty;
        string pin = Marshal.PtrToStringUTF8(pinPtr) ?? string.Empty;

        PairRequestReceived?.Invoke(this, new PairRequestEventArgs(devId, devName, pin));
    }

    private void OnNativeClipboardReceived(IntPtr contentPtr, IntPtr typePtr, IntPtr devIdPtr, IntPtr devNamePtr, IntPtr userData)
    {
        string content = Marshal.PtrToStringUTF8(contentPtr) ?? string.Empty;
        string contentType = Marshal.PtrToStringUTF8(typePtr) ?? "text";
        string devId = Marshal.PtrToStringUTF8(devIdPtr) ?? string.Empty;
        string devName = Marshal.PtrToStringUTF8(devNamePtr) ?? string.Empty;

        ClipboardReceived?.Invoke(this, new RemoteClipboardEventArgs(content, contentType, devId, devName));
    }

    private void OnNativeFileProgress(IntPtr tidPtr, ulong transferred, ulong total, ulong speed, int isSend, IntPtr userData)
    {
        string tid = Marshal.PtrToStringUTF8(tidPtr) ?? string.Empty;
        FileProgress?.Invoke(this, new FileProgressEvent
        {
            TransferId = tid,
            TransferredBytes = transferred,
            TotalBytes = total,
            SpeedBps = speed,
            IsSend = isSend != 0
        });
    }

    private void OnNativeFileCompleted(IntPtr tidPtr, IntPtr pathPtr, int isSend, int success, IntPtr errMsgPtr, IntPtr userData)
    {
        string tid = Marshal.PtrToStringUTF8(tidPtr) ?? string.Empty;
        string path = Marshal.PtrToStringUTF8(pathPtr) ?? string.Empty;
        string errMsg = Marshal.PtrToStringUTF8(errMsgPtr) ?? string.Empty;

        FileCompleted?.Invoke(this, new FileCompletedEvent
        {
            TransferId = tid,
            FilePath = path,
            IsSend = isSend != 0,
            Success = success != 0,
            ErrorMessage = errMsg
        });
    }

    private void OnNativeFileOffer(IntPtr tidPtr, IntPtr fromIdPtr, IntPtr fromNamePtr, IntPtr fileNamePtr, ulong fileSize, IntPtr userData)
    {
        string tid = Marshal.PtrToStringUTF8(tidPtr) ?? string.Empty;
        string fromId = Marshal.PtrToStringUTF8(fromIdPtr) ?? string.Empty;
        string fromName = Marshal.PtrToStringUTF8(fromNamePtr) ?? string.Empty;
        string fileName = Marshal.PtrToStringUTF8(fileNamePtr) ?? string.Empty;

        FileOfferReceived?.Invoke(this, new FileOfferEvent
        {
            TransferId = tid,
            FromDeviceId = fromId,
            FromDeviceName = fromName,
            FileName = fileName,
            FileSize = fileSize
        });
    }

    /// <summary>
    /// 广播剪贴板到所有已发现且在线的设备（以及手动添加的静态节点）
    /// </summary>
    public unsafe int Broadcast(string text, string contentType = "text")
    {
        ThrowIfDisposed();
        byte[] textBytes = Encoding.UTF8.GetBytes(text + "\0");
        byte[] typeBytes = Encoding.UTF8.GetBytes(contentType + "\0");
        fixed (byte* pText = textBytes)
        fixed (byte* pType = typeBytes)
        {
            return (int)MuClipNative.SyncBroadcast(_handle, pText, pType);
        }
    }

    /// <summary>
    /// 获取当前局域网内在线发现的所有设备及映射节点（使用租借内存池 ReadOnlySpan&lt;byte&gt; 零拷贝二进制解析）
    /// </summary>
    public List<PeerDeviceInfo> GetActivePeers()
    {
        ThrowIfDisposed();
        ulong leaseId = MuClipNative.SyncGetActivePeersPooled(_handle, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadPeersFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 获取所有已知设备（含离线与映射节点，使用租借内存池 ReadOnlySpan&lt;byte&gt; 零拷贝二进制解析）
    /// </summary>
    public List<PeerDeviceInfo> GetAllPeers()
    {
        ThrowIfDisposed();
        ulong leaseId = MuClipNative.SyncGetAllPeersPooled(_handle, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadPeersFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 动态修改配对 PIN 码
    /// </summary>
    public unsafe bool SetPairingPin(string pin)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(pin) || pin == "000000")
        {
            throw new ArgumentException("配对 PIN 码不能为空或使用弱默认值 000000。", nameof(pin));
        }
        byte[] bytes = Encoding.UTF8.GetBytes(pin + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.SyncSetPairingPin(_handle, p) == 0;
        }
    }

    /// <summary>
    /// 添加静态映射节点（支持公网 IP、Tailscale、FRP / DDNS 域名:端口，如 "192.168.1.100:42424" 或 "frp.example.com:55555"）
    /// 调用后立即在 PeerManager 生成 is_manual: true 的节点，并异步通过 TCP 映射端口发起双向探测 (MSG_HEARTBEAT)
    /// </summary>
    public unsafe bool AddManualPeer(string endpoint)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(endpoint)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(endpoint.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.SyncAddManualPeer(_handle, p) == 0;
        }
    }

    /// <summary>
    /// 移除静态映射节点（支持传入 "host:port"、"host" 或解析后的 device_id，同步从 PeerDevices 列表中移除）
    /// </summary>
    public unsafe bool RemoveManualPeer(string endpointOrDeviceId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(endpointOrDeviceId)) return false;
        byte[] bytes = Encoding.UTF8.GetBytes(endpointOrDeviceId.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.SyncRemoveManualPeer(_handle, p) == 0;
        }
    }

    /// <summary>
    /// 获取所有手动配置的静态/端口映射节点列表（使用租借内存池 ReadOnlySpan&lt;byte&gt; 零拷贝二进制解析）
    /// </summary>
    public List<string> GetManualPeers()
    {
        ThrowIfDisposed();
        ulong leaseId = MuClipNative.SyncGetManualPeersPooled(_handle, out IntPtr ptr, out nuint len);
        return MuClipBinaryDecoder.ReadStringListFromPooled(leaseId, ptr, len);
    }

    /// <summary>
    /// 测试与指定对端节点的连接与加密可用性（返回毫秒延迟，失败返回 -1）
    /// </summary>
    public unsafe int TestPeer(string peerAddr)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(peerAddr)) return -1;
        byte[] bytes = Encoding.UTF8.GetBytes(peerAddr.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            return MuClipNative.SyncTestPeer(_handle, p);
        }
    }

    /// <summary>
    /// 开启或关闭后台自动广播扫描附近设备（关闭后完全静默，0 局域网广播流量）
    /// </summary>
    public void SetAutoDiscovery(bool enabled)
    {
        ThrowIfDisposed();
        MuClipNative.SyncSetAutoDiscovery(_handle, enabled ? 1 : 0);
    }

    /// <summary>
    /// 检查当前是否开启了自动广播扫描
    /// </summary>
    public bool IsAutoDiscoveryEnabled()
    {
        ThrowIfDisposed();
        return MuClipNative.SyncIsAutoDiscovery(_handle) != 0;
    }

    /// <summary>
    /// 立即向局域网全网卡与子网发送单次探测广播（同时对所有映射节点发起 TCP 探测）
    /// </summary>
    public void TriggerScan()
    {
        ThrowIfDisposed();
        MuClipNative.SyncTriggerScan(_handle);
    }

    /// <summary>
    /// 单连接可靠发起配对握手（阻塞等待响应或超时）
    /// </summary>
    public unsafe (bool Success, string ResponderId, string ResponderName, string ErrorMessage, string LongTermKey) RequestPair(string peerAddr, string pin, uint timeoutSecs = 60)
    {
        if (!TryAcquireHandle(out IntPtr handle))
        {
            return (false, string.Empty, string.Empty, "同步引擎已关闭", string.Empty);
        }

        try
        {
            byte[] addrBytes = Encoding.UTF8.GetBytes(peerAddr.Trim() + "\0");
            byte[] pinBytes = Encoding.UTF8.GetBytes(pin.Trim() + "\0");

            fixed (byte* pAddr = addrBytes)
            fixed (byte* pPin = pinBytes)
            {
                int code = MuClipNative.SyncRequestPair(handle, pAddr, pPin, timeoutSecs, out var result);
                bool ok = code == 0 && result.Success == 0;
                string devId = result.GetResponderDeviceId();
                string devName = result.GetResponderDeviceName();
                string errMsg = result.GetErrorMessage();
                string ltk = result.GetLongTermKey();

                if (!ok && string.IsNullOrEmpty(errMsg))
                {
                    errMsg = result.Success switch
                    {
                        1 => "对方拒绝了配对请求",
                        2 => "配对码不匹配",
                        3 => "配对超时，对方未在限定时间内确认",
                        _ => "网络连接失败或对端不可达"
                    };
                }

                return (ok, devId, devName, errMsg, ltk);
            }
        }
        finally
        {
            ReleaseHandle();
        }
    }

    /// <summary>
    /// 响应收到的配对请求
    /// </summary>
    public unsafe int RespondPair(string requestId, string pin, bool accept)
    {
        ThrowIfDisposed();
        byte[] reqBytes = Encoding.UTF8.GetBytes(requestId.Trim() + "\0");
        byte[] pinBytes = Encoding.UTF8.GetBytes(pin.Trim() + "\0");

        fixed (byte* pReq = reqBytes)
        fixed (byte* pPin = pinBytes)
        {
            return MuClipNative.SyncRespondPair(_handle, pReq, pPin, accept ? 1 : 0);
        }
    }

    /// <summary>
    /// 在底层广播表中注册已配对设备 ID
    /// </summary>
    public unsafe void AddPairedDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        ThrowIfDisposed();
        byte[] bytes = Encoding.UTF8.GetBytes(deviceId.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            MuClipNative.SyncAddPairedDevice(_handle, p);
        }
    }

    /// <summary>
    /// 在底层广播表中移除配对设备 ID
    /// </summary>
    public unsafe void RemovePairedDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        ThrowIfDisposed();
        byte[] bytes = Encoding.UTF8.GetBytes(deviceId.Trim() + "\0");
        fixed (byte* p = bytes)
        {
            MuClipNative.SyncRemovePairedDevice(_handle, p);
        }
    }

    /// <summary>
    /// 向指定远端节点（如 "192.168.1.5:42424"）发送随机配对 PIN 码 (Legacy)
    /// </summary>
    [Obsolete("请使用安全的单流握手 RequestPair 方法。")]
    public bool SendPairRequest(string peerAddr, string pin)
    {
        var res = RequestPair(peerAddr, pin, 10);
        return res.Success;
    }

    /// <summary>
    /// 高速发送文件到指定节点（128KB 切片 + XChaCha20-Poly1305 加密）
    /// </summary>
    public unsafe string? SendFile(string peerAddr, string filePath)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(peerAddr) || string.IsNullOrWhiteSpace(filePath)) return null;

        byte[] addrBytes = Encoding.UTF8.GetBytes(peerAddr.Trim() + "\0");
        byte[] pathBytes = Encoding.UTF8.GetBytes(filePath.Trim() + "\0");

        fixed (byte* pAddr = addrBytes)
        fixed (byte* pPath = pathBytes)
        {
            IntPtr tidPtr = MuClipNative.SyncSendFile(_handle, pAddr, pPath);
            if (tidPtr == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUTF8(tidPtr);
            }
            finally
            {
                MuClipNative.FreeString(tidPtr);
            }
        }
    }

    /// <summary>
    /// 随时取消进行中的文件传输
    /// </summary>
    public unsafe bool CancelFileTransfer(string transferId)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(transferId)) return false;

        byte[] tidBytes = Encoding.UTF8.GetBytes(transferId.Trim() + "\0");
        fixed (byte* pTid = tidBytes)
        {
            return MuClipNative.SyncCancelFileTransfer(_handle, pTid) == 0;
        }
    }

    /// <summary>
    /// 设置文件接收保存默认目录
    /// </summary>
    public unsafe bool SetFileSaveDirectory(string dir)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(dir)) return false;

        byte[] dirBytes = Encoding.UTF8.GetBytes(dir.Trim() + "\0");
        fixed (byte* pDir = dirBytes)
        {
            return MuClipNative.SyncSetFileSaveDir(_handle, pDir) == 0;
        }
    }

    private bool TryAcquireHandle(out IntPtr handle)
    {
        while (true)
        {
            int current = Volatile.Read(ref _activeCalls);
            if (current <= 0 || Volatile.Read(ref _disposed) != 0)
            {
                handle = IntPtr.Zero;
                return false;
            }
            if (Interlocked.CompareExchange(ref _activeCalls, current + 1, current) == current)
            {
                handle = _handle;
                if (handle == IntPtr.Zero)
                {
                    ReleaseHandle();
                    return false;
                }
                return true;
            }
        }
    }

    private void ReleaseHandle()
    {
        if (Interlocked.Decrement(ref _activeCalls) == 0)
        {
            DestroyHandleCore();
        }
    }

    private void DestroyHandleCore()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            MuClipNative.SyncStop(handle);
        }

        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }

        if (_pairCallbackHandle.IsAllocated)
        {
            _pairCallbackHandle.Free();
        }

        if (_pairRequestCallbackHandle.IsAllocated)
        {
            _pairRequestCallbackHandle.Free();
        }

        if (_pairSuccessCallbackHandle.IsAllocated)
        {
            _pairSuccessCallbackHandle.Free();
        }

        if (_unpairCallbackHandle.IsAllocated)
        {
            _unpairCallbackHandle.Free();
        }

        if (_fileProgressCallbackHandle.IsAllocated)
        {
            _fileProgressCallbackHandle.Free();
        }

        if (_fileCompletedCallbackHandle.IsAllocated)
        {
            _fileCompletedCallbackHandle.Free();
        }

        if (_fileOfferCallbackHandle.IsAllocated)
        {
            _fileOfferCallbackHandle.Free();
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0 || _handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(MuClipSync));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ReleaseHandle();
        }

        GC.SuppressFinalize(this);
    }

    ~MuClipSync() => Dispose();
}
