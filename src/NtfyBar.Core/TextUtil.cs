using System.Text.RegularExpressions;

namespace NtfyBar.Core;

public static partial class TextUtil
{
    public static bool TryAbsoluteUri(string? s, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Scheme)) return false;
        // On Linux "/foo" parses as file:///foo; only accept explicit schemes.
        if (u.IsFile && !s.TrimStart().StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return false;
        uri = u;
        return true;
    }

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""')\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>First http(s) link in a body of text.</summary>
    public static Uri? FirstUrl(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = UrlRegex().Match(s);
        if (!m.Success) return null;
        var raw = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
        if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) raw = "https://" + raw;
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;
    }

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")] private static partial Regex ImageRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex LinkRegex();
    [GeneratedRegex(@"(\*\*|__)(.+?)\1")] private static partial Regex BoldRegex();
    [GeneratedRegex(@"(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?!\w)")] private static partial Regex StarEmRegex();
    [GeneratedRegex(@"(?<![\w_])_(?!\s)(.+?)(?<!\s)_(?!\w)")] private static partial Regex UnderEmRegex();
    [GeneratedRegex(@"~~(.+?)~~")] private static partial Regex StrikeRegex();
    [GeneratedRegex(@"`([^`]*)`")] private static partial Regex CodeRegex();

    /// <summary>Rough markdown → plain text for notification bodies.</summary>
    public static string Plain(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var lines = s.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var l = line;
            if (l.StartsWith('#'))
            {
                l = l.TrimStart('#');
                if (l.StartsWith(' ')) l = l[1..];
            }
            else if (l.StartsWith("> ")) l = l[2..];
            else if (l.TrimStart().StartsWith("```")) l = "";
            return l;
        });
        var text = string.Join("\n", lines);
        text = ImageRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = BoldRegex().Replace(text, "$2");
        text = StarEmRegex().Replace(text, "$1");
        text = UnderEmRegex().Replace(text, "$1");
        text = StrikeRegex().Replace(text, "$1");
        text = CodeRegex().Replace(text, "$1");
        return text;
    }

    /// <summary>Plain-text preview for list rows: markdown stripped, blank lines collapsed.</summary>
    public static string Preview(string? s) =>
        string.Join("\n", Plain(s).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>Single-line truncation with an ellipsis, never doubling one.</summary>
    public static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        var cut = s[..Math.Max(0, max - 1)].TrimEnd().TrimEnd('…', '.');
        return cut + "…";
    }
}
