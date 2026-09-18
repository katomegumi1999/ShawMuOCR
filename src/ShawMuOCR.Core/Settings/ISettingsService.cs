namespace ShawMuOCR.Core.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsFilePath { get; }
    event Action<AppSettings>? SettingsChanged;

    AppSettings Load();
    void Save(AppSettings settings);
}
