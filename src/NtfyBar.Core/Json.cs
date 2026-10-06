using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NtfyBar.Core;

public static class Json
{
    /// <summary>Lenient reader: unknown keys ignored, missing keys keep their defaults
    /// (the same tolerance as ntfy-bar's <c>decodeIfPresent</c>).</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static T? Parse<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static string Write<T>(T value, bool indented = false) =>
        JsonSerializer.Serialize(value, indented ? Indented : Options);

    public static AppSettings? ParseSettings(string json)
    {
        var s = Parse<AppSettings>(json);
        if (s is null) return null;
        // A JSON null for the list must not leave us with a null collection.
        return s with { Topics = (s.Topics ?? new()).Where(t => t is not null && !string.IsNullOrEmpty(t.Name)).ToList() };
    }

    public static PersistedState ParseState(string json)
    {
        var s = Parse<PersistedState>(json);
        if (s is null) return new PersistedState();
        return s with
        {
            Entries = (s.Entries ?? new()).Where(e => e?.Message is not null && e.Message.Id.Length > 0).ToList(),
            SeenIds = s.SeenIds ?? new(),
        };
    }

    /// <summary>Writes a file atomically (temp file + replace) so a crash never leaves half a file.</summary>
    public static void WriteFileAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
