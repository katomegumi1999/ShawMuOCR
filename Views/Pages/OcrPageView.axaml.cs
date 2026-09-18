using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class OcrPageView : UserControl
{
    public OcrPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;

        // 注册文件拖拽处理
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.RequestOpenFilePicker -= OnRequestOpenFilePicker;
            vm.RequestOpenFilePicker += OnRequestOpenFilePicker;
        }
    }

    private async void OnRequestOpenFilePicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要识别的图片或 PDF 文档",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("所有支持的文件 (*.png, *.jpg, *.jpeg, *.bmp, *.webp, *.pdf)")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff", "*.ico", "*.pdf" }
                },
                new FilePickerFileType("图片文件 (*.png, *.jpg, *.jpeg, *.bmp, *.webp)")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp", "*.tif", "*.tiff", "*.ico" }
                },
                new FilePickerFileType("PDF 文档 (*.pdf)")
                {
                    Patterns = new[] { "*.pdf" }
                },
                new FilePickerFileType("所有文件 (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        if (files != null && files.Count > 0 && DataContext is MainViewModel vm)
        {
            var path = files[0].Path.LocalPath;
            await vm.RecognizeFileAsync(path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files != null && files.Length > 0)
        {
            var firstFile = files[0];
            var path = firstFile.Path.LocalPath;
            if (!string.IsNullOrEmpty(path))
            {
                await vm.RecognizeFileAsync(path);
            }
        }
    }
}
