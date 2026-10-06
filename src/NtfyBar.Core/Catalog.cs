using System.Text.Json.Serialization;

namespace NtfyBar.Core;

/// <summary><c>GET /v1/catalog</c> response (spec §3.1).</summary>
public sealed record Catalog
{
    [JsonPropertyName("version")] public long Version { get; init; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; init; }
    [JsonPropertyName("history_days")] public int HistoryDays { get; init; }
    [JsonPropertyName("sync_topic")] public string? SyncTopic { get; init; }
    [JsonPropertyName("apps")] public List<CatalogApp> Apps { get; init; } = new();
}

public sealed record CatalogApp
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    /// <summary>"" when unset.</summary>
    [JsonPropertyName("icon")] public string? Icon { get; init; }
    [JsonPropertyName("sound")] public string? Sound { get; init; }
    [JsonPropertyName("topics")] public List<CatalogTopic> Topics { get; init; } = new();
}

public sealed record CatalogTopic
{
    [JsonPropertyName("topic")] public string Topic { get; init; } = "";
    /// <summary>"" = show the topic id.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }
    /// <summary>Already resolved by the server (never "").</summary>
    [JsonPropertyName("sound")] public string? Sound { get; init; }
    /// <summary><c>read-only</c> or <c>read-write</c>.</summary>
    [JsonPropertyName("permission")] public string? Permission { get; init; }
}

public static class SoundClass
{
    public const string Silent = "silent";
    public const string Default = "default";
    public const string Alert = "alert";
    public const string Urgent = "urgent";
    public static readonly string[] All = { Silent, Default, Alert, Urgent };

    /// <summary>Unknown or missing classes fall back to <c>default</c> (a closed set, spec D9).</summary>
    public static string Normalize(string? s) =>
        s is not null && All.Contains(s.Trim().ToLowerInvariant()) ? s.Trim().ToLowerInvariant() : Default;
}
