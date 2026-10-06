using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Daily check against GitHub Releases; a toast with a button opens the release page.
/// No silent self-replace (spec §9.4).</summary>
internal sealed class Updater : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)UpdateCheck.Interval.TotalMilliseconds };
    private string? _notifiedTag;

    public ReleaseInfo? Available { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        _timer.Tick += async (_, _) => await CheckAsync(manual: false);
        _timer.Start();
        _ = DelayedFirstCheck();
    }

    private async Task DelayedFirstCheck()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        await CheckAsync(manual: false);
    }

    public async Task CheckAsync(bool manual)
    {
        ReleaseInfo? latest;
        try
        {
            latest = await UpdateCheck.FetchLatestAsync(_http, CancellationToken.None);
        }
        catch (Exception e)
        {
            Log.Write($"update: check failed: {e.Message}");
            if (manual) MessageBox.Show($"Couldn't check for updates.\n\n{e.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (latest is not null && UpdateCheck.IsNewer(latest.TagName, AppInfo.Version))
        {
            Available = latest;
            Changed?.Invoke();
            Log.Write($"update: {latest.TagName} available (running {AppInfo.Version})");
            if (manual || _notifiedTag != latest.TagName)
            {
                _notifiedTag = latest.TagName;
                var page = Uri.TryCreate(latest.HtmlUrl, UriKind.Absolute, out var u) ? u : new Uri(UpdateCheck.ReleasesPage);
                try
                {
                    Toasts.ShowSimple("Update available",
                        $"ntfy-bar {latest.TagName} is out (you have v{AppInfo.Version}).",
                        "Download", page, page.AbsoluteUri);
                }
                catch (Exception e) { Log.Write($"update: toast failed: {e.Message}"); }
            }
        }
        else if (manual)
        {
            var msg = latest is null
                ? "No releases published yet."
                : $"You're on the latest version (v{AppInfo.Version}).";
            MessageBox.Show(msg, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
