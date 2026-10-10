using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MuClip.Views;

public partial class ClipboardStandaloneWindow : Window
{
    public ClipboardStandaloneWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
