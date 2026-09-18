using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ShawMuOCR.ViewModels;
using ShawMuOCR.Views;

namespace ShawMuOCR;

public class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        return param switch
        {
            MainViewModel vm => new MainWindow { DataContext = vm },
            SettingsViewModel vm => new SettingsWindow(vm),
            _ => new TextBlock { Text = "Not Found: " + param.GetType().Name }
        };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase;
    }
}
