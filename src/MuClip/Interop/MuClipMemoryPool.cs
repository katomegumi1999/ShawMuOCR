using System;

namespace MuClip.Interop;

/// <summary>
/// 手动租借内存池管理器（C# 托管端）
/// 与 Rust 端 global_buffer_pool 协同工作，提供纳秒级无碎片离堆内存租借。
/// </summary>
public static class MuClipMemoryPool
{
    /// <summary>
    /// 从原生内存池租借一块至少 minCapacity 字节的内存缓冲。
    /// 租借完成后，请务必使用 using 模式或调用 Dispose() 归还给内存池！
    /// </summary>
    public static NativeBufferLease Rent(int minCapacity)
    {
        if (minCapacity <= 0) minCapacity = 4096;
        ulong leaseId = MuClipNative.PoolAcquire((nuint)minCapacity, out IntPtr ptr, out nuint actualCap);
        if (leaseId == 0 || ptr == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"无法从 MuClip 原生内存池租借 {minCapacity} 字节缓冲！");
        }
        return new NativeBufferLease(leaseId, ptr, (int)actualCap);
    }

    /// <summary>
    /// 清理并释放原生内存池中的空闲缓存块
    /// </summary>
    public static void Clear()
    {
        MuClipNative.PoolClear();
    }
}
