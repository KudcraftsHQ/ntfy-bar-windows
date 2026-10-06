using Microsoft.Toolkit.Uwp.Notifications;
using NtfyBar.Core;
using Velopack;
using Velopack.Sources;

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

        var cases = SoundClass.All.Select(c => (Name: c, Class: c, Priority: 3))
            .Append((Name: "insistent p5", Class: SoundClass.Default, Priority: 5));
        foreach (var c in cases)
        {
            Check($"toast xml ({c.Name})", () =>
            {
                var topic = reconciled.Topics[0] with { Sound = c.Class };
                var m = new NtfyMessage
                {
                    Id = "selftest" + c.Class + c.Priority, Topic = topic.Name, Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Title = "Selftest", Message = "**Sound class** " + c.Class, Priority = c.Priority,
                    Click = "https://ntfy.sh",
                    Actions = new() { new NtfyAction { Action = "view", Label = "Open", Url = "https://ntfy.sh" } },
                };
                var xml = Toasts.Build(m, topic, settings, null, null).GetToastContent().GetContent();
                if (!xml.Contains("<toast")) throw new Exception("no <toast> element");
                var toastTag = System.Text.RegularExpressions.Regex.Match(xml, "<toast[^>]*>").Value;
                var scenario = System.Text.RegularExpressions.Regex.Match(toastTag, "scenario=\"[^\"]*\"").Value;
                return $"{xml.Length} chars, {(scenario.Length > 0 ? scenario + ", " : "")}audio={System.Text.RegularExpressions.Regex.Match(xml, "<audio[^>]*>").Value}";
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

        // CI end-to-end update test: an installed old version updates itself from a local release folder.
        var from = Array.IndexOf(args, "--update-from");
        if (from >= 0 && from + 1 < args.Length)
        {
            Check("velopack update", () =>
            {
                var mgr = new UpdateManager(new SimpleFileSource(new DirectoryInfo(args[from + 1])));
                if (!mgr.IsInstalled) throw new Exception("not running from a Velopack install");
                var info = mgr.CheckForUpdates() ?? throw new Exception($"no update found (current {mgr.CurrentVersion})");
                mgr.DownloadUpdates(info);
                var pending = mgr.UpdatePendingRestart ?? throw new Exception("downloaded, but no update pending");
                mgr.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
                return $"{mgr.CurrentVersion} -> {info.TargetFullRelease.Version} downloaded via {info.DeltasToTarget.Length} delta(s), applying after exit";
            });
        }
        else
        {
            Check("velopack", () =>
            {
                var mgr = new UpdateManager(new GithubSource(Updater.RepoUrl, null, false));
                return mgr.IsInstalled ? $"installed, v{mgr.CurrentVersion}" : "portable (not installed): updates by download only";
            });
        }

        Console.WriteLine(failures == 0 ? "selftest: PASS" : $"selftest: {failures} FAILED");
        Console.Out.Flush();
        return failures == 0 ? 0 : 1;
    }
}
