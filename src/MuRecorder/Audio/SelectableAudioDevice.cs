using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuRecorder.Audio;

/// <summary>
/// 音频预设模式。
/// </summary>
public enum AudioPreset
{
    /// <summary>不录制声音（全部取消勾选）。</summary>
    None = 0,
    /// <summary>仅系统声音（自动勾选默认扬声器）。</summary>
    System = 1,
    /// <summary>仅麦克风（自动勾选默认麦克风）。</summary>
    Microphone = 2,
    /// <summary>系统声音 + 麦克风（自动勾选默认扬声器与麦克风）。</summary>
    SystemAndMic = 3,
    /// <summary>自定义手动勾选。</summary>
    Custom = 4,
}

/// <summary>
/// 可多选勾选的音频设备 ViewModel 项。
/// </summary>
public partial class SelectableAudioDevice : ObservableObject
{
    public string Id { get; }
    public string Name { get; }
    public AudioDeviceFlow Flow { get; }
    public bool IsDefault { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string FlowIcon => Flow == AudioDeviceFlow.Render ? "🔊" : "🎙️";
    public string FlowLabel => Flow == AudioDeviceFlow.Render ? "系统声音" : "麦克风";
    public string DisplayName => IsDefault ? $"{Name} (系统默认)" : Name;

    public event Action? SelectionChanged;

    public SelectableAudioDevice(AudioDeviceInfo info, bool isSelected = false)
    {
        Id = info.Id;
        Name = info.Name;
        Flow = info.Flow;
        IsDefault = info.IsDefault;
        _isSelected = isSelected;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke();
    }
}
