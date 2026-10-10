using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using ShawMuOCR.Core.Batch;

namespace ShawMuOCR.ViewModels;

/// <summary>
/// 单个批量 OCR 任务项 ViewModel
/// </summary>
public partial class BatchOcrItemViewModel : ViewModelBase
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _fileName = string.Empty;

    [ObservableProperty]
    private string _directory = string.Empty;

    [ObservableProperty]
    private BatchFileType _fileType;

    [ObservableProperty]
    private string _fileTypeIcon = "📄";

    [ObservableProperty]
    private long _fileSizeBytes;

    [ObservableProperty]
    private string _fileSizeText = string.Empty;

    [ObservableProperty]
    private int _totalPages = 1;

    [ObservableProperty]
    private int _processedPages;

    [ObservableProperty]
    private BatchItemStatus _status = BatchItemStatus.Pending;

    [ObservableProperty]
    private string _statusText = "待处理";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _recognizedText = string.Empty;

    [ObservableProperty]
    private TimeSpan _duration = TimeSpan.Zero;

    [ObservableProperty]
    private string _durationText = "--";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isSelected = true;

    public int CharCount => RecognizedText?.Length ?? 0;

    public ObservableCollection<string> GeneratedFiles { get; } = new();

    public bool HasGeneratedFiles => GeneratedFiles.Count > 0;

    public bool IsCompleted => Status == BatchItemStatus.Completed;
    public bool IsFailed => Status == BatchItemStatus.Failed;
    public bool IsProcessing => Status == BatchItemStatus.Processing;

    public static BatchOcrItemViewModel FromFilePath(string filePath)
    {
        var fi = new FileInfo(filePath);
        string ext = fi.Extension.ToLowerInvariant();
        var fileType = ext == ".pdf" ? BatchFileType.Pdf : BatchFileType.Image;
        string icon = fileType == BatchFileType.Pdf ? "📕" : "🖼️";

        return new BatchOcrItemViewModel
        {
            FilePath = fi.FullName,
            FileName = fi.Name,
            Directory = fi.DirectoryName ?? string.Empty,
            FileType = fileType,
            FileTypeIcon = icon,
            FileSizeBytes = fi.Exists ? fi.Length : 0,
            FileSizeText = fi.Exists ? FormatBytes(fi.Length) : "0 B",
            TotalPages = fileType == BatchFileType.Pdf ? 0 : 1,
            ProcessedPages = 0,
            Status = BatchItemStatus.Pending,
            StatusText = "待处理",
            Progress = 0,
            IsSelected = true
        };
    }

    public void AddGeneratedFile(string path)
    {
        GeneratedFiles.Add(path);
        OnPropertyChanged(nameof(HasGeneratedFiles));
    }

    public void Reset()
    {
        Status = BatchItemStatus.Pending;
        StatusText = "待处理";
        Progress = 0;
        ProcessedPages = 0;
        RecognizedText = string.Empty;
        Duration = TimeSpan.Zero;
        DurationText = "--";
        ErrorMessage = null;
        GeneratedFiles.Clear();
        OnPropertyChanged(nameof(HasGeneratedFiles));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(CharCount));
    }

    public void MarkProcessing(string message)
    {
        Status = BatchItemStatus.Processing;
        StatusText = message;
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsFailed));
    }

    public void MarkCompleted(string text, TimeSpan duration)
    {
        Status = BatchItemStatus.Completed;
        RecognizedText = text;
        Duration = duration;
        DurationText = $"{duration.TotalSeconds:F1}s";
        Progress = 100;
        StatusText = $"✓ 已完成 (共 {CharCount} 字, 耗时 {DurationText})";
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(IsCompleted));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(HasGeneratedFiles));
    }

    public void MarkFailed(string error, TimeSpan duration)
    {
        Status = BatchItemStatus.Failed;
        ErrorMessage = error;
        Duration = duration;
        DurationText = $"{duration.TotalSeconds:F1}s";
        StatusText = $"✕ 失败: {error}";
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsProcessing));
        OnPropertyChanged(nameof(IsCompleted));
    }

    public void MarkCancelled()
    {
        if (Status == BatchItemStatus.Pending || Status == BatchItemStatus.Processing)
        {
            Status = BatchItemStatus.Cancelled;
            StatusText = "已取消";
            OnPropertyChanged(nameof(IsProcessing));
            OnPropertyChanged(nameof(IsCompleted));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }
}
