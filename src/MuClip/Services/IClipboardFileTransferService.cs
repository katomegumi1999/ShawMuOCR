using System;
using MuClip.Models;

namespace MuClip.Services;

/// <summary>
/// 局域网分片加密文件传输契约
/// 职责：提供高速文件传输分片管理、进度监听、文件接收存储目录配置与取消传输
/// </summary>
public interface IClipboardFileTransferService
{
    string FileSaveDirectory { get; }
    void SetFileSaveDirectory(string dir);
    string? SendFile(string peerEndpoint, string filePath);
    bool CancelFileTransfer(string transferId);

    event Action<FileProgressEvent>? FileProgress;
    event Action<FileCompletedEvent>? FileCompleted;
    event Action<FileOfferEvent>? FileOfferReceived;
}
