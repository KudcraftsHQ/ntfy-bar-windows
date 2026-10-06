using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;
using CoreScenario = NtfyBar.Core.ToastScenario;
using WinScenario = Microsoft.Toolkit.Uwp.Notifications.ToastScenario;

namespace NtfyBar.Win;

/// <summary>Builds and posts toasts (spec §9.1): local-file logos, the built-in sound set,
/// view/http buttons, one Action Center header per topic.</summary>
internal static class Toasts
{
    private const int MaxActionButtons = 3;

    /// <summary>Activation arrives on a background thread; marshalled to the UI thread for the handler.</summary>
    public static void Initialize(Action<ToastArguments> onActivated)
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument ?? "");
            Program.Ui.Post(_ => onActivated(args), null);
        };
    }

    public static bool NotificationsBlocked
    {
        get
        {
            try
            {
                return ToastNotificationManagerCompat.CreateToastNotifier().Setting != Windows.UI.Notifications.NotificationSetting.Enabled;
            }
            catch { return false; }
        }
    }

    public static ToastContentBuilder Build(NtfyMessage m, TopicConfig? topic, AppSettings settings, string? logoPath, string? imagePath)
    {
        var b = new ToastContentBuilder()
            .AddArgument(ToastArgs.Action, ToastArgs.Open)
            .AddArgument(ToastArgs.Id, m.Id);
        if (m.OpenUrl is { } url) b.AddArgument(ToastArgs.Url, url.AbsoluteUri);

        var label = settings.Label(m.Topic);
        var appName = topic?.AppName;
        var source = appName is not null && appName != label ? $"{appName} · {label}" : label;

        b.AddText(TextUtil.Truncate(m.HasTitle ? m.Title! : label, 120), hintMaxLines: 1);
        var body = TextUtil.Preview(m.Body);
        if (body.Length > 0) b.AddText(TextUtil.Truncate(body, 600));
        if (m.HasTitle) b.AddAttributionText(source);

        // One header per topic groups a topic's toasts together in Action Center.
        b.AddHeader(HeaderId(m.Topic), source, new ToastArguments().Add(ToastArgs.Action, "messages"));

        if (logoPath is not null) b.AddAppLogoOverride(new Uri(logoPath), ToastGenericAppLogoCrop.Default);
        if (imagePath is not null) b.AddInlineImage(new Uri(imagePath));

        var buttons = 0;
        if (m.Actions is { } actions)
        {
            for (var i = 0; i < actions.Count && buttons < MaxActionButtons; i++)
            {
                var a = actions[i];
                if (string.IsNullOrWhiteSpace(a.Label) || !TextUtil.TryAbsoluteUri(a.Url, out var target)) continue;
                if (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp) continue;
                switch (a.Action)
                {
                    case "view":
                        b.AddButton(new ToastButton().SetContent(TextUtil.Truncate(a.Label, 40)).SetProtocolActivation(target));
                        buttons++;
                        break;
                    case "http":
                        b.AddButton(new ToastButton().SetContent(TextUtil.Truncate(a.Label, 40))
                            .AddArgument(ToastArgs.Action, ToastArgs.Http)
                            .AddArgument(ToastArgs.Id, m.Id)
                            .AddArgument(ToastArgs.Index, i)
                            .SetBackgroundActivation());
                        buttons++;
                        break;
                    // "broadcast" is Android-only: skipped.
                }
            }
        }

        var plan = SoundPlan.For(topic?.Sound, m.EffectivePriority, settings.SoundForAll, settings.Insistent == true);
        if (plan.Silent)
            b.AddAudio(new ToastAudio { Silent = true });
        else
            b.AddAudio(new Uri(plan.Src!), loop: plan.Loop ? true : null);

        if (plan.Scenario != CoreScenario.Default)
        {
            b.SetToastScenario(plan.Scenario == CoreScenario.Alarm ? WinScenario.Alarm : WinScenario.Reminder);
            // Reminder/alarm toasts only stay on screen when they have a button.
            if (buttons == 0) b.AddButton(new ToastButtonDismiss());
        }
        return b;
    }

    public static void Show(ToastContentBuilder b, NtfyMessage m)
    {
        b.Show(t =>
        {
            t.Tag = Tag(m.Id);
            t.Group = Group(m.Topic);
            if (m.EffectivePriority <= 1) t.SuppressPopup = true; // min priority: Action Center only
        });
    }

    public static void ShowSimple(string title, string text, string? buttonLabel = null, Uri? buttonUrl = null, string? argsUrl = null)
    {
        var b = new ToastContentBuilder().AddText(title).AddText(text);
        if (argsUrl is not null) b.AddArgument(ToastArgs.Action, ToastArgs.Update).AddArgument(ToastArgs.Url, argsUrl);
        if (buttonLabel is not null && buttonUrl is not null)
            b.AddButton(new ToastButton().SetContent(buttonLabel).SetProtocolActivation(buttonUrl));
        b.Show();
    }

    public static void Remove(NtfyMessage m)
    {
        try { ToastNotificationManagerCompat.History.Remove(Tag(m.Id), Group(m.Topic)); } catch { /* not shown */ }
    }

    public static void ClearAll()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { /* nothing to clear */ }
    }

    private static string Tag(string id) => id.Length <= 64 ? id : id[..64];
    private static string Group(string topic) => topic.Length <= 64 ? topic : topic[..64];
    private static string HeaderId(string topic) => "t:" + topic;
}
