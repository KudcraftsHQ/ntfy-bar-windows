using System.Text.Json.Serialization;

namespace NtfyBar.Core;

/// <summary>A single ntfy message as delivered on the JSON stream (the subset of fields we use).
/// Field names match ntfy-bar's <c>NtfyMessage</c> so <c>state.json</c> has the same shape.</summary>
public sealed record NtfyMessage
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("time")] public long Time { get; init; }
    [JsonPropertyName("topic")] public string Topic { get; init; } = "";
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("priority")] public int? Priority { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("click")] public string? Click { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("attachment")] public NtfyAttachment? Attachment { get; init; }
    /// <summary>Windows-only addition (ntfy-bar ignores unknown keys): toast buttons.</summary>
    [JsonPropertyName("actions")] public List<NtfyAction>? Actions { get; init; }

    [JsonIgnore] public DateTimeOffset Date => DateTimeOffset.FromUnixTimeSeconds(Time);
    [JsonIgnore] public int EffectivePriority => Priority ?? 3;
    [JsonIgnore] public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    [JsonIgnore] public string DisplayTitle => HasTitle ? Title! : Topic;
    [JsonIgnore] public string Body => Message ?? "";

    /// <summary><c>click</c> URL if present, else the first URL found in the body.</summary>
    [JsonIgnore]
    public Uri? OpenUrl
    {
        get
        {
            if (TextUtil.TryAbsoluteUri(Click, out var u)) return u;
            return TextUtil.FirstUrl(Body);
        }
    }

    [JsonIgnore] public Uri? IconUrl => TextUtil.TryAbsoluteUri(Icon, out var u) ? u : null;

    /// <summary>Image attachment URL, if the attachment is an image.</summary>
    [JsonIgnore]
    public Uri? ImageUrl => Attachment is { IsImage: true } a && TextUtil.TryAbsoluteUri(a.Url, out var u) ? u : null;
}

public sealed record NtfyAttachment
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("size")] public long? Size { get; init; }
    [JsonPropertyName("expires")] public long? Expires { get; init; }
    [JsonPropertyName("url")] public string Url { get; init; } = "";

    [JsonIgnore]
    public bool IsImage
    {
        get
        {
            if (Type is not null && Type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return true;
            var path = Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.AbsolutePath : Name ?? "";
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return ext is "png" or "jpg" or "jpeg" or "gif" or "heic" or "webp" or "bmp" or "tiff";
        }
    }
}

/// <summary>ntfy action button (<c>view</c>, <c>http</c>, <c>broadcast</c>).</summary>
public sealed record NtfyAction
{
    [JsonPropertyName("action")] public string Action { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("headers")] public Dictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("clear")] public bool? Clear { get; init; }
}

/// <summary>Raw stream event. <c>open</c> / <c>keepalive</c> / <c>message</c> are handled; others ignored.</summary>
public sealed record NtfyEvent
{
    [JsonPropertyName("event")] public string Event { get; init; } = "";
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("time")] public long? Time { get; init; }
    [JsonPropertyName("topic")] public string? Topic { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("priority")] public int? Priority { get; init; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; init; }
    [JsonPropertyName("click")] public string? Click { get; init; }
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("attachment")] public NtfyAttachment? Attachment { get; init; }
    [JsonPropertyName("actions")] public List<NtfyAction>? Actions { get; init; }

    public NtfyMessage? AsMessage()
    {
        if (Event != "message" || Id is null || Time is null || Topic is null) return null;
        return new NtfyMessage
        {
            Id = Id, Time = Time.Value, Topic = Topic, Title = Title, Message = Message,
            Priority = Priority, Tags = Tags, Click = Click, Icon = Icon,
            Attachment = Attachment, Actions = Actions,
        };
    }
}

public sealed record Entry
{
    [JsonPropertyName("message")] public NtfyMessage Message { get; init; } = new();
    [JsonPropertyName("read")] public bool Read { get; set; }
    [JsonIgnore] public string Id => Message.Id;
}

/// <summary>One topic. JSON names and semantics match ntfy-bar's <c>TopicConfig</c> so a
/// settings file moves between the two apps unchanged.</summary>
public sealed record TopicConfig
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>Muted: still streamed and listed, but never notifies.</summary>
    [JsonPropertyName("muted")] public bool Muted { get; init; }
    /// <summary>Disabled: left out of the stream subscription entirely (history is kept).</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    /// <summary>Added by catalog sync. Managed topics are removed when the catalog drops them.</summary>
    [JsonPropertyName("managed")] public bool? Managed { get; init; }
    [JsonPropertyName("app")] public string? App { get; init; }
    [JsonPropertyName("appName")] public string? AppName { get; init; }
    [JsonPropertyName("appIcon")] public string? AppIcon { get; init; }
    /// <summary>Sound class from the catalog: silent | default | alert | urgent.</summary>
    [JsonPropertyName("sound")] public string? Sound { get; init; }
    /// <summary>Catalog topic name (null = show the topic id).</summary>
    [JsonPropertyName("displayName")] public string? DisplayName { get; init; }

    [JsonIgnore] public bool IsManaged => Managed == true;
    /// <summary>Display precedence: catalog topic name → topic id.</summary>
    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
}

public sealed record AppSettings
{
    [JsonPropertyName("serverURL")] public string ServerUrl { get; init; } = "";
    [JsonPropertyName("username")] public string Username { get; init; } = "";
    [JsonPropertyName("topics")] public List<TopicConfig> Topics { get; init; } = new();
    [JsonPropertyName("soundForAll")] public bool SoundForAll { get; init; }
    /// <summary>null = on unless the server is ntfy.sh.</summary>
    [JsonPropertyName("catalogEnabled")] public bool? CatalogEnabled { get; init; }
    /// <summary>The account's sync topic, learned from <c>/v1/catalog</c>.</summary>
    [JsonPropertyName("syncTopic")] public string? SyncTopic { get; init; }
    /// <summary>Windows only: priority-5 toasts loop an alarm until dismissed.</summary>
    [JsonPropertyName("insistent")] public bool? Insistent { get; init; }

    [JsonIgnore] public IEnumerable<string> TopicNames => Topics.Select(t => t.Name);
    /// <summary>Topics actually subscribed to on the stream.</summary>
    [JsonIgnore] public List<string> EnabledTopicNames => Topics.Where(t => t.Enabled).Select(t => t.Name).ToList();
    [JsonIgnore] public Uri? ServerUri => Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host) ? u : null;
    [JsonIgnore] public bool HasServer => ServerUri is not null;
    [JsonIgnore] public bool IsConfigured => HasServer && (EnabledTopicNames.Count > 0 || StreamSyncTopic is not null);

    [JsonIgnore]
    public bool IsCatalogEnabled => CatalogEnabled ?? (ServerUri is { } u && !u.Host.Equals("ntfy.sh", StringComparison.OrdinalIgnoreCase));

    /// <summary>Sync topic to add to the stream (only while the catalog is on).</summary>
    [JsonIgnore] public string? StreamSyncTopic => IsCatalogEnabled && !string.IsNullOrEmpty(SyncTopic) ? SyncTopic : null;

    /// <summary>Topics on the wire: enabled topics plus the sync topic.</summary>
    [JsonIgnore]
    public List<string> StreamTopics
    {
        get
        {
            var list = EnabledTopicNames;
            if (StreamSyncTopic is { } st && !list.Contains(st)) list.Add(st);
            return list;
        }
    }

    public TopicConfig? Topic(string name) => Topics.FirstOrDefault(t => t.Name == name);
    public bool IsMuted(string topic) => Topic(topic)?.Muted ?? false;
    /// <summary>Topics no longer in the list (removed) count as enabled so their history stays visible.</summary>
    public bool IsEnabled(string topic) => Topic(topic)?.Enabled ?? true;
    public string Label(string topic) => Topic(topic)?.Label ?? topic;

    /// <summary>Server URL without trailing slashes.</summary>
    [JsonIgnore] public string BaseUrl => ServerUrl.Trim().TrimEnd('/');
}

public sealed record PersistedState
{
    [JsonPropertyName("entries")] public List<Entry> Entries { get; init; } = new();
    [JsonPropertyName("lastMessageId")] public string? LastMessageId { get; init; }
    [JsonPropertyName("lastMessageTime")] public long? LastMessageTime { get; init; }
    [JsonPropertyName("seenIds")] public List<string> SeenIds { get; init; } = new();
}

public enum ConnectionState { NotConfigured, Connecting, Connected, Reconnecting, AuthError }

public sealed record ConnectionStatus(ConnectionState State, string? Reason = null, DateTimeOffset? RetryAt = null)
{
    public static readonly ConnectionStatus NotConfigured = new(ConnectionState.NotConfigured);
    public static readonly ConnectionStatus Connecting = new(ConnectionState.Connecting);
    public static readonly ConnectionStatus Connected = new(ConnectionState.Connected);

    public string Label => State switch
    {
        ConnectionState.NotConfigured => "Not configured",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Connected => "Connected",
        ConnectionState.Reconnecting => "Reconnecting…",
        ConnectionState.AuthError => "Authentication failed",
        _ => "",
    };

    public string? Detail => State switch
    {
        ConnectionState.Reconnecting => Reason,
        ConnectionState.AuthError => "Check username/password or token in Settings",
        _ => null,
    };
}
