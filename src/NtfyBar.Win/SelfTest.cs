using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;

namespace NtfyBar.Win;

/// <summary><c>ntfy-bar-windows.exe --selftest</c>: proves the published exe starts, the core logic
/// runs, the toast XML builds with each sound class, and the toast activator registers and shows one
/// toast. Exits 0 on success. Used by CI on windows-latest; safe to run on a user's PC.</summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        ConsoleAttach.Attach();
        var failures = 0;
        void Check(string name, Func<string> body)
        {
            try { Console.WriteLine($"ok   {name}: {body()}"); }
            catch (Exception e) { failures++; Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); }
        }

        Console.WriteLine($"ntfy-bar-windows {AppInfo.Version} selftest on {Environment.OSVersion}");

        Check("paths", () => $"{Storage.SettingsPath} | {Storage.StatePath}");

        var catalog = Json.Parse<Catalog>("""
            {"version":1,"sync_topic":"st_x","apps":[{"id":"demo","name":"Demo","icon":"","sound":"alert",
             "topics":[{"topic":"demo-orders","name":"Orders","sound":"alert","permission":"read-only"}]}]}
            """)!;
        var reconciled = CatalogSync.Reconcile(new List<TopicConfig>(), catalog);
        var settings = new AppSettings { ServerUrl = "https://ntfy.example.com", Topics = reconciled.Topics, Insistent = true };
        Check("catalog reconcile", () => reconciled.Added.Single() == "demo-orders" ? "1 topic added" : throw new Exception("unexpected"));

        foreach (var cls in SoundClass.All)
        {
            Check($"toast xml ({cls})", () =>
            {
                var topic = reconciled.Topics[0] with { Sound = cls };
                var m = new NtfyMessage
                {
                    Id = "selftest" + cls, Topic = topic.Name, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Title = "Selftest", Message = "**Sound class** " + cls, Priority = cls == "urgent" ? 5 : 3,
                    Click = "https://ntfy.sh",
                    Actions = new() { new NtfyAction { Action = "view", Label = "Open", Url = "https://ntfy.sh" } },
                };
                var xml = Toasts.Build(m, topic, settings, null, null).GetToastContent().GetContent();
                if (!xml.Contains("<toast")) throw new Exception("no <toast> element");
                return xml.Length + " chars, audio=" + (System.Text.RegularExpressions.Regex.Match(xml, "<audio[^>]*>").Value);
            });
        }

        var showToast = !args.Contains("--no-toast");
        if (showToast)
        {
            Check("show toast", () =>
            {
                new ToastContentBuilder()
                    .AddText("ntfy-bar selftest")
                    .AddText("If you can read this, notifications work.")
                    .AddAudio(new ToastAudio { Silent = true })
                    .Show(t => { t.Tag = "selftest"; t.Group = "selftest"; });
                Thread.Sleep(1500);
                var history = ToastNotificationManagerCompat.History.GetHistory();
                ToastNotificationManagerCompat.History.Remove("selftest", "selftest");
                return $"shown (history had {history.Count})";
            });
        }

        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: {failures} FAILED");
        Console.Out.Flush();
        return failures == 0 ? 0 : 1;
    }
}
