using System.Text.Json;

namespace NtfyBar.Core.Tests;

public class ModelsTests
{
    [Fact]
    public void Decodes_ntfy_bar_v1_settings_without_catalog_fields()
    {
        // What ntfy-bar (macOS) wrote before the catalog existed; "enabled" missing on one topic.
        var json = """{"serverURL":"https://ntfy.kudcrafts.com","username":"hammas","topics":[{"name":"a","muted":true},{"name":"b","muted":false,"enabled":false}],"soundForAll":true}""";
        var s = Json.ParseSettings(json)!;
        Assert.Equal("https://ntfy.kudcrafts.com", s.ServerUrl);
        Assert.Equal("hammas", s.Username);
        Assert.True(s.SoundForAll);
        Assert.True(s.Topics[0].Muted);
        Assert.True(s.Topics[0].Enabled); // default
        Assert.False(s.Topics[1].Enabled);
        Assert.Null(s.Topics[0].Managed);
        Assert.Null(s.CatalogEnabled);
        Assert.Equal(new[] { "a" }, s.EnabledTopicNames);
    }

    [Fact]
    public void Writes_the_same_field_names_as_ntfy_bar_and_omits_nulls()
    {
        var s = new AppSettings
        {
            ServerUrl = "https://x", Username = "u", SoundForAll = false, SyncTopic = "st_1",
            Topics = { new TopicConfig { Name = "t", Managed = true, App = "a", AppName = "A", Sound = "alert" } },
        };
        using var doc = JsonDocument.Parse(Json.Write(s));
        var root = doc.RootElement;
        Assert.Equal("https://x", root.GetProperty("serverURL").GetString());
        Assert.True(root.TryGetProperty("soundForAll", out _));
        Assert.Equal("st_1", root.GetProperty("syncTopic").GetString());
        Assert.False(root.TryGetProperty("catalogEnabled", out _));
        var t = root.GetProperty("topics")[0];
        foreach (var key in new[] { "name", "muted", "enabled", "managed", "app", "appName", "sound" })
            Assert.True(t.TryGetProperty(key, out _), key);
        Assert.False(t.TryGetProperty("appIcon", out _));
        Assert.False(t.TryGetProperty("displayName", out _));
        Assert.False(t.TryGetProperty("label", out _));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var s = new AppSettings
        {
            ServerUrl = "https://x", Username = "u", CatalogEnabled = false, Insistent = true,
            Topics = { new TopicConfig { Name = "t", Muted = true, Managed = true, DisplayName = "T" } },
        };
        var back = Json.ParseSettings(Json.Write(s))!;
        Assert.Equal(s.Topics, back.Topics);
        Assert.Equal(s with { Topics = back.Topics }, back);
    }

    [Fact]
    public void Garbage_settings_return_null_and_state_falls_back_to_empty()
    {
        Assert.Null(Json.ParseSettings("not json"));
        Assert.Empty(Json.ParseState("{").Entries);
        Assert.Empty(Json.ParseState("""{"entries":null,"seenIds":null}""").SeenIds);
    }

    [Fact]
    public void Persisted_state_round_trips_with_ntfy_bar_field_names()
    {
        var st = new PersistedState
        {
            Entries = { new Entry { Message = Samples.Msg("a", "t", 10) with { Attachment = new NtfyAttachment { Url = "https://x/a.png" } }, Read = true } },
            LastMessageId = "a", LastMessageTime = 10, SeenIds = { "a" },
        };
        var json = Json.Write(st);
        Assert.Contains("\"lastMessageId\":\"a\"", json);
        Assert.Contains("\"seenIds\":[\"a\"]", json);
        var back = Json.ParseState(json);
        Assert.Equal("a", back.Entries[0].Message.Id);
        Assert.True(back.Entries[0].Read);
        Assert.True(back.Entries[0].Message.Attachment!.IsImage);
    }

    [Theory]
    [InlineData("https://ntfy.kudcrafts.com", null, true)]
    [InlineData("https://ntfy.sh", null, false)]
    [InlineData("https://NTFY.SH/", null, false)]
    [InlineData("https://ntfy.sh", true, true)]
    [InlineData("https://ntfy.kudcrafts.com", false, false)]
    [InlineData("", null, false)]
    public void Catalog_enabled_default(string server, bool? flag, bool expected)
    {
        var s = new AppSettings { ServerUrl = server, CatalogEnabled = flag };
        Assert.Equal(expected, s.IsCatalogEnabled);
    }

    [Fact]
    public void Stream_topics_include_sync_topic_only_when_catalog_on()
    {
        var s = new AppSettings
        {
            ServerUrl = "https://n.example", SyncTopic = "st_1",
            Topics = { new TopicConfig { Name = "a" }, new TopicConfig { Name = "b", Enabled = false } },
        };
        Assert.Equal(new[] { "a", "st_1" }, s.StreamTopics);
        Assert.Equal(new[] { "a" }, (s with { CatalogEnabled = false }).StreamTopics);
        Assert.True((s with { Topics = new() }).IsConfigured); // sync topic alone keeps a signed-in client listening
    }

    [Fact]
    public void Message_open_url_prefers_click_then_first_link()
    {
        Assert.Equal("https://a.example/x", Samples.Msg("1", "t", 1) with { Click = "https://a.example/x" } is var m ? m.OpenUrl!.ToString() : null);
        Assert.Equal("https://b.example/y", Samples.Msg("1", "t", 1, "see https://b.example/y.").OpenUrl!.ToString());
        Assert.Null(Samples.Msg("1", "t", 1, "no link").OpenUrl);
        Assert.Null((Samples.Msg("1", "t", 1, "x") with { Click = "/relative" }).OpenUrl);
    }

    [Fact]
    public void Markdown_is_stripped_for_toasts()
    {
        Assert.Equal("Title\nbold and em, link and code", TextUtil.Plain("# Title\n**bold** and *em*, [link](https://x) and `code`"));
        Assert.Equal("a\nb", TextUtil.Preview("a\n\n\n  b  "));
        Assert.Equal("snake_case_name", TextUtil.Plain("snake_case_name"));
    }

    [Fact]
    public void Truncate_never_doubles_ellipsis()
    {
        Assert.Equal("abc", TextUtil.Truncate("abc", 5));
        Assert.Equal("abc…", TextUtil.Truncate("abc...defgh", 5));
    }
}
