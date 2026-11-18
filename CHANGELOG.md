# Changelog

**English** | [Русский](CHANGELOG.ru.md)

## 1.5.1 — 2026-11-18
- Windows: notifications are titled "Sync Watchdog" instead of the file name (it showed "SyncWatchdog.exe.old" right after an update). The exe now has proper file properties (name, version).
- Auto-update from 1.4.9 → 1.5 tested on a real PC: download, signature check, replacement, notification.

## 1.5 — 2026-11-18
- Windows: **automatic updates** from GitHub Releases, once a day. **Update automatically** checkbox (on by default); when off, a notification and an **Update to …** button. **Check for updates** button in the window.
- Windows: every update must carry a valid RSA-SHA256 signature (`.sig` next to the `.exe`); unsigned or altered files are rejected.
- Windows: app icon (file, Start menu, Installed apps, window, notifications).
- The repository is public under the MIT license.

## 1.4 — 2026-11-18
- **Windows 10/11 version** (`SyncWatchdog-1.4.exe`). Same rules as on the Mac. Setup window with **Install / Update**, no administrator rights; appears in the Start menu and in Settings → Apps → Installed apps, where it can be uninstalled completely.
- Windows: reads the tray icon tooltip through UI Automation; the **Always show the OneDrive and Dropbox icons on the taskbar** option moves icons back from under the "^" arrow, so hangs stay visible.
- Windows: graceful restart only — OneDrive `/shutdown`, Dropbox through the Windows Restart Manager.
- Windows: status words found on a real PC: OneDrive "Looking for changes", "Downloading files", "Processing changes"; Dropbox "Updating files". OneDrive tooltips with several lines are judged line by line.
- The Mac app is unchanged (still 1.3 inside the `.dmg`).

## 1.3 — 2026-11-17
- The app is signed with a neutral "Sync Watchdog" certificate. No personal data in the signature.
- The schedule is a regular LaunchAgent written by the app, instead of `SMAppService`, which launchd refuses to start without an Apple-issued signature. Now the watchdog really runs on any Mac.
- After updating from 1.1–1.2, open the app once and turn Accessibility on again (the signature has changed).

## 1.2 — 2026-11-17
- **Watch OneDrive / Watch Dropbox** checkboxes in the app window. Uncheck an app you keep closed on purpose, and the watchdog will not launch it. Apps that are not installed are skipped automatically.
- Intel Macs: tested under Rosetta (status reading, simulated hang, restart).

## 1.1 — 2026-11-17
- One-file installation: download the `.dmg`, drag the app into Applications, open it. The app schedules itself and asks for permissions. No Terminal needed.
- Starts automatically after a restart (listed in Login Items → Allow in the Background).
- Opening the app shows whether it is running and the last check, with **Open log** and **Uninstall** buttons.
- New file name `Sync Watchdog.app`, shown as "Сторож синхронизации" on Russian systems.
- Log and status messages in English or Russian, following the system language. Files moved to `~/Library/Logs/Sync Watchdog/`; old files are moved over automatically.
- Updating over an older version keeps working: the app re-registers itself every time it is opened.
- Universal build (Apple Silicon + Intel), macOS 13 or newer.

## 1.0 — 2026-11-17
- Standalone app "Sync Watchdog" (Сторож синхронизации) replaces the zsh script. The Accessibility permission is granted to this app only, not to every zsh script.
- Catches a client that is running but stuck: the same "Uploading…" / "Connecting…" status for 40 minutes → one restart; stuck again → notification.
- "Last check" file `last-check.txt`: shows that the watchdog ran and what it saw.
- Signed with an Apple Development certificate, so the permission survives rebuilds.
- Fixed: during a restart the watchdog did not notice that the client had already quit.
- Interface (notifications, app name) in Russian and English.

## 0.2 — 2026-11-16 (zsh script)
- First stuck-sync check based on the menu bar icon text. It caught Dropbox, which had been stuck on "Connecting..." since 17:09.

## 0.1 — 2026-11-13 (zsh script)
- Every 10 minutes, relaunches OneDrive and Dropbox if they quit after a self-update during sleep.
