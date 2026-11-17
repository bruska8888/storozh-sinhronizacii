# Changelog

**English** | [Русский](CHANGELOG.ru.md)

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
