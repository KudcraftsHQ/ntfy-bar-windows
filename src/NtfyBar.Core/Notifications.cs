namespace NtfyBar.Core;

public enum ToastScenario { Default, Reminder, Alarm }

/// <summary>How a toast should sound. Windows only allows the built-in
/// <c>ms-winsoundevent:</c> set for unpackaged apps (spec §9.1).</summary>
public sealed record SoundPlan(bool Silent, string? Src, bool Loop, ToastScenario Scenario)
{
    public const string DefaultSound = "ms-winsoundevent:Notification.Default";
    public const string AlertSound = "ms-winsoundevent:Notification.Reminder";
    public const string UrgentSound = "ms-winsoundevent:Notification.Looping.Alarm2";

    public static readonly SoundPlan None = new(true, null, false, ToastScenario.Default);

    /// <summary>Maps a topic's catalog sound class (or, for unmanaged topics, ntfy-bar's
    /// "priority 4–5 or sound-for-all" rule) to toast audio.</summary>
    /// <param name="soundClass">Catalog sound class; null for user-added topics.</param>
    public static SoundPlan For(string? soundClass, int priority, bool soundForAll, bool insistent)
    {
        string cls;
        if (soundClass is null)
            cls = soundForAll || priority >= 4 ? SoundClass.Default : SoundClass.Silent;
        else
            cls = SoundClass.Normalize(soundClass);

        if (cls == SoundClass.Silent) return None;
        // Low priority (1–2) is silent on catalog topics too, as in ntfy-bar (macOS).
        if (soundClass is not null && priority <= 2) return None;

        // Insistent mode: priority 5 loops until dismissed, whatever the class.
        if (insistent && priority >= 5) return new(false, UrgentSound, true, ToastScenario.Alarm);

        return cls switch
        {
            SoundClass.Alert => new(false, AlertSound, false, ToastScenario.Default),
            SoundClass.Urgent => new(false, UrgentSound, false, ToastScenario.Reminder),
            _ => new(false, DefaultSound, false, ToastScenario.Default),
        };
    }
}

public static class IconRules
{
    /// <summary>App icons are capped at 300 KB (spec §9.1).</summary>
    public const int MaxIconBytes = 300 * 1024;
    /// <summary>Image attachments shown inline in the toast.</summary>
    public const int MaxImageBytes = 3 * 1024 * 1024;

    /// <summary>Icon precedence in notifications (spec §14.7): message icon (server-injected) → app icon → none.</summary>
    public static Uri? NotificationIcon(NtfyMessage m, TopicConfig? topic)
    {
        if (m.IconUrl is { } u && IsAllowedScheme(u)) return u;
        if (TextUtil.TryAbsoluteUri(topic?.AppIcon, out var a) && a.Scheme == Uri.UriSchemeHttps) return a;
        return null;
    }

    public static bool IsAllowedScheme(Uri u) => u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp;

    /// <summary>App icons from the catalog are downloaded over https only.</summary>
    public static bool IsAllowedIcon(Uri u, Uri? server) =>
        u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && server is not null && SameHost(u, server));

    public static bool SameHost(Uri a, Uri b) => string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cache file name: sha256 of the URL.</summary>
    public static string CacheKey(Uri u)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u.AbsoluteUri));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>File extension by magic bytes; null for formats toasts can't show.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "jpg";
        if (b.Length >= 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "gif";
        if (b.Length >= 2 && b[0] == 'B' && b[1] == 'M') return "bmp";
        return null;
    }
}

/// <summary>Arguments carried by a toast and handed back on activation.</summary>
public static class ToastArgs
{
    public const string Action = "action";
    public const string Id = "id";
    public const string Url = "url";
    public const string Index = "idx";

    public const string Open = "open";
    public const string Http = "http";
    public const string Update = "update";
    public const string SignIn = "signin";
}
