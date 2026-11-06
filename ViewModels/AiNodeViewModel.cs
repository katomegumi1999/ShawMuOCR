using CommunityToolkit.Mvvm.ComponentModel;
using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.ViewModels;

/// <summary>
/// 设置界面中「AI 接口池」里的一个可编辑节点。
/// 与持久化模型 <see cref="AiApiProfile"/> 一一对应，编辑完成后由
/// <see cref="SettingsViewModel"/> 回写成 Profile 列表。
/// </summary>
public partial class AiNodeViewModel : ObservableObject
{
    public string Id { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _baseUrl;

    [ObservableProperty]
    private string _apiKey;

    [ObservableProperty]
    private string _model;

    [ObservableProperty]
    private bool _supportsVision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuotaText))]
    private int _monthlyTokenLimit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QuotaText))]
    private int _usedTokens;

    public AiNodeViewModel(AiApiProfile profile)
    {
        Id = profile.Id;
        _name = profile.Name;
        _baseUrl = profile.BaseUrl;
        _apiKey = profile.ApiKey;
        _model = profile.Model;
        _supportsVision = profile.SupportsVision;
        _monthlyTokenLimit = profile.MonthlyTokenLimit;
        _usedTokens = profile.UsedTokens;
    }

    public static AiNodeViewModel CreateDefault(int index) => new(new AiApiProfile
    {
        Name = $"节点 {index}",
        SupportsVision = true
    });

    /// <summary>
    /// 下拉框里显示的文本：名字 + 模型，便于区分多个同厂商节点
    /// </summary>
    public string DisplayName
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(Name) ? "(未命名)" : Name.Trim();
            return string.IsNullOrWhiteSpace(Model) ? name : $"{name} · {Model.Trim()}";
        }
    }

    public string QuotaText => MonthlyTokenLimit > 0
        ? $"{UsedTokens:N0} / {MonthlyTokenLimit:N0} Token"
        : "本月不限量";

    public AiApiProfile ToProfile() => new()
    {
        Id = Id,
        Name = string.IsNullOrWhiteSpace(Name) ? "(未命名)" : Name.Trim(),
        BaseUrl = string.IsNullOrWhiteSpace(BaseUrl) ? "https://api.openai.com/v1" : BaseUrl.Trim(),
        ApiKey = ApiKey?.Trim() ?? string.Empty,
        Model = Model?.Trim() ?? string.Empty,
        SupportsVision = SupportsVision,
        MonthlyTokenLimit = MonthlyTokenLimit < 0 ? 0 : MonthlyTokenLimit,
        UsedTokens = UsedTokens
    };

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(DisplayName));

    partial void OnModelChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}
