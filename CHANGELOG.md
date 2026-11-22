# Changelog

**English** | [Русский](CHANGELOG.ru.md)

## 1.8 (Mac) / 1.6.0 (Windows) — 2026-11-19
- If OneDrive or Dropbox shows a status the watchdog does not recognize (for example after a client update), it now sends one notification instead of silently ignoring it.
- If the client's icon is hidden for ~30 minutes, one notification explains that hangs cannot be detected (relaunching a closed client still works).
- Before restarting a client whose status has not changed for ~40 minutes, the watchdog checks whether it is still transferring data (a large file on a slow connection). If it is, the restart is postponed, for up to an hour.
- Windows 1.6.0 (2026-11-22): a pop-up notification vanishes after ~10 seconds, so warnings now also open a small window in the bottom-right corner that stays until you close it. New warnings are added to the same window; it does not take focus from what you are typing.

## 1.7.2 — 2026-11-19
- Mac: the app now has its own icon (same as on Windows).
- Minor build and install improvements.

## 1.7.1 — 2026-11-18
- Mac: minor fixes.

## 1.7 — 2026-11-18
- Mac: **auto-update**, like on Windows. Once a day the watchdog checks GitHub releases (`SyncWatchdog-mac-<version>.zip` + `.zip.sig`), verifies the RSA-SHA256 signature with the same key as the Windows version, then the app code signature (signer “Sync Watchdog”) and version, and replaces itself in Applications with a safety copy. A tampered or unsigned archive is never installed.
- **“Update automatically”** checkbox (on) and a **“Check for updates”** button; with the box off — one notification per new version.
- Accessibility permission survives updates (same signature).
- Releases are signed with `sign-release-mac.py` (key from Bitwarden, never left on disk).
- Every release carries both platforms’ files: the Windows watchdog only looks at the latest release.

## 1.6 — 2026-11-18
- Mac: an “Error…” / “sign in required” status is no longer silent. If it lasts ~30 minutes (3 checks), the watchdog sends one macOS notification and logs it. No restart — it does not fix such errors. Paused and offline stay silent.

## 1.5.2 — 2026-11-18
- Windows: no stale "Updated to version …" line in the window after an update; update events go to the log.

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
