using System.Collections.Concurrent;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Icons and image attachments, downloaded to %LOCALAPPDATA%\ntfy-bar\icons\&lt;sha256(url)&gt;.ext.
/// Unpackaged toasts can only show local files (spec §9.1), so everything goes through here.</summary>
internal sealed class IconCache
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private readonly HttpClient _http = new(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
    {
        Timeout = Timeout,
    };
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly ConcurrentDictionary<string, Image> _images = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new();

    public IconCache() => _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"ntfy-bar-windows/{AppInfo.Version}");

    public static string? CachedFile(Uri url)
    {
        var key = IconRules.CacheKey(url);
        try
        {
            return Directory.EnumerateFiles(Storage.IconsDir, key + ".*").FirstOrDefault();
        }
        catch (IOException) { return null; }
    }

    /// <param name="authorization">Only pass when the URL is on the ntfy server itself.</param>
    public Task<string?> FileAsync(Uri url, string? authorization, int maxBytes)
    {
        if (CachedFile(url) is { } hit) return Task.FromResult<string?>(hit);
        if (_failed.TryGetValue(url.AbsoluteUri, out var when) && DateTimeOffset.UtcNow - when < TimeSpan.FromMinutes(10))
            return Task.FromResult<string?>(null);
        return _inflight.GetOrAdd(url.AbsoluteUri, _ => DownloadAsync(url, authorization, maxBytes));
    }

    private async Task<string?> DownloadAsync(Uri url, string? authorization, int maxBytes)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (authorization is not null) req.Headers.TryAddWithoutValidation("Authorization", authorization);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new IOException($"HTTP {(int)resp.StatusCode}");
            if (resp.Content.Headers.ContentLength > maxBytes) throw new IOException("too large");
            await using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await s.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > maxBytes) throw new IOException("too large");
            }
            var bytes = ms.ToArray();
            var ext = IconRules.SniffImage(bytes) ?? throw new IOException("not a png/jpg/gif/bmp");
            var path = Path.Combine(Storage.IconsDir, $"{IconRules.CacheKey(url)}.{ext}");
            await File.WriteAllBytesAsync(path + ".tmp", bytes).ConfigureAwait(false);
            File.Move(path + ".tmp", path, overwrite: true);
            return path;
        }
        catch (Exception e)
        {
            _failed[url.AbsoluteUri] = DateTimeOffset.UtcNow;
            Log.Write($"icon: {url.Host} failed: {e.Message}");
            return null;
        }
        finally
        {
            _inflight.TryRemove(url.AbsoluteUri, out _);
        }
    }

    /// <summary>In-memory image for list rows; null until downloaded (then <paramref name="loaded"/> fires on the UI thread).</summary>
    public Image? GetImage(Uri url, string? authorization, Action loaded)
    {
        if (_images.TryGetValue(url.AbsoluteUri, out var img)) return img;
        if (CachedFile(url) is { } file && TryLoad(url, file) is { } fromDisk) return fromDisk;
        var task = FileAsync(url, authorization, IconRules.MaxIconBytes);
        if (!task.IsCompleted)
        {
            task.ContinueWith(t =>
            {
                if (t.Result is { } f && TryLoad(url, f) is not null) Program.Ui.Post(_ => loaded(), null);
            }, TaskScheduler.Default);
        }
        return null;
    }

    private Image? TryLoad(Uri url, string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            using var src = System.Drawing.Image.FromStream(fs);
            var copy = new Bitmap(src);
            _images[url.AbsoluteUri] = copy;
            return copy;
        }
        catch (Exception) { return null; }
    }
}
