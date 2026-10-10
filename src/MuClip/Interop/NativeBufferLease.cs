using System;

namespace MuClip.Interop;

/// <summary>
/// 手动租借内存池凭据（ref struct 极速零拷贝模式）
/// 专用于使用 using var lease = ... 在局部栈上下文中直接消费 Rust 租借内存。
/// </summary>
public readonly ref struct NativeBufferLease
{
    public ulong LeaseId { get; }
    public IntPtr Pointer { get; }
    public int Length { get; }

    public NativeBufferLease(ulong leaseId, IntPtr pointer, int length)
    {
        LeaseId = leaseId;
        Pointer = pointer;
        Length = length;
    }

    public unsafe ReadOnlySpan<byte> Span => Pointer != IntPtr.Zero && Length > 0
        ? new ReadOnlySpan<byte>((void*)Pointer, Length)
        : ReadOnlySpan<byte>.Empty;

    public unsafe Span<byte> WritableSpan => Pointer != IntPtr.Zero && Length > 0
        ? new Span<byte>((void*)Pointer, Length)
        : Span<byte>.Empty;

    public bool IsValid => LeaseId != 0 && Pointer != IntPtr.Zero;

    public void Dispose()
    {
        if (LeaseId != 0)
        {
            MuClipNative.PoolRelease(LeaseId);
        }
    }
}

/// <summary>
/// 手动租借内存池对象（类模式，可跨异步与委托传递）
/// </summary>
public sealed class NativeMemoryLease : IDisposable
{
    private ulong _leaseId;
    private IntPtr _pointer;
    private readonly int _length;
    private bool _disposed;

    public ulong LeaseId => _leaseId;
    public IntPtr Pointer => _pointer;
    public int Length => _length;

    public NativeMemoryLease(ulong leaseId, IntPtr pointer, int length)
    {
        _leaseId = leaseId;
        _pointer = pointer;
        _length = length;
    }

    public unsafe ReadOnlySpan<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _pointer != IntPtr.Zero && _length > 0
                ? new ReadOnlySpan<byte>((void*)_pointer, _length)
                : ReadOnlySpan<byte>.Empty;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            if (_leaseId != 0)
            {
                MuClipNative.PoolRelease(_leaseId);
                _leaseId = 0;
                _pointer = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }

    ~NativeMemoryLease() => Dispose();
}
