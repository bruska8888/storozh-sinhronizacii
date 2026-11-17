# Sync Watchdog — Windows draft / черновик

## Русский

Требуется Windows 10/11 x64 с .NET Framework 4.x. Запустите `build.cmd` обычным пользователем; SDK и права администратора не нужны. Результат — `windows/SyncWatchdog.exe`. Компилятор вызывается с `/langversion:5` и `/target:winexe`; зависимости берутся из Framework64, включая сборки WPF/UI Automation.

Дважды щёлкните EXE. Он копирует себя в `%LOCALAPPDATA%\Programs\Sync Watchdog`, запускает копию и создаёт/обновляет задачу `Sync Watchdog`. При предупреждении SmartScreen «Windows защитила ваш компьютер» выберите «Подробнее» → «Выполнить в любом случае», если доверяете собранному файлу. Приложение не подписано.

Задача выполняется раз в 10 минут только в интерактивном сеансе текущего пользователя, без повышения прав. Первый запуск — примерно через минуту. XML задаёт `StartWhenAvailable=false`, `MultipleInstancesPolicy=IgnoreNew`, `ExecutionTimeLimit=PT2M`, разрешает работу от батареи и не будит ПК. Повторное открытие окна пересоздаёт задачу. Удаление убирает задачу; папку программы затем удаляют вручную. Журнал и настройки сохраняются; уже начатая проверка может завершиться после удаления задачи.

Режимы (для ожидания завершения winexe в cmd используйте `start "" /wait`):

```bat
start "" /wait "%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe" --check
start "" /wait "%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe" --status
start "" /wait "%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe" --dump-tray
```

Данные — `%LOCALAPPDATA%\Sync Watchdog`:

- `last-check.txt`: итог каждой проверки, включая тестовую (пометка `[ТЕСТ FAKE]`).
- `journal.log`: действия, ошибки, первое отсутствие значка за эпизод.
- `settings.json`: `OneDrive`, `Dropbox`, `AlwaysShowIcons`.
- `OneDrive.json`, `Dropbox.json`: `prev`, `cnt`, `doneRestart`, `warned`.
- `status.txt`: результат `--status`, пути, клиенты, настройки, XML задачи или ошибка `schtasks`.
- `tray-dump.txt`: результат `--dump-tray`, все именованные потомки доступных поверхностей трея, включая тип и класс. Дамп шире списка кнопок, потому что Explorer может предоставлять значок как Custom/MenuItem.
- `verbose.txt`: подробности при `STOROZH_VERBOSE=1`.

Галочка показа значков меняет только существующие записи OneDrive/Dropbox в `HKCU\Control Panel\NotifyIconSettings`, устанавливая DWORD `IsPromoted=1`. Она не скрывает значки обратно после отключения. Записи повторно проверяются при каждом `--check`, чтобы подхватить появившиеся значки. Панель переполнения никогда не открывается, Explorer не перезапускается.

Счётчик соответствует Swift: первый занятый текст даёт `cnt=0`, следующие четыре одинаковых наблюдения — `cnt=4`, то есть первый перезапуск на пятом запуске проверки. После попытки перезапуска счётчик обнуляется, ещё четыре одинаковых наблюдения дают одно предупреждение. Смена текста сбрасывает эпизод. Неизвестные тексты считаются нормальными, пауза/ошибка/выход из аккаунта не вызывают действий. При отсутствии значка перезапуска по зависанию нет. Несколько аккаунтов OneDrive объединяются в стабильном порядке, причём занятые аккаунты имеют приоритет над неактивными.

Защитное отличие от Swift: `doneRestart` записывается **до** попытки штатного закрытия. Даже неудачная попытка считается использованной; повторного цикла закрытия зависшего клиента не будет. При повреждении JSON соответствующая проверка завершается ошибкой, а ограничения перезапуска и отключённые настройки не сбрасываются молча. Перед перезапуском проверяется ожидаемое содержимое Microsoft Connect Test, затем Apple captive portal, без принятия редиректов за доступ в интернет. Процессы другого сеанса не трогаются. Dropbox закрывается через `CloseMainWindow` и, если окна нет, `taskkill /PID` **без `/F`**; OneDrive — `/shutdown`. Ожидание завершения — до 60 с, после него пауза 3 с. Бюджет общей проверки может сократить ожидание второго клиента или отложить его действие до следующей проверки.

Тест использует установленные и работающие клиенты с включёнными галочками; FAKE подменяет только текст, **реальный перезапуск остаётся включённым**:

```bat
set FAKE_Dropbox=Dropbox Connecting...
set STOROZH_VERBOSE=1
for /L %%i in (1,1,5) do start "" /wait "%LOCALAPPDATA%\Programs\Sync Watchdog\SyncWatchdog.exe" --check
rem Ещё 4 проверки — одно уведомление. В интерактивном cmd вместо %%i используйте %i.
set FAKE_Dropbox=
set STOROZH_VERBOSE=
```

Перед повторением сценария используйте другой текст FAKE для нового эпизода. Не запускайте тест одновременно с обычными проверками. Для автоматизации регрессии полезно в дальнейшем добавить отдельный тестовый драйвер с подменой запуска/закрытия процессов.

### Что проверить на живом Windows 11

1. Сборку встроенным csc, установку без админа и XML через `schtasks /Query /TN "Sync Watchdog" /XML`.
2. Реальные Name видимых значков и наличие полных подсказок: обновлено, отправка, пауза, нет сети; сравнить `tray-dump.txt` и `last-check.txt`. UIA-провайдер может не отдавать скрытые значки; дамп их не открывает.
3. Применение `IsPromoted` без перезапуска Explorer: сразу или после перезапуска клиента. Это внутреннее поведение Explorer, зависящее от сборки Windows.
4. Отсутствие вспышек панели переполнения; на Win10 — доступность скрытого `NotifyIconOverflowWindow` без открытия.
5. Пять одинаковых FAKE-проверок → одна попытка перезапуска; ещё четыре → одно уведомление. При отказе штатного закрытия клиент остаётся нетронутым.
6. Отключённый клиент не запускается и не перезапускается; отсутствующий клиент недоступен в окне. Закрытый включённый клиент запускается.
7. Сон и пробуждение без пачки пропущенных запусков, работа от батареи, среднее время обычной проверки и CPU.
8. Уведомления Windows при включённом/выключенном режиме «Не беспокоить», масштабирование окна 125–200%, русскую и английскую локали.

На Mac выполнена только статическая проверка исходника. Сборка, UI Automation, реестр и задача ещё не проверены на Windows. Публикация Release, изменение корневых README/CHANGELOG и паспорта компьютера не выполнялись: этот черновик ограничен папкой `windows/`.

## English

Windows 10/11 x64, .NET Framework 4.x. Run `build.cmd` without elevation; no SDK is needed. Double-click the resulting EXE to copy it into `%LOCALAPPDATA%\Programs\Sync Watchdog`, register an interactive, least-privilege task every ten minutes, and open settings/status. SmartScreen may show “Windows protected your PC”: choose “More info” → “Run anyway” if you trust the binary you built. The executable is unsigned.

Use `--check`, `--status`, or `--dump-tray`. With this windowless executable, diagnostics are written to `%LOCALAPPDATA%\Sync Watchdog\status.txt` and `tray-dump.txt`; checks write `last-check.txt`, including marked FAKE runs. `STOROZH_VERBOSE=1` enables `verbose.txt`. `FAKE_OneDrive` / `FAKE_Dropbox` replace status text only: enabled, installed clients can still be restarted. Use `start "" /wait` to wait for completion in cmd.

Settings enable each installed client independently. The icon visibility option promotes existing Explorer registry entries; unchecking does not hide them again. Overflow is never opened automatically. Uninstall deletes the task and asks you to remove the program directory manually; existing logs/settings remain.

Counters match Swift: the first busy sample establishes the baseline; four further unchanged samples trigger one restart attempt, then four more trigger one warning. The restart-attempt flag is persisted before shutdown to avoid loops after interruption or failed shutdown. Process shutdown is graceful, never forced. Unknown status text is left alone. A missing tray icon never triggers a hang restart. Multiple OneDrive accounts are considered in stable order, prioritizing busy accounts.

This draft has only been statically checked on macOS. Validate the actual Framework compiler, task registration without elevation, Explorer UIA names in each client state, immediate registry promotion, Win10 hidden-tray access, Win11 notification delivery, graceful client shutdown, FAKE counters, sleep/resume, DPI/localization, and normal-run performance on a real Windows PC. Automated testing should eventually use an isolated driver that substitutes process actions. No release or files outside `windows/` were changed.
