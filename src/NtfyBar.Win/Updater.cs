using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;
using Velopack;
using Velopack.Sources;

namespace NtfyBar.Win;

public enum UpdateStatus { NotInstalled, Idle, Checking, Downloading, ReadyToRestart, UpToDate, Failed }

/// <summary>Auto-update through Velopack with GitHub Releases as the source.
/// Installed copies check once a day (and on demand), download in the background (delta packages
/// when available), and apply the update when the app quits or on the next start; "Restart to update"
/// applies it now. A portable copy (the single-file exe, or a dev build) can't update itself, so it
/// only tells the user where to get the installer.</summary>
internal sealed class Updater : IDisposable
{
    public const string RepoUrl = "https://github.com/KudcraftsHQ/ntfy-bar-windows";

    private readonly UpdateManager? _manager;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = (int)UpdateCheck.Interval.TotalMilliseconds };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private bool _busy;
    private string? _notifiedVersion;

    public UpdateStatus Status { get; private set; }
    public int Progress { get; private set; }
    /// <summary>The downloaded version waiting for a restart, or (portable) the newer release found.</summary>
    public string? AvailableVersion { get; private set; }
    public string? Error { get; private set; }
    public bool IsInstalled => _manager is not null;
    public event Action? Changed;

    public Updater() : this(new GithubSource(RepoUrl, accessToken: null, prerelease: false)) { }

    /// <summary>Any Velopack source; the selftest passes a local folder.</summary>
    public Updater(IUpdateSource source)
    {
        try
        {
            var manager = new UpdateManager(source);
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception e)
        {
            Log.Write($"update: not available: {e.Message}");
        }
        Status = _manager is null ? UpdateStatus.NotInstalled : UpdateStatus.Idle;
        if (_manager?.UpdatePendingRestart is { } pending)
        {
            // Downloaded earlier but not applied yet (e.g. the PC was shut down).
            AvailableVersion = pending.Version.ToString();
            Status = UpdateStatus.ReadyToRestart;
        }
    }

    public string CurrentVersion => _manager?.CurrentVersion?.ToString() ?? AppInfo.Version;

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

    private void Set(UpdateStatus s, string? error = null)
    {
        Status = s;
        Error = error;
        Changed?.Invoke();
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        if (Status == UpdateStatus.ReadyToRestart)
        {
            if (manual) RestartToApply();
            return;
        }
        _busy = true;
        try
        {
            if (_manager is null) await CheckPortableAsync(manual);
            else await CheckInstalledAsync(_manager, manual);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CheckInstalledAsync(UpdateManager manager, bool manual)
    {
        Set(UpdateStatus.Checking);
        UpdateInfo? info;
        try
        {
            info = await manager.CheckForUpdatesAsync();
        }
        catch (Exception e)
        {
            Log.Write($"update: check failed: {e.Message}");
            Set(UpdateStatus.Failed, e.Message);
            if (manual) MessageBox.Show($"Couldn't check for updates.\n\n{e.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (info is null)
        {
            Set(UpdateStatus.UpToDate);
            if (manual) MessageBox.Show($"You're on the latest version (v{CurrentVersion}).", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var version = info.TargetFullRelease.Version.ToString();
        Log.Write($"update: downloading {version} (running {CurrentVersion}, {info.DeltasToTarget.Length} delta(s))");
        Progress = 0;
        Set(UpdateStatus.Downloading);
        try
        {
            await manager.DownloadUpdatesAsync(info, p =>
            {
                Program.Ui.Post(_ => { Progress = p; Changed?.Invoke(); }, null);
            });
        }
        catch (Exception e)
        {
            Log.Write($"update: download failed: {e.Message}");
            Set(UpdateStatus.Failed, e.Message);
            if (manual) MessageBox.Show($"Couldn't download the update.\n\n{e.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AvailableVersion = version;
        Set(UpdateStatus.ReadyToRestart);
        Log.Write($"update: {version} ready, applies on quit or next start");
        if (manual || _notifiedVersion != version)
        {
            _notifiedVersion = version;
            try
            {
                new ToastContentBuilder()
                    .AddArgument(ToastArgs.Action, ToastArgs.RestartToUpdate)
                    .AddText($"ntfy-bar {version} is ready")
                    .AddText("It installs the next time ntfy-bar quits or starts. Restart now to update straight away.")
                    .AddButton(new ToastButton().SetContent("Restart now").AddArgument(ToastArgs.Action, ToastArgs.RestartToUpdate))
                    .AddButton(new ToastButtonDismiss("Later"))
                    .AddAudio(new ToastAudio { Silent = true })
                    .Show(t => { t.Tag = "update"; t.Group = "ntfy-bar"; });
            }
            catch (Exception e) { Log.Write($"update: toast failed: {e.Message}"); }
        }
    }

    /// <summary>Portable / unpackaged copy: compare with the latest GitHub release and point at the installer.</summary>
    private async Task CheckPortableAsync(bool manual)
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
        var page = latest is not null && Uri.TryCreate(latest.HtmlUrl, UriKind.Absolute, out var u) ? u : new Uri(UpdateCheck.ReleasesPage);
        if (latest is not null && UpdateCheck.IsNewer(latest.TagName, AppInfo.Version))
        {
            AvailableVersion = latest.TagName.TrimStart('v');
            Changed?.Invoke();
            if (manual || _notifiedVersion != latest.TagName)
            {
                _notifiedVersion = latest.TagName;
                try
                {
                    Toasts.ShowSimple("Update available",
                        $"ntfy-bar {latest.TagName} is out. This copy is portable and can't update itself: install it with ntfy-bar-windows-Setup.exe to get automatic updates.",
                        "Download", page, page.AbsoluteUri);
                }
                catch (Exception e) { Log.Write($"update: toast failed: {e.Message}"); }
            }
        }
        else if (manual)
        {
            var answer = MessageBox.Show(
                $"You're on the latest version (v{AppInfo.Version}).\n\nThis copy is portable, so it can't update itself. " +
                "Install ntfy-bar with ntfy-bar-windows-Setup.exe from the releases page to get automatic updates. Open the page?",
                AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) AppController.OpenUrl(page.AbsoluteUri);
        }
    }

    public void OpenReleasePage() => AppController.OpenUrl(UpdateCheck.ReleasesPage);

    /// <summary>Applies the downloaded update now and starts the new version (the process exits).</summary>
    public void RestartToApply()
    {
        if (_manager?.UpdatePendingRestart is not { } pending) return;
        Log.Write($"update: restarting into {pending.Version}");
        BeforeExit?.Invoke();
        _manager.ApplyUpdatesAndRestart(pending);
    }

    /// <summary>Saves state etc. before the updater replaces the files.</summary>
    public event Action? BeforeExit;

    /// <summary>On quit: hand the downloaded update to Velopack's updater, which applies it once we've exited.</summary>
    public void ApplyOnExit()
    {
        if (_manager?.UpdatePendingRestart is not { } pending) return;
        try
        {
            Log.Write($"update: applying {pending.Version} after exit");
            _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
        catch (Exception e) { Log.Write($"update: apply on exit failed: {e.Message}"); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
