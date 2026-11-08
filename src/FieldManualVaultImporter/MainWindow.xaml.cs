using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FieldManualVaultImporter.Vault;
using Microsoft.Win32;

namespace FieldManualVaultImporter;

/// <summary>
/// One window: where the files go, which collections, which file types; then Start, Pause/Resume, Cancel. A run that
/// stopped (pause, crash, power cut) is offered again when its destination is chosen or the program starts.
/// </summary>
public partial class MainWindow : Window
{
    private const string VaultFolder = "Field Manual Vault";
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldManualVaultImporter", "settings.json");

    private readonly ObservableCollection<string> _log = new();
    private readonly Dictionary<string, CheckBox> _collections = new();
    private readonly Dictionary<string, CheckBox> _formats = new();
    private readonly Dictionary<string, int> _counts = Collections.All.ToDictionary(c => c.Key, c => c.Count);
    private RunState? _state;
    private RunState? _unfinished;
    private CancellationTokenSource? _cts;
    private StopReason _stopReason;
    private Task? _run;
    private bool _paused;
    private BrowserWindow? _browser;
    private VaultBlockedException? _blocked;
    private bool _pagesViaBrowser;
    private bool _filesViaBrowser;
    private readonly List<DateTime> _challenges = new();

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        foreach (var (key, _, _) in Collections.All)
        {
            var box = new CheckBox { Margin = new Thickness(6, 5, 6, 5), IsChecked = key != "historic", Tag = key };
            box.Checked += (_, _) => UpdateCollectionsSummary();
            box.Unchecked += (_, _) => UpdateCollectionsSummary();
            _collections[key] = box;
            CollectionsList.Children.Add(box);
        }
        UpdateCollectionsSummary();
        foreach (var (key, label) in Formats.All)
        {
            var box = new CheckBox { Content = label, IsChecked = true, Margin = new Thickness(0, 2, 0, 4) };
            box.Checked += (_, _) => UpdateFormatsHint();
            box.Unchecked += (_, _) => UpdateFormatsHint();
            _formats[key] = box;
            FormatsList.Children.Add(box);
        }
        UpdateFormatsHint();

        Destination.Text = LoadSettings()?.Destination ?? "";
        DestinationChanged();
        Loaded += async (_, _) => await RefreshCountsAsync();
    }

    // ------------------------------------------------------------------ settings

    private sealed record Settings(string? Destination);

    private static Settings? LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(Destination.Text.Trim())));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ choices

    private string Root => Path.Combine(Destination.Text.Trim(), VaultFolder);

    private bool HasDestination => Destination.Text.Trim() is { Length: > 0 } d && Path.IsPathFullyQualified(d);

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Where should the Field Manual Vault folder go?" };
        if (HasDestination && Directory.Exists(Destination.Text.Trim())) dialog.InitialDirectory = Destination.Text.Trim();
        if (dialog.ShowDialog(this) != true) return;
        // Picking the "Field Manual Vault" folder itself means its parent.
        var folder = dialog.FolderName;
        if (string.Equals(Path.GetFileName(folder.TrimEnd('\\')), VaultFolder, StringComparison.OrdinalIgnoreCase)) folder = Path.GetDirectoryName(folder.TrimEnd('\\'))!;
        Destination.Text = folder;
        DestinationChanged();
    }

    private void Destination_LostFocus(object sender, RoutedEventArgs e) => DestinationChanged();

    private void DestinationChanged()
    {
        if (!HasDestination)
        {
            DestinationHint.Text = "Choose a folder: the files go to <folder>\\Field Manual Vault\\<Branch>\\<Type>\\";
            Banner.Visibility = Visibility.Collapsed;
            return;
        }
        var free = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Root)!);
            if (drive.IsReady) free = $" · {VaultRunner.Size(drive.AvailableFreeSpace)} free";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { }
        DestinationHint.Text = $"Files go to {Root}\\<Branch>\\<Type>\\{free}";
        CheckUnfinished();
    }

    private void CheckUnfinished()
    {
        _unfinished = _run == null && HasDestination ? RunState.Load(Root) : null;
        if (_unfinished is not { Phase: not RunPhase.Done } s)
        {
            _unfinished = null;
            Banner.Visibility = Visibility.Collapsed;
            return;
        }
        var files = s.ChosenFiles.ToList();
        var done = files.Count(f => File.Exists(Path.Combine(s.Root, f.RelativePath)));
        var what = s.Phase == RunPhase.Catalog
            ? $"the catalog was being read ({s.Manuals.Count(m => m.Detailed):N0} publications so far)"
            : $"{done:N0} of {files.Count:N0} files downloaded";
        BannerText.Text = $"An unfinished run was found in {s.Root} (started {s.Started:g}): {what} · "
                          + string.Join(", ", s.Collections.Select(Collections.Short)) + " · " + string.Join(" and ", s.Formats) + ".";
        Banner.Visibility = Visibility.Visible;
    }

    private void UpdateCollectionsSummary()
    {
        var chosen = Collections.All.Where(c => _collections[c.Key].IsChecked == true).ToList();
        foreach (var (key, label, _) in Collections.All)
            _collections[key].Content = new DockPanel
            {
                Width = 420,
                Children =
                {
                    new TextBlock { Text = _counts[key].ToString("N0"), Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Right },
                    new TextBlock { Text = label },
                },
            };
        foreach (DockPanel panel in _collections.Values.Select(b => b.Content)) DockPanel.SetDock(panel.Children[0], Dock.Right);
        CollectionsSummary.Text = chosen.Count == 0
            ? "Choose at least one collection"
            : $"{(chosen.Count == 1 ? chosen[0].Label : $"{chosen.Count} of {Collections.All.Length} collections")} · {chosen.Sum(c => _counts[c.Key]):N0} manuals";
    }

    private void UpdateFormatsHint()
    {
        var chosen = _formats.Where(f => f.Value.IsChecked == true).Select(f => f.Key).ToList();
        FormatsHint.Text = chosen.Count switch
        {
            0 => "Choose at least one file type",
            1 => $"Only the {chosen[0]} of each publication is downloaded",
            _ => "Both files of each publication are downloaded",
        };
    }

    private void CollectionsPopup_Closed(object? sender, EventArgs e) => CollectionsToggle.IsChecked = false;

    private async Task RefreshCountsAsync()
    {
        try
        {
            using var site = new VaultSite();
            if (await site.CountsAsync(CancellationToken.None) is { } counts)
            {
                foreach (var (k, v) in counts) _counts[k] = v;
                UpdateCollectionsSummary();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or VaultBlockedException) { }
    }

    // ------------------------------------------------------------------ run

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_unfinished != null)
        {
            var answer = MessageBox.Show(this, "An unfinished run was found in this folder.\n\nYes: resume it.\nNo: start over with the choices above (files already downloaded are kept and not downloaded again).",
                Title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) { Resume_Click(sender, e); return; }
        }
        StartOver_Click(sender, e);
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        if (NewState() is not { } state) return;
        Run(state);
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        if (_unfinished is not { } state) return;
        // The choices of that run come back into the window.
        foreach (var (key, box) in _collections) box.IsChecked = state.Collections.Contains(key);
        foreach (var (key, box) in _formats) box.IsChecked = state.Formats.Contains(key);
        Run(state);
    }

    /// <summary>A new run from the window's choices, or null (with a message) when one is missing.</summary>
    private RunState? NewState()
    {
        if (!HasDestination) return Warn("Choose the destination folder.");
        var collections = Collections.All.Where(c => _collections[c.Key].IsChecked == true).Select(c => c.Key).ToList();
        if (collections.Count == 0) return Warn("Choose at least one collection.");
        var formats = Formats.All.Where(f => _formats[f.Key].IsChecked == true).Select(f => f.Key).ToList();
        if (formats.Count == 0) return Warn("Choose at least one file type.");
        try { Directory.CreateDirectory(Root); }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException) { return Warn($"Can't create {Root}: {x.Message}"); }
        SaveSettings();
        return new RunState { Root = Root, Collections = collections, Formats = formats };
    }

    private RunState? Warn(string message)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private async void Run(RunState state)
    {
        _state = state;
        _unfinished = null;
        Banner.Visibility = Visibility.Collapsed;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var site = new VaultSite { Browser = _browser?.Fetcher, PagesThroughBrowser = _pagesViaBrowser, FilesThroughBrowser = _filesViaBrowser };
        var runner = new VaultRunner(state, new Progress<RunStatus>(Show), Log, site);
        var ct = _cts.Token;
        _run = Task.Run(() => runner.RunAsync(ct), ct);
        try
        {
            await _run;
            SetRunning(false);
            StageText.Text = "Done";
            OverallBar.Value = 1;
            CurrentText.Text = $"Files in {state.Root}";
            TimeText.Text = "—";
            Footer.Text = "Finished";
            MessageBox.Show(this, "Finished. The files are in:\n" + state.Root, Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (_stopReason == StopReason.Paused) { Stopped("Paused · Resume continues where it stopped; it's safe to close"); }
        catch (OperationCanceledException) { Cancelled(); }
        catch (VaultBlockedException x) { Challenged(x); }
        catch (Exception x) when (x is IOException or HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
            Stopped($"Stopped: {x.Message}", $"The run stopped:\n{x.Message}\n\nResume continues where it stopped.");
        }
        finally
        {
            _run = null;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// The site asked for a check: the run pauses and the user may complete it in the built-in browser. Three checks
    /// within an hour usually mean the site wants less traffic, so then the advice is to continue later.
    /// </summary>
    private void Challenged(VaultBlockedException x)
    {
        _blocked = x;
        _challenges.Add(DateTime.Now);
        _challenges.RemoveAll(t => DateTime.Now - t > TimeSpan.FromHours(1));
        var host = x.IsFile ? "The file server" : "The site";
        Stopped($"{host} asked for a browser check · Open the site to verify, or Resume later",
            _challenges.Count >= 3
                ? $"{host} has asked for a browser check {_challenges.Count} times in the last hour. That usually means it wants less traffic: "
                  + "it's best to continue later with Resume.\n\nYou can still complete the check now with \"Open the site to verify\"."
                : $"{host} asked for a browser check (Cloudflare).\n\nPress \"Open the site to verify\" and complete the check in the window that opens, "
                  + "as you would in Edge. The run then continues by itself, a little slower.");
        VerifyButton.Visibility = Visibility.Visible;
        VerifyButton.IsEnabled = true;
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_blocked is not { } blocked || _state == null) return;
        VerifyButton.IsEnabled = false;
        Footer.Text = "Complete the check in the browser window…";
        _browser ??= new BrowserWindow { Owner = this };
        bool passed;
        try { passed = await _browser.VerifyAsync(blocked.Url); }
        catch (Exception x) when (x is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            VerifyButton.IsEnabled = true;
            MessageBox.Show(this, "The built-in browser couldn't start (Microsoft Edge WebView2 Runtime):\n" + x.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!passed)
        {
            VerifyButton.IsEnabled = true;
            Footer.Text = "The check wasn't completed · Open the site to verify, or Resume later";
            return;
        }
        if (blocked.IsFile) _filesViaBrowser = true;
        else _pagesViaBrowser = true;
        Log($"Check completed: {(blocked.IsFile ? "files" : "pages")} now go through the built-in browser, at a slower pace");
        _blocked = null;
        _paused = false;
        Run(_state);
    }

    /// <summary>Stopped but resumable: the Pause button becomes Resume.</summary>
    private void Stopped(string footer, string? message = null)
    {
        _paused = true;
        SetRunning(false);
        PauseButton.Content = "Resume";
        PauseButton.IsEnabled = true;
        CancelButton.IsEnabled = true;
        StageText.Text += " · paused";
        TimeText.Text = "Paused";
        Footer.Text = footer;
        Log(footer);
        if (message != null) MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancelled()
    {
        SetRunning(false);
        _paused = false;
        StageText.Text = "Cancelled";
        TimeText.Text = "—";
        Footer.Text = "Cancelled · the files and progress are kept; Start offers to resume";
        Log("Cancelled");
        CheckUnfinished();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_paused && _state != null)
        {
            _paused = false;
            PauseButton.Content = "Pause";
            Run(_state);
            return;
        }
        _stopReason = StopReason.Paused;
        PauseButton.IsEnabled = false;
        Footer.Text = "Pausing…";
        _cts?.Cancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop the run? The files downloaded so far and the progress are kept.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_paused)
        {
            Cancelled();
            return;
        }
        _stopReason = StopReason.Cancelled;
        _cts?.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Help_Click(object sender, RoutedEventArgs e) => InfoWindow.ShowHelp(this);

    private void About_Click(object sender, RoutedEventArgs e) => InfoWindow.ShowAbout(this);

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _browser?.CloseForGood();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_run is not { IsCompleted: false } run) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "Pause the run and close? The next start offers to resume it.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _stopReason = StopReason.Paused;
        _cts?.Cancel();
        try { await run; }
        catch (Exception) { /* stopped: the state is saved */ }
        _run = null;
        Close();
    }

    private void SetRunning(bool running)
    {
        if (running) VerifyButton.Visibility = Visibility.Collapsed;
        Choices.IsEnabled = !running;
        StartButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        CancelButton.IsEnabled = running;
        if (running)
        {
            PauseButton.Content = "Pause";
            Footer.Text = "Progress is saved after every file";
        }
    }

    // ------------------------------------------------------------------ progress

    private void Show(RunStatus s)
    {
        StageText.Text = s.Stage;
        OverallBar.Value = s.Overall;
        OverallPercent.Text = $"{s.Overall:P0}";
        CurrentText.Text = s.Current;
        CurrentBar.Value = s.CurrentPercent;
        CurrentPercent.Text = s.CurrentPercent > 0 ? $"{s.CurrentPercent:P0}" : "";
        ManualsText.Text = s.ManualsTotal > 0 ? $"{s.ManualsDone:N0} / {s.ManualsTotal:N0}" : "—";
        FilesText.Text = s.FilesTotal > 0 ? $"{s.FilesDone:N0} / {s.FilesTotal:N0}" : "—";
        BytesText.Text = s.BytesTotal > 0 ? $"{VaultRunner.Size(s.BytesDone)} / {VaultRunner.Size(s.BytesTotal)}" : "—";
        TimeText.Text = s.TimeLeft is { } t
            ? t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : "< 1 min"
            : "—";
    }

    private void Log(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (_log.Count > 500) _log.RemoveAt(0);
            LogList.ScrollIntoView(_log[^1]);
        });
    }
}
