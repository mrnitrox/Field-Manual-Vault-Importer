using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace FieldManualVaultImporter.Vault;

/// <summary>What the window shows while a run is going.</summary>
public sealed record RunStatus(
    string Stage, double Overall, string Current, double CurrentPercent,
    int ManualsDone, int ManualsTotal, int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, TimeSpan? TimeLeft);

/// <summary>Why a run stopped before the end.</summary>
public enum StopReason { Paused, Cancelled }

/// <summary>
/// One run: catalog (listing pages, then each publication's page) → download (the chosen file types of each). Every
/// step can be stopped at any point and picks up where it left off: the catalog is saved as it grows, and downloads go
/// to .part files that continue with an HTTP range request.
/// </summary>
public sealed class VaultRunner
{
    private const int SaveEvery = 20;
    /// <summary>Chunk size of a download through the built-in browser (passed to the program as base64).</summary>
    private const int BrowserChunk = 4 << 20;

    private readonly RunState _state;
    private readonly IProgress<RunStatus> _status;
    private readonly Action<string> _log;
    private readonly VaultSite _site;
    private readonly Stopwatch _clock = new();
    private readonly Queue<(TimeSpan At, long Bytes)> _speed = new();
    private long _bytesDone;
    private long _bytesThisSession;
    private int _savedAfter;

    public VaultRunner(RunState state, IProgress<RunStatus> status, Action<string> log, VaultSite? site = null)
    {
        _site = site ?? new VaultSite();
        _state = state;
        _status = status;
        _log = log;
    }

    public RunState State => _state;

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _clock.Start();
            if (_state.Phase == RunPhase.Catalog)
            {
                await CatalogAsync(ct);
                _state.Phase = RunPhase.Download;
                _state.Save();
            }
            if (_state.Phase == RunPhase.Download)
            {
                await DownloadAsync(ct);
                _state.Phase = RunPhase.Done;
                _state.Save();
            }
        }
        finally
        {
            _site.Dispose();
            TrySave();
        }
    }

    private void TrySave()
    {
        try { _state.Save(); }
        catch (IOException e) { _log($"Couldn't save the progress: {e.Message}"); }
    }

    // ------------------------------------------------------------------ 1. catalog

    private async Task CatalogAsync(CancellationToken ct)
    {
        foreach (var coll in _state.Collections.Where(c => !_state.ListedCollections.Contains(c)))
        {
            var label = Collections.Label(coll);
            var page = _state.PagesRead.GetValueOrDefault(coll) + 1;
            var last = page;
            while (true)
            {
                Report($"1. Catalog · {label} · page {page} of {Math.Max(last, page)}", CatalogFraction(), "Reading the list of publications", 0);
                var (slugs, lastPage) = await _site.ListAsync(coll, page, ct);
                last = Math.Max(last, lastPage);
                var known = _state.Manuals.Select(m => m.Slug).ToHashSet();
                var added = slugs.Where(known.Add).Select(s => new VaultManual { Slug = s, Collection = coll }).ToList();
                _state.Manuals.AddRange(added);
                _state.PagesRead[coll] = page;
                _state.Save();
                if (slugs.Count == 0 || page >= last) break;
                page++;
            }
            _state.ListedCollections.Add(coll);
            _state.Save();
            _log($"{label}: {_state.Manuals.Count(m => m.Collection == coll):N0} publications listed");
        }

        var pending = _state.Manuals.Where(m => !m.Detailed && _state.Collections.Contains(m.Collection)).ToList();
        var done = 0;
        foreach (var m in pending)
        {
            Report($"1. Catalog · publication {done + 1:N0} of {pending.Count:N0}", CatalogFraction(), m.Slug, 0);
            try
            {
                await _site.ReadManualAsync(m, ct);
                Unique(m);
            }
            catch (Exception e) when (e is HttpRequestException or FormatException or InvalidCastException or KeyNotFoundException or IndexOutOfRangeException)
            {
                m.Error = e.Message;
                _log($"Couldn't read {m.Slug}: {e.Message}");
            }
            if (++done % SaveEvery == 0) _state.Save();
        }
        _state.Save();
        var failed = _state.Manuals.Count(m => !m.Detailed);
        _log($"Catalog: {_state.Chosen.Count():N0} publications, {_state.ChosenFiles.Count():N0} files, {Size(_state.ChosenFiles.Sum(f => f.Size))}"
             + (failed > 0 ? $" · {failed} couldn't be read (retried on the next resume)" : ""));
    }

    private double CatalogFraction()
    {
        var pages = _state.Collections.Sum(c => Math.Max(1, (Collections.All.FirstOrDefault(x => x.Key == c).Count + 29) / 30));
        var read = _state.Collections.Sum(c => _state.PagesRead.GetValueOrDefault(c));
        var details = _state.Manuals.Count(m => m.Detailed);
        var total = pages + Math.Max(_state.Manuals.Count, 1);
        return Math.Clamp((double)(read + details) / total, 0, 1);
    }

    /// <summary>Two publications whose files would get the same name: the later one gets its Vault id added.</summary>
    private void Unique(VaultManual m)
    {
        var taken = _state.Manuals.Where(o => o != m && o.Detailed).SelectMany(o => o.Files)
            .Select(f => f.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in m.Files.Where(f => taken.Contains(f.RelativePath)))
            f.RelativePath = Path.Combine(Path.GetDirectoryName(f.RelativePath)!,
                Path.GetFileNameWithoutExtension(f.RelativePath) + $" [{VaultSite.SafeName(m.Id)}]" + Path.GetExtension(f.RelativePath));
    }

    // ------------------------------------------------------------------ 2. download

    /// <summary>
    /// Complete: a file only gets its final name once it has every byte the server announced. (The sizes the Vault lists
    /// can be a few KB off the real files, so they aren't compared.)
    /// </summary>
    public bool IsDone(VaultFile f) => File.Exists(FullPath(f));

    private string FullPath(VaultFile f) => Path.Combine(_state.Root, f.RelativePath);

    private async Task DownloadAsync(CancellationToken ct)
    {
        var manuals = _state.Chosen.ToList();
        var files = _state.ChosenFiles.ToList();
        foreach (var f in files) f.Missing = false;   // a file the site lacked last time is tried again
        var check = files.Count(f => File.Exists(FullPath(f)) || File.Exists(FullPath(f) + ".part"));
        if (check > 0)
        {
            Report("2. Download · checking the files on disk", 0, $"Checking {check:N0} files…", 0);
            await Task.Run(() => _bytesDone = files.Where(IsDone).Sum(f => new FileInfo(FullPath(f)).Length), ct);
            var partial = files.Count(f => !IsDone(f) && File.Exists(FullPath(f) + ".part"));
            _log($"On disk: {files.Count(IsDone):N0} complete files" + (partial > 0 ? $", {partial} partial (continued from where they stopped)" : ""));
        }

        foreach (var m in manuals)
        {
            foreach (var f in _state.FilesOf(m).Where(f => !f.Missing && !IsDone(f)))
            {
                _folder = Path.GetDirectoryName(f.RelativePath)!.Replace(Path.DirectorySeparatorChar.ToString(), " › ");
                ct.ThrowIfCancellationRequested();
                await DownloadFileAsync(f, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(_site.Slow ? 2000 : 300), ct);
            }
        }
        var missing = files.Where(f => f.Missing).ToList();
        _log(missing.Count == 0 ? "All files downloaded" : $"Downloaded; {missing.Count} files aren't on the site (404)");
    }

    private async Task DownloadFileAsync(VaultFile f, CancellationToken ct)
    {
        var path = FullPath(f);
        var part = path + ".part";
        var name = Path.GetFileName(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (_site.FilesThroughBrowser && _site.Browser != null)
                {
                    if (!await BrowserDownloadAsync(f, part, name, ct)) return;
                    File.Move(part, path, overwrite: true);
                    Downloaded(f, new FileInfo(path).Length, name);
                    return;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, f.Url);
                if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
                using var response = await _site.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                VaultSite.ThrowIfChallenge(response, f.Url);
                if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType == "text/html")
                    throw new VaultBlockedException(f.Url);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    f.Missing = true;
                    _log($"Not on the site (404): {name}");
                    return;
                }
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    File.Delete(part);   // the partial file doesn't match the one on the site any more
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.StatusCode != HttpStatusCode.PartialContent) have = 0;   // no range support: from the start
                var total = response.Content.Headers.ContentRange?.Length ?? (have + response.Content.Headers.ContentLength) ?? f.Size;
                if (f.Size <= 0 && total > 0) f.Size = total;

                await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                await using (var input = await response.Content.ReadAsStreamAsync(ct))
                {
                    var buffer = new byte[1 << 17];
                    var written = have;
                    var lastReport = TimeSpan.Zero;
                    int n;
                    while ((n = await input.ReadAsync(buffer, ct)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), ct);
                        written += n;
                        _bytesDone += n;
                        _bytesThisSession += n;
                        if (_clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                        {
                            lastReport = _clock.Elapsed;
                            ReportDownload(name, written, total);
                        }
                    }
                }
                var length = new FileInfo(part).Length;
                if (total > 0 && length != total) throw new IOException($"Incomplete: {length:N0} of {total:N0} bytes");
                File.Move(part, path, overwrite: true);
                Downloaded(f, length, name);
                return;
            }
            catch (IOException e) when ((e.HResult & 0xFFFF) is 0x70 or 0x27)   // disk full
            {
                throw new IOException($"The destination drive is full ({e.Message}).", e);
            }
            catch (Exception e) when (attempt < 5 && !ct.IsCancellationRequested && e is IOException or HttpRequestException or TaskCanceledException
                                      && (e as HttpRequestException)?.StatusCode is null or >= HttpStatusCode.InternalServerError or HttpStatusCode.TooManyRequests)
            {
                _log($"Retrying {name} ({e.Message})");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt * attempt), ct);
            }
        }
    }

    private void Downloaded(VaultFile f, long length, string name)
    {
        _counted = DateTime.MinValue;
        f.Size = length;   // the real size, for the totals (the site's figure can be off or missing)
        if (++_savedAfter % SaveEvery == 0) TrySave();
        _log($"✓ {name}");
    }

    /// <summary>
    /// The file in 4 MB range requests sent by the built-in browser, appended to the .part file, so it continues after a
    /// pause as a direct download does. False when the site doesn't have the file (404).
    /// </summary>
    private async Task<bool> BrowserDownloadAsync(VaultFile f, string part, string name, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (total > 0 && have >= total) break;
            var r = await _site.Browser!.GetAsync(f.Url, have, have + BrowserChunk - 1, binary: true, ct);
            if (r.Mitigated || (r.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ?? false)) throw new VaultBlockedException(f.Url);
            if (r.Status == 404)
            {
                f.Missing = true;
                _log($"Not on the site (404): {name}");
                return false;
            }
            if (r.Status == 416)
            {
                // "bytes */N": nothing left past the end. Complete if the .part has exactly N bytes, else start over.
                if (r.ContentRange is { } all && long.TryParse(all[(all.LastIndexOf('/') + 1)..], out var size) && size == have) { total = size; break; }
                File.Delete(part);   // the partial file doesn't match the one on the site any more
                total = 0;
                continue;
            }
            if (r.Status is < 200 or >= 300 || r.Data == null) throw new HttpRequestException($"Response status code does not indicate success: {r.Status}.");
            if (r.Status == 200) have = 0;   // the whole file came back: no range support
            else if (r.ContentRange is { } cr && cr.LastIndexOf('/') is var slash and > 0 && long.TryParse(cr[(slash + 1)..], out var t)) total = t;
            await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                await output.WriteAsync(r.Data, ct);
            _bytesDone += r.Data.Length;
            _bytesThisSession += r.Data.Length;
            if (r.Status == 200) { total = r.Data.Length; break; }
            if (r.Data.Length == 0) throw new IOException("The server sent an empty range.");
            ReportDownload(name, have + r.Data.Length, total);
        }
        var length = new FileInfo(part).Length;
        if (total > 0 && length != total) throw new IOException($"Incomplete: {length:N0} of {total:N0} bytes");
        return true;
    }

    private void ReportDownload(string name, long written, long total)
    {
        var files = SelectedFiles();
        var bytesTotal = files.Sum(f => f.Size);
        _speed.Enqueue((_clock.Elapsed, _bytesThisSession));
        while (_speed.Count > 2 && _clock.Elapsed - _speed.Peek().At > TimeSpan.FromSeconds(20)) _speed.Dequeue();
        var (at, bytes) = _speed.Peek();
        var seconds = (_clock.Elapsed - at).TotalSeconds;
        var rate = seconds > 1 ? (_bytesThisSession - bytes) / seconds : 0;
        TimeSpan? left = rate > 0 ? TimeSpan.FromSeconds(Math.Max(0, bytesTotal - _bytesDone) / rate) : null;
        var (manualsDone, filesDone) = Counts();
        _status.Report(new RunStatus(
            $"2. Download · {_folder}", bytesTotal > 0 ? (double)_bytesDone / bytesTotal : 0,
            $"{name} · {Size(written)} of {Size(total)}" + (rate > 0 ? $" · {Size((long)rate)}/s" : ""), total > 0 ? (double)written / total : 0,
            manualsDone, ManualsTotal(), filesDone, files.Count, _bytesDone, bytesTotal, left));
    }

    private string _folder = "";

    private List<VaultFile> SelectedFiles() => _state.ChosenFiles.ToList();

    private int ManualsTotal() => _state.Manuals.Count(m => _state.Collections.Contains(m.Collection));

    // Counting done files reads the disk: at most every few seconds, and right after a file completes.
    private (int Manuals, int Files) _counts;
    private DateTime _counted = DateTime.MinValue;

    private (int Manuals, int Files) Counts()
    {
        if (DateTime.UtcNow - _counted < TimeSpan.FromSeconds(3)) return _counts;
        var manuals = _state.Chosen.ToList();
        var done = manuals.Select(m => _state.FilesOf(m).Count(f => !f.Missing && IsDone(f))).ToList();
        _counts = (manuals.Where((m, i) => done[i] == _state.FilesOf(m).Count(f => !f.Missing)).Count(), done.Sum());
        _counted = DateTime.UtcNow;
        return _counts;
    }

    // ------------------------------------------------------------------ reporting

    private void Report(string stage, double overall, string current, double currentPercent)
    {
        var files = SelectedFiles();
        var (manualsDone, filesDone) = _state.Phase == RunPhase.Catalog ? (0, 0) : Counts();
        _status.Report(new RunStatus(stage, overall, current, currentPercent, manualsDone, ManualsTotal(),
            filesDone, files.Count, _bytesDone, files.Sum(f => f.Size), null));
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
