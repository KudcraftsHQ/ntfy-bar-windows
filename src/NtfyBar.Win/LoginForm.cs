using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary>Sign in: username + password → <c>POST /v1/account/token</c> (label
/// <c>ntfy-bar-win-&lt;machine&gt;</c>) → token in Credential Manager; the password is not kept.</summary>
internal sealed class LoginForm : Form
{
    private readonly AppController _app;
    private readonly TextBox _server = new() { Dock = DockStyle.Fill, PlaceholderText = "https://ntfy.example.com" };
    private readonly TextBox _username = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.FromArgb(200, 40, 40), MaximumSize = new Size(380, 0) };
    private readonly Button _ok = new() { Text = "Sign in", AutoSize = true };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
    private CancellationTokenSource? _cts;

    public LoginForm(AppController app, string server, string username)
    {
        _app = app;
        Text = "Sign in to ntfy";
        Icon = AppIcon.Load();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        void Row(string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 8, 3) }, 0, t.RowCount);
            t.Controls.Add(c, 1, t.RowCount);
            t.RowCount++;
        }
        Row("Server URL", _server);
        Row("Username", _username);
        Row("Password", _password);
        var hint = new Label
        {
            Text = $"Creates an access token named {NtfyApi.TokenLabel(Environment.MachineName)}. You can revoke it in the ntfy web app under Account › Access tokens.",
            AutoSize = true, MaximumSize = new Size(380, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 6),
        };
        t.Controls.Add(hint, 0, t.RowCount);
        t.SetColumnSpan(hint, 2);
        t.RowCount++;
        t.Controls.Add(_error, 0, t.RowCount);
        t.SetColumnSpan(_error, 2);
        t.RowCount++;
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_ok);
        t.Controls.Add(buttons, 0, t.RowCount);
        t.SetColumnSpan(buttons, 2);
        Controls.Add(t);

        AcceptButton = _ok;
        CancelButton = _cancel;
        _server.Text = server;
        _username.Text = username;
        _ok.Click += async (_, _) => await SubmitAsync();
        FormClosing += (_, _) => _cts?.Cancel();
        Shown += (_, _) => (_server.Text.Length == 0 ? _server : _username.Text.Length == 0 ? _username : _password).Focus();
    }

    private async Task SubmitAsync()
    {
        var server = _server.Text.Trim().TrimEnd('/');
        var user = _username.Text.Trim();
        var pass = _password.Text;
        if (!(Uri.TryCreate(server, UriKind.Absolute, out var u) && u.Scheme is "https" or "http"))
        {
            _error.Text = "Enter the server as a full URL, e.g. https://ntfy.example.com";
            return;
        }
        if (user.Length == 0 || pass.Length == 0) { _error.Text = "Enter your username and password."; return; }

        _error.Text = "";
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var token = await NtfyApi.MintTokenAsync(_app.Http, server, user, pass, NtfyApi.TokenLabel(Environment.MachineName), _cts.Token);
            _app.SignedIn(server, user, token);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (SignInException e) { _error.Text = e.Message; }
        catch (OperationCanceledException) { /* closed */ }
        catch (Exception e) { _error.Text = e.Message; }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _ok.Enabled = !busy;
        _ok.Text = busy ? "Signing in…" : "Sign in";
        UseWaitCursor = busy;
    }
}
