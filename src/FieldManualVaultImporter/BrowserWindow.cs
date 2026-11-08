using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FieldManualVaultImporter.Vault;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace FieldManualVaultImporter;

/// <summary>
/// The built-in browser (WebView2, the Edge engine in Windows 10/11). When the site challenges, the user completes the
/// check here, as in Edge; the window then hides itself and keeps serving requests for the run, so they come from the
/// browser that passed the check. Requests are sent from a page of the host they go to (the Vault page, or a page of
/// the file server), so its cookies go along as for any same-site request.
/// </summary>
public sealed class BrowserWindow : Window
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FieldManualVaultImporter", "WebView2");

    private readonly WebView2 _web = new();
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 10, 16, 10) };
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private TaskCompletionSource<bool>? _verify;
    private TaskCompletionSource<bool>? _navigated;
    private int _nextId;
    private bool _ready;
    private bool _closing;

    public BrowserWindow()
    {
        Title = "Verify the site · Field Manual Vault import";
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Bg");
        _hint.Text = "Complete the check below as you would in Edge. This window closes by itself when the site shows its page, and the run continues.";
        var panel = new DockPanel();
        var top = new Border { Background = (System.Windows.Media.Brush)FindResource("Bg.Chrome"), Child = _hint };
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(_web);
        Content = panel;
    }

    /// <summary>Shows <paramref name="url"/> until its real content loads (true) or the user closes the window (false).</summary>
    public async Task<bool> VerifyAsync(string url)
    {
        _verify = new TaskCompletionSource<bool>();
        Show();
        Activate();
        await EnsureReadyAsync();
        _web.CoreWebView2.Navigate(url);
        var passed = await _verify.Task;
        _verify = null;
        Hide();
        return passed;
    }

    private async Task EnsureReadyAsync()
    {
        if (_ready) return;
        var env = await CoreWebView2Environment.CreateAsync(null, DataFolder);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.NavigationCompleted += OnNavigationCompleted;
        core.WebMessageReceived += OnWebMessage;
        // A challenged file: once the check is passed the file itself loads. A download means the check is passed.
        core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            _verify?.TrySetResult(true);
        };
        _ready = true;
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _navigated?.TrySetResult(e.IsSuccess);
        if (_verify == null) return;
        string title;
        try { title = JsonSerializer.Deserialize<string>(await _web.CoreWebView2.ExecuteScriptAsync("document.title")) ?? ""; }
        catch (Exception) { return; }
        // The challenge page ("Just a moment…") reloads into the real page once the check is passed.
        if (e.IsSuccess && !title.StartsWith("Just a moment", StringComparison.OrdinalIgnoreCase) && !title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase))
            _verify?.TrySetResult(true);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing) return;
        e.Cancel = true;   // kept for the run's requests; closed with the main window
        _verify?.TrySetResult(false);
        Hide();
    }

    public void CloseForGood()
    {
        _closing = true;
        foreach (var p in _pending.Values) p.TrySetCanceled();
        Close();
    }

    // ------------------------------------------------------------------ requests for the run

    /// <summary>Thread-safe: the request runs on the window's thread.</summary>
    public IBrowserFetcher Fetcher => new FetcherImpl(this);

    private sealed class FetcherImpl(BrowserWindow w) : IBrowserFetcher
    {
        public Task<BrowserResponse> GetAsync(string url, long? from, long? to, bool binary, CancellationToken ct) =>
            w.Dispatcher.InvokeAsync(() => w.GetAsync(url, from, to, binary, ct)).Task.Unwrap();
    }

    private async Task<BrowserResponse> GetAsync(string url, long? from, long? to, bool binary, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            await EnsureReadyAsync();
            await EnsureOriginAsync(url, ct);
            var id = ++_nextId;
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var range = from is { } f ? $"headers: {{ Range: 'bytes={f}-{to}' }}," : "";
            var read = binary
                ? """
                  const b = await r.blob();
                  const d = await new Promise((ok, no) => { const fr = new FileReader(); fr.onload = () => ok(fr.result); fr.onerror = () => no(fr.error); fr.readAsDataURL(b); });
                  out.data = d.slice(d.indexOf(',') + 1);
                  """
                : "out.text = await r.text();";
            var script = $$"""
                (async () => {
                  const out = { id: {{id}} };
                  try {
                    const r = await fetch({{JsonSerializer.Serialize(url)}}, { {{range}} cache: 'no-store', credentials: 'same-origin' });
                    out.status = r.status;
                    out.type = r.headers.get('content-type');
                    out.range = r.headers.get('content-range');
                    out.mitigated = (r.headers.get('cf-mitigated') || '').includes('challenge');
                    {{read}}
                  } catch (e) { out.status = 0; out.error = String(e); }
                  chrome.webview.postMessage(out);
                })();
                """;
            await _web.CoreWebView2.ExecuteScriptAsync(script);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            JsonElement m;
            try { m = await tcs.Task.WaitAsync(timeout.Token); }
            finally { _pending.Remove(id); }
            var status = m.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
            if (status == 0) throw new HttpRequestException("The browser couldn't reach the site: " + (m.TryGetProperty("error", out var err) ? err.GetString() : "no answer"));
            return new BrowserResponse(status, Str(m, "type"), Str(m, "range"),
                m.TryGetProperty("mitigated", out var mit) && mit.ValueKind == JsonValueKind.True,
                Str(m, "text"), Str(m, "data") is { } b64 ? Convert.FromBase64String(b64) : null);
        }
        finally { _one.Release(); }
    }

    private static string? Str(JsonElement m, string name) => m.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The browser shows a page of the request's host, so the request is same-site and carries its cookies.</summary>
    private async Task EnsureOriginAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url);
        if (_web.CoreWebView2.Source is { Length: > 0 } src && Uri.TryCreate(src, UriKind.Absolute, out var at)
            && at.Scheme == target.Scheme && at.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase)) return;
        var landing = target.Host.Equals(new Uri(VaultSite.Base).Host, StringComparison.OrdinalIgnoreCase)
            ? VaultSite.Base + "/field-manual-vault/faq"
            : target.GetLeftPart(UriPartial.Authority) + "/";
        _navigated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _web.CoreWebView2.Navigate(landing);
        await _navigated.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
        _navigated = null;
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = JsonDocument.Parse(e.WebMessageAsJson).RootElement.Clone();
            if (m.TryGetProperty("id", out var id) && _pending.TryGetValue(id.GetInt32(), out var tcs)) tcs.TrySetResult(m);
        }
        catch (JsonException) { }
    }
}
