# Sync Watchdog for Windows — developer notes

**English** | [Русский](#русский)

User guide: the Windows section of the main [README](../README.md#windows-1011).

## Build

```bat
windows\build.cmd
```

Uses the C# compiler that ships with .NET Framework 4.x (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no SDK and no admin rights. The source is one file, `src/SyncWatchdog.cs`, C# 5 syntax (this compiler is old: no `$"..."`, `?.`, `nameof`). Output: `windows\SyncWatchdog.exe` (`/target:winexe`, no console window). For a release, rename it to `SyncWatchdog-<version>.exe`; the version is the `Version` constant in the source.

## Modes

| Run | What happens |
|---|---|
| no arguments, from anywhere but the install folder | setup window **Install / Update** → copy to `%LOCALAPPDATA%\Programs\Sync Watchdog`, scheduled task, Start menu shortcut, *Installed apps* entry (HKCU) |
| no arguments, installed copy | status/settings window |
| `--check` | one check (this is what Task Scheduler runs every 10 minutes) |
| `--status` | diagnostics → `%LOCALAPPDATA%\Sync Watchdog\status.txt` |
| `--dump-tray` | every named tray element with its position → `tray-dump.txt` |
| `--uninstall` | removes the task, shortcut, *Installed apps* entry, data and program folder |

A winexe does not write to the console; use `start "" /wait SyncWatchdog.exe --check` in cmd to wait for it. Environment: `FAKE_OneDrive` / `FAKE_Dropbox` replace the status text (the restart stays real), `STOROZH_VERBOSE=1` writes `verbose.txt`, `STOROZH_LANG=en|ru` overrides the UI language.

Hang test (5 runs → one real Dropbox restart; 4 more → one notification):

```bat
set FAKE_Dropbox=Dropbox Connecting...
for /L %i in (1,1,5) do start "" /wait "%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe" --check
set FAKE_Dropbox=
```

## Findings from testing on Windows 11 (2026-11-17/18)

- Tray buttons are `SystemTray.NormalButton` inside `Shell_TrayWnd`; the tooltip is the UIA `Name`. Icons are matched by `Name` starting with "OneDrive" / "Dropbox".
- The hidden "^" panel does not exist in UIA until opened, so hidden icons cannot be read. Setting `HKCU\Control Panel\NotifyIconSettings\<id>\IsPromoted = 1` brings the icon back onto the taskbar live, without restarting Explorer.
- Dropbox is an Electron tray app: `WM_CLOSE` / `taskkill` without `/F` only hides it. The Restart Manager (`RmShutdown` without the force flag) closes it gracefully in 2–5 s — but only when the **root** process is registered; registering the Chromium helper processes makes it refuse.
- OneDrive tooltips can hold several lines, each starting with "OneDrive - <account>"; they are classified line by line (busy wins).
- The scheduled task XML is written as UTF-16 with BOM and no XML declaration; `schtasks /Create /XML` accepts it.
- A check takes about 1 s; a Dropbox restart about 5 s.

---

## Русский

Инструкция для пользователя — раздел Windows в основном [README.ru.md](../README.ru.md#windows-1011).

**Сборка:** `windows\build.cmd`. Компилятор C# из .NET Framework 4.x, который уже есть в Windows; SDK и права администратора не нужны. Исходник — один файл `src/SyncWatchdog.cs`, синтаксис C# 5. Результат — `windows\SyncWatchdog.exe`; для релиза переименовать в `SyncWatchdog-<версия>.exe`, версия — константа `Version` в исходнике.

**Режимы:** без аргументов не из папки установки — окно установки/обновления; без аргументов из установленной копии — окно состояния; `--check` — одна проверка (так запускает Планировщик); `--status`, `--dump-tray` — диагностика в `%LOCALAPPDATA%\Sync Watchdog\`; `--uninstall` — полное удаление. Переменные: `FAKE_OneDrive` / `FAKE_Dropbox` подменяют статус (перезапуск при этом настоящий), `STOROZH_VERBOSE=1` — подробный `verbose.txt`, `STOROZH_LANG=en|ru` — язык окна.

**Что выяснилось при проверке на Windows 11:**

- Значки трея — кнопки `SystemTray.NormalButton` в `Shell_TrayWnd`, подсказка — свойство `Name` в UI Automation.
- Скрытой панели под «^» в UI Automation нет, пока её не открыли, поэтому спрятанные значки не читаются. `IsPromoted = 1` в `HKCU\Control Panel\NotifyIconSettings\<id>` возвращает значок на панель сразу, без перезапуска Explorer.
- Dropbox на `WM_CLOSE` и `taskkill` без `/F` только прячется. Restart Manager без принуждения закрывает его за 2–5 с, но только если передать **главный** процесс; со служебными процессами Chromium он отказывается.
- Подсказка OneDrive бывает из нескольких строк «OneDrive - <аккаунт> …»; каждая оценивается отдельно, «занят» важнее.
- Проверка занимает около 1 с, перезапуск Dropbox — около 5 с.
