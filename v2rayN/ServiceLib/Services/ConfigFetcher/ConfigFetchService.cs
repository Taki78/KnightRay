namespace ServiceLib.Services.ConfigFetcher;

public enum FetchStatus { Pending, Working, Duplicate, Invalid, Unsupported, Unreachable, Untested, SourceError, LimitReached }

public sealed record FetchResult(string Name, string Source, FetchStatus Status, string Detail,
    ProfileItem? Profile = null, long? LatencyMs = null, string? ShareLink = null);

public sealed class ConfigFetchService
{
    public const int DefaultEntriesPerFile = 35;
    private const int MaxDownloadBytes = 32 * 1024 * 1024;
    private const int MaxPasteBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 3000;
    private const int DownloadConcurrency = 6;
    private static readonly HttpClient SourceHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
    };
    private static readonly string[] SupportedSchemes =
    [
        "vmess", "vless", "ss", "trojan", "socks", "socks4", "socks5", "hysteria2", "hy2", "tuic",
        "wireguard", "anytls", "naive", "naive+https", "naive+quic", "hysteria2+realm", "hysteria2+realm+http"
    ];

    public Task<List<FetchResult>> FetchAsync(IEnumerable<FetcherSource> sources, string pasted,
        IProgress<string> progress, CancellationToken token, int entriesPerFile = DefaultEntriesPerFile) =>
        FetchAsync(sources, pasted, progress, token, entriesPerFile, SourceHttp);

    internal async Task<List<FetchResult>> FetchAsync(IEnumerable<FetcherSource> sources, string pasted,
        IProgress<string> progress, CancellationToken token, int entriesPerFile, HttpClient http)
    {
        var targets = sources.Where(s => s.Enabled)
            .SelectMany(s => s.Expand().Select((url, index) => (url, label: s.Keys.Length > 0 ? $"{s.Name} / {s.Keys[index]}" : s.Name)))
            .GroupBy(target => target.url)
            .ToList();
        var results = new List<FetchResult>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(string content, string label, int limit, int maxBytes)
        {
            foreach (var result in Parse(content, label, Math.Min(limit, MaxEntries - results.Count), maxBytes))
            {
                token.ThrowIfCancellationRequested();
                if (result.Profile is null)
                {
                    results.Add(result);
                    continue;
                }
                var key = Identity(result.Profile);
                if (seen.TryGetValue(key, out var index))
                {
                    var original = results[index];
                    if (!original.Source.Split(" · ").Contains(label, StringComparer.Ordinal))
                    {
                        results[index] = original with { Source = $"{original.Source} · {label}" };
                    }
                    results.Add(result with { Status = FetchStatus.Duplicate, Detail = "Same connection settings as an earlier entry.", Profile = null, ShareLink = null });
                }
                else
                {
                    seen[key] = results.Count;
                    results.Add(result);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(pasted))
        {
            Add(pasted, "Pasted links", MaxEntries, MaxPasteBytes);
        }
        using var downloads = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new Queue<Task<(string Label, string? Content, string? Error)>>();
        var next = 0;
        void FillWindow()
        {
            while (pending.Count < DownloadConcurrency && next < targets.Count)
            {
                pending.Enqueue(FetchFileAsync(targets[next++]));
            }
        }

        async Task<(string Label, string? Content, string? Error)> FetchFileAsync(
            IGrouping<Uri, (Uri url, string label)> target)
        {
            var label = string.Join(" · ", target.Select(item => item.label).Distinct());
            try
            {
                return (label, await DownloadAsync(http, target.Key, downloads.Token), null);
            }
            catch (Exception ex) when (!downloads.IsCancellationRequested &&
                ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                return (label, null, ex switch
                {
                    HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
                    OperationCanceledException => "Download timed out.",
                    InvalidDataException => "Response was too large or a redirect was refused.",
                    _ => "Download failed."
                });
            }
        }

        try
        {
            for (var count = 0; count < targets.Count; count++)
            {
                token.ThrowIfCancellationRequested();
                if (results.Count >= MaxEntries)
                {
                    results.Add(new("Entry limit", "Run", FetchStatus.LimitReached, $"Stopped after {MaxEntries} entries."));
                    break;
                }

                FillWindow();
                progress.Report($"Reading file {count + 1} of {targets.Count}");
                // Keep source priority stable even when a later download finishes first.
                var payload = await pending.Dequeue();
                if (payload.Content is { } content)
                {
                    Add(content, payload.Label, entriesPerFile, MaxDownloadBytes);
                }
                else
                {
                    results.Add(new("Source file", payload.Label, FetchStatus.SourceError, payload.Error!));
                }
            }
        }
        finally
        {
            if (pending.Count > 0)
            {
                await downloads.CancelAsync();
                try { await Task.WhenAll(pending); }
                catch (OperationCanceledException) { }
            }
        }
        return results;
    }

    internal static IEnumerable<FetchResult> Parse(string content, string source, int limit, int maxBytes = MaxPasteBytes)
    {
        if (Encoding.UTF8.GetByteCount(content) > maxBytes)
        {
            yield return new("Source file", source, FetchStatus.SourceError, "Input exceeds the size limit.");
            yield break;
        }

        content = content.Trim().TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(content))
        {
            yield return new("Source file", source, FetchStatus.SourceError, "The source was empty.");
            yield break;
        }
        if (content.StartsWith('{') || content.StartsWith('[') ||
            content.StartsWith("proxies:", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("outbounds:", StringComparison.OrdinalIgnoreCase))
        {
            yield return new("Source file", source, FetchStatus.Unsupported, "Expected plain or Base64 share links.");
            yield break;
        }
        if (!content.Contains(':'))
        {
            var decoded = DecodeSubscription(content);
            if (decoded.Contains("://", StringComparison.Ordinal))
            {
                content = decoded;
            }
        }

        var entryLimit = limit;
        using var reader = new StringReader(content);
        var lineNumber = 0;
        while (reader.ReadLine() is { } raw)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (limit-- <= 0)
            {
                yield return new("Entry limit", source, FetchStatus.LimitReached, $"Only the first {entryLimit} entries were read.");
                yield break;
            }

            var fallback = $"Entry {lineNumber}";
            var delimiter = line.IndexOf("://", StringComparison.Ordinal);
            if (delimiter < 1)
            {
                yield return new(fallback, source, FetchStatus.Invalid, "Expected a share link.");
                continue;
            }
            var scheme = line[..delimiter].ToLowerInvariant();
            var link = scheme + line[delimiter..];
            var profile = FmtHandler.ResolveConfig(link, out _, logParseErrors: false);
            if (profile is null)
            {
                yield return SupportedSchemes.Contains(scheme)
                    ? new(fallback, source, FetchStatus.Invalid, "Could not parse the share link.")
                    : new(fallback, source, FetchStatus.Unsupported, "Unsupported share link format.");
                continue;
            }
            var name = SafeName(profile, fallback);
            if (profile.IsComplex() || profile.ConfigType == EConfigType.Outbound)
            {
                yield return new(name, source, FetchStatus.Unsupported, "Not a standalone proxy.");
                continue;
            }
            if (!profile.IsValid() || Uri.CheckHostName(profile.Address.Trim('[', ']')) == UriHostNameType.Unknown ||
                (profile.ConfigType is EConfigType.Trojan or EConfigType.Hysteria2 or EConfigType.TUIC or EConfigType.Anytls &&
                 string.IsNullOrWhiteSpace(profile.Password)))
            {
                yield return new(name, source, FetchStatus.Invalid, "Missing or invalid connection settings.");
                continue;
            }

            yield return new(name, source, FetchStatus.Pending, "Ready to test.", profile, ShareLink: link);
        }
    }

    private static string DecodeSubscription(string input)
    {
        var normalized = string.Concat(input.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        var bytes = new byte[normalized.Length * 3 / 4];
        return Convert.TryFromBase64String(normalized, bytes, out var length)
            ? Encoding.UTF8.GetString(bytes, 0, length)
            : string.Empty;
    }

    private static string SafeName(ProfileItem profile, string fallback)
    {
        var name = profile.Remarks;
        if (string.IsNullOrWhiteSpace(name) || name.Contains("://", StringComparison.Ordinal) ||
            (profile.Password is { Length: >= 4 } password && name.Contains(password, StringComparison.OrdinalIgnoreCase)))
        {
            return fallback;
        }
        return new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
            .Take(100).ToArray());
    }

    internal static string Identity(ProfileItem profile)
    {
        var fields = JsonUtils.SerializeToNode(profile)!.AsObject();
        foreach (var key in new[] { "IndexId", "ConfigVersion", "Subid", "IsSub", "DisplayLog", "Remarks" })
        {
            fields.Remove(key);
        }
        fields["Address"] = IPAddress.TryParse(profile.Address.Trim('[', ']'), out var ip) ? ip.ToString() : profile.Address.ToLowerInvariant();
        fields["Network"] = profile.GetNetwork();
        fields["ProtoExtra"] = JsonUtils.SerializeToNode(profile.GetProtocolExtra());
        fields["TransportExtra"] = JsonUtils.SerializeToNode(profile.GetTransportExtra());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields.ToJsonString())));
    }

    private static async Task<string> DownloadAsync(HttpClient http, Uri uri, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                if (response.Headers.Location is not { } location || !Uri.TryCreate(uri, location, out var next) || !FetcherSource.IsHttps(next))
                {
                    throw new InvalidDataException();
                }
                uri = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new InvalidDataException();
            }
            using var data = new MemoryStream();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[8192];
            while (await stream.ReadAsync(buffer, timeout.Token) is var read && read > 0)
            {
                if (data.Length + read > MaxDownloadBytes)
                {
                    throw new InvalidDataException();
                }
                data.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length);
        }
        throw new InvalidDataException();
    }
}
