namespace NtfyBar.Core;

/// <summary>Imports server/user/password/topics from the ntfy CLI's <c>client.yml</c> on first run.
/// Hand-rolled line parser (same as ntfy-bar): flat keys and <c>subscribe:</c> → <c>- topic: x</c>.</summary>
public static class CliConfigImporter
{
    public sealed record Result(string? Host, string? User, string? Password, string? Token, List<string> Topics);

    public static Result Parse(string text)
    {
        string? host = null, user = null, password = null, token = null;
        var topics = new List<string>();
        var inSubscribe = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = StripComment(raw);
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var indented = line[0] is ' ' or '\t' || trimmed.StartsWith('-');
            if (!indented)
            {
                inSubscribe = trimmed.StartsWith("subscribe:", StringComparison.Ordinal);
                if (KeyValue(trimmed) is not { } kv) continue;
                switch (kv.Key)
                {
                    case "default-host": host = kv.Value; break;
                    case "default-user": user = kv.Value; break;
                    case "default-password": password = kv.Value; break;
                    case "default-token": token = kv.Value; break;
                }
            }
            else if (inSubscribe)
            {
                var item = trimmed.StartsWith("- ", StringComparison.Ordinal) ? trimmed[2..] : trimmed;
                if (KeyValue(item) is { Key: "topic" } kv && kv.Value.Length > 0)
                {
                    // Topics may be full URLs (https://host/topic); keep the last path component.
                    var name = kv.Value.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? kv.Value;
                    if (!topics.Contains(name)) topics.Add(name);
                }
            }
        }
        return new Result(host, user, password, token, topics);
    }

    private static string StripComment(string line)
    {
        if (line.TrimStart().StartsWith('#')) return "";
        var i = line.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    private static (string Key, string Value)? KeyValue(string s)
    {
        var i = s.IndexOf(':');
        if (i < 0) return null;
        var key = s[..i].Trim();
        var value = s[(i + 1)..].Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '"' or '\'') value = value[1..^1];
        return (key, value);
    }
}
