# Sync Watchdog (Сторож синхронизации) for macOS

**English** | [Русский](README.ru.md)

A tiny background app. It makes sure **OneDrive** and **Dropbox** on a Mac are actually syncing files, not just running.

## Why

OneDrive and Dropbox sometimes fail in two ways:

1. **They quit.** This usually happens during a self-update while the Mac is asleep.
2. **They hang while still running.** The app is open, but uploading has stopped. The menu shows "Uploading files (2%)" or "Connecting..." for hours.

A "is the process running?" check only catches the first case. The watchdog catches both.

## How it works

macOS (`launchd`) starts the watchdog every 10 minutes. It runs one check in a fraction of a second and exits. It has no windows and no icons.

| What it sees | What it does |
|---|---|
| OneDrive/Dropbox is not running | Launches it in the background |
| Status "Up to date" (Выполнено… / Обновлено) | Nothing |
| "Uploading…", "Connecting…", "Syncing…", and the text keeps changing | Nothing, work is in progress |
| The same "Uploading…" / "Connecting…" text for **4 checks in a row (~40 min)** | Checks the internet connection, then restarts **only that client, once**, with a normal quit |
| Stuck the same way again after the restart | Shows a notification and writes to the log. Leaves the client alone until its status changes |
| Paused, offline, or sign-in required | Nothing |

The status comes from the text of the menu bar icon. It is the same tooltip you see when you hover the mouse over it. The watchdog reads it through the Accessibility API, so it needs the Accessibility permission.

The load is close to zero: two short queries to the icons every 10 minutes. It never scans files and makes no network requests, except one internet check before a restart.

## Files on the Mac

| What | Where |
|---|---|
| App | `~/Applications/Сторож синхронизации.app` |
| Schedule (every 10 min) | `~/Library/LaunchAgents/com.anton.storozh-sinhronizacii.plist` |
| **Last check** (overwritten on every run) | `~/Library/Logs/storozh_sinhronizacii_status.txt` |
| **Action log** (written only when it does something) | `~/Library/Logs/storozh_sinhronizacii.log` |
| State between runs | `~/Library/Application Support/storozh-sinhronizacii/*.json` |

## Installation (new Mac or reinstall)

You need Apple Command Line Tools: `xcode-select --install`. Full Xcode is not required.

```bash
cd path/to/storozh-sinhronizacii
./install.sh
```

The script:

1. builds the app from `Sources/main.swift`;
2. puts it into `~/Applications`;
3. signs it with an "Apple Development" certificate if one is in the keychain, otherwise with an ad-hoc signature;
4. installs the launchd job and runs it right away;
5. shows the system permission prompt.

Then go to **System Settings → Privacy & Security → Accessibility → turn on "Сторож синхронизации"**.

To check that everything works:

```bash
cat ~/Library/Logs/storozh_sinhronizacii_status.txt
```

A line with "НЕТ ДОСТУПА" (no access) means the permission has not been granted.

### Rebuilding after changes

```bash
./install.sh --no-ask
```

If the app is signed with an Apple Development certificate, the permission is kept. With an ad-hoc signature, macOS resets the permission after every rebuild. Remove the entry with "−" in Accessibility, run `./install.sh`, and turn the switch on again. Sometimes the switch is on but access still doesn't work. The same fix applies: remove it with "−" and add it again.

## Testing without a real hang

The `FAKE_<client>` variable overrides the status:

```bash
B=~/Applications/Сторож\ синхронизации.app/Contents/MacOS/storozh
for i in 1 2 3 4 5; do FAKE_Dropbox="Dropbox Соединение..." STOROZH_VERBOSE=1 "$B"; done
```

On the 5th run Dropbox really restarts. Four more runs give the "stuck again" notification. A normal run without `FAKE_` resets the episode. The "last check" file is not overwritten during such a test.

## Turn off / uninstall

```bash
launchctl bootout gui/$(id -u)/com.anton.storozh-sinhronizacii
```

Then delete the app, the plist, and the Accessibility entry.

## Settings in the code

In `Sources/main.swift`:

- `APPS` — which clients to check;
- `POROG` — how many checks without change count as a hang;
- `classify()` — which status words mean "busy", "paused" and "fine" (Russian and English).

Log and "last check" messages are written in Russian. Notifications and the app name follow the system language (Russian or English).
