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
                await ApplyAsync(r.Catalog!, generation);
                if (generation != _generation) { _again = true; return; }
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
                _app.ReportAuthError();
                break;
            case CatalogFetchKind.Unavailable:
                LastError = "This server has no topic catalog";
                break;
            default:
                LastError = r.Error;
                Log.Write($"catalog: {r.Error}");
                break;
        }
        if (!AuthError) _app.AuthMaybeRecovered();
        _app.NotifyChanged();
    }

    /// <summary>Backfill first, then save the topics: the save reconnects the stream, and the
    /// replayed history of a new topic must already be marked seen (and is never notified).</summary>
    private async Task ApplyAsync(Catalog catalog, int generation)
    {
        var before = _app.Settings;
        var result = CatalogSync.Reconcile(before.Topics, catalog);
        // Take the server's sync topic as-is (empty = none), as ntfy-bar does.
        var syncTopic = CatalogSync.SyncTopicOf(catalog);

        var now = DateTimeOffset.UtcNow;
        foreach (var topic in result.Added) _app.Store.QuietHistory(topic, now);
        if (result.Added.Count > 0)
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(result.Added.Select(async topic =>
            {
                await gate.WaitAsync();
                try { await BackfillAsync(topic, before); }
                finally { gate.Release(); }
            }));
            if (generation != _generation) return; // account/server changed while backfilling
        }

        if (result.Changed || syncTopic != _app.Settings.SyncTopic)
        {
            Log.Write($"catalog: v{catalog.Version} +{result.Added.Count} -{result.Removed.Count} ~{result.Updated.Count}");
            // Re-reconcile against the current list so a mute/enable made while backfilling isn't lost.
            var latest = CatalogSync.Reconcile(_app.Settings.Topics, catalog);
            _app.Update(s => s with { Topics = latest.Topics, SyncTopic = syncTopic });
        }

        foreach (var app in catalog.Apps)
            if (TextUtil.TryAbsoluteUri(app.Icon, out var icon) && IconRules.IsAllowedIcon(icon, before.ServerUri))
                _ = _app.Icons.FileAsync(icon, _app.AuthorizationFor(icon), IconRules.MaxIconBytes);
    }

    private async Task BackfillAsync(string topic, AppSettings settings)
    {
        try
        {
            var messages = await CatalogSync.BackfillAsync(_app.Http, settings.BaseUrl, topic, _app.Authorization, BackfillSince, CancellationToken.None);
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
