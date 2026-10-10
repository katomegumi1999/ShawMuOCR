using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MuClip.ViewModels;

namespace MuClip.Views;

public partial class ClipboardView : UserControl
{
    private Window? _attachedWindow;
    private Visual? _parentVisual;

    public ClipboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        var historyListBox = this.FindControl<ListBox>("HistoryListBox");
        if (historyListBox != null)
        {
            historyListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnHistoryListScrollChanged);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is Window win)
        {
            _attachedWindow = win;
            _attachedWindow.PropertyChanged += OnHostWindowPropertyChanged;
        }
        if (Parent is Visual parentVisual)
        {
            _parentVisual = parentVisual;
            _parentVisual.PropertyChanged += OnHostWindowPropertyChanged;
        }
        UpdateHostActiveState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_attachedWindow != null)
        {
            _attachedWindow.PropertyChanged -= OnHostWindowPropertyChanged;
            _attachedWindow = null;
        }
        if (_parentVisual != null)
        {
            _parentVisual.PropertyChanged -= OnHostWindowPropertyChanged;
            _parentVisual = null;
        }
        base.OnDetachedFromVisualTree(e);
        if (DataContext is ClipboardViewModel vm)
        {
            vm.SetHostViewActive(false);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateHostActiveState();
        }
    }

    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
        {
            UpdateHostActiveState();
        }
    }

    private void UpdateHostActiveState()
    {
        if (_attachedWindow == null) return;
        if (DataContext is ClipboardViewModel vm)
        {
            bool parentVisible = _parentVisual?.IsVisible ?? true;
            bool windowVisible = _attachedWindow.IsVisible && _attachedWindow.WindowState != WindowState.Minimized;
            vm.SetHostViewActive(IsVisible && parentVisible && windowVisible);
        }
    }

    private void OnHistoryListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is ScrollViewer sv && DataContext is ClipboardViewModel vm)
        {
            if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 150)
            {
                vm.LoadMoreHistory();
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ClipboardViewModel vm)
        {
            UpdateHostActiveState();
            vm.RequestPickFileFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "选择要发送的文件",
                    AllowMultiple = false
                });
                if (files.Count > 0)
                {
                    return files[0].TryGetLocalPath() ?? files[0].Path.LocalPath;
                }
                return null;
            };

            vm.RequestPickFolderFunc = async () =>
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择文件接收保存目录"
                });
                if (folders.Count > 0)
                {
                    return folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                }
                return null;
            };
        }
    }
}
