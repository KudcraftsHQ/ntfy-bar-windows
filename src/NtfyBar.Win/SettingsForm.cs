using System.Text.RegularExpressions;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Settings window, laid out like ntfy-bar's: Server, Topics, Notifications, General.
/// Managed (catalog) rows are read-only except Subscribed and Mute.</summary>
internal sealed partial class SettingsForm : Form
{
    private readonly AppController _app;

    private readonly TextBox _server = new() { Dock = DockStyle.Fill, PlaceholderText = "https://ntfy.example.com" };
    private readonly TextBox _username = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TextBox _token = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _status = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _save = new() { Text = "Save && Reconnect", AutoSize = true };
    private readonly Button _removeToken = new() { Text = "Remove Token", AutoSize = true };
    private readonly Button _signIn = new() { Text = "Sign in…", AutoSize = true };

    private readonly CheckBox _catalog = new() { Text = "Sync topics from the server's catalog", AutoSize = true };
    private readonly Button _syncNow = new() { Text = "Sync now", AutoSize = true };
    private readonly Label _syncInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left };

    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
        EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly TextBox _newTopic = new() { Width = 220, PlaceholderText = "Add topic" };
    private readonly Button _add = new() { Text = "Add", AutoSize = true };
    private readonly Button _remove = new() { Text = "Remove…", AutoSize = true };
    private readonly Label _topicError = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 40, 40) };

    private readonly CheckBox _soundForAll = new() { Text = "Play sound for every message", AutoSize = true };
    private readonly CheckBox _insistent = new() { Text = "Insistent alarm for max-priority (5) messages: loops until dismissed", AutoSize = true };
    private readonly LinkLabel _notifSettings = new() { Text = "Notifications are turned off for ntfy-bar. Open Windows Settings", AutoSize = true, Visible = false };
    private readonly CheckBox _autostart = new() { Text = "Launch at login", AutoSize = true };
    private readonly Label _autostartError = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 40, 40) };

    private bool _loading;

    public SettingsForm(AppController app)
    {
        _app = app;
        Text = "ntfy-bar Settings";
        Icon = AppIcon.Load();
        StartPosition = FormStartPosition.CenterScreen;
        Size = LogicalToDeviceUnits(new Size(620, 720));
        MinimumSize = LogicalToDeviceUnits(new Size(520, 600));
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(10);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(ServerGroup(), 0, 0);
        root.Controls.Add(TopicsGroup(), 0, 1);
        root.Controls.Add(NotificationsGroup(), 0, 2);
        root.Controls.Add(GeneralGroup(), 0, 3);
        Controls.Add(root);

        _app.Changed += OnAppChanged;
        FormClosed += (_, _) => _app.Changed -= OnAppChanged;
        Shown += (_, _) => { LoadConnection(); RefreshAll(); };
        Activated += (_, _) => RefreshNotificationsBlocked();
    }

    // ---- layout ----

    private GroupBox ServerGroup()
    {
        var g = new GroupBox { Text = "Server", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddRow(t, "Server URL", _server);
        AddRow(t, "Username", _username);
        AddRow(t, "Password", _password);
        AddRow(t, "Access token", _token);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.Add(_save);
        buttons.Controls.Add(_removeToken);
        buttons.Controls.Add(_signIn);
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(_status, 0, 0);
        bar.Controls.Add(buttons, 1, 0);
        t.Controls.Add(bar, 0, t.RowCount);
        t.SetColumnSpan(bar, 2);
        t.RowCount++;

        var catalogRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        catalogRow.Controls.Add(_catalog);
        catalogRow.Controls.Add(_syncNow);
        catalogRow.Controls.Add(_syncInfo);
        t.Controls.Add(catalogRow, 0, t.RowCount);
        t.SetColumnSpan(catalogRow, 2);
        t.RowCount++;

        t.Controls.Add(Hint("Password and token are stored in Windows Credential Manager. Leave a field empty to keep the saved value. " +
                            "Sign in creates a per-device access token and forgets the password."), 0, t.RowCount);
        t.SetColumnSpan(t.Controls[^1], 2);
        t.RowCount++;
        g.Controls.Add(t);

        foreach (var box in new[] { _server, _username, _password, _token }) box.TextChanged += (_, _) => UpdateSaveEnabled();
        _save.Click += (_, _) => ApplyConnection();
        _removeToken.Click += (_, _) => { _app.UpdateCredentials(null, ""); RefreshAll(); };
        _signIn.Click += (_, _) => SignIn();
        _catalog.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var on = _catalog.Checked;
            _app.Update(s => s with { CatalogEnabled = on });
        };
        _syncNow.Click += (_, _) => _app.Catalog.Reset();
        AcceptButton = _save;
        return g;
    }

    private GroupBox TopicsGroup()
    {
        var g = new GroupBox { Text = "Topics", Dock = DockStyle.Fill, Padding = new Padding(8) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", FillWeight = 10, ToolTipText = "Off = not subscribed; history is kept" });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "mute", HeaderText = "Mute", FillWeight = 12, ToolTipText = "Muted topics are listed but never notify" });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "topic", HeaderText = "Topic", ReadOnly = true, FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "app", HeaderText = "App", ReadOnly = true, FillWeight = 22 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "sound", HeaderText = "Sound", ReadOnly = true, FillWeight = 14 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "source", HeaderText = "Source", ReadOnly = true, FillWeight = 16 });
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += GridValueChanged;
        _grid.SelectionChanged += (_, _) => UpdateRemoveEnabled();
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
        t.Controls.Add(_grid, 0, 0);

        var addRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        addRow.Controls.Add(_newTopic);
        addRow.Controls.Add(_add);
        addRow.Controls.Add(_remove);
        t.Controls.Add(addRow, 0, 1);
        t.Controls.Add(_topicError, 0, 2);
        t.Controls.Add(Hint("Turn a topic off to stop subscribing without losing it. Muted topics are still listed but never notify. " +
                            "Topics from the catalog can't be removed here, only turned off or muted."), 0, 3);
        g.Controls.Add(t);

        _add.Click += (_, _) => AddTopic();
        _newTopic.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddTopic(); e.SuppressKeyPress = true; } };
        _newTopic.TextChanged += (_, _) => _add.Enabled = _newTopic.Text.Trim().Length > 0;
        _add.Enabled = false;
        _remove.Click += (_, _) => RemoveSelected();
        return g;
    }

    private GroupBox NotificationsGroup()
    {
        var g = new GroupBox { Text = "Notifications", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        f.Controls.Add(_soundForAll);
        f.Controls.Add(Hint("High-priority messages (4–5) always play a sound. Topics from the catalog use their app's sound. Muted topics never notify."));
        f.Controls.Add(_insistent);
        f.Controls.Add(_notifSettings);
        g.Controls.Add(f);
        _soundForAll.CheckedChanged += (_, _) => { if (!_loading) { var v = _soundForAll.Checked; _app.Update(s => s with { SoundForAll = v }); } };
        _insistent.CheckedChanged += (_, _) => { if (!_loading) { var v = _insistent.Checked; _app.Update(s => s with { Insistent = v ? true : null }); } };
        _notifSettings.LinkClicked += (_, _) => AppController.OpenUrl("ms-settings:notifications");
        return g;
    }

    private GroupBox GeneralGroup()
    {
        var g = new GroupBox { Text = "General", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        var f = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        f.Controls.Add(_autostart);
        f.Controls.Add(_autostartError);
        f.Controls.Add(Hint($"Version {AppInfo.Version}. Settings: {Storage.SettingsPath}"));
        g.Controls.Add(f);
        _autostart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            try { Autostart.Set(_autostart.Checked); _autostartError.Text = ""; }
            catch (Exception e) { _autostartError.Text = e.Message; }
        };
        return g;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control c)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) }, 0, t.RowCount);
        t.Controls.Add(c, 1, t.RowCount);
        t.RowCount++;
    }

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 2, 3, 6),
    };

    // ---- state ----

    private void OnAppChanged()
    {
        if (!IsDisposed && Visible) RefreshAll();
    }

    private void LoadConnection()
    {
        _server.Text = _app.Settings.ServerUrl;
        _username.Text = _app.Settings.Username;
        _password.Text = "";
        _token.Text = "";
    }

    private void RefreshAll()
    {
        _loading = true;
        try
        {
            var s = _app.Settings;
            _password.PlaceholderText = _app.HasPassword ? "Saved in Credential Manager" : "Not set";
            _token.PlaceholderText = _app.HasToken ? "Saved in Credential Manager" : "Optional — overrides password";
            _removeToken.Visible = _app.HasToken;
            UpdateSaveEnabled();

            var st = _app.Status;
            _status.Text = "● " + st.Label + (st.Detail is { } d ? $" — {d}" : "");
            _status.ForeColor = st.State switch
            {
                ConnectionState.Connected => Color.FromArgb(39, 174, 96),
                ConnectionState.AuthError => Color.FromArgb(200, 40, 40),
                ConnectionState.NotConfigured => SystemColors.GrayText,
                _ => Color.FromArgb(220, 130, 20),
            };

            _catalog.Checked = s.IsCatalogEnabled;
            _syncNow.Enabled = s.IsCatalogEnabled && s.HasServer;
            _syncInfo.Text = !s.IsCatalogEnabled ? ""
                : _app.Catalog.LastError is { } err ? err
                : _app.Catalog.LastSync is { } at ? $"Last sync {at:HH:mm}"
                : "Not synced yet";
            _syncInfo.ForeColor = _app.Catalog.AuthError ? Color.FromArgb(200, 40, 40) : SystemColors.GrayText;

            _soundForAll.Checked = s.SoundForAll;
            _insistent.Checked = s.Insistent == true;
            _autostart.Checked = Autostart.IsEnabled;
            RefreshNotificationsBlocked();
            RefreshGrid();
        }
        finally { _loading = false; }
    }

    private void RefreshNotificationsBlocked() => _notifSettings.Visible = Toasts.NotificationsBlocked;

    private void RefreshGrid()
    {
        var selected = _grid.CurrentRow?.Tag as string;
        var firstRow = _grid.FirstDisplayedScrollingRowIndex;
        _grid.Rows.Clear();
        foreach (var group in TopicGroups.Build(_app.Settings.Topics))
        foreach (var t in group.Topics)
        {
            var i = _grid.Rows.Add(t.Enabled, t.Muted, t.Label == t.Name ? t.Name : $"{t.Label}  ({t.Name})",
                t.AppName ?? "", t.Sound ?? "", t.IsManaged ? "catalog" : "you");
            var row = _grid.Rows[i];
            row.Tag = t.Name;
            row.Cells["mute"].ReadOnly = !t.Enabled;
            if (!t.Enabled) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
            if (t.Name == selected) row.Selected = true;
        }
        if (firstRow >= 0 && firstRow < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = firstRow;
        UpdateRemoveEnabled();
    }

    private void GridValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0) return;
        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is not string name) return;
        var value = row.Cells[e.ColumnIndex].Value is true;
        var column = _grid.Columns[e.ColumnIndex].Name;
        BeginInvoke(() =>
        {
            if (column == "on") _app.SetTopic(name, t => t with { Enabled = value });
            else if (column == "mute") _app.SetTopic(name, t => t with { Muted = value });
        });
    }

    private void UpdateRemoveEnabled()
    {
        var name = _grid.CurrentRow?.Tag as string;
        var t = name is null ? null : _app.Settings.Topic(name);
        _remove.Enabled = t is not null && !t.IsManaged;
    }

    private void UpdateSaveEnabled()
    {
        var s = _app.Settings;
        _save.Enabled = _server.Text.Trim() != s.ServerUrl || _username.Text.Trim() != s.Username
            || _password.Text.Length > 0 || _token.Text.Length > 0;
    }

    // ---- actions ----

    private void ApplyConnection()
    {
        var server = _server.Text.Trim();
        if (server.Length > 0 && !(Uri.TryCreate(server, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
        {
            MessageBox.Show(this, "Enter the server as a full URL, e.g. https://ntfy.example.com", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var user = _username.Text.Trim();
        _app.Reconfigure(server, user, _password.Text.Length > 0 ? _password.Text : null, _token.Text.Length > 0 ? _token.Text.Trim() : null);
        LoadConnection();
        RefreshAll();
    }

    public void OpenSignIn() => BeginInvoke(SignIn);

    private void SignIn()
    {
        using var dlg = new LoginForm(_app, _server.Text.Trim().Length > 0 ? _server.Text.Trim() : _app.Settings.ServerUrl,
            _username.Text.Trim().Length > 0 ? _username.Text.Trim() : _app.Settings.Username);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            LoadConnection();
            RefreshAll();
        }
    }

    [GeneratedRegex("^[-_A-Za-z0-9]{1,64}$")]
    private static partial Regex TopicRegex();

    private void AddTopic()
    {
        var name = _newTopic.Text.Trim();
        if (name.Length == 0) return;
        if (!TopicRegex().IsMatch(name)) { _topicError.Text = "Topics may only contain letters, digits, - and _ (max 64)."; return; }
        if (_app.Settings.TopicNames.Contains(name)) { _topicError.Text = $"Already subscribed to {name}."; return; }
        _app.Update(s => s with { Topics = s.Topics.Append(new TopicConfig { Name = name }).ToList() });
        _newTopic.Text = "";
        _topicError.Text = "";
    }

    private void RemoveSelected()
    {
        if (_grid.CurrentRow?.Tag is not string name) return;
        var t = _app.Settings.Topic(name);
        if (t is null || t.IsManaged) return;
        var answer = MessageBox.Show(this,
            $"Remove topic {name}?\n\nIts history will be kept. To stop receiving it temporarily, turn it off instead.",
            "Remove topic", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK) return;
        _app.Update(s => s with { Topics = s.Topics.Where(x => x.Name != name).ToList() });
    }
}
