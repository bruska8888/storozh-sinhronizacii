# Sync Watchdog for macOS and Windows

**English** | [Русский](README.ru.md)

**Sync Watchdog keeps OneDrive and Dropbox working on your Mac or Windows PC after sleep, closing the lid, or hibernation.**

**Mac:** [Installation](#installation-on-mac) · **Windows 10/11:** [Installation](#windows-1011)

After the computer wakes up, OneDrive and Dropbox often quietly stop syncing. Sometimes the app quits during a self-update while the Mac is asleep. Sometimes it is still open, but uploading is stuck for hours on "Uploading files (2%)" or "Connecting...". You only find out when a file is missing on another computer.

Sync Watchdog checks both apps every 10 minutes in the background. It is fine to have only one of them installed. If an app has quit, it starts it again. If an app is stuck, it restarts it once. If that doesn't help, it shows you a notification.

---

## Installation on Mac

1. Download **`SyncWatchdog-<version>.dmg`** from the [Releases](../../releases/latest) page.
2. Open the `.dmg` and drag **Sync Watchdog** into the **Applications** folder.
3. Open **Sync Watchdog** from Applications.

### The first launch: Apple's warning

The app is not signed with a paid Apple Developer ID, so on the first launch macOS will show *"Sync Watchdog" Not Opened* or *Apple could not verify…*. This is expected. To allow it once:

1. Click **Done** (or **OK**) in that message.
2. Open **System Settings → Privacy & Security**.
3. Scroll down to the **Security** section. Next to *"Sync Watchdog" was blocked…*, click **Open Anyway**.
4. Confirm with your password or Touch ID, then click **Open Anyway** again.

macOS asks this only once. After that the app opens normally.

### Two switches

On first launch Sync Watchdog installs itself to run in the background and asks for two permissions:

- **Accessibility** (*System Settings → Privacy & Security → Accessibility*). Turn on **Sync Watchdog**. This lets it read the status text of the OneDrive and Dropbox menu bar icons. Without it, the watchdog can still restart apps that quit, but it cannot see when they hang.
- **Allow in the Background** (*System Settings → General → Login Items*). Usually it is on by default and macOS just shows a notification. If Sync Watchdog is switched off there, turn it on.

That's it. The watchdog starts by itself after every restart of the Mac.

## Everyday use

Sync Watchdog has no windows and no menu bar icon. To see what it is doing, open it from Applications. A window shows:

- whether it is running;
- the result of the last check, for example *OneDrive — OK*;
- **Watch OneDrive** and **Watch Dropbox** checkboxes. If you keep one of them closed on purpose, uncheck it and the watchdog will leave it alone. If only one of the apps is installed, the other one is simply skipped;
- **Open log**: the history of everything it has done (launches, restarts, warnings);
- **Uninstall**: turns the watchdog off. After that, drag the app to the Trash.

## Updating

Download the new `.dmg`, drag the app into Applications, choose **Replace**, and open it once. You don't need to remove the old version first. If macOS asks for Accessibility again, turn the switch back on.

---

## Windows 10/11

### Installation

1. Download **`SyncWatchdog-<version>.exe`** from the [Releases](../../releases/latest) page.
2. Run it. The program is not signed with a paid certificate, so on the first run Windows may show *"Windows protected your PC"*. Click **More info**, then **Run anyway**. Windows asks this only once.
3. Click **Install**. No administrator rights are needed.

The watchdog copies itself to `%LOCALAPPDATA%\Programs\Sync Watchdog`, starts working in the background right away, and appears in the **Start menu** as *Sync Watchdog* and in **Settings → Apps → Installed apps**. The downloaded file is no longer needed and can be deleted.

### Everyday use

There is no tray icon. Windows Task Scheduler starts the watchdog every 10 minutes; one check takes about a second. To see what it is doing, open **Sync Watchdog** from the Start menu:

- the result of the last check, for example *• OneDrive — OK*;
- **Monitor OneDrive** and **Monitor Dropbox** checkboxes, the same as on the Mac;
- **Always show the OneDrive and Dropbox icons on the taskbar** (on by default). Windows hides tray icons under the **^** arrow, and the watchdog can read a status only from a visible icon. With this option on, it moves the two icons back onto the taskbar by itself. Without it, an app that quit is still relaunched, but a hang is not noticed;
- **Open log** and **Uninstall watchdog**.

### Updating and uninstalling

- **Automatic updates** (from 1.5): once a day the watchdog asks this repository's [Releases](../../releases/latest) for a newer version. With **Update automatically** on (the default) it installs it by itself and shows a notification; with it off, it only tells you, and you click **Update to …** in the window. Every update is checked against a digital signature built into the program, so a file that was not published by the author is never installed. The window also has **Check for updates**.
- **Manual update:** run the new `.exe` and click **Update**. Settings and the log are kept.
- **Uninstall:** *Settings → Apps → Installed apps → Sync Watchdog → Uninstall*, or the button in the window. This removes everything: the scheduled task, the Start menu shortcut, the program, the log and the settings.

### How it works on Windows

The rules are the same as on the Mac (see the table below): a client that quit is launched; the same "busy" status for 4 checks in a row means one restart; stuck again means a notification.

- The status is the tooltip of the tray icon, read through Windows UI Automation. The hidden "^" panel is never opened.
- Restart is always graceful, never forced: OneDrive gets `/shutdown`; Dropbox is asked to quit through the Windows Restart Manager, the way Windows Update closes apps. If an app refuses to quit, it is left as is.
- Time while the PC sleeps is not counted: the watchdog counts checks, not minutes, and missed runs are not replayed after waking up.
- Windows, the log and notifications follow the system language: Russian on a Russian Windows, English otherwise.

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe` |
| Last check (overwritten every run) | `%LOCALAPPDATA%\Sync Watchdog\last-check.txt` |
| Log of actions | `%LOCALAPPDATA%\Sync Watchdog\journal.log` |
| Settings and state | `%LOCALAPPDATA%\Sync Watchdog\settings.json`, `OneDrive.json`, `Dropbox.json` |
| Schedule | Task Scheduler → *Sync Watchdog* |

Requirements: Windows 10 or 11, 64-bit (.NET Framework 4.x is built into Windows). Tested on Windows 11.

---

## How it works on the Mac (details)

macOS (`launchd`) starts the watchdog every 10 minutes. It runs one check in a fraction of a second and exits.

| What it sees | What it does |
|---|---|
| OneDrive/Dropbox is not running | Launches it in the background |
| Status "Up to date" | Nothing |
| "Uploading…", "Connecting…", "Syncing…", and the text keeps changing | Nothing, work is in progress |
| The same "Uploading…" / "Connecting…" text for **4 checks in a row (~40 min)** | Checks the internet connection, then restarts **only that app, once**, with a normal quit |
| Stuck the same way again after the restart | Shows a notification and writes to the log. Leaves the app alone until its status changes |
| Paused, offline, or sign-in required | Nothing |

Time while the Mac is asleep is not counted: 40 minutes means 40 minutes awake.

The status is the text of the menu bar icon, the same tooltip you see when you hover over it. The watchdog reads it through the macOS Accessibility API. The load is close to zero: two short queries every 10 minutes, no file scanning, and no network requests except one internet check before a restart.

Messages, the log, and the app name follow the system language (English or Russian).

### Files

Everything is stored on your Mac. Nothing is sent anywhere, and no cloud or account is needed.

| What | Where |
|---|---|
| App | `/Applications/Sync Watchdog.app` |
| Last check (overwritten every run) | `~/Library/Logs/Sync Watchdog/last-check.txt` |
| Log of actions (written only when it does something) | `~/Library/Logs/Sync Watchdog/journal.log` |
| Settings and state between runs | `~/Library/Application Support/Sync Watchdog/` |

The log can also be opened in the **Console** app: *Log Reports → Sync Watchdog*.

### Requirements

macOS 13 Ventura or newer. Apple Silicon and Intel (the Intel build is tested under Rosetta, not yet on a real Intel Mac).

---

## For developers

Build from source (needs Apple Command Line Tools, `xcode-select --install`; full Xcode is not required):

```bash
./build.sh      # → build/Sync Watchdog.app and dist/SyncWatchdog-<version>.dmg
./install.sh    # build, copy to /Applications and open
```

`build.sh` signs the app with a self-signed certificate named "Sync Watchdog" if the keychain has one, so the Accessibility permission survives updates. Otherwise it uses an ad-hoc signature, and the permission has to be granted again after every rebuild. Do not sign with a personal "Apple Development" certificate: it contains the owner's email address. `build.sh` stops if it finds an email address in the signature. If the switch is on but access does not work, remove the app from the Accessibility list with "−" and open the app again.

The schedule is a regular LaunchAgent (`~/Library/LaunchAgents/com.anton.storozh-sinhronizacii.plist`) that the app writes itself when opened. Apple's newer `SMAppService` API was tried in 1.1–1.2, but launchd starts such agents only for apps signed by Apple-issued certificates.

Diagnostics:

```bash
"/Applications/Sync Watchdog.app/Contents/MacOS/SyncWatchdog" --status
```

Test without a real hang (`FAKE_<app>` overrides the status):

```bash
B="/Applications/Sync Watchdog.app/Contents/MacOS/SyncWatchdog"
for i in 1 2 3 4 5; do FAKE_Dropbox="Dropbox Connecting..." STOROZH_VERBOSE=1 "$B"; done
```

On the 5th run Dropbox really restarts. Four more runs give the "stuck again" notification. A normal run resets the episode.

Windows version: one C# file built with the compiler that ships with Windows (no SDK, no admin rights). See [`windows/README.md`](windows/README.md):

```bat
windows\build.cmd
```

Settings in `Sources/main.swift`: `APPS` (which apps to check), `POROG` (how many checks without change count as a hang), `classify()` (which status words mean busy, paused, or OK, in Russian and English).
