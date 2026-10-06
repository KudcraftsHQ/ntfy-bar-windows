namespace NtfyBar.Core;

/// <summary>Small append-only debug log. Never logs credentials or message bodies.</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }
    private const long MaxBytes = 1024 * 1024;

    public static void Write(string line)
    {
        var path = FilePath;
        if (path is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void RotateIfNeeded()
    {
        var path = FilePath;
        if (path is null) return;
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
