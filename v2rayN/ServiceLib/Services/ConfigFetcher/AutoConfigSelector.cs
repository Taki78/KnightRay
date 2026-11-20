namespace ServiceLib.Services.ConfigFetcher;

public sealed class AutoConfigSelector(Config config)
{
    public static bool IsEligible(ProfileItem item, ISet<string> chainedGroups) =>
        !string.IsNullOrWhiteSpace(item.IndexId) && !chainedGroups.Contains(item.Subid ?? "") && !item.IsComplex() &&
        item.ConfigType is not (EConfigType.Custom or EConfigType.Outbound) && item.IsValid() &&
        (AppManager.Instance.GetCoreType(item, item.ConfigType) switch
        {
            ECoreType.Xray => Global.XraySupportConfigType.Contains(item.ConfigType),
            ECoreType.sing_box => Global.SingboxSupportConfigType.Contains(item.ConfigType),
            _ => false,
        });

    public static string Label(ProfileItem item)
    {
        var remarks = item.Remarks ?? "";
        if (remarks.Contains("://", StringComparison.Ordinal) ||
            (item.Password is { Length: >= 4 } secret && remarks.Contains(secret, StringComparison.OrdinalIgnoreCase)))
        {
            return item.ConfigType.ToString();
        }
        var label = new string(remarks.Where(c => !char.IsControl(c)).Take(100).ToArray()).Trim();
        return string.IsNullOrEmpty(label) ? item.ConfigType.ToString() : label;
    }

    public static async Task<HashSet<string>> GetChainedGroupsAsync() =>
        (await ConfigDbQuery.SubItems() ?? []).Where(s => !string.IsNullOrWhiteSpace(s.PrevProfile) ||
            !string.IsNullOrWhiteSpace(s.NextProfile) || !string.IsNullOrWhiteSpace(s.PreResolver)).Select(s => s.Id).ToHashSet();

    public async Task<(FetchResult? Choice, int Checked, int Passed, int Untested)> CheckAsync(
        IReadOnlyList<string> ids, string? activeId, IProgress<string>? progress, CancellationToken token)
    {
        var profiles = await ConfigDbQuery.GetProfileItemsByIndexIds(ids);
        var chainedGroups = await GetChainedGroupsAsync();
        token.ThrowIfCancellationRequested();
        var pending = profiles.Where(p => IsEligible(p, chainedGroups))
            .Select(p => new FetchResult(Label(p), "Saved config", FetchStatus.Pending, "", p)).ToArray();
        var results = await new FetcherProbe(config).TestAsync(pending, progress, token, requireConfiguredCore: true);
        token.ThrowIfCancellationRequested();
        return (Choose(results, activeId), results.Count, results.Count(r => r.Status == FetchStatus.Working),
            results.Count(r => r.Status == FetchStatus.Untested));
    }

    public static bool StillMatches(ProfileItem candidate, ProfileItem saved) =>
        ConfigFetchService.Identity(candidate) == ConfigFetchService.Identity(saved) && candidate.Subid == saved.Subid;

    internal static FetchResult? Choose(IEnumerable<FetchResult> results, string? activeId) =>
        results.Where(r => r.Status == FetchStatus.Working && r.Profile is not null && r.LatencyMs >= 0)
            .OrderBy(r => r.LatencyMs)
            .ThenByDescending(r => r.Profile!.IndexId == activeId)
            .ThenBy(r => r.Profile!.IndexId, StringComparer.Ordinal)
            .FirstOrDefault();
}
