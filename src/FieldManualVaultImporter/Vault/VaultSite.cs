using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace FieldManualVaultImporter.Vault;

/// <summary>
/// The site answered with a bot check (Cloudflare challenge) instead of the page or file. The run pauses; the program
/// never answers it. The user can complete it in the built-in browser, which then sends that host's requests.
/// </summary>
public sealed class VaultBlockedException : Exception
{
    public VaultBlockedException(string url) : base("The site asked for a browser check (Cloudflare).") => Url = url;

    /// <summary>What was being read: the check is completed on this address.</summary>
    public string Url { get; }

    /// <summary>The file server challenged (not the Vault's pages).</summary>
    public bool IsFile => !new Uri(Url).Host.Equals(new Uri(VaultSite.Base).Host, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the T.REX ARMS Field Manual Vault the way a visitor does: the collection listings (?coll=…&amp;page=N) and each
/// publication's page, whose HTML carries its details for hydration. robots.txt allows these pages and disallows the
/// site's data endpoints (/api, __data.json), which aren't used. One page request at a time, at least a second apart.
/// </summary>
public sealed partial class VaultSite : IDisposable
{
    public const string Base = "https://www.trex-arms.com";

    private readonly HttpClient _http;
    private DateTime _lastPage = DateTime.MinValue;

    public VaultSite()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FieldManualVaultImporter", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(personal library import; one request at a time)"));
    }

    public HttpClient Http => _http;

    /// <summary>The built-in browser in which the user completed a check, if any.</summary>
    public IBrowserFetcher? Browser { get; init; }
    /// <summary>The Vault's pages are read through <see cref="Browser"/> (they were challenged).</summary>
    public bool PagesThroughBrowser { get; init; }
    /// <summary>The files are downloaded through <see cref="Browser"/> (the file server was challenged).</summary>
    public bool FilesThroughBrowser { get; init; }
    /// <summary>After any challenge, everything goes slower: two seconds between pages and between files.</summary>
    public bool Slow => PagesThroughBrowser || FilesThroughBrowser;

    private TimeSpan PageGap => TimeSpan.FromSeconds(Slow ? 2 : 1);

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------ pages

    private async Task<string> PageAsync(string pathAndQuery, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var wait = _lastPage + PageGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastPage = DateTime.UtcNow;
            if (PagesThroughBrowser && Browser != null)
            {
                var r = await Browser.GetAsync(Base + pathAndQuery, null, null, binary: false, ct);
                if (r.Mitigated || IsChallengePage(r.Text ?? "")) throw new VaultBlockedException(Base + pathAndQuery);
                if (r.Status is 429 or 503 or 0 && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30 * attempt), ct);
                    continue;
                }
                if (r.Status is < 200 or >= 300) throw new HttpRequestException($"Response status code does not indicate success: {r.Status}.", null, (HttpStatusCode)r.Status);
                return r.Text ?? "";
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var response = await _http.GetAsync(Base + pathAndQuery, timeout.Token);
                ThrowIfChallenge(response, Base + pathAndQuery);
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable && attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30 * attempt), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync(timeout.Token);
                if (IsChallengePage(html)) throw new VaultBlockedException(Base + pathAndQuery);
                return html;
            }
            catch (Exception e) when (attempt < 4 && !ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException
                                      && (e as HttpRequestException)?.StatusCode is null or >= HttpStatusCode.InternalServerError)
            {
                await Task.Delay(TimeSpan.FromSeconds(10 * attempt), ct);
            }
        }
    }

    public static void ThrowIfChallenge(HttpResponseMessage response, string url)
    {
        if (response.Headers.TryGetValues("cf-mitigated", out var v) && v.Any(x => x.Contains("challenge", StringComparison.OrdinalIgnoreCase)))
            throw new VaultBlockedException(url);
    }

    public static bool IsChallengePage(string html) =>
        html.Length < 60_000 && (html.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase) || html.Contains("cf-chl-", StringComparison.Ordinal));

    /// <summary>The manual counts the Vault shows next to each collection, or null if they can't be read.</summary>
    public async Task<Dictionary<string, int>?> CountsAsync(CancellationToken ct)
    {
        var html = await PageAsync("/field-manual-vault", ct);
        var counts = new Dictionary<string, int>();
        foreach (var (key, label, _) in Collections.All)
        {
            var name = key == "historic" ? "Historic" : label;
            var m = Regex.Match(html, Regex.Escape(name) + @"\s*(?:<[^>]*>\s*)*([\d,]+)\s*<");
            if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var n)) counts[key] = n;
        }
        return counts.Count == Collections.All.Length ? counts : null;
    }

    /// <summary>The publications on one listing page and the highest page number its pager links to.</summary>
    public async Task<(List<string> Slugs, int LastPage)> ListAsync(string collection, int page, CancellationToken ct)
    {
        var html = await PageAsync($"/field-manual-vault?coll={collection}" + (page > 1 ? $"&page={page}" : ""), ct);
        var slugs = SlugLink().Matches(html).Select(m => m.Groups[1].Value)
            .Where(s => s.Any(char.IsDigit)).Distinct().ToList();   // publication slugs carry a number; faq, citations… don't
        var last = PageLink().Matches(html).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(page).Max();
        return (slugs, last);
    }

    /// <summary>Reads a publication's page into <paramref name="m"/>.</summary>
    public async Task ReadManualAsync(VaultManual m, CancellationToken ct)
    {
        var html = await PageAsync("/field-manual-vault/" + m.Slug, ct);
        var at = html.IndexOf("data:{pub:{", StringComparison.Ordinal);
        if (at < 0) throw new FormatException("The page has no publication data.");
        if (JsLiteral.Parse(html, at + "data:{pub:".Length) is not Dictionary<string, object?> pub) throw new FormatException("Unreadable publication data.");

        m.Id = Str(pub, "id") ?? m.Slug;
        m.Designation = Str(pub, "designation") ?? "";
        m.PubType = Str(pub, "pub_type") ?? "";
        m.Branch = Str(pub, "branch") ?? "";
        m.BranchLabel = Str(pub, "branch_label") ?? Culture.TextInfo.ToTitleCase(m.Branch);
        m.Services = (pub.GetValueOrDefault("services") as List<object?>)?.OfType<string>().ToList() ?? new();
        m.EditionDate = Str(pub, "edition_date");
        m.ChangeLevel = Str(pub, "change_level");
        m.Status = Str(pub, "status");
        m.Pages = pub.GetValueOrDefault("page_count") is long p and > 0 ? (int)p : null;
        m.Function = Str(pub, "functional_category");
        m.Echelon = Str(pub, "echelon");
        m.Purpose = Str(pub, "purpose");
        m.Proponent = Str(pub, "proponent");
        m.Toc = Toc(pub.GetValueOrDefault("toc") as List<object?>);
        m.Title = TitleFrom(html, m.Designation) ?? Culture.TextInfo.ToTitleCase((Str(pub, "title") ?? m.Slug).ToLowerInvariant());

        var sizes = (pub.GetValueOrDefault("assets") as List<object?>)?.OfType<Dictionary<string, object?>>()
            .ToDictionary(a => (Str(a, "format") ?? "").ToUpperInvariant(), a => a.GetValueOrDefault("file_size") is long n ? n : 0)
            ?? new Dictionary<string, long>();
        m.Files.Clear();
        foreach (Match d in DownloadLink().Matches(html))
        {
            var url = WebUtility.HtmlDecode(d.Groups[1].Value);
            var format = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToUpperInvariant();
            if (format is not ("PDF" or "EPUB") || m.Files.Any(f => f.Format == format)) continue;
            var name = WebUtility.HtmlDecode(d.Groups[2].Value);
            if (string.IsNullOrWhiteSpace(name)) name = $"{m.Designation} {m.Title}.{format.ToLowerInvariant()}";
            m.Files.Add(new VaultFile
            {
                Format = format,
                Url = url,
                RelativePath = Path.Combine(SafeName(m.BranchLabel), SafeName(m.PubType.Length > 0 ? m.PubType : "Other"), SafeName(name)),
                Size = sizes.GetValueOrDefault(format),
            });
        }
        var image = OgImage().Match(html);
        m.CoverUrl = pub.GetValueOrDefault("has_thumb") is true && image.Success ? WebUtility.HtmlDecode(image.Groups[1].Value) : null;
        m.CoverFile = m.CoverUrl != null ? Path.Combine(".covers", SafeName(m.Id) + Path.GetExtension(new Uri(m.CoverUrl).AbsolutePath)) : null;
        m.Error = null;
        m.Detailed = true;
    }

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");

    private static string? Str(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) switch
    {
        string s when s.Trim().Length > 0 => s.Trim(),
        long n => n.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>The title as the page shows it ("TC 3-21.76: Ranger Handbook | …" → "Ranger Handbook"); the data has it in capitals.</summary>
    private static string? TitleFrom(string html, string designation)
    {
        var m = OgTitle().Match(html);
        if (!m.Success) return null;
        var t = WebUtility.HtmlDecode(m.Groups[1].Value);
        var bar = t.LastIndexOf(" | ", StringComparison.Ordinal);
        if (bar > 0) t = t[..bar];
        if (designation.Length > 0 && t.StartsWith(designation + ": ", StringComparison.OrdinalIgnoreCase)) t = t[(designation.Length + 2)..];
        return t.Trim().Length > 0 ? t.Trim() : null;
    }

    /// <summary>[[level, text, page], …] → one line per entry, indented by level, with its page.</summary>
    private static string? Toc(List<object?>? toc)
    {
        if (toc == null || toc.Count == 0) return null;
        var lines = toc.OfType<List<object?>>().Where(e => e.Count >= 2 && e[1] is string).Select(e =>
        {
            var level = e[0] is long l ? (int)Math.Clamp(l, 1, 6) : 1;
            var page = e.Count > 2 && e[2] is long p ? "  " + p.ToString(CultureInfo.InvariantCulture) : "";
            return new string(' ', (level - 1) * 4) + ((string)e[1]!).Trim() + page;
        });
        return string.Join(Environment.NewLine, lines);
    }

    public static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length > 0 ? s : "_";
    }

    [GeneratedRegex("href=\"/field-manual-vault/([a-z0-9-]+)\"")]
    private static partial Regex SlugLink();

    [GeneratedRegex("href=\"/field-manual-vault\\?[^\"]*?page=(\\d+)\"")]
    private static partial Regex PageLink();

    [GeneratedRegex("<a class=\"download[^\"]*\" href=\"([^\"]+)\"[^>]*?download=\"([^\"]*)\"")]
    private static partial Regex DownloadLink();

    [GeneratedRegex("property=\"og:title\" content=\"([^\"]+)\"")]
    private static partial Regex OgTitle();

    [GeneratedRegex("property=\"og:image\" content=\"([^\"]+)\"")]
    private static partial Regex OgImage();
}
