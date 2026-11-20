namespace ServiceLib.Services.ConfigFetcher;

public sealed record FetcherSource(string Id, string Name, string Url, string[] Keys, bool Enabled = true)
{
    public IReadOnlyList<Uri> Expand()
    {
        if (string.IsNullOrWhiteSpace(Name) || Keys is null || Keys.Length > 32 || string.IsNullOrWhiteSpace(Url))
        {
            throw new FormatException("Enter a source name and at most 32 file keys.");
        }

        const string token = "{proto}";
        var index = Url.IndexOf(token, StringComparison.Ordinal);
        var isTemplate = index >= 0;
        var schemeEnd = Url.IndexOf("://", StringComparison.Ordinal);
        var slash = schemeEnd < 0 ? -1 : Url.IndexOf('/', schemeEnd + 3);
        if (isTemplate != (Keys.Length > 0) ||
            (isTemplate && (Url.IndexOf(token, index + token.Length, StringComparison.Ordinal) >= 0 ||
                            slash < 0 || index <= slash)) ||
            Keys.Any(key => !Regex.IsMatch(key, @"\A[A-Za-z0-9_-][A-Za-z0-9_.-]{0,99}\z")))
        {
            throw new FormatException("Use one {proto} placeholder in the file path with valid file keys, or enter a direct URL without keys.");
        }

        IEnumerable<string> urls = isTemplate ? Keys.Select(key => Url.Replace(token, key, StringComparison.Ordinal)) : [Url];
        return urls.Select(value =>
        {
            if (value.Contains('{') || value.Contains('}') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsHttps(uri))
            {
                throw new FormatException("Use an HTTPS URL without embedded credentials or fragments.");
            }
            return uri;
        }).ToList();
    }

    internal static bool IsHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}

public sealed class FetcherSourceStore(string path)
{
    public static List<FetcherSource> Defaults() =>
    [
        new("barry", "barry-far/V2ray-Config (15-min updates)", "https://raw.githubusercontent.com/barry-far/V2ray-Config/main/Splitted-By-Protocol/{proto}.txt", ["vless", "vmess", "trojan", "ss", "ssr"]),
        new("matin", "MatinGhanbari/v2ray-configs (15-min updates)", "https://raw.githubusercontent.com/MatinGhanbari/v2ray-configs/main/subscriptions/filtered/subs/{proto}.txt", ["vless", "vmess"]),
        new("mahan", "MahanKenway/Freedom-V2Ray (2-hour updates)", "https://raw.githubusercontent.com/MahanKenway/Freedom-V2Ray/main/configs/{proto}.txt", ["vless", "vmess"]),
        new("morpheus", "morpheusadam/v2ray-config (daily rebuild, verified)", "https://raw.githubusercontent.com/morpheusadam/v2ray-config/main/subs/bundles/{proto}.txt", ["best", "all", "iran"]),
        new("au1rxx", "Au1rxx/free-vpn-subscriptions (hourly updates)", "https://github.com/Au1rxx/free-vpn-subscriptions/raw/main/output/{proto}.txt", ["v2ray-base64"]),
        new("delta", "Delta-Kronecker/V2ray-Config (daily updates)", "https://github.com/Delta-Kronecker/V2ray-Config/raw/refs/heads/main/config/{proto}.txt", ["all_configs"]),
        new("mahdi", "mahdibland/V2RayAggregator (speed-tested nodes)", "https://raw.githubusercontent.com/mahdibland/ShadowsocksAggregator/master/{proto}", ["Eternity"])
    ];

    public static List<FetcherSource> MissingDefaults(IEnumerable<FetcherSource> sources)
    {
        var existingIds = sources.Select(source => source.Id).ToHashSet(StringComparer.Ordinal);
        return Defaults().Where(source => !existingIds.Contains(source.Id)).ToList();
    }

    public List<FetcherSource> Load()
    {
        if (!File.Exists(path))
        {
            var defaults = Defaults();
            Save(defaults);
            return defaults;
        }
        var saved = JsonSerializer.Deserialize(File.ReadAllText(path), FetcherJsonContext.Default.ListFetcherSource)
            ?? throw new InvalidDataException("The source list could not be read.");
        foreach (var source in saved)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id))
            {
                throw new InvalidDataException("The source list contains an invalid entry.");
            }
            _ = source.Expand();
        }
        return saved;
    }

    public void Save(List<FetcherSource> sources)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temp, options))
            {
                JsonSerializer.Serialize(stream, sources, FetcherJsonContext.Default.ListFetcherSource);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

[JsonSerializable(typeof(List<FetcherSource>))]
internal partial class FetcherJsonContext : JsonSerializerContext;
