using System.Net;
using System.Net.Http.Headers;

namespace NtfyBar.Core;

public sealed record ReconcileResult(
    List<TopicConfig> Topics,
    List<string> Added,
    List<string> Removed,
    List<string> Updated)
{
    public bool Changed => Added.Count > 0 || Removed.Count > 0 || Updated.Count > 0;
}

public enum CatalogFetchKind { Ok, NotModified, AuthError, Unavailable, Error }

public sealed record CatalogFetchResult(CatalogFetchKind Kind, Catalog? Catalog = null, string? ETag = null, string? Error = null, int Status = 0);

public static class CatalogSync
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Pure reconcile (spec §8.2 / §14.5):
    /// add catalog topics we don't have as managed, enabled, unmuted;
    /// update catalog fields on topics we have, keeping the user's mute/enable;
    /// drop managed topics the catalog no longer lists;
    /// never add, remove, mute or disable an unmanaged (user-added) topic. An unmanaged topic that
    /// is also in the catalog gets its app name/icon/sound so it groups with its app, and loses
    /// them again when the catalog drops it.</summary>
    public static ReconcileResult Reconcile(IReadOnlyList<TopicConfig> current, Catalog catalog)
    {
        var listed = new Dictionary<string, (CatalogApp App, CatalogTopic Topic)>();
        var order = new List<string>();
        foreach (var app in catalog.Apps ?? new())
        foreach (var t in app.Topics ?? new())
        {
            if (string.IsNullOrEmpty(t.Topic) || listed.ContainsKey(t.Topic)) continue;
            listed[t.Topic] = (app, t);
            order.Add(t.Topic);
        }

        var result = new List<TopicConfig>();
        var added = new List<string>();
        var removed = new List<string>();
        var updated = new List<string>();
        var have = new HashSet<string>();

        foreach (var topic in current)
        {
            if (!have.Add(topic.Name)) continue; // de-duplicate defensively
            if (listed.TryGetValue(topic.Name, out var hit))
            {
                var next = Decorate(topic, hit.App, hit.Topic);
                if (next != topic) updated.Add(topic.Name);
                result.Add(next);
            }
            else if (topic.IsManaged)
            {
                removed.Add(topic.Name);
            }
            else
            {
                var next = topic.App is null && topic.AppName is null && topic.AppIcon is null && topic.Sound is null && topic.DisplayName is null
                    ? topic
                    : topic with { App = null, AppName = null, AppIcon = null, Sound = null, DisplayName = null };
                if (next != topic) updated.Add(topic.Name);
                result.Add(next);
            }
        }

        foreach (var name in order)
        {
            if (have.Contains(name)) continue;
            var (app, t) = listed[name];
            result.Add(Decorate(new TopicConfig { Name = name, Enabled = true, Muted = false, Managed = true }, app, t));
            added.Add(name);
        }

        return new ReconcileResult(result, added, removed, updated);
    }

    private static TopicConfig Decorate(TopicConfig topic, CatalogApp app, CatalogTopic t) => topic with
    {
        App = NullIfEmpty(app.Id),
        AppName = NullIfEmpty(app.Name) ?? NullIfEmpty(app.Id),
        AppIcon = NullIfEmpty(app.Icon),
        Sound = SoundClass.Normalize(NullIfEmpty(t.Sound) ?? app.Sound),
        DisplayName = NullIfEmpty(t.Name),
    };

    /// <summary>The server's sync topic, taken as-is: empty means none (ntfy-bar parity).</summary>
    public static string? SyncTopicOf(Catalog catalog) => string.IsNullOrWhiteSpace(catalog.SyncTopic) ? null : catalog.SyncTopic;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary><c>GET {base}/v1/catalog</c> with <c>If-None-Match</c>. Only <see cref="CatalogFetchKind.Ok"/>
    /// may be reconciled; everything else leaves the topic list alone.</summary>
    public static async Task<CatalogFetchResult> FetchAsync(HttpClient http, string baseUrl, string? authorization, string? etag, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/v1/catalog");
            if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            // The ETag is an opaque hash of this user's view (CONTRACT-CHANGES): echo it byte for byte.
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            switch (resp.StatusCode)
            {
                case HttpStatusCode.NotModified:
                    return new CatalogFetchResult(CatalogFetchKind.NotModified, ETag: etag, Status: code);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new CatalogFetchResult(CatalogFetchKind.AuthError, Status: code, Error: $"HTTP {code}");
                case HttpStatusCode.NotFound:
                    return new CatalogFetchResult(CatalogFetchKind.Unavailable, Status: code, Error: "Server has no catalog (HTTP 404)");
            }
            if (code != 200) return new CatalogFetchResult(CatalogFetchKind.Error, Status: code, Error: $"HTTP {code}");
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var catalog = Json.Parse<Catalog>(body);
            if (catalog is null) return new CatalogFetchResult(CatalogFetchKind.Error, Status: code, Error: "Malformed catalog JSON");
            var newTag = resp.Headers.TryGetValues("ETag", out var tags) ? tags.FirstOrDefault() : null;
            return new CatalogFetchResult(CatalogFetchKind.Ok, catalog, newTag, Status: code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return new CatalogFetchResult(CatalogFetchKind.Error, Error: e.Message);
        }
    }

    /// <summary>One-off history for a freshly added managed topic: <c>GET /{topic}/json?poll=1&amp;since=7d</c>.</summary>
    public static async Task<List<NtfyMessage>> BackfillAsync(HttpClient http, string baseUrl, string topic, string? authorization, string since, CancellationToken ct)
    {
        var list = new List<NtfyMessage>();
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(topic)}/json?poll=1&since={Uri.EscapeDataString(since)}");
        if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return list;
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (StreamParser.Parse(line) is { Kind: StreamLineKind.Message, Message: { } m }) list.Add(m);
        }
        return list;
    }
}
