namespace ShawMuOCR.Core.Settings;

/// <summary>
/// 一组可复用的 AI 接口配置。用户在设置里自由添加多组，翻译与 OCR 各自选择使用哪一组。
///
/// 身份标识用 <see cref="Id"/>（创建时生成的 GUID）而非列表下标：
/// 凭据加密缓存是按名字索引的，若用下标，删除第一条会让后续所有密文错位，
/// 表现为「B 档案静默拿到 A 的密钥」，不报任何错。
/// </summary>
public sealed class AiApiProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 用户可改的显示名（如「通义千问」「公司内网网关」），仅用于界面展示与选择
    /// </summary>
    public string Name { get; set; } = "新接口";

    /// <summary>
    /// 接口基地址，只需到 /v1，/chat/completions 由客户端统一拼接
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    /// <summary>
    /// 接口密钥（DPAPI 加密落盘）
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// 模型名称
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// 是否支持图片输入。只有勾选的档案才会出现在 OCR 引擎的可选列表里，
    /// 避免用户把纯文本模型配进 OCR 后收到一堆 400。
    /// </summary>
    public bool SupportsVision { get; set; }

    /// <summary>
    /// 每月 Token 额度（0 表示不限制）。所有功能共用同一个计数器。
    /// </summary>
    public int MonthlyTokenLimit { get; set; } = 200000;

    /// <summary>
    /// 本月已用 Token，跨月由 <see cref="AiSettings.UsageMonth"/> 触发清零
    /// </summary>
    public int UsedTokens { get; set; }

    public AiApiProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        SupportsVision = SupportsVision,
        MonthlyTokenLimit = MonthlyTokenLimit,
        UsedTokens = UsedTokens
    };
}

/// <summary>
/// AI 接口档案池与其全局用量记账。
/// 这是翻译、OCR（以及后续任何 AI 功能）共用的唯一配置与额度来源。
/// </summary>
public sealed class AiSettings
{
    /// <summary>
    /// 翻译提供商里代表"AI 节点"的前缀，完整形式如 "ai:legacy-openai"。
    /// 放在这里而不是翻译模块，是因为节点池是唯一事实来源，两边都要用它。
    /// </summary>
    public const string TranslateProviderPrefix = "ai:";

    public List<AiApiProfile> Profiles { get; set; } = new();

    /// <summary>
    /// 用量统计所属月份（"yyyy-MM"）。与当前月份不符时清零所有档案的 UsedTokens。
    /// </summary>
    public string UsageMonth { get; set; } = string.Empty;

    /// <summary>
    /// 旧版单配置迁移完成的标记。用显式标记而不是「档案为空」判断，
    /// 否则用户删光所有档案后，迁移会再次执行并凭空造出一个档案。
    /// </summary>
    public bool Migrated { get; set; }

    public AiApiProfile? Find(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        return Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
    }

    public AiSettings Clone() => new()
    {
        Profiles = Profiles.Select(p => p.Clone()).ToList(),
        UsageMonth = UsageMonth,
        Migrated = Migrated
    };
}
