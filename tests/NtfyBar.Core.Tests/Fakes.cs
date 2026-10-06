using System.Net;
using System.Text;

namespace NtfyBar.Core.Tests;

/// <summary>HttpMessageHandler that answers from a function and records every request.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string?> Bodies { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        return respond(request, Requests.Count - 1);
    }

    public static HttpResponseMessage Text(HttpStatusCode code, string body, string? etag = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }
}

internal static class Samples
{
    /// <summary>The §3.1 example response (comments removed).</summary>
    public const string CatalogJson = """
    {
      "version": 1759740000123,
      "base_url": "https://ntfy.kudcrafts.com",
      "history_days": 90,
      "sync_topic": "st_abc123",
      "apps": [
        {
          "id": "facemap", "name": "FaceMap",
          "icon": "https://facemap.fyi/icon-192.png",
          "sound": "default",
          "topics": [
            { "topic": "facemap-orders", "name": "Orders", "sound": "alert",  "permission": "read-only" },
            { "topic": "facemap-alerts", "name": "",       "sound": "default", "permission": "read-write" }
          ]
        },
        {
          "id": "kudtrading", "name": "Kudtrading", "icon": "", "sound": "urgent",
          "topics": [ { "topic": "kudtrading", "name": "", "sound": "urgent", "permission": "read-only" } ]
        }
      ]
    }
    """;

    public static Catalog Catalog() => Json.Parse<Catalog>(CatalogJson)!;

    public static Catalog CatalogWith(params (string App, string Topic, string Name, string Sound)[] topics) => new()
    {
        Version = 1,
        SyncTopic = "st_x",
        Apps = topics.GroupBy(t => t.App).Select(g => new CatalogApp
        {
            Id = g.Key, Name = char.ToUpperInvariant(g.Key[0]) + g.Key[1..], Icon = "", Sound = "default",
            Topics = g.Select(t => new CatalogTopic { Topic = t.Topic, Name = t.Name, Sound = t.Sound, Permission = "read-only" }).ToList(),
        }).ToList(),
    };

    public static NtfyMessage Msg(string id, string topic, long time, string? body = "hi", int? priority = null) =>
        new() { Id = id, Topic = topic, Time = time, Message = body, Priority = priority };
}
