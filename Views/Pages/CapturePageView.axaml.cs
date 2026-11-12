using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using ShawMuOCR.Core.Capture;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views.Pages;

public partial class CapturePageView : UserControl
{
    private MainViewModel? _vm;

    public CapturePageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        if (EmbeddedEditor != null)
        {
            EmbeddedEditor.RequestClearImage += OnEmbeddedEditorRequestClearImage;
        }
    }

    private void OnEmbeddedEditorRequestClearImage(object? sender, EventArgs e)
    {
        _vm?.ClearCommand.Execute(null);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MainViewModel;

        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            SyncCurrentImageFromViewModel();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncCurrentImageFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PreviewImage) ||
            e.PropertyName == nameof(MainViewModel.CurrentScreenBounds) ||
            e.PropertyName == nameof(MainViewModel.CurrentPage))
        {
            SyncCurrentImageFromViewModel();
        }
    }

    private void SyncCurrentImageFromViewModel()
    {
        if (_vm == null || EmbeddedEditor == null) return;

        EmbeddedEditor.ConfigureServicesAndCallbacks(
            _vm.Coordinator,
            _vm.ClipboardService,
            _vm.SettingsService,
            _vm.Logger);

        var skBmp = _vm.GetCurrentSkBitmapCopy();
        if (skBmp != null && !skBmp.IsNull)
        {
            EmbeddedEditor.SetImage(skBmp, selectionBounds: _vm.CurrentScreenBounds);
        }
        else
        {
            EmbeddedEditor.ResetToEmpty();
        }
    }
}
