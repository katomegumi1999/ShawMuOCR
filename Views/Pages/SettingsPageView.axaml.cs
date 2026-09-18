using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class SettingsPageView : UserControl
{
    public SettingsPageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.RequestPickModelFolder -= OnRequestPickModelFolder;
            vm.RequestPickModelFolder += OnRequestPickModelFolder;
        }
    }

    private async Task<string?> OnRequestPickModelFolder()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含 PaddleOCR ONNX 模型的文件夹",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            return folders[0].TryGetLocalPath();
        }

        return null;
    }
}
