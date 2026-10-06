using Microsoft.Win32;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Launch at login via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ntfy-bar";

    private static string Command => $"\"{Environment.ProcessPath}\" --autostart";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
    }

    /// <summary>True if the Run entry points at a different exe (e.g. an older download).</summary>
    public static bool PointsElsewhere
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !s.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
        }
        catch (Exception e)
        {
            Log.Write($"autostart: {e.Message}");
            throw;
        }
    }
}
