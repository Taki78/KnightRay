namespace ServiceLib.Services.ConfigFetcher;

public sealed class FetcherProbe(Config config)
{
    private static readonly Uri Endpoint = new("https://www.gstatic.com/generate_204");

    public static bool CanTest(FetchResult result) => result.Profile is not null &&
        result.Status is FetchStatus.Pending or FetchStatus.Untested or FetchStatus.Unreachable;

    public async Task<List<FetchResult>> TestAsync(IReadOnlyList<FetchResult> input,
        IProgress<string>? progress, CancellationToken token, IProgress<FetchResult>? completed = null,
        bool requireConfiguredCore = false)
    {
        var results = input.ToArray();
        var items = Enumerable.Range(0, results.Length)
            .Where(i => CanTest(results[i])).ToArray();
        if (items.Length == 0)
        {
            return results.ToList();
        }

        var cores = new[] { ECoreType.Xray, ECoreType.sing_box }.ToDictionary(core => core, core =>
        {
            var info = CoreInfoManager.Instance.GetCoreInfo(core)!;
            return (Info: info, Path: CoreInfoManager.Instance.GetCoreExecFile(info, out _));
        });
        var count = 0;
        progress?.Report($"Testing {items.Length} configs");
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                async (index, ct) =>
                {
                    var result = results[index];
                    var profile = result.Profile!;
                    var preferred = AppManager.Instance.GetCoreType(profile, profile.ConfigType);
                    var supported = cores.Keys.Where(core => core == ECoreType.Xray
                        ? Global.XraySupportConfigType.Contains(profile.ConfigType)
                        : Global.SingboxSupportConfigType.Contains(profile.ConfigType)).ToArray();
                    var compatible = supported
                        .Where(core => (!requireConfiguredCore || core == preferred) && NodeValidator.Validate(profile, core).Success)
                        .OrderByDescending(core => core == preferred).ToArray();
                    var selected = compatible.FirstOrDefault(core => !string.IsNullOrEmpty(cores[core].Path));
                    if (supported.Length == 0)
                    {
                        results[index] = result with { Status = FetchStatus.Unsupported, Detail = "This protocol cannot be tested with Xray or sing-box." };
                    }
                    else if (compatible.Length == 0)
                    {
                        results[index] = result with { Status = FetchStatus.Invalid, Detail = requireConfiguredCore
                            ? "The configured core cannot use this profile." : "The connection settings do not pass core validation." };
                    }
                    else if (!compatible.Contains(selected) || string.IsNullOrEmpty(cores[selected].Path))
                    {
                        results[index] = result with { Status = FetchStatus.Untested, Detail = $"Install {compatible[0]} from Help → Check for updates, then retry." };
                    }
                    else
                    {
                        var core = cores[selected];
                        var tested = await TestOneAsync(result, core.Info, core.Path, ct);
                        if (tested.Status == FetchStatus.Working && !requireConfiguredCore)
                        {
                            profile.CoreType = selected;
                        }
                        results[index] = tested;
                    }
                    completed?.Report(results[index]);
                    if (progress is not null)
                    {
                        progress.Report($"Tested {Interlocked.Increment(ref count)} of {items.Length}");
                    }
                });

            if (results.Any(r => r.Status == FetchStatus.Unreachable) &&
                !results.Any(r => r.Status == FetchStatus.Working) && !await IsEndpointReachableAsync(token))
            {
                for (var i = 0; i < results.Length; i++)
                {
                    if (results[i].Status == FetchStatus.Unreachable)
                    {
                        results[i] = results[i] with { Status = FetchStatus.Untested, Detail = "Could not confirm the test endpoint was available." };
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (var index in items)
            {
                if (results[index].Status == FetchStatus.Pending)
                {
                    results[index] = results[index] with { Status = FetchStatus.Untested, Detail = "Canceled before the test finished." };
                }
            }
        }
        return results.ToList();
    }

    private async Task<FetchResult> TestOneAsync(FetchResult result, CoreInfo core, string executable, CancellationToken token)
    {
        try
        {
            var directory = Directory.CreateTempSubdirectory("v2raytaki-probe-");
            try
            {
                var path = Path.Combine(directory.FullName, "config.json");
                var profile = JsonUtils.DeepCopy(result.Profile!);
                profile.CoreType = core.CoreType;
                var context = new CoreConfigContext
                {
                    Node = profile,
                    RunCoreType = core.CoreType,
                    AppConfig = config,
                    SimpleDnsItem = config.SimpleDNSItem,
                    IsLinux = OperatingSystem.IsLinux(),
                };

                using var reservation = new TcpListener(IPAddress.Loopback, 0);
                reservation.Start();
                var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                var generated = core.CoreType == ECoreType.sing_box
                    ? new CoreConfigSingboxService(context).GenerateClientSpeedtestConfig(port)
                    : new CoreConfigV2rayService(context).GenerateClientSpeedtestConfig(port);
                if (!generated.Success || generated.Data is null)
                {
                    return result with { Status = FetchStatus.Unsupported, Detail = "Could not generate a test config for this core." };
                }
                var json = JsonNode.Parse(generated.Data.ToString()!)!.AsObject();
                json["log"] = core.CoreType == ECoreType.sing_box
                    ? new JsonObject { ["disabled"] = true }
                    : new JsonObject { ["loglevel"] = "none" };
                if (core.CoreType == ECoreType.sing_box && json["route"] is JsonObject route)
                {
                    route["final"] = Global.ProxyTag;
                }
                await File.WriteAllTextAsync(path, json.ToJsonString(), token);

                var start = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = directory.FullName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var arg in new[] { "run", "-c", path })
                {
                    start.ArgumentList.Add(arg);
                }
                foreach (var entry in core.Environment)
                {
                    start.Environment[entry.Key] = entry.Value;
                }

                reservation.Stop();
                using var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                if (!process.Start())
                {
                    return result with { Status = FetchStatus.Untested, Detail = "Could not start the test core." };
                }
                try
                {
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    using var http = CreateProbeClient(new WebProxy($"http://127.0.0.1:{port}") { BypassProxyOnLocal = false });
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        try
                        {
                            var ready = await WaitForListenerAsync(port, process, timeout.Token);
                            if (!ready)
                            {
                                return result with { Status = FetchStatus.Untested, Detail = "The test core did not start." };
                            }

                            var timer = Stopwatch.StartNew();
                            using var response = await http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                            if (response.StatusCode == HttpStatusCode.NoContent && !process.HasExited)
                            {
                                return result with { Status = FetchStatus.Working, Detail = "Expected HTTPS response received.", LatencyMs = timer.ElapsedMilliseconds };
                            }
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
                        {
                            // Retry once for temporary network problems.
                        }
                        if (process.HasExited)
                        {
                            return result with { Status = FetchStatus.Untested, Detail = "The test core exited before completing the probe." };
                        }
                    }
                    return result with { Status = FetchStatus.Unreachable, Detail = "No successful HTTPS response after two attempts." };
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) when (process.HasExited) { }
                    }
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or UnauthorizedAccessException or JsonException)
        {
            return result with { Status = FetchStatus.Untested, Detail = "Could not run the isolated test core." };
        }
    }

    private static async Task<bool> WaitForListenerAsync(int port, Process process, CancellationToken token)
    {
        for (var attempt = 0; attempt < 30 && !process.HasExited; attempt++)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, token);
                return !process.HasExited;
            }
            catch (SocketException) when (!token.IsCancellationRequested)
            {
                await Task.Delay(70, token);
            }
        }
        return false;
    }

    private static HttpClient CreateProbeClient(IWebProxy? proxy) =>
        new(new HttpClientHandler { Proxy = proxy, UseProxy = proxy is not null, AllowAutoRedirect = false, UseCookies = false });

    private static async Task<bool> IsEndpointReachableAsync(CancellationToken token)
    {
        using var http = CreateProbeClient(null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync(Endpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}
