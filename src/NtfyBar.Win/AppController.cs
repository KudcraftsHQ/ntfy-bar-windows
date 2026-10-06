using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>App state: settings, message store, the stream, catalog sync and notifications.
/// The Windows counterpart of ntfy-bar's <c>AppModel</c>. Everything runs on the UI thread.</summary>
internal sealed class AppController : IDisposable
{
    private readonly DateTimeOffset _launch = DateTimeOffset.UtcNow;
    private readonly HttpClient _streamHttp = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    })
    { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>Short requests: catalog, backfill, sign-in, http actions.</summary>
    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
    public IconCache Icons { get; } = new();
    public CatalogService Catalog { get; }
    public MessageStore Store { get; }

    private AppSettings _settings;
    private string _password;
    private string _token;
    private StreamClient? _client;
    private CancellationTokenSource? _streamCts;
    private readonly System.Windows.Forms.Timer _reconnectTimer = new() { Interval = 600 };
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 800 };
    private readonly System.Windows.Forms.Timer _networkTimer = new() { Interval = 2000 };
    private bool _started;

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.NotConfigured;
    public bool HasPassword => _password.Length > 0;
    public bool HasToken => _token.Length > 0;

    /// <summary>Anything visible changed (messages, read state, topics, status).</summary>
    public event Action? Changed;
    public event Action<NtfyMessage>? MessageAdded;

    public AppController()
    {
        var saved = Storage.LoadSettings();
        if (saved is null && File.Exists(Storage.SettingsPath)) Storage.SetAsideUnreadableSettings();
        _settings = saved ?? Storage.ImportCliConfig();
        if (saved is null) Storage.SaveSettings(_settings);
        _password = Credentials.Get(Credentials.Password);
        _token = Credentials.Get(Credentials.Token);
        Store = new MessageStore(Storage.LoadState(), _launch);
        Catalog = new CatalogService(this);

        _reconnectTimer.Tick += (_, _) => { _reconnectTimer.Stop(); RestartStream(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _networkTimer.Tick += (_, _) =>
        {
            _networkTimer.Stop();
            if (!NetworkInterface.GetIsNetworkAvailable()) return;
            Log.Write("network: changed, reconnecting");
            RestartStream();
            Catalog.Kick();
        };
    }

    public AppSettings Settings => _settings;

    public void NotifyChanged() => Changed?.Invoke();

    public void Start()
    {
        if (_started) return;
        _started = true;

        Toasts.Initialize(OnToastActivated);
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        RestartStream();
        Catalog.Start();
    }

    // ---- settings ----

    /// <summary>Applies a settings change, saves it and reconnects / resyncs when the connection changed.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        var old = _settings;
        var next = change(old);
        if (ReferenceEquals(next, old)) return;
        var serverChanged = !string.Equals(next.BaseUrl, old.BaseUrl, StringComparison.OrdinalIgnoreCase);
        var accountChanged = serverChanged || next.Username != old.Username;
        // The sync topic belongs to the old account: streaming it as the new one is a 403 for the whole stream.
        if (accountChanged) next = next with { SyncTopic = null };
        _settings = next;
        Storage.SaveSettings(next);

        var connectionChanged = serverChanged
            || next.Username != old.Username
            || !next.StreamTopics.SequenceEqual(old.StreamTopics);
        if (connectionChanged) ScheduleReconnect();
        if (accountChanged || next.IsCatalogEnabled != old.IsCatalogEnabled) Catalog.Reset();
        Changed?.Invoke();
    }

    public void SetTopic(string name, Func<TopicConfig, TopicConfig> change) =>
        Update(s => s with { Topics = s.Topics.Select(t => t.Name == name ? change(t) : t).ToList() });

    // ---- credentials ----

    public void UpdateCredentials(string? password, string? token)
    {
        StoreCredentials(password, token);
        // New credentials may be a different account: drop its sync topic until the next 200.
        if (_settings.SyncTopic is not null) Update(s => s with { SyncTopic = null });
        CredentialsChanged();
    }

    private void StoreCredentials(string? password, string? token)
    {
        if (password is not null)
        {
            _password = password;
            Credentials.Set(Credentials.Password, _settings.Username, password);
        }
        if (token is not null)
        {
            _token = token;
            Credentials.Set(Credentials.Token, _settings.Username, token);
        }
    }

    private void CredentialsChanged()
    {
        _authPrompted = false;
        RestartStream();
        Catalog.Reset();
        Changed?.Invoke();
    }

    /// <summary>Settings "Save &amp; Reconnect": credentials first, then server/user, then one reconnect.</summary>
    public void Reconfigure(string server, string username, string? password, string? token)
    {
        _reconnectTimer.Stop();
        StoreCredentials(password, token);
        var credsChanged = password is not null || token is not null;
        Update(s => s with { ServerUrl = server, Username = username, SyncTopic = credsChanged ? null : s.SyncTopic });
        CredentialsChanged();
    }

    /// <summary>After sign-in: keep the token, forget the password (spec §8.4). The credentials are
    /// swapped before the server/user change, so the old token never goes to the new server.</summary>
    public void SignedIn(string server, string username, string token)
    {
        _reconnectTimer.Stop();
        StoreCredentials("", token);
        Update(s => s with { ServerUrl = server, Username = username, SyncTopic = null });
        CredentialsChanged();
        Log.Write("signin: token stored");
    }

    // ---- auth errors ----

    private bool _authPrompted;

    /// <summary>The stream or the catalog was refused (401/403).</summary>
    public bool AuthProblem => Status.State == ConnectionState.AuthError || Catalog.AuthError;

    public event Action? SignInRequested;

    /// <summary>Tell the user once per failure episode, with a toast that opens Sign in.</summary>
    public void ReportAuthError()
    {
        Changed?.Invoke();
        if (_authPrompted) return;
        _authPrompted = true;
        Log.Write("auth: credentials refused, asking to sign in again");
        try
        {
            new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                .AddArgument(ToastArgs.Action, ToastArgs.SignIn)
                .AddText("Sign in to ntfy again")
                .AddText($"{_settings.ServerUri?.Host ?? "The server"} refused the saved credentials, so no notifications are arriving.")
                .AddButton(new Microsoft.Toolkit.Uwp.Notifications.ToastButton().SetContent("Sign in…").AddArgument(ToastArgs.Action, ToastArgs.SignIn))
                .Show(t => { t.Tag = "auth"; t.Group = "ntfy-bar"; });
        }
        catch (Exception e) { Log.Write($"auth: toast failed: {e.Message}"); }
    }

    public void AuthMaybeRecovered()
    {
        if (!AuthProblem) AuthRecovered();
    }

    private void AuthRecovered()
    {
        if (!_authPrompted) return;
        _authPrompted = false;
        try { ToastNotificationManagerCompat.History.Remove("auth", "ntfy-bar"); } catch { /* not shown */ }
    }

    public string? Authorization => StreamRules.Authorization(_settings.Username, _password, _token);

    /// <summary>Credentials are only sent to the ntfy server itself, never to third-party hosts.</summary>
    public string? AuthorizationFor(Uri url) =>
        _settings.ServerUri is { } server && IconRules.SameHost(url, server) ? Authorization : null;

    // ---- stream ----

    private void ScheduleReconnect()
    {
        _reconnectTimer.Stop();
        _reconnectTimer.Start();
    }

    public void RestartStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = new CancellationTokenSource();

        var client = new StreamClient(_streamHttp, StreamTarget);
        _client = client;
        client.StatusChanged += s => { if (_client == client) SetStatus(s); };
        client.Opened += () => { if (_client == client) Catalog.Kick(); };
        client.MessageReceived += m => { if (_client == client) Ingest(m); };
        client.Log += Log.Write;
        _ = client.RunAsync(_streamCts.Token);
    }

    private StreamTarget? StreamTarget()
    {
        var s = _settings;
        if (!s.IsConfigured) return null;
        var since = StreamRules.Since(Store.LastMessageId, Store.LastMessageTime, DateTimeOffset.UtcNow);
        var url = StreamRules.StreamUrl(s.BaseUrl, s.StreamTopics, since);
        return url is null ? null : new StreamTarget(url, Authorization);
    }

    private void SetStatus(ConnectionStatus s)
    {
        Status = s;
        if (s.State == ConnectionState.AuthError) ReportAuthError();
        else
        {
            if (s.State == ConnectionState.Connected && !Catalog.AuthError) AuthRecovered();
            Changed?.Invoke();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Program.Ui.Post(_ =>
        {
            Log.Write("wake: reconnecting");
            RestartStream();
            Catalog.Kick();
        }, null);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) =>
        Program.Ui.Post(_ => { _networkTimer.Stop(); _networkTimer.Start(); }, null);

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => OnNetworkChanged(sender, e);

    // ---- messages ----

    private void Ingest(NtfyMessage m)
    {
        var r = Store.Ingest(m, _settings);
        switch (r.Outcome)
        {
            case IngestOutcome.SyncTopic:
                if (r.SyncSignal)
                {
                    Log.Write("sync: event received");
                    Catalog.Kick();
                }
                return;
            case IngestOutcome.Duplicate:
                return;
        }
        Log.Write($"message: id={m.Id} topic={m.Topic} notified={r.Notify}");
        ScheduleSave();
        MessageAdded?.Invoke(m);
        Changed?.Invoke();
        if (r.Notify) _ = NotifyAsync(m);
    }

    /// <summary>History for a freshly added catalog topic: listed, never notified.</summary>
    public void IngestBackfill(IEnumerable<NtfyMessage> messages)
    {
        var added = 0;
        foreach (var m in messages)
            if (Store.Ingest(m, _settings, updateCursor: false).Outcome == IngestOutcome.Added) added++;
        if (added == 0) return;
        ScheduleSave();
        Changed?.Invoke();
    }

    private async Task NotifyAsync(NtfyMessage m)
    {
        try
        {
            var topic = _settings.Topic(m.Topic);
            string? logo = null, image = null;
            if (IconRules.NotificationIcon(m, topic) is { } iconUrl)
                logo = await Icons.FileAsync(iconUrl, AuthorizationFor(iconUrl), IconRules.MaxIconBytes);
            if (m.ImageUrl is { } imageUrl && IconRules.IsAllowedScheme(imageUrl))
                image = await Icons.FileAsync(imageUrl, AuthorizationFor(imageUrl), IconRules.MaxImageBytes);
            // Settings may have changed while the images downloaded.
            if (_settings.IsMuted(m.Topic)) return;
            Toasts.Show(Toasts.Build(m, topic, _settings, logo, image), m);
        }
        catch (Exception e)
        {
            Log.Write($"notification: failed: {e.Message}");
        }
    }

    private void OnToastActivated(ToastArguments args)
    {
        args.TryGetValue(ToastArgs.Action, out string? action);
        args.TryGetValue(ToastArgs.Id, out string? id);
        args.TryGetValue(ToastArgs.Url, out string? url);
        switch (action)
        {
            case ToastArgs.Open:
                if (id is not null) MarkRead(new[] { id });
                if (url is not null) OpenUrl(url);
                else ShowMessagesRequested?.Invoke();
                break;
            case ToastArgs.Http:
                if (id is not null && args.TryGetValue(ToastArgs.Index, out string? idxText) && int.TryParse(idxText, out var idx)) _ = RunHttpActionAsync(id, idx);
                break;
            case ToastArgs.SignIn:
                SignInRequested?.Invoke();
                break;
            case ToastArgs.Update:
                if (url is not null) OpenUrl(url);
                break;
            default:
                ShowMessagesRequested?.Invoke();
                break;
        }
    }

    public event Action? ShowMessagesRequested;

    private async Task RunHttpActionAsync(string id, int index)
    {
        var entry = Store.Find(id);
        if (entry?.Message.Actions is not { } actions || index < 0 || index >= actions.Count) return;
        var a = actions[index];
        if (!TextUtil.TryAbsoluteUri(a.Url, out var url)) return;
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod(string.IsNullOrEmpty(a.Method) ? "POST" : a.Method.ToUpperInvariant()), url);
            if (a.Body is not null) req.Content = new StringContent(a.Body);
            foreach (var (k, v) in a.Headers ?? new())
                if (!req.Headers.TryAddWithoutValidation(k, v)) req.Content?.Headers.TryAddWithoutValidation(k, v);
            using var resp = await Http.SendAsync(req);
            Log.Write($"action: http {(int)resp.StatusCode} for message {id}");
            if (!resp.IsSuccessStatusCode)
                Toasts.ShowSimple($"\"{a.Label}\" failed", $"{url.Host} returned HTTP {(int)resp.StatusCode}.");
        }
        catch (Exception e)
        {
            Log.Write($"action: http failed: {e.Message}");
            Toasts.ShowSimple($"\"{a.Label}\" failed", e.Message);
        }
    }

    public void MarkRead(IEnumerable<string> ids)
    {
        if (!Store.MarkRead(ids)) return;
        ScheduleSave();
        Changed?.Invoke();
    }

    public void MarkAllRead()
    {
        Store.MarkAllRead();
        Toasts.ClearAll();
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Clear(string? topic)
    {
        Store.Clear(topic);
        ScheduleSave();
        Changed?.Invoke();
    }

    public void Open(Entry e)
    {
        MarkRead(new[] { e.Id });
        Toasts.Remove(e.Message);
        if (e.Message.OpenUrl is { } u) OpenUrl(u.AbsoluteUri);
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme is not ("http" or "https" or "mailto" or "ms-settings")) return; // never launch files or arbitrary handlers
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"open: failed: {e.Message}"); }
    }

    // ---- persistence ----

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow() => Storage.SaveState(Store.ToState());

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _streamCts?.Cancel();
        if (_saveTimer.Enabled) SaveNow();
        _saveTimer.Dispose();
        _reconnectTimer.Dispose();
        _networkTimer.Dispose();
        Catalog.Dispose();
        _streamHttp.Dispose();
        Http.Dispose();
    }
}
