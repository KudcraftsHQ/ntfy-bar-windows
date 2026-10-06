using System.Net;
using System.Text;

namespace NtfyBar.Core.Tests;

public class StreamParserTests
{
    [Fact]
    public void Parses_open_keepalive_message_and_ignores_others()
    {
        Assert.Equal(StreamLineKind.Open, StreamParser.Parse("""{"id":"x","time":1,"event":"open","topic":"a,b"}""").Kind);
        Assert.Equal(StreamLineKind.Keepalive, StreamParser.Parse("""{"id":"x","time":1,"event":"keepalive","topic":"a"}""").Kind);
        Assert.Equal(StreamLineKind.Ignored, StreamParser.Parse("""{"id":"x","time":1,"event":"poll_request","topic":"a"}""").Kind);
        Assert.Equal(StreamLineKind.Ignored, StreamParser.Parse("garbage").Kind);
        Assert.Equal(StreamLineKind.Ignored, StreamParser.Parse("").Kind);
        Assert.Equal(StreamLineKind.Ignored, StreamParser.Parse("""{"event":"message","topic":"a"}""").Kind); // no id/time

        var m = StreamParser.Parse("""
            {"id":"m1","time":1700000000,"expires":1,"event":"message","topic":"facemap-orders","title":"New order","message":"Rp 99.000","priority":4,"tags":["moneybag"],"click":"https://x/o/1","icon":"https://x/i.png","actions":[{"id":"a1","action":"view","label":"Open","url":"https://x"},{"action":"http","label":"Ack","url":"https://x/ack","method":"POST","headers":{"X-A":"1"},"body":"ok"}],"attachment":{"name":"a.jpg","type":"image/jpeg","size":10,"expires":2,"url":"https://x/a.jpg"}}
            """);
        Assert.Equal(StreamLineKind.Message, m.Kind);
        var msg = m.Message!;
        Assert.Equal("m1", msg.Id);
        Assert.Equal(4, msg.EffectivePriority);
        Assert.Equal("New order", msg.DisplayTitle);
        Assert.Equal(2, msg.Actions!.Count);
        Assert.Equal("POST", msg.Actions[1].Method);
        Assert.Equal("1", msg.Actions[1].Headers!["X-A"]);
        Assert.Equal("https://x/a.jpg", msg.ImageUrl!.ToString());
    }

    [Theory]
    [InlineData("""{"event":"sync","source":"abc"}""", true)]
    [InlineData("""{ "event" : "sync" }""", true)]
    [InlineData("""{"event":"other"}""", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void Detects_sync_signal(string body, bool expected) =>
        Assert.Equal(expected, StreamParser.IsSyncSignal(Samples.Msg("1", "st_x", 1, body)));
}

public class StreamRulesTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(50, 60)]
    public void Backoff_is_exponential_capped_at_60_with_15_percent_jitter(int attempt, double baseSeconds)
    {
        Assert.Equal(baseSeconds * 0.85, StreamRules.Backoff(attempt, 0).TotalSeconds, 6);
        Assert.Equal(baseSeconds * 1.15, StreamRules.Backoff(attempt, 1).TotalSeconds, 6);
        Assert.Equal(baseSeconds, StreamRules.Backoff(attempt, 0.5).TotalSeconds, 6);
    }

    [Fact]
    public void Since_rule_matches_ntfy_bar()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        Assert.Equal("1h", StreamRules.Since(null, null, now));
        Assert.Equal("abc", StreamRules.Since("abc", 1_000_000 - 3600, now));
        Assert.Equal((1_000_000 - 7 * 3600).ToString(), StreamRules.Since("abc", 1_000_000 - 7 * 3600, now));
        Assert.Equal("999000", StreamRules.Since(null, 999_000, now));
    }

    [Fact]
    public void Stream_url()
    {
        Assert.Equal("https://n.example/a,b/json?since=1h", StreamRules.StreamUrl("https://n.example/", new[] { "a", "b" }, "1h")!.ToString());
        Assert.Null(StreamRules.StreamUrl("https://n.example", Array.Empty<string>(), null));
        Assert.Null(StreamRules.StreamUrl("ftp://n.example", new[] { "a" }, null));
    }

    [Fact]
    public void Authorization_prefers_token()
    {
        Assert.Equal("Bearer tk_1", StreamRules.Authorization("u", "p", "tk_1"));
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("u:p")), StreamRules.Authorization("u", "p", ""));
        Assert.Null(StreamRules.Authorization("", "p", null));
    }
}

public class StreamClientTests
{
    private static HttpResponseMessage Lines(params string[] lines) =>
        FakeHandler.Text(HttpStatusCode.OK, string.Join("\n", lines) + "\n");

    [Fact]
    public async Task Streams_messages_then_reconnects_with_backoff_and_fresh_target()
    {
        using var cts = new CancellationTokenSource();
        var since = "1h";
        var h = new FakeHandler((_, i) => i == 0
            ? Lines("""{"id":"o","time":1,"event":"open","topic":"a"}""",
                    """{"id":"k","time":2,"event":"keepalive","topic":"a"}""",
                    """{"id":"m1","time":3,"event":"message","topic":"a","message":"x"}""")
            : Lines("""{"id":"o2","time":4,"event":"open","topic":"a"}"""));
        var delays = new List<TimeSpan>();
        var client = new StreamClient(new HttpClient(h),
            () => new StreamTarget(StreamRules.StreamUrl("https://n.example", new[] { "a" }, since)!, "Bearer tk"),
            random: () => 0.5,
            delay: (d, _) => { delays.Add(d); if (delays.Count >= 2) cts.Cancel(); return Task.CompletedTask; });
        var messages = new List<NtfyMessage>();
        var statuses = new List<ConnectionState>();
        var opens = 0;
        client.MessageReceived += m => { messages.Add(m); since = m.Id; };
        client.StatusChanged += s => statuses.Add(s.State);
        client.Opened += () => opens++;

        await client.RunAsync(cts.Token);

        Assert.Equal(new[] { "m1" }, messages.Select(m => m.Id));
        Assert.Equal(2, opens);
        Assert.Equal("?since=1h", h.Requests[0].RequestUri!.Query);
        Assert.Equal("?since=m1", h.Requests[1].RequestUri!.Query); // reconnect resumes from the last id
        Assert.Equal("Bearer tk", h.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Equal(ConnectionState.Connecting, statuses[0]);
        Assert.Contains(ConnectionState.Connected, statuses);
        Assert.Contains(ConnectionState.Reconnecting, statuses);
        // "open" resets the attempt counter, so every close waits ~1 s (min 1 s), not longer.
        Assert.All(delays, d => Assert.InRange(d.TotalSeconds, 0.9, 1.2));
    }

    [Fact]
    public async Task Auth_errors_back_off_exponentially_and_report_auth_state()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var retryIn = new List<double>();
        ConnectionStatus? last = null;
        var client = new StreamClient(new HttpClient(h),
            () => new StreamTarget(new Uri("https://n.example/a/json"), null),
            random: () => 0.5,
            delay: (_, _) => { if (retryIn.Count >= 5) cts.Cancel(); return Task.CompletedTask; });
        client.StatusChanged += s =>
        {
            last = s;
            if (s.RetryAt is { } at) retryIn.Add(Math.Round((at - DateTimeOffset.UtcNow).TotalSeconds));
        };

        await client.RunAsync(cts.Token);

        Assert.Equal(ConnectionState.AuthError, last!.State);
        Assert.Equal(new double[] { 1, 2, 4, 8, 16 }, retryIn.Take(5));
    }

    [Fact]
    public async Task Http_errors_reconnect_and_target_null_stops()
    {
        var calls = 0;
        var h = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var client = new StreamClient(new HttpClient(h),
            () => calls++ < 2 ? new StreamTarget(new Uri("https://n.example/a/json"), null) : null,
            delay: (_, _) => Task.CompletedTask);
        var states = new List<ConnectionStatus>();
        client.StatusChanged += s => states.Add(s);

        await client.RunAsync(CancellationToken.None);

        Assert.Equal(2, h.Requests.Count);
        Assert.Contains(states, s => s.State == ConnectionState.Reconnecting && s.Reason!.Contains("502"));
        Assert.Equal(ConnectionState.NotConfigured, states[^1].State);
    }

    [Fact]
    public async Task Cancellation_stops_promptly()
    {
        using var cts = new CancellationTokenSource();
        var h = new FakeHandler((_, _) => { cts.Cancel(); throw new TaskCanceledException(); });
        var client = new StreamClient(new HttpClient(h), () => new StreamTarget(new Uri("https://n.example/a/json"), null));
        await client.RunAsync(cts.Token); // must return, not throw
    }
}
