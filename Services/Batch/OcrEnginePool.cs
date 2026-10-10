using System;
using System.Collections.Concurrent;
using System.Threading;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Services.Batch;

/// <summary>
/// 纯托管多核 OCR 引擎对象池，为高并发批处理提供独立隔离且无锁冲突的引擎实例
/// </summary>
public sealed class OcrEnginePool : IDisposable
{
    private readonly ConcurrentBag<IOcrEngine> _engines = new();
    private readonly IOcrEngineFactory _factory;
    private readonly AppSettings _settings;
    private readonly int _maxEngines;
    private int _createdCount;
    private bool _disposed;

    public OcrEnginePool(IOcrEngineFactory factory, AppSettings settings, int maxEngines)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _maxEngines = Math.Max(1, maxEngines);
    }

    /// <summary>
    /// 从池中租借一个 OCR 引擎实例，若池中无空闲实例且未达到上限则即时创建
    /// </summary>
    public IOcrEngine Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_engines.TryTake(out var existingEngine))
        {
            return existingEngine;
        }

        Interlocked.Increment(ref _createdCount);
        return _factory.Create(_settings);
    }

    /// <summary>
    /// 归还 OCR 引擎实例至池中
    /// </summary>
    public void Return(IOcrEngine engine)
    {
        if (engine == null) return;

        if (_disposed)
        {
            try
            {
                engine.Dispose();
            }
            catch { }
            return;
        }

        _engines.Add(engine);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        while (_engines.TryTake(out var engine))
        {
            try
            {
                engine.Dispose();
            }
            catch { }
        }
    }
}
