using System.Text.Json.Serialization;

namespace NtfyBar.Core;

public sealed record ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; init; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; init; } = "";
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("draft")] public bool Draft { get; init; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; init; }
}

public static class UpdateCheck
{
    public const string LatestUrl = "https://api.github.com/repos/KudcraftsHQ/ntfy-bar-windows/releases/latest";
    public const string ReleasesPage = "https://github.com/KudcraftsHQ/ntfy-bar-windows/releases";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>"v1.2.3", "1.2", "v1.2.3-rc.1+abc" → 1.2.3 (pre-release/build suffixes dropped).</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().TrimStart('v', 'V');
        var cut = t.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0) t = t[..cut];
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var nums = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return null;
        return new Version(nums[0], nums[1], nums[2]);
    }

    public static bool IsNewer(string? latestTag, string? current)
    {
        var l = ParseVersion(latestTag);
        var c = ParseVersion(current);
        return l is not null && c is not null && l > c;
    }

    public static async Task<ReleaseInfo?> FetchLatestAsync(HttpClient http, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "ntfy-bar-windows");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var r = Json.Parse<ReleaseInfo>(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return r is { Draft: false, Prerelease: false } ? r : null;
    }
}
