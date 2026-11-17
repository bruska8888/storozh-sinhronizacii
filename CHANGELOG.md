# Changelog

**English** | [Русский](CHANGELOG.ru.md)

## 1.0 — 2026-11-17
- Standalone app "Sync Watchdog" (Сторож синхронизации) replaces the zsh script. The Accessibility permission is granted to this app only, not to every zsh script.
- Catches a client that is running but stuck: the same "Uploading…" / "Connecting…" status for 40 minutes → one restart; stuck again → notification.
- "Last check" file `~/Library/Logs/storozh_sinhronizacii_status.txt`: shows that the watchdog ran and what it saw.
- Signed with an Apple Development certificate, so the permission survives rebuilds.
- Fixed: during a restart the watchdog did not notice that the client had already quit.
- Interface (notifications, app name) in Russian and English.

## 0.2 — 2026-11-16 (zsh script)
- First stuck-sync check based on the menu bar icon text. It caught Dropbox, which had been stuck on "Connecting..." since 17:09.

## 0.1 — 2026-11-13 (zsh script)
- Every 10 minutes, relaunches OneDrive and Dropbox if they quit after a self-update during sleep.
