using System.Collections.ObjectModel;
using ServiceLib.Services.ConfigFetcher;

namespace v2rayN.Desktop.Views;

public partial class ConfigFetcherWindow : Window
{
    private readonly FetcherSourceStore _store = new(Utils.GetConfigPath("fetcher-sources.json"));
    private readonly ConfigFetchService _fetcher = new();
    private readonly ObservableCollection<FetchResult> _working = [];
    private readonly ObservableCollection<FetchResult> _other = [];
    private List<FetcherSource> _sources = [];
    private List<FetchResult> _results = [];
    private CancellationTokenSource? _run;
    private string? _editing;
    private bool _importing;
    private bool _closeRequested;
    private bool _canSave = true;

    public bool Imported { get; private set; }

    public ConfigFetcherWindow()
    {
        InitializeComponent();
        EntriesPerFile.Value = ConfigFetchService.DefaultEntriesPerFile;
        WorkingList.ItemsSource = _working;
        OtherList.ItemsSource = _other;
        WorkingList.AddHandler(KeyDownEvent, WorkingListKeyDown, RoutingStrategies.Tunnel);
        WorkingList.SelectionChanged += (_, _) => UpdateShareButton();
        try
        {
            _sources = _store.Load();
            SourceList.ItemsSource = _sources;
            SourceList.SelectedItem = _sources.FirstOrDefault(s => s.Enabled);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException or System.Text.Json.JsonException)
        {
            SourcePreview.Text = "The saved source list could not be read. Fix or back up fetcher-sources.json to edit sources.";
            _canSave = false;
            RestoreDefaultsButton.IsEnabled = false;
        }
        Closing += (_, args) =>
        {
            if (_run is not null || _importing)
            {
                args.Cancel = true;
                _closeRequested = true;
                CancelRun();
                StatusText.Text = _importing ? "Finishing import before closing…" : "Canceling before closing…";
            }
        };
        UpdateButtons();
    }

    private void SourceSelected(object? sender, SelectionChangedEventArgs args)
    {
        if (SourceList.SelectedItem is FetcherSource selected)
        {
            _editing = selected.Id;
            SourceName.Text = selected.Name;
            SourceUrl.Text = selected.Url;
            SourceKeys.Text = string.Join(", ", selected.Keys);
            SourceEnabled.IsChecked = selected.Enabled;
            SourcePreview.Text = string.Empty;
        }
    }

    private void NewSource(object? sender, RoutedEventArgs args)
    {
        SourceList.SelectedItem = null;
        _editing = null;
        SourceName.Text = SourceUrl.Text = SourceKeys.Text = SourcePreview.Text = string.Empty;
        SourceEnabled.IsChecked = true;
        SourceName.Focus();
    }

    private FetcherSource ReadSource() => new(_editing ?? Guid.NewGuid().ToString("N"),
        SourceName.Text?.Trim() ?? string.Empty, SourceUrl.Text?.Trim() ?? string.Empty,
        (SourceKeys.Text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        SourceEnabled.IsChecked == true);

    private void PreviewSource(object? sender, RoutedEventArgs args)
    {
        try
        {
            SourcePreview.Text = string.Join(Environment.NewLine,
                ReadSource().Expand().Select(uri => uri.GetLeftPart(UriPartial.Path) + (uri.Query.Length > 0 ? "?[hidden]" : "")));
        }
        catch (FormatException ex) { SourcePreview.Text = ex.Message; }
    }

    private void SaveSource(object? sender, RoutedEventArgs args)
    {
        var item = ReadSource();
        try
        {
            _ = item.Expand();
            var updated = _sources.ToList();
            var index = updated.FindIndex(s => s.Id == item.Id);
            if (index < 0)
            {
                updated.Add(item);
            }
            else
            {
                updated[index] = item;
            }
            if (SaveSources(updated))
            {
                SourceList.SelectedItem = item;
                SourcePreview.Text = "Source saved.";
            }
        }
        catch (FormatException ex) { SourcePreview.Text = ex.Message; }
    }

    private void DeleteSource(object? sender, RoutedEventArgs args)
    {
        if (SourceList.SelectedItem is FetcherSource selected &&
            SaveSources(_sources.Where(s => s.Id != selected.Id).ToList()))
        {
            NewSource(sender, args);
            SourcePreview.Text = "Source deleted.";
        }
    }

    private void RestoreDefaults(object? sender, RoutedEventArgs args)
    {
        var missing = FetcherSourceStore.MissingDefaults(_sources);
        if (missing.Count == 0)
        {
            SourcePreview.Text = "All default repositories are already in the list.";
            return;
        }

        var selected = SourceList.SelectedItem;
        var draftName = SourceName.Text;
        var draftUrl = SourceUrl.Text;
        var draftKeys = SourceKeys.Text;
        var draftEnabled = SourceEnabled.IsChecked;
        if (SaveSources([.. _sources, .. missing]))
        {
            if (selected is not null)
            {
                SourceList.SelectedItem = selected;
                SourceName.Text = draftName;
                SourceUrl.Text = draftUrl;
                SourceKeys.Text = draftKeys;
                SourceEnabled.IsChecked = draftEnabled;
            }
            else if (string.IsNullOrWhiteSpace(draftName) && string.IsNullOrWhiteSpace(draftUrl) && string.IsNullOrWhiteSpace(draftKeys))
            {
                SourceList.SelectedItem = missing[0];
            }
            SourcePreview.Text = $"Restored {missing.Count} default {(missing.Count == 1 ? "repository" : "repositories")}.";
        }
    }

    private bool SaveSources(List<FetcherSource> sources)
    {
        if (!_canSave)
        {
            SourcePreview.Text = "The saved source list must be fixed before it can be edited.";
            return false;
        }
        try
        {
            _store.Save(sources);
            _sources = sources;
            SourceList.ItemsSource = sources;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SourcePreview.Text = "Could not save sources.";
            return false;
        }
    }

    private async void Run(object? sender, RoutedEventArgs args) => await RunAsync(fetch: true);
    private async void Retest(object? sender, RoutedEventArgs args) => await RunAsync(fetch: false);
    private void Cancel(object? sender, RoutedEventArgs args) => CancelRun();
    private void CloseFetcher(object? sender, RoutedEventArgs args) => Close();

    private void CancelRun()
    {
        if (_run is { IsCancellationRequested: false } run)
        {
            StatusText.Text = "Canceling…";
            CancelButton.IsEnabled = false;
            run.Cancel();
        }
    }

    private async Task RunAsync(bool fetch)
    {
        if (_run is not null || _importing)
        {
            return;
        }

        var scope = FetchScope.SelectedIndex;
        var selected = SourceList.SelectedItem as FetcherSource;
        var sources = scope switch
        {
            0 when selected is { Enabled: true } => [selected],
            1 => _sources.Where(s => s.Enabled).ToArray(),
            _ => Array.Empty<FetcherSource>()
        };
        var limit = (int)(EntriesPerFile.Value ?? ConfigFetchService.DefaultEntriesPerFile);
        var pasted = Pasted.Text ?? string.Empty;
        if (fetch && scope != 2 && sources.Length == 0)
        {
            Pages.SelectedIndex = 0;
            StatusText.Text = scope == 0 ? "Select an enabled repository first." : "Enable at least one repository first.";
            return;
        }
        if (fetch && scope == 2 && string.IsNullOrWhiteSpace(pasted))
        {
            Pages.SelectedItem = PasteTab;
            StatusText.Text = "Paste share links first.";
            return;
        }
        if (scope != 2)
        {
            pasted = string.Empty;
        }

        using var cancel = new CancellationTokenSource();
        _run = cancel;
        UpdateButtons();
        var fetching = fetch;
        var target = scope switch { 0 => selected?.Name, 1 => "all enabled repositories", _ => "pasted links" };
        var progress = new Progress<string>(value =>
        {
            if (_run == cancel && fetching && !cancel.IsCancellationRequested)
            {
                StatusText.Text = $"{value} — {target}";
            }
        });
        StatusText.Text = fetch ? $"Reading {target}…" : "Preparing to retest…";
        try
        {
            var staged = fetch
                ? await Task.Run(() => _fetcher.FetchAsync(sources, pasted, progress, cancel.Token, limit))
                : _results;
            cancel.Token.ThrowIfCancellationRequested();
            fetching = false;
            var total = staged.Count(FetcherProbe.CanTest);
            var finished = 0;
            SetVisibleResults(staged.Where(result => !FetcherProbe.CanTest(result)));
            Pages.SelectedItem = _working.Count > 0 ? WorkingTab : OtherTab;
            StatusText.Text = $"Testing {total} unique configs…";
            var completed = new Progress<FetchResult>(result =>
            {
                if (_run != cancel || cancel.IsCancellationRequested)
                {
                    return;
                }
                if (result.Status == FetchStatus.Working)
                {
                    _working.Add(result);
                    if (_working.Count == 1 && Pages.SelectedItem == OtherTab)
                    {
                        Pages.SelectedItem = WorkingTab;
                    }
                }
                else
                {
                    _other.Add(result);
                }
                UpdateTabCounts();
                StatusText.Text = $"Tested {++finished} of {total} · {_working.Count} working so far";
            });
            _results = await Task.Run(() => new FetcherProbe(ConfigTextHandler.Config)
                .TestAsync(staged, null, cancel.Token, completed));
            ShowResults(cancel.IsCancellationRequested ? "Canceled. " : string.Empty);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            StatusText.Text = "Fetch canceled. Previous results are still available.";
        }
        catch (Exception)
        {
            ShowResults(string.Empty);
            StatusText.Text = "Could not complete the run. Previous results are still available.";
        }
        finally
        {
            _run = null;
            FinishOperation();
        }
    }

    private void ShowResults(string prefix)
    {
        SetVisibleResults(_results);
        var totals = new[] { $"{_working.Count} working" }.Concat(
            _other.GroupBy(r => r.Status).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key switch
            {
                FetchStatus.SourceError => "download issues",
                FetchStatus.LimitReached => "file limits reached",
                _ => g.Key.ToString().ToLowerInvariant()
            }}"));
        StatusText.Text = prefix + (_results.Count == 0 ? "No configs found." : string.Join(" · ", totals));
    }

    private void SetVisibleResults(IEnumerable<FetchResult> results)
    {
        _working.Clear();
        _other.Clear();
        foreach (var result in results)
        {
            (result.Status == FetchStatus.Working ? _working : _other).Add(result);
        }
        UpdateTabCounts();
    }

    private void UpdateTabCounts()
    {
        WorkingTab.Header = $"Working ({_working.Count})";
        OtherTab.Header = $"Other results ({_other.Count})";
    }

    private void SelectAllWorking(object? sender, RoutedEventArgs args)
    {
        if (SelectAllButton.IsEnabled && WorkingList.Items.Count is var count && count > 0)
        {
            WorkingList.Selection.SelectRange(0, count - 1);
        }
    }

    private void WorkingListKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.A && args.KeyModifiers == KeyModifiers.Control)
        {
            args.Handled = true;
            SelectAllWorking(sender, args);
        }
    }

    private async void ImportSelected(object? sender, RoutedEventArgs args)
    {
        if (_run is not null || _importing)
        {
            return;
        }
        var selected = WorkingList.SelectedItems?.OfType<FetchResult>().ToArray() ?? [];
        if (selected.Length == 0)
        {
            StatusText.Text = "Select one or more working configs first.";
            return;
        }

        _importing = true;
        UpdateButtons();
        try
        {
            var added = await FetcherImport.ImportAsync(selected);
            Imported |= added > 0;
            StatusText.Text = $"Added {added} to Fetched Configs. Skipped {selected.Length - added} already saved.";
        }
        catch (Exception)
        {
            StatusText.Text = "Import failed. No partial batch was saved.";
        }
        finally
        {
            _importing = false;
            FinishOperation();
        }
    }

    private string? SelectedShareLink() =>
        WorkingList.SelectedItems is [FetchResult { Status: FetchStatus.Working, ShareLink: { Length: > 0 } link }] ? link : null;

    private async void ShareWorkingQr(object? sender, RoutedEventArgs args)
    {
        if (SelectedShareLink() is not { } link || _run is not null || _importing)
        {
            return;
        }

        var qr = new QrcodeView(link);
        var close = new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var content = new StackPanel { Margin = new Thickness(12) };
        if (!qr.HasQrCode)
        {
            content.Children.Add(new TextBlock { Text = "Could not fit this config in a QR code. Copy its link below." });
        }
        content.Children.Add(qr);
        content.Children.Add(close);
        var dialog = new Window
        {
            Title = "Share config as QR code",
            Content = content,
            ShowInTaskbar = false,
            CanResize = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        close.Click += (_, _) => dialog.Close();
        try
        {
            await dialog.ShowDialog(this);
        }
        finally
        {
            qr.ReleaseQrCode();
        }
    }

    private void FinishOperation()
    {
        if (_closeRequested)
        {
            Close();
        }
        else
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var idle = _run is null && !_importing;
        RunButton.IsEnabled = idle;
        FetchScope.IsEnabled = EntriesPerFile.IsEnabled = idle;
        RetestButton.IsEnabled = idle && _results.Any(FetcherProbe.CanTest);
        SelectAllButton.IsEnabled = ImportButton.IsEnabled = idle && WorkingList.Items.Count > 0;
        UpdateShareButton();
        CancelButton.IsEnabled = _run is { IsCancellationRequested: false };
    }

    private void UpdateShareButton() =>
        ShareQrButton.IsEnabled = _run is null && !_importing && SelectedShareLink() is not null;
}
