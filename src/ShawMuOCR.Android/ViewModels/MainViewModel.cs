using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ShawMuOCR.Android.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    public FileOcrViewModel FileOcr { get; }
    public CameraOcrViewModel CameraOcr { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    private ViewModelBase _currentPage;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    public MainViewModel(
        FileOcrViewModel fileOcr,
        CameraOcrViewModel cameraOcr,
        SettingsViewModel settings)
    {
        FileOcr = fileOcr;
        CameraOcr = cameraOcr;
        Settings = settings;
        _currentPage = fileOcr;
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        CurrentPage = value switch
        {
            1 => CameraOcr,
            2 => Settings,
            _ => FileOcr
        };
    }

    [RelayCommand]
    private void NavigateTo(object? parameter)
    {
        if (parameter is int index)
        {
            SelectedTabIndex = index;
        }
        else if (parameter is string str && int.TryParse(str, out var parsed))
        {
            SelectedTabIndex = parsed;
        }
    }
}
