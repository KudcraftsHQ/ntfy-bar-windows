using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Catalog sync loop (spec §8.3 / §9.3 / §14.3): on launch, every 15 min with ETag,
/// on the sync-topic signal, on resume / network change, and on every stream (re)connect.
/// Only an HTTP 200 reconciles.</summary>
internal sealed class CatalogService : IDisposable
{
    private const string BackfillSince = "7d";

    private readonly AppController _app;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)CatalogSync.Interval.TotalMilliseconds };
    private bool _running;
    private bool _again;
    private string? _etag;
    private int _generation;

    public DateTimeOffset? LastSync { get; private set; }
    public string? LastError { get; private set; }
    public bool AuthError { get; private set; }
    public bool InProgress => _running;

    public CatalogService(AppController app) => _app = app;

    public void Start()
    {
        _timer.Tick += (_, _) => Kick();
        _timer.Start();
        Kick();
    }

    /// <summary>Server, account or the catalog switch changed: forget the ETag and fetch again.</summary>
    public void Reset()
    {
        _etag = null;
        _generation++;
        AuthError = false;
        LastError = null;
        Kick();
    }

    public void Kick()
    {
        if (_running) { _again = true; return; }
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        _running = true;
        try
        {
            do
            {
                _again = false;
                await SyncOnceAsync();
            } while (_again);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task SyncOnceAsync()
    {
        var s = _app.Settings;
        if (!s.IsCatalogEnabled || !s.HasServer) return;
        var auth = _app.Authorization;
        if (auth is null)
        {
            LastError = "Sign in to see your topics";
            _app.NotifyChanged();
            return;
        }

        var generation = _generation;
        var r = await CatalogSync.FetchAsync(_app.Http, s.BaseUrl, auth, _etag, CancellationToken.None);
        if (generation != _generation) { _again = true; return; } // server/credentials changed mid-flight

        switch (r.Kind)
        {
            case CatalogFetchKind.Ok:
                Apply(r.Catalog!);
                _etag = r.ETag;
                LastSync = DateTimeOffset.Now;
                LastError = null;
                AuthError = false;
                break;
            case CatalogFetchKind.NotModified:
                LastSync = DateTimeOffset.Now;
                LastError = null;
                AuthError = false;
                break;
            case CatalogFetchKind.AuthError:
                AuthError = true;
                LastError = "Sign in again: the server refused the saved credentials";
                Log.Write($"catalog: auth error HTTP {r.Status}");
                break;
            case CatalogFetchKind.Unavailable:
                LastError = "This server has no topic catalog";
                break;
            default:
                LastError = r.Error;
                Log.Write($"catalog: {r.Error}");
                break;
        }
        _app.NotifyChanged();
    }

    private void Apply(Catalog catalog)
    {
        var before = _app.Settings;
        var result = CatalogSync.Reconcile(before.Topics, catalog);
        var syncTopic = string.IsNullOrEmpty(catalog.SyncTopic) ? before.SyncTopic : catalog.SyncTopic;
        if (result.Changed || syncTopic != before.SyncTopic)
        {
            Log.Write($"catalog: v{catalog.Version} +{result.Added.Count} -{result.Removed.Count} ~{result.Updated.Count}");
            _app.Update(s => s with { Topics = result.Topics, SyncTopic = syncTopic });
        }

        foreach (var app in catalog.Apps)
            if (TextUtil.TryAbsoluteUri(app.Icon, out var icon) && IconRules.IsAllowedIcon(icon, before.ServerUri))
                _ = _app.Icons.FileAsync(icon, _app.AuthorizationFor(icon), IconRules.MaxIconBytes);

        foreach (var topic in result.Added) _ = BackfillAsync(topic);
    }

    private async Task BackfillAsync(string topic)
    {
        try
        {
            var s = _app.Settings;
            var messages = await CatalogSync.BackfillAsync(_app.Http, s.BaseUrl, topic, _app.Authorization, BackfillSince, CancellationToken.None);
            Log.Write($"catalog: backfilled {messages.Count} messages for {topic}");
            _app.IngestBackfill(messages);
        }
        catch (Exception e)
        {
            Log.Write($"catalog: backfill {topic} failed: {e.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}
