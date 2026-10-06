# ntfy-bar-windows

A small Windows tray client for [ntfy](https://ntfy.sh), built for self-hosted servers. It is the
Windows sibling of [ntfy-bar](https://github.com/KudcraftsHQ/ntfy-bar) (macOS) and works the same
way: one streaming connection to your topics, a list of recent messages, and native notifications.
It uses the same settings model as ntfy-bar, so a settings file works in both apps.

## Features

- **Tray only.** No taskbar button. The bell fills in and shows a count when there are unread messages,
  shows a slash when the server rejects your credentials, and is dimmed while it's reconnecting.
- **Sign in once, every topic appears.** On a server with the Kudcrafts topic catalog, signing in lists
  every topic your account can read, grouped by app, with the app's icon and sound. New topics appear
  within seconds and topics you lose access to disappear.
- **Live stream.** One long-lived JSON stream for all topics, with an access token or Basic auth.
- **Nothing missed.** Reconnects resume from the last message received. Backoff is exponential,
  capped at 60 s, and the app reconnects right away after sleep or a network change.
- **Native toasts.** Title, body, the app's icon, image attachments, ntfy `view` and `http` action
  buttons, and one Action Center group per topic. Clicking a toast opens the message's link.
- **Message list.** Left-click the tray icon. Newest first, grouped by day, filtered by topic with
  unread counts. Click a message to open its link.
- **Per-topic control** from the tray menu or Settings: turn a topic off to stop subscribing without
  deleting it, or mute it to keep receiving messages silently.
- **Credential Manager.** The password and access token are stored in Windows Credential Manager,
  never in the settings file.
- **Launch at login**, update check against GitHub Releases, and first-run import from the ntfy CLI's
  `client.yml`.

## Install

1. Download `ntfy-bar-windows-vX.Y.Z.exe` from
   [Releases](https://github.com/KudcraftsHQ/ntfy-bar-windows/releases). It's a single, self-contained
   exe (about 80 MB); no .NET install is needed. Windows 10 1809 or later, x64.
2. Put it somewhere permanent, for example `%LOCALAPPDATA%\Programs\ntfy-bar\`. Launch at login points
   at this path.
3. Run it. The build is **not code-signed yet**, so SmartScreen says "Windows protected your PC".
   Click **More info → Run anyway**. You only need to do this once per version.
4. Settings opens on first run. Click **Sign in…**, enter the server URL, your username and password.

You can check the download against `SHA256SUMS` on the release page:
`Get-FileHash .\ntfy-bar-windows-vX.Y.Z.exe -Algorithm SHA256`.

To update, download the new exe, quit ntfy-bar from the tray menu, replace the file and start it again.
The app checks for a new release once a day and shows a notification with a download button.

## Configuration

Right-click the tray icon → **Settings…**

| Setting | Notes |
|---|---|
| Server URL | e.g. `https://ntfy.example.com` |
| Sign in… | Username + password → creates a per-device access token (`ntfy-bar-win-<PC name>`) that **never expires**, and forgets the password. Revoke it in the ntfy web app under Account › Access tokens. If the server ever refuses it, the bell shows a slash, a notification asks you to sign in again, and the tray menu offers **Sign in again…** |
| Username / Password / Access token | Manual alternative to Sign in. The token is sent as `Bearer` and wins over the password |
| Sync topics from the server's catalog | On by default for every server except ntfy.sh. **Sync now** refetches immediately |
| Topics | **On** = subscribed, **Mute** = listed but no notification. Add your own topics below the list. Catalog topics can only be turned off or muted; they disappear by themselves when your access is removed |
| Play sound for every message | By default only priority 4–5 messages play a sound (for your own topics; catalog topics use their app's sound) |
| Insistent alarm | Priority-5 messages loop an alarm sound until dismissed |
| Launch at login | Adds `ntfy-bar` to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

### First-run import

If there are no settings yet, ntfy-bar reads the ntfy CLI config at `%APPDATA%\ntfy\client.yml` and
imports `default-host`, `default-user`, `default-password` (or `default-token`) and every `- topic:`
under `subscribe:`. The secret goes straight into Credential Manager.

## Sounds and icons on Windows

Windows is stricter than macOS for apps that aren't installed from the Store (unpackaged apps):

- **Sounds** come from the built-in set only. Custom sound files are ignored by Windows unless the app is
  an MSIX package. The catalog's sound classes map like this:

  | Class | Windows sound |
  |---|---|
  | `silent` | no sound |
  | `default` | Notification.Default |
  | `alert` | Notification.Reminder |
  | `urgent` | Notification.Looping.Alarm2 (played once); the toast stays on screen until dismissed |

  Priority 1–2 messages on catalog topics are silent, as in ntfy-bar. With **Insistent alarm** on, priority-5 messages loop the alarm until dismissed. You can turn sound
  on or off for ntfy-bar in **Settings › System › Notifications › ntfy-bar**, but not pick a different
  sound.
- **Icons**: a toast can only show a local image file, so app icons and image attachments are
  downloaded (https only, icons up to 300 KB) to `%LOCALAPPDATA%\ntfy-bar\icons\` first. If an icon
  can't be downloaded in 5 s, the toast shows without it.
- `broadcast` actions (Android only) are skipped.

## How it works

- **Streaming.** `GET {server}/{topic1,topic2,…}/json`, read line by line. `open`, `keepalive` and
  `message` events are handled. No data for 120 s counts as a dead connection.
- **Resuming.** Reconnects pass `?since=<last message id>`, or its timestamp if it's older than 6 hours.
  The very first connection loads the last hour into the list without notifying.
- **Catalog.** `GET /v1/catalog` on launch, every 15 minutes (with `If-None-Match`), after sleep and
  network changes, on every stream reconnect, and immediately when the server posts `{"event":"sync"}`
  on your account's sync topic (which is streamed with your topics, never shown). Only a 200 response
  changes the topic list. Topics new to this PC get the last 7 days of history first, and that history
  never notifies, even when the stream replays it.
- **Notifications.** Only messages that arrive while the app runs (60 s grace) and haven't been seen
  before notify, so backlog never floods you. Muted and switched-off topics never notify.
- **Credentials** are only sent to your ntfy server, never to third-party icon hosts.

## Files

| Path | Holds |
|---|---|
| `%APPDATA%\ntfy-bar\settings.json` | Server, username, topics, options. Same JSON as ntfy-bar's settings, so it can be copied between the apps |
| `%LOCALAPPDATA%\ntfy-bar\state.json` | The last 2000 messages, read state and the resume position |
| `%LOCALAPPDATA%\ntfy-bar\icons\` | Downloaded icons and images |
| `%LOCALAPPDATA%\ntfy-bar\debug.log` | Connects, disconnects, HTTP errors and message ids. Never credentials or message bodies |
| Credential Manager › Windows Credentials | `ntfy-bar/password`, `ntfy-bar/token` |

## Troubleshooting

- **No notifications.** Check **Settings › System › Notifications** (and Focus / Do not disturb).
  Settings in ntfy-bar shows a warning when Windows has notifications turned off for it.
- **"Authentication failed"** means the server returned 401 or 403. Sign in again.
- **Check the install.** `ntfy-bar-windows.exe --selftest` from a terminal prints a short report and
  shows one test notification.
- **Start over.** Quit, delete `%APPDATA%\ntfy-bar\settings.json`, start again. A settings file the app
  can't read is renamed to `settings.json.unreadable-<time>`, never overwritten.

## Uninstall

Quit from the tray menu, then run `ntfy-bar-windows.exe --uninstall` (removes launch at login, the saved
credentials and the notification registration) and delete the exe. Settings and history stay in the
folders above until you delete them.

## Build from source

Requires the .NET 8 SDK.

```sh
dotnet test tests/NtfyBar.Core.Tests                    # runs on Windows, Linux and macOS
dotnet publish src/NtfyBar.Win -c Release -r win-x64    # → single self-contained exe
```

The Windows project also builds (but doesn't run) on Linux and macOS thanks to
`EnableWindowsTargeting`. CI builds, tests, publishes and smoke-runs the exe on `windows-latest` for every
pull request; pushing a `vX.Y.Z` tag publishes a GitHub Release with the exe and `SHA256SUMS`.

```
src/NtfyBar.Core/     platform-independent: models, JSON, catalog sync + reconcile, stream parser,
                      reconnect/backoff, message store, sound mapping, update check (xUnit-tested)
src/NtfyBar.Win/      WinForms tray app: TrayApp, MessagesForm, SettingsForm, LoginForm, Toasts,
                      AppController (state), CatalogService, IconCache, Credentials, Autostart, Updater
tests/NtfyBar.Core.Tests/
```

## License

MIT © Kudcrafts. See [LICENSE](LICENSE).
