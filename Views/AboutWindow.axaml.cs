using Avalonia.Controls;
using Avalonia.Interactivity;
using ShawMuOCR.ViewModels;

namespace ShawMuOCR.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    public AboutWindow(AboutViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (s, e) => viewModel.Dispose();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
