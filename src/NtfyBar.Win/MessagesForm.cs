using System.Drawing.Drawing2D;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>The message list, ntfy-bar's popover on Windows: opens above the tray, newest first,
/// grouped by day, topic filter with unread counts, click a row to open its link.
/// Opening marks what's visible as read; those rows keep their dot until the window closes.</summary>
internal sealed class MessagesForm : Form
{
    private const int RowHeight = 68;
    private const int HeaderHeight = 28;

    private readonly AppController _app;
    private readonly Action _openSettings;
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly Label _status = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ListBox _list = new()
    {
        DrawMode = DrawMode.OwnerDrawVariable,
        BorderStyle = BorderStyle.None,
        IntegralHeight = false,
        Dock = DockStyle.Fill,
    };
    private readonly Label _empty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = SystemColors.GrayText,
        Visible = false,
    };
    private readonly LinkLabel _emptyAction = new() { AutoSize = true, Visible = false };
    private readonly HashSet<string> _fresh = new();
    private string? _topicFilter;
    private DateTime _hiddenAt;
    private bool _updatingFilter;

    public bool JustHidden => (DateTime.UtcNow - _hiddenAt).TotalMilliseconds < 300;

    public MessagesForm(AppController app, Action openSettings)
    {
        _app = app;
        _openSettings = openSettings;
        Text = "ntfy";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        MinimumSize = new Size(340, 360);
        Size = LogicalToDeviceUnits(new Size(400, 560));
        KeyPreview = true;

        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8, 8, 8, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_filter, 0, 0);
        _status.Anchor = AnchorStyles.Right;
        header.Controls.Add(_status, 1, 0);

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight };
        footer.Controls.Add(FooterButton("Mark all as read", (_, _) => { _app.MarkAllRead(); _fresh.Clear(); _list.Invalidate(); }));
        footer.Controls.Add(FooterButton("Clear…", (_, _) => ConfirmClear()));
        footer.Controls.Add(FooterButton("Settings…", (_, _) => _openSettings()));

        _list.MeasureItem += (_, e) => e.ItemHeight = LogicalToDeviceUnits(_list.Items[e.Index] is DayHeader ? HeaderHeight : RowHeight);
        _list.DrawItem += DrawItem;
        _list.MouseUp += ListMouseUp;
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter && _list.SelectedItem is Entry en) _app.Open(en); };
        _list.MouseMove += (_, e) =>
        {
            var i = _list.IndexFromPoint(e.Location);
            _list.Cursor = i >= 0 && _list.Items[i] is Entry { Message.OpenUrl: not null } ? Cursors.Hand : Cursors.Default;
        };

        _emptyAction.LinkClicked += (_, _) => _openSettings();
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_list);
        body.Controls.Add(_empty);
        body.Controls.Add(_emptyAction);
        body.Resize += (_, _) => _emptyAction.Location = new Point((body.Width - _emptyAction.Width) / 2, body.Height / 2 + 24);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        _filter.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingFilter) return;
            _topicFilter = (_filter.SelectedItem as FilterItem)?.Topic;
            RefreshList();
        };
        Deactivate += (_, _) => { if (Visible) Hide(); };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) { _hiddenAt = DateTime.UtcNow; _fresh.Clear(); }
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private static Button FooterButton(string text, EventHandler click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.System };
        b.Click += click;
        return b;
    }

    public void ShowNearTray(string? topic)
    {
        if (topic is not null) _topicFilter = topic;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        RefreshList();
        MarkVisibleRead();
        Show();
        Activate();
    }

    private void MarkVisibleRead()
    {
        var unread = Visible_Entries().Where(e => !e.Read).Select(e => e.Id).ToList();
        _fresh.UnionWith(unread);
        _app.MarkRead(unread);
    }

    private IEnumerable<Entry> Visible_Entries()
    {
        var s = _app.Settings;
        return _app.Store.Entries.Where(e => s.IsEnabled(e.Message.Topic) && (_topicFilter is null || e.Message.Topic == _topicFilter));
    }

    public void RefreshList()
    {
        if (IsDisposed) return;
        UpdateFilter();
        UpdateStatus();
        // Messages arriving while the window is open are read on sight, but keep their dot.
        if (Visible && ContainsFocus) MarkVisibleRead();

        var top = _list.TopIndex;
        var selected = (_list.SelectedItem as Entry)?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        DateTime? day = null;
        var headers = Visible_Entries().Select(e => e.Message.Date.LocalDateTime.Date).Distinct().Count() > 1;
        foreach (var e in Visible_Entries().Take(500))
        {
            var d = e.Message.Date.LocalDateTime.Date;
            if (headers && d != day) { _list.Items.Add(new DayHeader(d)); day = d; }
            _list.Items.Add(e);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.TopIndex = Math.Min(top, _list.Items.Count - 1);
        if (selected is not null)
            for (var i = 0; i < _list.Items.Count; i++)
                if (_list.Items[i] is Entry en && en.Id == selected) { _list.SelectedIndex = i; break; }

        var empty = _list.Items.Count == 0;
        _empty.Visible = empty;
        _list.Visible = !empty;
        _emptyAction.Visible = empty && !_app.Settings.HasServer;
        if (empty)
        {
            if (!_app.Settings.HasServer)
            {
                _empty.Text = "Connect to your ntfy server";
                _emptyAction.Text = "Open Settings…";
            }
            else
            {
                var n = _app.Settings.EnabledTopicNames.Count;
                _empty.Text = n == 0 ? "No topics yet" : $"No messages yet\nNew messages from {n} topic{(n == 1 ? "" : "s")} appear here.";
            }
        }
    }

    private void UpdateStatus()
    {
        var s = _app.Status;
        _status.Text = "● " + s.Label;
        _status.ForeColor = s.State switch
        {
            ConnectionState.Connected => Color.FromArgb(39, 174, 96),
            ConnectionState.AuthError => Color.FromArgb(220, 60, 50),
            ConnectionState.NotConfigured => SystemColors.GrayText,
            _ => Color.FromArgb(230, 140, 30),
        };
    }

    private sealed record FilterItem(string? Topic, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record DayHeader(DateTime Day);

    private void UpdateFilter()
    {
        var s = _app.Settings;
        var items = new List<FilterItem>();
        var total = _app.Store.UnreadCount(s);
        items.Add(new FilterItem(null, total > 0 ? $"All topics ({total})" : "All topics"));
        foreach (var g in TopicGroups.Build(s.Topics.Where(t => t.Enabled)))
        foreach (var t in g.Topics)
        {
            var n = t.Muted ? 0 : _app.Store.UnreadCount(s, t.Name);
            var label = g.Header is not null && g.Header != "Other topics" ? $"{g.Header} · {t.Label}" : t.Label;
            items.Add(new FilterItem(t.Name, n > 0 ? $"{label} ({n})" : label));
        }
        if (_topicFilter is not null && items.All(i => i.Topic != _topicFilter)) _topicFilter = null;

        _updatingFilter = true;
        try
        {
            if (!items.SequenceEqual(_filter.Items.Cast<FilterItem>()))
            {
                _filter.Items.Clear();
                _filter.Items.AddRange(items.Cast<object>().ToArray());
            }
            _filter.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Topic == _topicFilter));
        }
        finally { _updatingFilter = false; }
    }

    private void ListMouseUp(object? sender, MouseEventArgs e)
    {
        var i = _list.IndexFromPoint(e.Location);
        if (i < 0 || _list.Items[i] is not Entry entry) return;
        _list.SelectedIndex = i;
        if (e.Button == MouseButtons.Left)
        {
            _app.Open(entry);
            return;
        }
        if (e.Button != MouseButtons.Right) return;
        var menu = new ContextMenuStrip();
        if (entry.Message.OpenUrl is { } url)
            menu.Items.Add("Open link", null, (_, _) => _app.Open(entry));
        menu.Items.Add("Copy message", null, (_, _) =>
        {
            var text = entry.Message.HasTitle ? $"{entry.Message.Title}\n{entry.Message.Body}" : entry.Message.Body;
            if (text.Length > 0) Clipboard.SetText(text);
        });
        if (!_fresh.Contains(entry.Id) && entry.Read)
            menu.Items.Add("Mark as unread", null, (_, _) => { entry.Read = false; _app.NotifyChanged(); });
        menu.Items.Add(new ToolStripSeparator());
        var topic = entry.Message.Topic;
        menu.Items.Add($"Clear {_app.Settings.Label(topic)}…", null, (_, _) => ConfirmClear(topic));
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(_list, e.Location);
    }

    private void ConfirmClear(string? topic = null)
    {
        topic ??= _topicFilter;
        var what = topic is null ? "all messages" : $"all messages from {_app.Settings.Label(topic)}";
        TopMost = false;
        var ok = MessageBox.Show(this, $"Clear {what}? This only affects this PC.", "Clear messages",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        TopMost = true;
        if (ok) _app.Clear(topic);
    }

    // ---- drawing ----

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var b = e.Bounds;

        if (_list.Items[e.Index] is DayHeader h)
        {
            using var bg = new SolidBrush(SystemColors.Window);
            g.FillRectangle(bg, b);
            using var hf = new Font(Font.FontFamily, 8.25f, FontStyle.Bold);
            TextRenderer.DrawText(g, DayTitle(h.Day).ToUpperInvariant(), hf, new Rectangle(b.X + Scale(14), b.Y, b.Width, b.Height),
                SystemColors.GrayText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            return;
        }

        var entry = (Entry)_list.Items[e.Index];
        var m = entry.Message;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Color.FromArgb(232, 240, 252) : SystemColors.Window)) g.FillRectangle(bg, b);
        using (var sep = new Pen(Color.FromArgb(236, 236, 236))) g.DrawLine(sep, b.Left + Scale(56), b.Bottom - 1, b.Right, b.Bottom - 1);

        var isNew = !entry.Read || _fresh.Contains(entry.Id);
        if (isNew)
        {
            using var dot = new SolidBrush(Color.FromArgb(0, 122, 255));
            var d = Scale(7);
            g.FillEllipse(dot, b.X + Scale(5), b.Y + Scale(20), d, d);
        }

        // icon: message icon → newest icon on the topic → app icon → coloured initial
        var iconRect = new Rectangle(b.X + Scale(16), b.Y + Scale(10), Scale(32), Scale(32));
        var topicConfig = _app.Settings.Topic(m.Topic);
        Image? img = null;
        var iconUrl = m.IconUrl ?? (TextUtil.TryAbsoluteUri(_app.Store.LatestIcon(m.Topic), out var li) ? li : null)
            ?? (TextUtil.TryAbsoluteUri(topicConfig?.AppIcon, out var ai) ? ai : null);
        if (iconUrl is not null && IconRules.IsAllowedScheme(iconUrl))
            img = _app.Icons.GetImage(iconUrl, _app.AuthorizationFor(iconUrl), () => _list.Invalidate());
        if (img is not null)
        {
            using var clip = RoundRect(iconRect, Scale(7));
            g.SetClip(clip);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(img, iconRect);
            g.ResetClip();
        }
        else
        {
            using var circle = new SolidBrush(TrayIcons.TopicColor(m.Topic));
            using var path = RoundRect(iconRect, Scale(7));
            g.FillPath(circle, path);
            var initial = (topicConfig?.AppName ?? _app.Settings.Label(m.Topic)).FirstOrDefault(char.IsLetterOrDigit);
            using var f = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            TextRenderer.DrawText(g, char.ToUpperInvariant(initial == default ? '#' : initial).ToString(), f, iconRect, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        var x = b.X + Scale(58);
        var right = b.Right - Scale(10);
        var time = TimeLabel(m.Date.LocalDateTime);
        var timeSize = TextRenderer.MeasureText(g, time, Font);
        TextRenderer.DrawText(g, time, Font, new Point(right - timeSize.Width, b.Y + Scale(8)), SystemColors.GrayText);

        var titleWidth = right - timeSize.Width - Scale(8) - x;
        var prio = m.EffectivePriority >= 4 ? (m.EffectivePriority == 5 ? "‼ " : "! ") : "";
        using var titleFont = new Font(Font, isNew ? FontStyle.Bold : FontStyle.Regular);
        var title = m.HasTitle ? m.Title! : _app.Settings.Label(m.Topic);
        if (prio.Length > 0)
        {
            var ps = TextRenderer.MeasureText(g, prio, titleFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, prio, titleFont, new Point(x, b.Y + Scale(8)), m.EffectivePriority == 5 ? Color.FromArgb(220, 60, 50) : Color.FromArgb(230, 140, 30), TextFormatFlags.NoPadding);
            x += ps.Width;
            titleWidth -= ps.Width;
        }
        TextRenderer.DrawText(g, title, titleFont, new Rectangle(x, b.Y + Scale(7), titleWidth, Scale(18)), SystemColors.WindowText,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        x = b.X + Scale(58);

        var source = topicConfig?.AppName is { } app ? $"{app} · {_app.Settings.Label(m.Topic)}" : _app.Settings.Label(m.Topic);
        var preview = TextUtil.Preview(m.Body).Replace('\n', ' ');
        if (m.HasTitle || preview.Length == 0)
        {
            using var small = new Font(Font.FontFamily, 8.25f);
            TextRenderer.DrawText(g, source, small, new Rectangle(x, b.Y + Scale(26), right - x, Scale(16)), SystemColors.GrayText,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
        var previewTop = m.HasTitle || preview.Length == 0 ? Scale(43) : Scale(27);
        TextRenderer.DrawText(g, preview, Font, new Rectangle(x, b.Y + previewTop, right - x, b.Bottom - previewTop - b.Y - Scale(4)),
            Color.FromArgb(90, 90, 90), TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
    }

    private int Scale(int v) => LogicalToDeviceUnits(v);

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static string DayTitle(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";
        if (day > today.AddDays(-7)) return day.ToString("dddd");
        return day.ToString(day.Year == today.Year ? "d MMMM" : "d MMMM yyyy");
    }

    private static string TimeLabel(DateTime t)
    {
        var ago = DateTime.Now - t;
        if (ago < TimeSpan.FromMinutes(1)) return "now";
        if (ago < TimeSpan.FromHours(1)) return $"{(int)ago.TotalMinutes}m";
        return t.ToString("HH:mm");
    }
}

internal static class AppIcon
{
    private static Icon? _icon;

    public static Icon Load()
    {
        if (_icon is not null) return _icon;
        using var s = typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico");
        _icon = s is null ? SystemIcons.Application : new Icon(s);
        return _icon;
    }
}
