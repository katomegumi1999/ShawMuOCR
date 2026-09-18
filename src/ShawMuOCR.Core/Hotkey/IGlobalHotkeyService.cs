namespace ShawMuOCR.Core.Hotkey;

public interface IGlobalHotkeyService : IDisposable
{
    bool IsRegistered { get; }
    HotkeyGesture CurrentGesture { get; }
    bool IsPaused { get; set; }

    bool Register(HotkeyGesture gesture, Action callback);
    void Unregister();

    bool Register(int id, HotkeyGesture gesture, Action callback);
    void Unregister(int id);
    bool IsRegisteredId(int id);
    HotkeyGesture GetGesture(int id);
}
