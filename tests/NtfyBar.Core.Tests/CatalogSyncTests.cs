using System.Net;

namespace NtfyBar.Core.Tests;

public class CatalogSyncTests
{
    [Fact]
    public void Decodes_spec_example()
    {
        var c = Samples.Catalog();
        Assert.Equal(1759740000123, c.Version);
        Assert.Equal(90, c.HistoryDays);
        Assert.Equal("st_abc123", c.SyncTopic);
        Assert.Equal(2, c.Apps.Count);
        Assert.Equal("alert", c.Apps[0].Topics[0].Sound);
        Assert.Equal("read-write", c.Apps[0].Topics[1].Permission);
    }

    [Fact]
    public void Decodes_with_missing_optional_fields_and_unknown_keys()
    {
        var c = Json.Parse<Catalog>("""{"version":2,"apps":[{"id":"a","topics":[{"topic":"a-x","locked":true}]}],"extra":1}""");
        Assert.NotNull(c);
        Assert.Equal("a-x", c!.Apps[0].Topics[0].Topic);
        Assert.Null(c.SyncTopic);
    }

    [Fact]
    public void Adds_missing_topics_as_managed_enabled_unmuted_with_metadata()
    {
        var r = CatalogSync.Reconcile(new List<TopicConfig>(), Samples.Catalog());

        Assert.Equal(new[] { "facemap-orders", "facemap-alerts", "kudtrading" }, r.Added);
        var orders = r.Topics.Single(t => t.Name == "facemap-orders");
        Assert.True(orders.IsManaged);
        Assert.True(orders.Enabled);
        Assert.False(orders.Muted);
        Assert.Equal("facemap", orders.App);
        Assert.Equal("FaceMap", orders.AppName);
        Assert.Equal("https://facemap.fyi/icon-192.png", orders.AppIcon);
        Assert.Equal("alert", orders.Sound);
        Assert.Equal("Orders", orders.DisplayName);
        Assert.Equal("Orders", orders.Label);

        var alerts = r.Topics.Single(t => t.Name == "facemap-alerts");
        Assert.Null(alerts.DisplayName); // "" = show topic id
        Assert.Equal("facemap-alerts", alerts.Label);

        var kt = r.Topics.Single(t => t.Name == "kudtrading");
        Assert.Null(kt.AppIcon); // "" = unset
        Assert.Equal("urgent", kt.Sound);
    }

    [Fact]
    public void Updates_metadata_but_keeps_user_mute_and_enable()
    {
        var current = new List<TopicConfig>
        {
            new() { Name = "facemap-orders", Managed = true, Muted = true, Enabled = false, AppName = "Old", Sound = "default", DisplayName = "Old name" },
        };
        var r = CatalogSync.Reconcile(current, Samples.Catalog());
        var t = r.Topics.Single(x => x.Name == "facemap-orders");
        Assert.True(t.Muted);
        Assert.False(t.Enabled);
        Assert.Equal("FaceMap", t.AppName);
        Assert.Equal("alert", t.Sound);
        Assert.Equal("Orders", t.DisplayName);
        Assert.Contains("facemap-orders", r.Updated);
        Assert.DoesNotContain("facemap-orders", r.Added);
    }

    [Fact]
    public void Removes_managed_topics_no_longer_listed()
    {
        var current = new List<TopicConfig>
        {
            new() { Name = "gone-topic", Managed = true, App = "gone" },
            new() { Name = "facemap-orders", Managed = true },
        };
        var r = CatalogSync.Reconcile(current, Samples.Catalog());
        Assert.Equal(new[] { "gone-topic" }, r.Removed);
        Assert.DoesNotContain(r.Topics, t => t.Name == "gone-topic");
    }

    [Fact]
    public void Never_removes_or_disables_unmanaged_topics()
    {
        var current = new List<TopicConfig>
        {
            new() { Name = "my-own", Muted = true, Enabled = false },
            new() { Name = "legacy", Managed = false },
        };
        var r = CatalogSync.Reconcile(current, Samples.Catalog());
        Assert.Equal(current[0], r.Topics[0]);
        Assert.Equal(current[1], r.Topics[1]);
        Assert.Empty(r.Removed);
        Assert.DoesNotContain("my-own", r.Updated);
    }

    [Fact]
    public void Unmanaged_topic_in_catalog_is_decorated_but_stays_unmanaged_and_order_is_kept()
    {
        var current = new List<TopicConfig> { new() { Name = "mine" }, new() { Name = "facemap-orders", Muted = true } };
        var r = CatalogSync.Reconcile(current, Samples.Catalog());
        Assert.Equal("mine", r.Topics[0].Name);
        var t = r.Topics[1];
        Assert.Equal("facemap-orders", t.Name);
        Assert.False(t.IsManaged);
        Assert.True(t.Muted);
        Assert.Equal("FaceMap", t.AppName);
        Assert.DoesNotContain("facemap-orders", r.Added);

        // ...and loses the catalog fields again when the catalog drops it, but is not removed.
        var r2 = CatalogSync.Reconcile(r.Topics, Samples.CatalogWith());
        var t2 = r2.Topics.Single(x => x.Name == "facemap-orders");
        Assert.Null(t2.AppName);
        Assert.Null(t2.Sound);
        Assert.True(t2.Muted);
        Assert.DoesNotContain("facemap-orders", r2.Removed);
        Assert.Equal(new[] { "facemap-alerts", "kudtrading" }, r2.Removed);
    }

    [Fact]
    public void Reconcile_is_idempotent()
    {
        var r1 = CatalogSync.Reconcile(new List<TopicConfig>(), Samples.Catalog());
        var r2 = CatalogSync.Reconcile(r1.Topics, Samples.Catalog());
        Assert.False(r2.Changed);
        Assert.Equal(r1.Topics, r2.Topics);
    }

    [Fact]
    public void Empty_catalog_removes_all_managed_and_keeps_unmanaged()
    {
        var current = new List<TopicConfig> { new() { Name = "a", Managed = true }, new() { Name = "b" } };
        var r = CatalogSync.Reconcile(current, new Catalog());
        Assert.Equal(new[] { "b" }, r.Topics.Select(t => t.Name));
    }

    [Fact]
    public void Unknown_sound_class_falls_back_to_default()
    {
        var r = CatalogSync.Reconcile(new List<TopicConfig>(), Samples.CatalogWith(("x", "x-1", "", "file:///evil.wav")));
        Assert.Equal("default", r.Topics[0].Sound);
    }

    [Fact]
    public void Duplicate_topic_across_apps_is_added_once()
    {
        var c = Samples.CatalogWith(("a", "a-x", "", "default"));
        c.Apps.Add(new CatalogApp { Id = "b", Name = "B", Topics = { new CatalogTopic { Topic = "a-x" } } });
        var r = CatalogSync.Reconcile(new List<TopicConfig>(), c);
        Assert.Single(r.Topics);
        Assert.Equal("a", r.Topics[0].App);
    }

    // ---- fetch ----

    [Fact]
    public async Task Fetch_200_parses_and_returns_etag_and_sends_auth()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(HttpStatusCode.OK, Samples.CatalogJson, "\"1759740000123\""));
        var r = await CatalogSync.FetchAsync(new HttpClient(h), "https://n.example/", "Bearer tk_x", null, default);
        Assert.Equal(CatalogFetchKind.Ok, r.Kind);
        Assert.Equal("\"1759740000123\"", r.ETag);
        Assert.Equal(3, r.Catalog!.Apps.Sum(a => a.Topics.Count));
        Assert.Equal("https://n.example/v1/catalog", h.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer tk_x", h.Requests[0].Headers.GetValues("Authorization").Single());
        Assert.Empty(h.Requests[0].Headers.IfNoneMatch);
    }

    [Fact]
    public async Task Fetch_sends_if_none_match_and_maps_304()
    {
        var h = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified));
        var r = await CatalogSync.FetchAsync(new HttpClient(h), "https://n.example", null, "\"42\"", default);
        Assert.Equal(CatalogFetchKind.NotModified, r.Kind);
        Assert.Equal("\"42\"", h.Requests[0].Headers.IfNoneMatch.Single().ToString());
        Assert.Null(r.Catalog);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CatalogFetchKind.AuthError)]
    [InlineData(HttpStatusCode.Forbidden, CatalogFetchKind.AuthError)]
    [InlineData(HttpStatusCode.NotFound, CatalogFetchKind.Unavailable)]
    [InlineData(HttpStatusCode.TooManyRequests, CatalogFetchKind.Error)]
    [InlineData(HttpStatusCode.InternalServerError, CatalogFetchKind.Error)]
    public async Task Fetch_non_200_never_yields_a_catalog(HttpStatusCode code, CatalogFetchKind kind)
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(code, Samples.CatalogJson));
        var r = await CatalogSync.FetchAsync(new HttpClient(h), "https://n.example", null, null, default);
        Assert.Equal(kind, r.Kind);
        Assert.Null(r.Catalog);
    }

    [Fact]
    public async Task Fetch_malformed_json_is_an_error_not_an_empty_catalog()
    {
        var h = new FakeHandler((_, _) => FakeHandler.Text(HttpStatusCode.OK, "<html>proxy</html>"));
        var r = await CatalogSync.FetchAsync(new HttpClient(h), "https://n.example", null, null, default);
        Assert.Equal(CatalogFetchKind.Error, r.Kind);
    }

    [Fact]
    public async Task Fetch_network_failure_is_an_error()
    {
        var h = new FakeHandler((_, _) => throw new HttpRequestException("boom"));
        var r = await CatalogSync.FetchAsync(new HttpClient(h), "https://n.example", null, null, default);
        Assert.Equal(CatalogFetchKind.Error, r.Kind);
    }

    [Fact]
    public async Task Backfill_polls_since_7d_and_parses_messages()
    {
        var lines = """
        {"id":"a","time":100,"event":"message","topic":"t","message":"one"}
        {"id":"b","time":200,"event":"message","topic":"t","message":"two"}
        """;
        var h = new FakeHandler((_, _) => FakeHandler.Text(HttpStatusCode.OK, lines));
        var list = await CatalogSync.BackfillAsync(new HttpClient(h), "https://n.example", "t", null, "7d", default);
        Assert.Equal(new[] { "a", "b" }, list.Select(m => m.Id));
        Assert.Equal("https://n.example/t/json?poll=1&since=7d", h.Requests[0].RequestUri!.ToString());
    }
}
