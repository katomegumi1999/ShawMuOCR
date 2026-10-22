using System;
using System.IO;
using MuClip.Models;

namespace MuClip.Services;

public partial class MuClipService
{
    private string _fileSaveDirectory = string.Empty;

    public string FileSaveDirectory => _fileSaveDirectory;

    public event Action<FileProgressEvent>? FileProgress;
    public event Action<FileCompletedEvent>? FileCompleted;
    public event Action<FileOfferEvent>? FileOfferReceived;

    public void SetFileSaveDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
        catch { }
        _fileSaveDirectory = dir;
        _sync?.SetFileSaveDirectory(dir);
    }

    public string? SendFile(string peerEndpoint, string filePath)
    {
        if (_sync == null) return null;
        return _sync.SendFile(peerEndpoint, filePath);
    }

    public bool CancelFileTransfer(string transferId)
    {
        if (_sync == null) return false;
        return _sync.CancelFileTransfer(transferId);
    }
}
