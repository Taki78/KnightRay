using ServiceLib.Services.ConfigFetcher;

namespace v2rayN.Desktop.Views;

// Owned by MainWindow so checks continue when the app is hidden in the tray.
internal sealed class AutoConfigSession(Func<MainWindowViewModel?> getModel)
{
    public const int MinIntervalSeconds = 5;
    public const int DefaultIntervalSeconds = 300;
    public const int MaxIntervalSeconds = 7200;

    private readonly AutoConfigSelector _selector = new(ConfigTextHandler.Config);
    private CancellationTokenSource? _run;
    public event Action? Changed;
    public bool Running => _run is not null;
    public bool IsStopping => _run?.IsCancellationRequested == true;
    public int ProfileCount { get; private set; }
    public string Status { get; private set; } = "Ready to check all configs in this list.";

    public void Start(string[] ids, int intervalSeconds)
    {
        if (Running || ids.Length == 0 ||
            intervalSeconds is < MinIntervalSeconds or > MaxIntervalSeconds)
        {
            return;
        }
        ProfileCount = ids.Length;
        _run = new CancellationTokenSource();
        _ = RunAsync(ids, TimeSpan.FromSeconds(intervalSeconds), _run);
    }

    public void Stop()
    {
        if (_run is { IsCancellationRequested: false } run)
        {
            run.Cancel();
            Update("Stopping automatic checks…");
        }
    }

    private async Task RunAsync(string[] ids, TimeSpan interval, CancellationTokenSource run)
    {
        var token = run.Token;
        try
        {
            while (true)
            {
                var expectedActive = ConfigTextHandler.Config.IndexId;
                var summary = "";
                try
                {
                    Update("Checking selected configs…");
                    var progress = new Progress<string>(message =>
                    {
                        if (ReferenceEquals(_run, run) && !token.IsCancellationRequested)
                        {
                            Update(message);
                        }
                    });
                    var checkedProfiles = await Task.Run(() => _selector.CheckAsync(ids, expectedActive, progress, token), token);
                    token.ThrowIfCancellationRequested();
                    var choice = checkedProfiles.Choice;
                    if (checkedProfiles.Checked == 0)
                    {
                        Update("Stopped: no supported configs in that selection. Select other rows and try again.");
                        return;
                    }
                    var counts = $"Passed {checkedProfiles.Passed} of {checkedProfiles.Checked}.";
                    if (choice?.Profile is not { } candidate)
                    {
                        summary = counts + (checkedProfiles.Untested > 0
                            ? " Current config unchanged. Some tests could not run; check the installed cores and your network."
                            : " No working config found. Current config unchanged.");
                    }
                    else if (ConfigTextHandler.Config.IndexId != expectedActive)
                    {
                        summary = counts + " Active config changed during the check; skipped switching.";
                    }
                    else if (candidate.IndexId == expectedActive)
                    {
                        summary = $"{counts} Keeping {choice.Name} ({choice.LatencyMs} ms).";
                    }
                    else if (await TrySwitchAsync(candidate, expectedActive, token))
                    {
                        summary = $"{counts} Selected {choice.Name} ({choice.LatencyMs} ms).";
                    }
                    else
                    {
                        summary = counts + " A config changed during the check; skipped switching.";
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    summary = "Could not complete this check.";
                }
                token.ThrowIfCancellationRequested();
                Update($"{summary} Next check at {DateTime.Now.Add(interval):T}.");
                await Task.Delay(interval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Update("Automatic checks stopped.");
        }
        finally
        {
            _run = null;
            run.Dispose();
            Changed?.Invoke();
        }
    }

    private async Task<bool> TrySwitchAsync(ProfileItem candidate, string? expectedActive, CancellationToken token)
    {
        if (getModel() is not { } model || await ConfigDbQuery.GetProfileItem(candidate.IndexId) is not { } saved ||
            !AutoConfigSelector.StillMatches(candidate, saved) ||
            !AutoConfigSelector.IsEligible(saved, await AutoConfigSelector.GetChainedGroupsAsync()))
        {
            return false;
        }
        token.ThrowIfCancellationRequested();
        if (ConfigTextHandler.Config.IndexId != expectedActive)
        {
            return false;
        }

        // This runs on the UI thread; changing IndexId before the first await avoids overriding a concurrent manual selection.
        if (await ConfigTextHandler.SetDefaultServerIndex(saved.IndexId) != 0)
        {
            return false;
        }
        await model.Reload();
        await model.ProfilesViewModel.RefreshServers();
        return ConfigTextHandler.Config.IndexId == saved.IndexId;
    }

    private void Update(string status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
