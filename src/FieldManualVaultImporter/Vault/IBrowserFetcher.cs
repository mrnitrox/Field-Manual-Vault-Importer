namespace FieldManualVaultImporter.Vault;

/// <summary>A response read by the built-in browser.</summary>
/// <param name="Mitigated">Cloudflare answered with a challenge (cf-mitigated header, or a challenge page instead of the file).</param>
public sealed record BrowserResponse(int Status, string? ContentType, string? ContentRange, bool Mitigated, string? Text, byte[]? Data);

/// <summary>
/// Requests sent by the built-in browser in which the user completed the site's check: they carry that browser's
/// clearance as any page it loads would. Used only after a challenge, and only for the host that challenged.
/// </summary>
public interface IBrowserFetcher
{
    /// <param name="from">First byte of a range request (with <paramref name="to"/>), or null for the whole resource.</param>
    /// <param name="binary">Return the body as bytes (a file) rather than text (a page).</param>
    Task<BrowserResponse> GetAsync(string url, long? from, long? to, bool binary, CancellationToken ct);
}
