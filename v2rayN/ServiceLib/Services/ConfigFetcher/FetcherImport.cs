namespace ServiceLib.Services.ConfigFetcher;

public static class FetcherImport
{
    public const string GroupId = "v2raytaki-fetched-configs";

    public static Task<int> ImportAsync(IEnumerable<FetchResult> results)
    {
        var profiles = results
            .Where(r => r.Status == FetchStatus.Working && r.Profile is not null)
            .Select(r => JsonUtils.DeepCopy(r.Profile!)).ToList();
        return SQLiteHelper.Instance.ImportFetchedProfilesAsync(profiles);
    }
}
