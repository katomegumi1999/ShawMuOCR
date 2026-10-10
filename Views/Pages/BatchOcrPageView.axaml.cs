using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class BatchOcrPageView : UserControl
{
    public BatchOcrPageView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is BatchOcrViewModel vm)
        {
            vm.RequestOpenFilePicker -= OnRequestOpenFilePicker;
            vm.RequestOpenFilePicker += OnRequestOpenFilePicker;

            vm.RequestOpenFolderPicker -= OnRequestOpenFolderPicker;
            vm.RequestOpenFolderPicker += OnRequestOpenFolderPicker;

            vm.RequestOpenOutputDirPicker -= OnRequestOpenOutputDirPicker;
            vm.RequestOpenOutputDirPicker += OnRequestOpenOutputDirPicker;
        }
    }

    private async void OnRequestOpenFilePicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "批量选择要转换的图片或 PDF 文档",
            AllowMultiple = true,
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

        if (files != null && files.Count > 0 && DataContext is BatchOcrViewModel vm)
        {
            var paths = new List<string>(files.Count);
            foreach (var f in files)
            {
                paths.Add(f.Path.LocalPath);
            }
            vm.AddFiles(paths);
        }
    }

    private async void OnRequestOpenFolderPicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含图片或 PDF 文档的文件夹 (自动递归遍历)",
            AllowMultiple = false
        });

        if (folders != null && folders.Count > 0 && DataContext is BatchOcrViewModel vm)
        {
            string folderPath = folders[0].Path.LocalPath;
            vm.AddDirectory(folderPath);
        }
    }

    private async void OnRequestOpenOutputDirPicker()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择批量 OCR 导出目录",
            AllowMultiple = false
        });

        if (folders != null && folders.Count > 0 && DataContext is BatchOcrViewModel vm)
        {
            vm.CustomOutputDir = folders[0].Path.LocalPath;
        }
    }
}
