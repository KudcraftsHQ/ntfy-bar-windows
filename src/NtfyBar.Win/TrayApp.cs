using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Tray icon + menu. Left click opens the message list (ntfy-bar's popover);
/// right click: status, topics grouped by app with per-topic mute/enable, and the app commands.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly AppController _app;
    private readonly Updater _updater = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private MessagesForm? _messages;
    private SettingsForm? _settings;
    private Icon? _icon;
    private (ConnectionState, int, int, bool) _iconKey;

    public TrayApp(bool autostart)
    {
        _app = new AppController();
        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _menu.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };
        _tray.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) ToggleMessages(); };

        _app.Changed += Refresh;
        _app.ShowMessagesRequested += ShowMessages;
        _updater.Changed += Refresh;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        Refresh();
        _app.Start();
        _updater.Start();

        if (!_app.Settings.HasServer) ShowSettings();
        else if (!autostart && Toasts.NotificationsBlocked)
            _tray.ShowBalloonTip(5000, AppInfo.Name, "Notifications are turned off for ntfy-bar in Windows Settings.", ToolTipIcon.Warning);
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) =>
        Program.Ui.Post(_ => { _iconKey = default; Refresh(); }, null);

    private void Refresh()
    {
        var unread = _app.Store.UnreadCount(_app.Settings);
        var size = SystemInformation.SmallIconSize.Width;
        var light = TrayIcons.TaskbarIsLight();
        var key = (_app.Status.State, Math.Min(unread, 100), size, light);
        if (key != _iconKey)
        {
            _iconKey = key;
            var old = _icon;
            _icon = TrayIcons.Render(_app.Status.State, unread, size, light);
            _tray.Icon = _icon;
            old?.Dispose();
        }
        var tip = $"{AppInfo.Name} — {_app.Status.Label}";
        if (unread > 0) tip += $" — {unread} unread";
        _tray.Text = tip.Length > 127 ? tip[..127] : tip;
        _messages?.RefreshList();
    }

    private void BuildMenu()
    {
        _menu.SuspendLayout();
        foreach (ToolStripItem item in _menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();

        var status = _app.Status;
        _menu.Items.Add(new ToolStripMenuItem($"{AppInfo.Name} — {status.Label}") { Enabled = false });
        if (status.Detail is { } detail) _menu.Items.Add(new ToolStripMenuItem(detail) { Enabled = false });
        if (_app.Catalog.LastError is { } err && _app.Settings.IsCatalogEnabled)
            _menu.Items.Add(new ToolStripMenuItem(err) { Enabled = false });

        var open = new ToolStripMenuItem("Open messages", null, (_, _) => ShowMessages());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());

        AddTopics();

        _menu.Items.Add(new ToolStripSeparator());
        var unread = _app.Store.UnreadCount(_app.Settings);
        _menu.Items.Add(new ToolStripMenuItem("Mark all as read", null, (_, _) => _app.MarkAllRead()) { Enabled = unread > 0 });
        if (_app.Settings.IsCatalogEnabled)
            _menu.Items.Add(new ToolStripMenuItem("Sync topics now", null, (_, _) => _app.Catalog.Kick()) { Enabled = _app.Settings.HasServer });
        _menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        var updateLabel = _updater.Available is { } rel ? $"Update available: {rel.TagName}…" : "Check for updates…";
        _menu.Items.Add(new ToolStripMenuItem(updateLabel, null, async (_, _) =>
        {
            if (_updater.Available is { } r) AppController.OpenUrl(r.HtmlUrl);
            else await _updater.CheckAsync(manual: true);
        }));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit ntfy-bar", null, (_, _) => Quit()));
        _menu.ResumeLayout();
    }

    private void AddTopics()
    {
        var topics = _app.Settings.Topics;
        if (topics.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No topics yet") { Enabled = false });
            return;
        }
        var groups = TopicGroups.Build(topics);
        foreach (var group in groups)
        {
            if (group.Header is not null)
            {
                var header = new ToolStripMenuItem(group.Header) { Enabled = false };
                header.Font = new Font(header.Font, FontStyle.Bold);
                _menu.Items.Add(header);
            }
            foreach (var t in group.Topics) _menu.Items.Add(TopicItem(t));
        }
    }

    private ToolStripMenuItem TopicItem(TopicConfig t)
    {
        var unread = _app.Store.UnreadCount(_app.Settings, t.Name);
        var text = t.Label;
        if (!t.Enabled) text += "  (off)";
        else if (t.Muted) text += "  (muted)";
        else if (unread > 0) text += $"  ({unread})";
        var item = new ToolStripMenuItem(text) { Checked = t.Enabled && !t.Muted };
        if (t.Label != t.Name) item.ToolTipText = t.Name;

        var name = t.Name;
        item.DropDownItems.Add(new ToolStripMenuItem("Subscribed", null, (_, _) => _app.SetTopic(name, x => x with { Enabled = !x.Enabled }))
        {
            Checked = t.Enabled,
            ToolTipText = "Off = not subscribed; history is kept",
        });
        item.DropDownItems.Add(new ToolStripMenuItem("Muted", null, (_, _) => _app.SetTopic(name, x => x with { Muted = !x.Muted }))
        {
            Checked = t.Muted,
            Enabled = t.Enabled,
            ToolTipText = "Listed, but no notifications",
        });
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(new ToolStripMenuItem("Show messages", null, (_, _) => ShowMessages(name)));
        return item;
    }

    public void ShowMessages() => ShowMessages(null);

    private void ShowMessages(string? topic)
    {
        if (_messages is null || _messages.IsDisposed)
        {
            _messages = new MessagesForm(_app, ShowSettings);
        }
        _messages.ShowNearTray(topic);
    }

    private void ToggleMessages()
    {
        if (_messages is { IsDisposed: false, Visible: true } && !_messages.JustHidden) _messages.Hide();
        else ShowMessages();
    }

    public void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed) _settings = new SettingsForm(_app);
        _settings.Show();
        if (_settings.WindowState == FormWindowState.Minimized) _settings.WindowState = FormWindowState.Normal;
        _settings.Activate();
    }

    private void Quit()
    {
        _tray.Visible = false;
        _app.SaveNow();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _icon?.Dispose();
            _messages?.Dispose();
            _settings?.Dispose();
            _updater.Dispose();
            _app.Dispose();
        }
        base.Dispose(disposing);
    }
}
