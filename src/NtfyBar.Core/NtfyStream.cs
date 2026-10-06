using System.Net;
using System.Text.Json;

namespace NtfyBar.Core;

public enum StreamLineKind { Ignored, Open, Keepalive, Message }

public readonly record struct StreamLine(StreamLineKind Kind, NtfyMessage? Message = null);

/// <summary>Parses one line of <c>GET /{topics}/json</c>.</summary>
public static class StreamParser
{
    public static StreamLine Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new(StreamLineKind.Ignored);
        var ev = Json.Parse<NtfyEvent>(line);
        if (ev is null) return new(StreamLineKind.Ignored);
        return ev.Event switch
        {
            "open" => new(StreamLineKind.Open),
            "keepalive" => new(StreamLineKind.Keepalive),
            "message" when ev.AsMessage() is { } m => new(StreamLineKind.Message, m),
            _ => new(StreamLineKind.Ignored),
        };
    }

    /// <summary>True if a message on the sync topic is the catalog/account sync signal
    /// (<c>{"event":"sync"}</c> in the body, spec §14.3).</summary>
    public static bool IsSyncSignal(NtfyMessage m)
    {
        var body = m.Message;
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("event", out var e)
                && e.ValueKind == JsonValueKind.String
                && e.GetString() == "sync";
        }
        catch (JsonException)
        {
            return body.Replace(" ", "").Contains("\"event\":\"sync\"", StringComparison.Ordinal);
        }
    }
}

public static class StreamRules
{
    public const int BackoffCapSeconds = 60;

    /// <summary>Exponential backoff with jitter: <c>min(60, 2^(n-1)) ± 15 %</c>. <paramref name="attempt"/> ≥ 1.</summary>
    public static TimeSpan Backoff(int attempt, double random01)
    {
        var baseSeconds = Math.Min(BackoffCapSeconds, Math.Pow(2, Math.Max(0, attempt - 1)));
        var jitter = 0.85 + 0.30 * Math.Clamp(random01, 0, 1);
        return TimeSpan.FromSeconds(baseSeconds * jitter);
    }

    /// <summary>Resume from the last seen message. Message ids only resolve while the server still
    /// caches them, so fall back to a timestamp for positions older than 6 h.
    /// The first-ever connect pulls 1 h of backlog into the list without notifying.</summary>
    public static string Since(string? lastMessageId, long? lastMessageTime, DateTimeOffset now)
    {
        if (lastMessageTime is not { } time) return "1h";
        if (!string.IsNullOrEmpty(lastMessageId) && now.ToUnixTimeSeconds() - time < 6 * 3600) return lastMessageId;
        return time.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary><c>{base}/{t1,t2,…}/json?since=…</c>, or null when there is nothing to subscribe to.</summary>
    public static Uri? StreamUrl(string baseUrl, IReadOnlyList<string> topics, string? since)
    {
        if (topics.Count == 0) return null;
        var b = baseUrl.Trim().TrimEnd('/');
        var path = string.Join(",", topics.Select(Uri.EscapeDataString));
        var url = $"{b}/{path}/json";
        if (!string.IsNullOrEmpty(since)) url += "?since=" + Uri.EscapeDataString(since);
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? u : null;
    }

    public static string? BasicAuth(string username, string password) =>
        string.IsNullOrEmpty(username) ? null
            : "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{username}:{password}"));

    /// <summary>Token wins over the password (as in ntfy-bar).</summary>
    public static string? Authorization(string username, string? password, string? token) =>
        !string.IsNullOrEmpty(token) ? "Bearer " + token : BasicAuth(username, password ?? "");
}

public sealed class StreamUnauthorizedException(int status) : Exception($"HTTP {status}")
{
    public int Status { get; } = status;
}

public sealed class StreamHttpException(int status) : Exception($"Server returned HTTP {status}")
{
    public int Status { get; } = status;
}

/// <summary>What the stream loop needs from the app, read fresh on every (re)connect.</summary>
public sealed record StreamTarget(Uri Url, string? Authorization);

/// <summary>One long-lived JSON stream with reconnect + backoff. Platform-independent; callbacks run
/// on the caller's synchronization context when <see cref="RunAsync"/> is started from one.</summary>
public sealed class StreamClient
{
    /// <summary>ntfy sends a keepalive every ~45 s; no byte for this long means a dead connection.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;
    private readonly Func<StreamTarget?> _target;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<NtfyMessage>? MessageReceived;
    public event Action? Opened;
    public event Action<string>? Log;

    public int Attempt { get; private set; }
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.NotConfigured;

    public StreamClient(HttpClient http, Func<StreamTarget?> target, Func<double>? random = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _target = target;
        _random = random ?? Random.Shared.NextDouble;
        _delay = delay ?? Task.Delay;
    }

    private void SetStatus(ConnectionStatus s)
    {
        Status = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>Runs until cancelled or until there is nothing to subscribe to.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var target = _target();
            if (target is null)
            {
                SetStatus(ConnectionStatus.NotConfigured);
                return;
            }
            if (Attempt == 0) SetStatus(ConnectionStatus.Connecting);
            try
            {
                await StreamOnceAsync(target, ct);
                Attempt++;
                Log?.Invoke("stream: closed by server");
                SetStatus(new ConnectionStatus(ConnectionState.Reconnecting, "Connection closed", Next()));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (StreamUnauthorizedException e)
            {
                Attempt++;
                Log?.Invoke($"stream: auth error HTTP {e.Status}");
                SetStatus(new ConnectionStatus(ConnectionState.AuthError, $"HTTP {e.Status}", Next()));
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                Attempt++;
                var reason = e switch
                {
                    StreamHttpException h => h.Message,
                    TimeoutException or OperationCanceledException => "Connection timed out",
                    HttpRequestException { InnerException: { } inner } => inner.Message,
                    _ => e.Message,
                };
                Log?.Invoke($"stream: error: {reason}");
                SetStatus(new ConnectionStatus(ConnectionState.Reconnecting, reason, Next()));
            }

            var wait = Status.RetryAt is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(1);
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            try { await _delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private DateTimeOffset Next() => DateTimeOffset.UtcNow + StreamRules.Backoff(Attempt, _random());

    private async Task StreamOnceAsync(StreamTarget target, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, target.Url);
        if (target.Authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", target.Authorization);
        Log?.Invoke($"stream: connecting ({target.Url.Query})");

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(IdleTimeout);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
        var code = (int)resp.StatusCode;
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new StreamUnauthorizedException(code);
        if (code < 200 || code >= 300) throw new StreamHttpException(code);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("No data from server for 120 s");
            }
            if (line is null) return; // closed by server
            var parsed = StreamParser.Parse(line);
            switch (parsed.Kind)
            {
                case StreamLineKind.Open:
                    Attempt = 0;
                    SetStatus(ConnectionStatus.Connected);
                    Log?.Invoke("stream: open");
                    Opened?.Invoke();
                    break;
                case StreamLineKind.Keepalive:
                    if (Status.State != ConnectionState.Connected) SetStatus(ConnectionStatus.Connected);
                    break;
                case StreamLineKind.Message:
                    MessageReceived?.Invoke(parsed.Message!);
                    break;
            }
        }
    }
}
