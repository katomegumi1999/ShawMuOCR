using ShawMuOCR.Core.Settings;

namespace ShawMuOCR.Core.Ai;

/// <summary>
/// 某个节点的额度快照
/// </summary>
public sealed record AiQuotaSnapshot(
    string ProfileId,
    string ProfileName,
    int UsedTokens,
    int MonthlyLimit,
    double Percent);

/// <summary>
/// 公共额度接口：API 池里每个节点的用量都由它统一检查与记账。
/// 翻译、OCR 以及后续任何 AI 功能都必须走这里，否则计数会分裂，月度上限失效。
/// </summary>
public interface IAiQuotaService
{
    /// <summary>取得节点额度快照（会自动处理跨月清零）</summary>
    AiQuotaSnapshot GetSnapshot(string profileId);

    /// <summary>
    /// 请求前检查。返回 false 表示已超额，<paramref name="denyMessage"/> 为可直接展示给用户的原因。
    /// </summary>
    bool TryCheck(string profileId, out string? denyMessage);

    /// <summary>
    /// 请求成功后记账。tokens &lt;= 0 时按 1 计，避免"用得越多越不计数"。
    /// </summary>
    void Commit(string profileId, int tokens);

    event Action? QuotaUpdated;
}

public sealed class AiQuotaService : IAiQuotaService
{
    private readonly ISettingsService _settingsService;
    private readonly object _lock = new();

    public event Action? QuotaUpdated;

    public AiQuotaService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    public AiQuotaSnapshot GetSnapshot(string profileId)
    {
        ResetIfMonthChanged();

        var profile = _settingsService.Current.Ai.Find(profileId);
        if (profile == null)
        {
            return new AiQuotaSnapshot(profileId, "(已删除的节点)", 0, 0, 0);
        }

        double percent = profile.MonthlyTokenLimit > 0
            ? Math.Min(100.0, (double)profile.UsedTokens / profile.MonthlyTokenLimit * 100.0)
            : 0;

        return new AiQuotaSnapshot(profile.Id, profile.Name, profile.UsedTokens, profile.MonthlyTokenLimit, percent);
    }

    public bool TryCheck(string profileId, out string? denyMessage)
    {
        denyMessage = null;
        ResetIfMonthChanged();

        var profile = _settingsService.Current.Ai.Find(profileId);
        if (profile == null)
        {
            denyMessage = "所选 AI 节点已被删除，请到【设置 ➔ AI 接口】重新选择。";
            return false;
        }

        if (profile.MonthlyTokenLimit > 0 && profile.UsedTokens >= profile.MonthlyTokenLimit)
        {
            denyMessage =
                $"节点「{profile.Name}」已达到本月 Token 额度上限（{profile.MonthlyTokenLimit:N0}），已自动拦截请求以防产生额外费用。可到【设置 ➔ AI 接口】调整额度或清空用量。";
            return false;
        }

        return true;
    }

    public void Commit(string profileId, int tokens)
    {
        int delta = tokens > 0 ? tokens : 1;
        ResetIfMonthChanged();

        lock (_lock)
        {
            var current = _settingsService.Current;
            var updated = current.Clone();
            var profile = updated.Ai.Find(profileId);
            if (profile == null) return;

            profile.UsedTokens += delta;
            _settingsService.Save(updated);
        }

        QuotaUpdated?.Invoke();
    }

    /// <summary>
    /// 跨月清零。所有节点共用同一个月份标记，避免逐节点记录月份
    /// 导致"某个月用过 / 某个月没用"的节点各自漂移。
    /// </summary>
    private void ResetIfMonthChanged()
    {
        string month = DateTime.Now.ToString("yyyy-MM");
        lock (_lock)
        {
            var current = _settingsService.Current;
            if (string.Equals(current.Ai.UsageMonth, month, StringComparison.Ordinal))
            {
                return;
            }

            var updated = current.Clone();
            updated.Ai.UsageMonth = month;
            foreach (var profile in updated.Ai.Profiles)
            {
                profile.UsedTokens = 0;
            }
            _settingsService.Save(updated);
        }

        QuotaUpdated?.Invoke();
    }
}
