using ServiceLib.Services.ConfigFetcher;

namespace ServiceLib.Tests.Services;

[NotInParallel]
public class ConfigFetcherTests
{
    private const string Original = "vless://a4958752-662d-4f31-892d-ef14cfaaa13a@proxy.example:443?type=ws&path=%2Fapi&security=tls#first";

    [Test]
    public async Task Staging_DeduplicatesLabelsButRetainsCredentialsAndTransportDifferences()
    {
        var text = string.Join('\n', Original, Original.Replace("#first", "#renamed"),
            Original.Replace("a4958752", "a4958753"), Original.Replace("%2Fapi", "%2Fother"),
            "trojan://@proxy.example:443", "ssr://unsupported");
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var progress = new Progress<string>();
        var service = new ConfigFetchService();
        var plain = await service.FetchAsync([], text, progress, CancellationToken.None);
        var decoded = await service.FetchAsync([], encoded, progress, CancellationToken.None);

        await plain.Count(x => x.Status == FetchStatus.Pending).Should().BeEqualTo(3);
        await plain.Count(x => x.Status == FetchStatus.Duplicate).Should().BeEqualTo(1);
        await plain.Count(x => x.Status == FetchStatus.Invalid).Should().BeEqualTo(1);
        await plain.Count(x => x.Status == FetchStatus.Unsupported).Should().BeEqualTo(1);
        await string.Join(',', decoded.Select(x => x.Status)).Should().BeEqualTo(string.Join(',', plain.Select(x => x.Status)));
    }

    [Test]
    public async Task OversizedDownload_DoesNotDiscardOtherFilesInTheSameRepository()
    {
        using var http = new HttpClient(new FetchFixture());
        var source = new FetcherSource("test", "Test repository", "https://example.com/{proto}.txt", ["oversized", "small"]);
        var rows = await new ConfigFetchService().FetchAsync([source], "", new Progress<string>(),
            CancellationToken.None, 1, http);
        await string.Join(',', rows.Select(row => row.Status)).Should().BeEqualTo("SourceError,Pending,LimitReached");
    }

    [Test]
    public async Task FetchStartsLaterFilesWhileAnEarlierFileIsStillDownloading()
    {
        using var fixture = new OverlapFixture();
        using var http = new HttpClient(fixture, disposeHandler: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var source = new FetcherSource("test", "Test repository", "https://example.com/{proto}.txt",
            ["first", "slow", "3", "4", "5", "6", "last"]);
        var rows = await new ConfigFetchService().FetchAsync([source], "", new Progress<string>(), cancel.Token, 1, http);

        await rows.First(row => row.Status == FetchStatus.Pending).Name.Should().BeEqualTo("first");
        await rows.Count(row => row.Status == FetchStatus.Duplicate).Should().BeEqualTo(6);
    }

    private sealed class OverlapFixture : HttpMessageHandler
    {
        private readonly TaskCompletionSource _lastStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/last.txt") _lastStarted.SetResult();
            if (path == "/slow.txt") await _lastStarted.Task.WaitAsync(cancellationToken);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Original.Replace("#first", $"#{Path.GetFileNameWithoutExtension(path)}")),
            };
        }
    }

    private sealed class FetchFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new StringContent($"{Original}\n{Original}");
            if (request.RequestUri!.AbsolutePath == "/oversized.txt")
            {
                content.Headers.ContentLength = 64 * 1024 * 1024;
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }

    [Test]
    public async Task Sources_KeepAnEmptyListAfterTheDefaultsAreDeleted()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "sources.json");
            var store = new FetcherSourceStore(path);
            var sources = store.Load();
            await sources.Count.Should().BeEqualTo(7);
            await sources.SelectMany(x => x.Expand()).Count().Should().BeEqualTo(15);
            await sources.Last().Expand().Single().AbsolutePath.EndsWith("/Eternity", StringComparison.Ordinal).Should().BeTrue();
            var custom = new FetcherSource("added", "Personal source", "https://example.com/sub?key=secret", [], false);
            store.Save([custom]);
            await new FetcherSourceStore(path).Load().Single().Enabled.Should().BeFalse();
            store.Save([]);
            await new FetcherSourceStore(path).Load().Count.Should().BeEqualTo(0);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Import_InsertsOnlyPassingProfilesAndIsIdempotent()
    {
        TestDatabase.Initialize();
        var database = SQLiteHelper.Instance;
        var profile = FmtHandler.ResolveConfig(Original, out _)!;
        profile.Address = $"{Guid.NewGuid():N}.example";
        try
        {
            var working = new FetchResult("first", "fixture", FetchStatus.Working, "", profile);
            var failed = working with { Status = FetchStatus.Unreachable };
            await (await FetcherImport.ImportAsync([failed])).Should().BeEqualTo(0);
            await (await FetcherImport.ImportAsync([working])).Should().BeEqualTo(1);
            await (await FetcherImport.ImportAsync([working])).Should().BeEqualTo(0);
            var saved = await database.TableAsync<ProfileItem>().Where(x => x.Address == profile.Address).FirstAsync();
            await saved.Subid.Should().BeEqualTo(FetcherImport.GroupId);
        }
        finally
        {
            var saved = await database.TableAsync<ProfileItem>().Where(x => x.Address == profile.Address).ToListAsync();
            foreach (var item in saved)
            {
                await database.DeleteAsync(item);
            }
        }
    }
    [Test]
    public async Task Import_RollsBackAllRowsWhenAnInsertFails()
    {
        TestDatabase.Initialize();
        var database = SQLiteHelper.Instance;
        var prefix = Guid.NewGuid().ToString("N");
        var profile = FmtHandler.ResolveConfig(Original, out _)!;
        profile.Address = $"{prefix}.example";
        var rejected = JsonUtils.DeepCopy(profile)!;
        rejected.Address = $"fail-{prefix}.example";
        var trigger = $"fetcher_{prefix}";
        await database.ExecuteAsync($"CREATE TRIGGER {trigger} BEFORE INSERT ON ProfileItem " +
            $"WHEN NEW.Address = '{rejected.Address}' BEGIN SELECT RAISE(ABORT, 'fixture'); END");
        try
        {
            var working = new FetchResult("first", "fixture", FetchStatus.Working, "", profile);
            var failed = working with { Profile = rejected };
            var threw = false;
            try
            {
                await FetcherImport.ImportAsync([working, failed]);
            }
            catch (SQLiteException)
            {
                threw = true;
            }
            await threw.Should().BeTrue();
            await (await database.TableAsync<ProfileItem>().Where(x => x.Address == profile.Address).CountAsync()).Should().BeEqualTo(0);
        }
        finally
        {
            await database.ExecuteAsync($"DROP TRIGGER {trigger}");
            await database.ExecuteAsync($"DELETE FROM ProfileItem WHERE Address = '{profile.Address}'");
        }
    }

}
