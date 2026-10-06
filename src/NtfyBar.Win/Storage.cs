using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Settings in %APPDATA%\ntfy-bar (roams), state/icons/log in %LOCALAPPDATA%\ntfy-bar.
/// Nothing secret is written here; credentials live in Credential Manager.</summary>
internal static class Storage
{
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ntfy-bar");
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ntfy-bar");

    public static string SettingsPath => Path.Combine(RoamingDir, "settings.json");
    public static string StatePath => Path.Combine(LocalDir, "state.json");
    public static string IconsDir => Path.Combine(LocalDir, "icons");
    public static string LogPath => Path.Combine(LocalDir, "debug.log");

    /// <summary>The ntfy CLI's config on Windows.</summary>
    public static string CliConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ntfy", "client.yml");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RoamingDir);
        Directory.CreateDirectory(LocalDir);
        Directory.CreateDirectory(IconsDir);
    }

    public static AppSettings? LoadSettings()
    {
        try { return File.Exists(SettingsPath) ? Json.ParseSettings(File.ReadAllText(SettingsPath)) : null; }
        catch (Exception e) { Log.Write($"settings: load failed: {e.Message}"); return null; }
    }

    public static void SaveSettings(AppSettings s)
    {
        try { Json.WriteFileAtomic(SettingsPath, Json.Write(s, indented: true)); }
        catch (Exception e) { Log.Write($"settings: save failed: {e.Message}"); }
    }

    public static PersistedState LoadState()
    {
        try { return File.Exists(StatePath) ? Json.ParseState(File.ReadAllText(StatePath)) : new PersistedState(); }
        catch (Exception e) { Log.Write($"state: load failed: {e.Message}"); return new PersistedState(); }
    }

    public static void SaveState(PersistedState s)
    {
        try { Json.WriteFileAtomic(StatePath, Json.Write(s)); }
        catch (Exception e) { Log.Write($"state: save failed: {e.Message}"); }
    }

    /// <summary>First run: import host, user, secret and topics from the ntfy CLI's client.yml.</summary>
    public static AppSettings ImportCliConfig()
    {
        try
        {
            if (!File.Exists(CliConfigPath)) { Log.Write("import: no ntfy CLI config found"); return new AppSettings(); }
            var cli = CliConfigImporter.Parse(File.ReadAllText(CliConfigPath));
            if (!string.IsNullOrEmpty(cli.Password)) Credentials.Set(Credentials.Password, cli.User ?? "", cli.Password);
            if (!string.IsNullOrEmpty(cli.Token)) Credentials.Set(Credentials.Token, cli.User ?? "", cli.Token);
            Log.Write($"import: imported host, user and {cli.Topics.Count} topics from ntfy CLI config");
            return new AppSettings
            {
                ServerUrl = cli.Host ?? "",
                Username = cli.User ?? "",
                Topics = cli.Topics.Select(t => new TopicConfig { Name = t }).ToList(),
            };
        }
        catch (Exception e)
        {
            Log.Write($"import: failed: {e.Message}");
            return new AppSettings();
        }
    }
}
