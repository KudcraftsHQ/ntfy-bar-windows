using System.Net;
using System.Text;

namespace NtfyBar.Core.Tests;

public class MessageStoreTests
{
    private static readonly DateTimeOffset Launch = DateTimeOffset.FromUnixTimeSeconds(10_000);
    private static readonly AppSettings Settings = new()
    {
        ServerUrl = "https://n.example", SyncTopic = "st_x",
        Topics = { new TopicConfig { Name = "a" }, new TopicConfig { Name = "muted", Muted = true }, new TopicConfig { Name = "off", Enabled = false } },
    };

    [Fact]
    public void Dedupes_sorts_newest_first_and_moves_cursor()
    {
        var s = new MessageStore(new PersistedState(), Launch);
        Assert.Equal(IngestOutcome.Added, s.Ingest(Samples.Msg("1", "a", 10_010), Settings).Outcome);
        Assert.Equal(IngestOutcome.Added, s.Ingest(Samples.Msg("2", "a", 10_005), Settings).Outcome);
        Assert.Equal(IngestOutcome.Duplicate, s.Ingest(Samples.Msg("1", "a", 10_010), Settings).Outcome);
        Assert.Equal(new[] { "1", "2" }, s.Entries.Select(e => e.Id));
        Assert.Equal("1", s.LastMessageId);
        Assert.Equal(10_010, s.LastMessageTime);
    }

    [Fact]
    public void Notifies_only_fresh_unmuted_enabled_messages()
    {
        var s = new MessageStore(new PersistedState(), Launch);
        Assert.True(s.Ingest(Samples.Msg("1", "a", 10_000 - 59), Settings).Notify);   // within 60 s grace
        Assert.False(s.Ingest(Samples.Msg("2", "a", 10_000 - 61), Settings).Notify);  // backlog
        Assert.False(s.Ingest(Samples.Msg("3", "muted", 10_100), Settings).Notify);
        Assert.False(s.Ingest(Samples.Msg("4", "off", 10_100), Settings).Notify);
        Assert.Equal(2, s.UnreadCount(Settings)); // muted and disabled don't count
    }

    [Fact]
    public void Sync_topic_messages_are_never_stored_or_notified()
    {
        var s = new MessageStore(new PersistedState(), Launch);
        var r = s.Ingest(Samples.Msg("1", "st_x", 10_100, """{"event":"sync"}"""), Settings);
        Assert.Equal(IngestOutcome.SyncTopic, r.Outcome);
        Assert.True(r.SyncSignal);
        Assert.False(r.Notify);
        Assert.Empty(s.Entries);
        Assert.Null(s.LastMessageTime);
        Assert.False(s.Ingest(Samples.Msg("2", "st_x", 10_100, "noise"), Settings).SyncSignal);
    }

    [Fact]
    public void Backfill_does_not_move_the_resume_position()
    {
        var s = new MessageStore(new PersistedState { LastMessageId = "x", LastMessageTime = 9_000 }, Launch);
        s.Ingest(Samples.Msg("old", "a", 9_500), Settings, updateCursor: false);
        Assert.Equal("x", s.LastMessageId);
        Assert.Single(s.Entries);
    }

    [Fact]
    public void Caps_entries_and_restores_from_state()
    {
        var s = new MessageStore(new PersistedState(), Launch);
        for (var i = 0; i < MessageStore.MaxEntries + 10; i++) s.Ingest(Samples.Msg($"m{i}", "a", i), Settings);
        Assert.Equal(MessageStore.MaxEntries, s.Entries.Count);
        Assert.Equal($"m{MessageStore.MaxEntries + 9}", s.Entries[0].Id);

        var restored = new MessageStore(s.ToState(), Launch);
        Assert.Equal(IngestOutcome.Duplicate, restored.Ingest(Samples.Msg("m5", "a", 5), Settings).Outcome); // seen id survives
        Assert.Equal(s.LastMessageId, restored.LastMessageId);
    }

    [Fact]
    public void Mark_read_and_clear()
    {
        var s = new MessageStore(new PersistedState(), Launch);
        s.Ingest(Samples.Msg("1", "a", 10_100), Settings);
        s.Ingest(Samples.Msg("2", "b", 10_100) with { Icon = "https://x/i.png" }, Settings);
        Assert.True(s.MarkRead(new[] { "1" }));
        Assert.False(s.MarkRead(new[] { "1" }));
        Assert.Equal("https://x/i.png", s.LatestIcon("b"));
        Assert.True(s.MarkAllRead());
        Assert.Equal(0, s.UnreadCount(Settings));
        s.Clear("a");
        Assert.Equal(new[] { "2" }, s.Entries.Select(e => e.Id));
        s.Clear();
        Assert.Empty(s.Entries);
    }
}

public class SoundPlanTests
{
    [Theory]
    [InlineData("silent", 5, null, false)]
    [InlineData("default", 1, SoundPlan.DefaultSound, false)]
    [InlineData("alert", 3, SoundPlan.AlertSound, false)]
    [InlineData("urgent", 3, SoundPlan.UrgentSound, false)]
    [InlineData("bogus", 3, SoundPlan.DefaultSound, false)]
    public void Maps_catalog_sound_classes(string cls, int prio, string? src, bool loop)
    {
        var p = SoundPlan.For(cls, prio, soundForAll: false, insistent: false);
        Assert.Equal(src is null, p.Silent);
        Assert.Equal(src, p.Src);
        Assert.Equal(loop, p.Loop);
    }

    [Fact]
    public void Urgent_uses_reminder_scenario_without_looping()
    {
        var p = SoundPlan.For("urgent", 3, false, false);
        Assert.Equal(ToastScenario.Reminder, p.Scenario);
        Assert.False(p.Loop);
    }

    [Fact]
    public void Insistent_loops_only_priority_5_and_never_for_silent()
    {
        Assert.Equal(new SoundPlan(false, SoundPlan.UrgentSound, true, ToastScenario.Alarm), SoundPlan.For("default", 5, false, true));
        Assert.False(SoundPlan.For("default", 4, false, true).Loop);
        Assert.False(SoundPlan.For("default", 5, false, false).Loop);
        Assert.True(SoundPlan.For("silent", 5, false, true).Silent);
    }

    [Fact]
    public void Unmanaged_topics_keep_ntfy_bar_rule()
    {
        Assert.True(SoundPlan.For(null, 3, soundForAll: false, insistent: false).Silent);
        Assert.Equal(SoundPlan.DefaultSound, SoundPlan.For(null, 4, false, false).Src);
        Assert.Equal(SoundPlan.DefaultSound, SoundPlan.For(null, 1, soundForAll: true, insistent: false).Src);
    }
}

public class IconRulesTests
{
    [Fact]
    public void Precedence_message_icon_then_app_icon()
    {
        var topic = new TopicConfig { Name = "t", AppIcon = "https://app/icon.png" };
        Assert.Equal("https://msg/i.png", IconRules.NotificationIcon(Samples.Msg("1", "t", 1) with { Icon = "https://msg/i.png" }, topic)!.ToString());
        Assert.Equal("https://app/icon.png", IconRules.NotificationIcon(Samples.Msg("1", "t", 1), topic)!.ToString());
        Assert.Null(IconRules.NotificationIcon(Samples.Msg("1", "t", 1), new TopicConfig { Name = "t", AppIcon = "http://app/icon.png" }));
        Assert.Null(IconRules.NotificationIcon(Samples.Msg("1", "t", 1), null));
    }

    [Fact]
    public void Https_only_except_own_server()
    {
        var server = new Uri("http://ntfy.local");
        Assert.True(IconRules.IsAllowedIcon(new Uri("https://x/a.png"), server));
        Assert.True(IconRules.IsAllowedIcon(new Uri("http://ntfy.local/file/a.png"), server));
        Assert.False(IconRules.IsAllowedIcon(new Uri("http://evil/a.png"), server));
        Assert.False(IconRules.IsAllowedIcon(new Uri("file:///c:/a.png"), server));
    }

    [Fact]
    public void Sniffs_image_formats_and_hashes_urls()
    {
        Assert.Equal("png", IconRules.SniffImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.Equal("jpg", IconRules.SniffImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.Equal("gif", IconRules.SniffImage(Encoding.ASCII.GetBytes("GIF89a")));
        Assert.Null(IconRules.SniffImage(Encoding.ASCII.GetBytes("<svg></svg>")));
        Assert.Equal(64, IconRules.CacheKey(new Uri("https://x/a.png")).Length);
    }
}

public class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.2.0", "0.1.0", true)]
    [InlineData("v0.1.0", "0.1.0", false)]
    [InlineData("v0.1.0", "0.1.0+abc123", false)]
    [InlineData("v1.10.0", "1.9.9", true)]
    [InlineData("v1.0", "1.0.1", false)]
    [InlineData("garbage", "1.0.0", false)]
    [InlineData("v2.0.0-rc.1", "1.0.0", true)]
    public void Compares_versions(string latest, string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(latest, current));

    [Fact]
    public async Task Fetches_latest_release_with_user_agent()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(HttpStatusCode.OK, """{"tag_name":"v0.3.0","html_url":"https://github.com/KudcraftsHQ/ntfy-bar-windows/releases/tag/v0.3.0","draft":false,"prerelease":false}"""));
        var r = await UpdateCheck.FetchLatestAsync(new HttpClient(h), default);
        Assert.Equal("v0.3.0", r!.TagName);
        Assert.Equal("ntfy-bar-windows", h.Requests[0].Headers.UserAgent.ToString());
    }
}

public class SignInTests
{
    [Fact]
    public async Task Mints_token_with_basic_auth_and_label()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(HttpStatusCode.OK, """{"token":"tk_abc","label":"ntfy-bar-win-DESK","last_access":1}"""));
        var token = await NtfyApi.MintTokenAsync(new HttpClient(h), "https://n.example/", "partner", "pw", "ntfy-bar-win-DESK", default);
        Assert.Equal("tk_abc", token);
        var req = h.Requests[0];
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://n.example/v1/account/token", req.RequestUri!.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("partner:pw")), req.Headers.GetValues("Authorization").Single());
        Assert.Equal("""{"label":"ntfy-bar-win-DESK"}""", h.Bodies[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Wrong username or password.")]
    [InlineData(HttpStatusCode.InternalServerError, "Server returned HTTP 500.")]
    public async Task Sign_in_errors_are_readable(HttpStatusCode code, string message)
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(code, """{"code":40101,"error":"unauthorized"}"""));
        var e = await Assert.ThrowsAsync<SignInException>(() => NtfyApi.MintTokenAsync(new HttpClient(h), "https://n.example", "u", "p", "l", default));
        Assert.Equal(message, e.Message);
    }

    [Fact]
    public void Token_label_is_sanitised()
    {
        Assert.Equal("ntfy-bar-win-DESKTOP-01", NtfyApi.TokenLabel("DESKTOP-01"));
        Assert.Equal("ntfy-bar-win-pc", NtfyApi.TokenLabel("  "));
        Assert.True(NtfyApi.TokenLabel(new string('x', 200)).Length <= 64);
    }
}

public class CliImportTests
{
    [Fact]
    public void Parses_client_yml()
    {
        var r = CliConfigImporter.Parse("""
            # ntfy client config
            default-host: https://ntfy.kudcrafts.com
            default-user: "hammas"
            default-password: 's3cret' # trailing comment
            subscribe:
              - topic: facemap-orders
              - topic: https://ntfy.kudcrafts.com/kudtrading
                command: echo hi
              - topic: facemap-orders
            """.Replace("\n            ", "\n"));
        Assert.Equal("https://ntfy.kudcrafts.com", r.Host);
        Assert.Equal("hammas", r.User);
        Assert.Equal("s3cret", r.Password);
        Assert.Equal(new[] { "facemap-orders", "kudtrading" }, r.Topics);
    }
}

public class TopicGroupsTests
{
    [Fact]
    public void Groups_by_app_name_then_other_topics()
    {
        var groups = TopicGroups.Build(new[]
        {
            new TopicConfig { Name = "mine" },
            new TopicConfig { Name = "kt", AppName = "Kudtrading" },
            new TopicConfig { Name = "fm-a", AppName = "FaceMap" },
            new TopicConfig { Name = "fm-b", AppName = "FaceMap" },
        });
        Assert.Equal(new[] { "FaceMap", "Kudtrading", "Other topics" }, groups.Select(g => g.Header));
        Assert.Equal(new[] { "fm-a", "fm-b" }, groups[0].Topics.Select(t => t.Name));
        Assert.Null(TopicGroups.Build(new[] { new TopicConfig { Name = "x" } }).Single().Header);
    }
}
