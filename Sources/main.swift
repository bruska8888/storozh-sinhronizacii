// Сторож синхронизации / Sync Watchdog
// Запускается launchd раз в 10 минут, делает одну проверку и выходит.
// 1) OneDrive/Dropbox закрылся — запускает его в фоне.
// 5) Незнакомый текст значка или значка нет ~30 мин — одно уведомление; перед перезапуском — замер сети (1.8).
// 4) Раз в сутки — новая версия из GitHub Releases (SyncWatchdog-mac-X.zip + .sig), подпись RSA проверяется (с 1.7).
// 3) Статус «Ошибка…»/«нужен вход» держится ~30 мин — одно уведомление (с 1.6), без перезапуска.
// 2) Клиент жив, но статус значка в строке меню («Отправка файлов (2%)», «Соединение...»)
//    не меняется POROG проверок подряд — один штатный перезапуск.
//    Снова завис — уведомление, больше не трогает, пока статус не сменится.
// Нужен доступ «Универсальный доступ» для этого приложения (читает текст значков).
//
// Режимы:
//   двойной щелчок (Finder)       — установщик: ставит себя на расписание, просит доступ,
//                                   повторно — показывает состояние и даёт удалить;
//   STOROZH_MODE=check / --check  — одна проверка (так запускает launchd);
//   --ask                         — только запрос доступа.

import AppKit
import ApplicationServices
import ServiceManagement
import Foundation

let APPS = ["OneDrive", "Dropbox"]
let POROG = 4  // проверок по 10 мин без изменений = зависание
let home = FileManager.default.homeDirectoryForCurrentUser
let logDir = home.appendingPathComponent("Library/Logs/Sync Watchdog")
let logURL = logDir.appendingPathComponent("journal.log")
let stateDir = home.appendingPathComponent("Library/Application Support/Sync Watchdog")
let env = ProcessInfo.processInfo.environment
let statusURL = logDir.appendingPathComponent("last-check.txt")

// Переезд со старых имён (версии до 1.1) / migrate files from versions before 1.1
func migrateOldFiles() {
    let fm = FileManager.default
    try? fm.createDirectory(at: logDir, withIntermediateDirectories: true)
    let oldLog = home.appendingPathComponent("Library/Logs/storozh_sinhronizacii.log")
    if fm.fileExists(atPath: oldLog.path) && !fm.fileExists(atPath: logURL.path) { try? fm.moveItem(at: oldLog, to: logURL) }
    try? fm.removeItem(at: home.appendingPathComponent("Library/Logs/storozh_sinhronizacii_status.txt"))
    let oldState = home.appendingPathComponent("Library/Application Support/storozh-sinhronizacii")
    if fm.fileExists(atPath: oldState.path) && !fm.fileExists(atPath: stateDir.path) { try? fm.moveItem(at: oldState, to: stateDir) }
}
var report: [String] = []   // «последняя проверка» — перезаписывается каждый запуск

// MARK: - Строки (RU/EN)

let isRU = (Locale.preferredLanguages.first ?? "ru").hasPrefix("ru")
func L(_ ru: String, _ en: String) -> String { isRU ? ru : en }

// MARK: - Журнал

func log(_ msg: String) {
    let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm"
    let line = "\(f.string(from: Date())) — \(msg)\n"
    try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
    if let h = try? FileHandle(forWritingTo: logURL) {
        h.seekToEndOfFile(); h.write(line.data(using: .utf8)!); try? h.close()
    } else {
        try? line.write(to: logURL, atomically: true, encoding: .utf8)
    }
    if env["STOROZH_VERBOSE"] != nil { print(line, terminator: "") }
}

// MARK: - Состояние

struct State: Codable {
    var prev = ""          // прошлый текст статуса
    var cnt = 0            // сколько проверок подряд без изменений при «занят»
    var doneRestart = false
    var warned = false
    var netWaits: Int? = 0 // сколько раз отложил перезапуск: программа передаёт данные (1.8)
}

func stateURL(_ app: String) -> URL { stateDir.appendingPathComponent("\(app).json") }

// Настройки: за какими программами следить / settings: which apps to watch
let watchURL = stateDir.appendingPathComponent("settings.json")
func isInstalled(_ app: String) -> Bool {
    FileManager.default.fileExists(atPath: "/Applications/\(app).app")
        || FileManager.default.fileExists(atPath: home.appendingPathComponent("Applications/\(app).app").path)
}
func loadWatch() -> [String: Bool] {
    guard let d = try? Data(contentsOf: watchURL), let w = try? JSONDecoder().decode([String: Bool].self, from: d) else { return [:] }
    return w
}
func saveWatch(_ w: [String: Bool]) {
    try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
    if let d = try? JSONEncoder().encode(w) { try? d.write(to: watchURL) }
}
func isWatched(_ app: String) -> Bool { loadWatch()[app] ?? true }   // по умолчанию — следить
func loadState(_ app: String) -> State {
    guard let d = try? Data(contentsOf: stateURL(app)),
          let s = try? JSONDecoder().decode(State.self, from: d) else { return State() }
    return s
}
func saveState(_ app: String, _ s: State) {
    try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
    if let d = try? JSONEncoder().encode(s) { try? d.write(to: stateURL(app)) }
}

// MARK: - Процессы

func running(_ app: String) -> NSRunningApplication? {
    NSWorkspace.shared.runningApplications.first {
        $0.executableURL?.lastPathComponent == app && $0.bundleURL?.lastPathComponent == "\(app).app"
    }
}

@discardableResult
func run(_ path: String, _ args: [String]) -> Int32 {
    let p = Process(); p.executableURL = URL(fileURLWithPath: path); p.arguments = args
    p.standardOutput = FileHandle.nullDevice; p.standardError = FileHandle.nullDevice
    do { try p.run() } catch { return -1 }
    p.waitUntilExit(); return p.terminationStatus
}

func launch(_ app: String) { run("/usr/bin/open", ["-g", "-a", app]) }

func restart(_ app: String) -> Bool {
    guard let r = running(app) else { launch(app); return true }
    // Список NSWorkspace без главного цикла не обновляется — ждём по самому процессу.
    let pid = r.processIdentifier
    let alive = { kill(pid, 0) == 0 }
    r.terminate()
    for _ in 0..<60 {
        if !alive() { break }
        Thread.sleep(forTimeInterval: 1)
    }
    if alive() {
        log(L("\(app): завис, штатно не закрылся — оставил как есть", "\(app): hung and did not quit normally — left as is"))
        return false
    }
    Thread.sleep(forTimeInterval: 3)
    launch(app)
    return true
}

func internetOK() -> Bool {
    run("/usr/bin/curl", ["-s", "-o", "/dev/null", "-m", "10", "https://captive.apple.com"]) == 0
}

// MARK: - Сеть по процессу (1.8)
// Второе подтверждение зависания: передаёт ли программа данные прямо сейчас.
// nettop -L 1 — мгновенный снимок байтов по живым соединениям (0,02 с, без разрешений);
// счётчики не накопительные (закрытое соединение пропадает), поэтому два снимка с паузой.
// Second confirmation of a hang: is the app moving data right now (two cheap nettop snapshots).

func capture(_ path: String, _ args: [String]) -> String? {
    let p = Process(); p.executableURL = URL(fileURLWithPath: path); p.arguments = args
    let pipe = Pipe(); p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
    do { try p.run() } catch { return nil }
    let data = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    return p.terminationStatus == 0 ? String(data: data, encoding: .utf8) : nil
}

// Байты по живым соединениям всех процессов программы («OneDrive», «OneDrive File P…»): pid → байты
func appTraffic(_ app: String) -> [String: Int64]? {
    guard let out = capture("/usr/bin/nettop", ["-P", "-L", "1", "-n", "-x", "-J", "bytes_in,bytes_out"]) else { return nil }
    var res: [String: Int64] = [:]
    for line in out.split(separator: "\n") {
        let f = line.split(separator: ",", omittingEmptySubsequences: false)
        guard f.count >= 3, f[0].hasPrefix(app), let i = Int64(f[1]), let o = Int64(f[2]) else { continue }
        res[String(f[0])] = i + o
    }
    return res
}

let NET_PAUSE: TimeInterval = 30      // пауза между снимками, с
let NET_MIN: Int64 = 128 * 1024       // столько байт за паузу = «передаёт»
let NET_MAX_WAITS = 6                 // откладывать перезапуск не дольше ~1 ч / postpone at most ~1 h

// true — передаёт, false — стоит, nil — не удалось узнать
func activeTransfer(_ app: String) -> Bool? {
    if let fake = env["FAKE_NET_\(app)"] { return fake == "1" }
    guard let a = appTraffic(app) else { return nil }
    Thread.sleep(forTimeInterval: NET_PAUSE)
    guard let b = appTraffic(app) else { return nil }
    var moved: Int64 = 0
    for (k, v) in b { moved += max(0, v - (a[k] ?? 0)) }
    if env["STOROZH_VERBOSE"] != nil { print("\(app): за \(Int(NET_PAUSE)) с передано \(moved) байт (\(a.keys.sorted()))") }
    return moved >= NET_MIN
}

func notify(_ title: String, _ text: String) {
    if env["STOROZH_TEST"] != nil { print("NOTIFY: \(title) | \(text)"); return }   // проверка без всплывающих окон
    let esc = { (s: String) in s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") }
    run("/usr/bin/osascript", ["-e", "display notification \"\(esc(text))\" with title \"\(esc(title))\""])
}

// MARK: - Текст значка в строке меню (Accessibility)

enum StatusResult { case text(String), noAccess, notFound }

func menuBarStatus(_ app: NSRunningApplication) -> StatusResult {
    guard AXIsProcessTrusted() else { return .noAccess }
    let el = AXUIElementCreateApplication(app.processIdentifier)
    var bar: CFTypeRef?
    guard AXUIElementCopyAttributeValue(el, "AXExtrasMenuBar" as CFString, &bar) == .success,
          let barEl = bar else { return .notFound }
    var kids: CFTypeRef?
    guard AXUIElementCopyAttributeValue(barEl as! AXUIElement, kAXChildrenAttribute as CFString, &kids) == .success,
          let arr = kids as? [AXUIElement], let item = arr.first else { return .notFound }
    for attr in [kAXHelpAttribute, kAXTitleAttribute, kAXDescriptionAttribute] {
        var v: CFTypeRef?
        if AXUIElementCopyAttributeValue(item, attr as CFString, &v) == .success,
           let s = v as? String, !s.trimmingCharacters(in: .whitespaces).isEmpty {
            return .text(s.split(whereSeparator: \.isNewline).joined(separator: " "))
        }
    }
    return .notFound
}

// MARK: - Разбор статуса

enum Busy { case yes, no, skip, attention, unknown }
let POROG_OSHIBKA = 3  // проверок по 10 мин с ошибкой подряд = уведомление / checks with an error before notifying

func classify(_ st: String) -> Busy {
    let s = st.lowercased()
    let idle = ["выполнено", "обновлено", "up to date", "files are up to date", "синхронизировано"]
    if idle.contains(where: s.contains) { return .no }
    if APPS.contains(where: { $0.lowercased() == s.trimmingCharacters(in: .whitespaces) }) { return .no }  // только имя — запуск
    // Ошибка / нужен вход: перезапуск не поможет, но человеку надо сказать (1.6)
    let attention = ["ошибк", "требуется", "войдите", "error", "sign in", "signed out", "problem", "проблем"]
    if attention.contains(where: s.contains) { return .attention }
    let skip = ["приостановл", "пауз", "нет подключения", "не в сети",
                "paused", "offline", "not connected"]
    if skip.contains(where: s.contains) { return .skip }
    let busy = ["отправк", "скачиван", "загрузк", "соединени", "подключени", "синхрониз", "обработк",
                "индекс", "подготовк", "проверк",
                "upload", "download", "connecting", "syncing", "processing", "indexing", "preparing"]
    if busy.contains(where: s.contains) { return .yes }
    // Незнакомый текст (новая версия программы сменила слова): не трогаем, но сообщаем (1.8)
    return .unknown
}

// Незнакомые статусы, о которых уже сообщили (цифры заменены на #, чтобы «(37%)» не плодил новые)
// Unknown statuses already reported (digits normalized)
let unknownURL = stateDir.appendingPathComponent("unknown-statuses.json")
func unknownKey(_ app: String, _ st: String) -> String {
    app + ": " + String(st.map { $0.isNumber ? "#" : $0 })
}
func unknownReported(_ key: String) -> Bool {
    guard let d = try? Data(contentsOf: unknownURL), let a = try? JSONDecoder().decode([String].self, from: d) else { return false }
    return a.contains(key)
}
func markUnknownReported(_ key: String) {
    var a: [String] = []
    if let d = try? Data(contentsOf: unknownURL), let old = try? JSONDecoder().decode([String].self, from: d) { a = old }
    a.append(key); a = Array(a.suffix(50))
    try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
    if let d = try? JSONEncoder().encode(a) { try? d.write(to: unknownURL) }
}
let POROG_NOICON = 3   // проверок без значка подряд = одно уведомление / checks without an icon before notifying

// MARK: - Запрос доступа (install.sh запускает с --ask)

if CommandLine.arguments.contains("--ask") {
    let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    _ = AXIsProcessTrustedWithOptions(opts)
    exit(0)
}

// MARK: - Проверка (режим launchd)

func runCheck() {
    migrateOldFiles()

    for app in APPS {
        guard isInstalled(app) else { continue }
        guard isWatched(app) else {
            report.append(L("\(app): не слежу — выключено галочкой", "\(app): not watched — unchecked in settings"))
            try? FileManager.default.removeItem(at: stateURL(app))
            continue
        }

        guard let r = running(app) else {
            launch(app)
            log(L("\(app) не работал, запущен", "\(app) was not running, launched"))
            report.append(L("\(app): не работал — запущен", "\(app): was not running — launched"))
            try? FileManager.default.removeItem(at: stateURL(app))
            continue
        }

        var s = loadState(app)
        var st: String
        let fake = env["FAKE_\(app)"]               // для проверки: подменить статус («__noicon__» — значка нет)
        if let fake = fake, fake != "__noicon__" {
            st = fake
        } else {
            switch fake == "__noicon__" ? .notFound : menuBarStatus(r) {
            case .text(let t): st = t
            case .noAccess:
                if s.prev != "__noaccess__" {
                    log(L("\(app): нет разрешения «Универсальный доступ» — зависания не вижу", "\(app): no Accessibility permission — cannot detect hangs"))
                }
                report.append(L("\(app): НЕТ ДОСТУПА «Универсальный доступ» — зависания не вижу", "\(app): NO ACCESS (Accessibility) — cannot detect hangs"))
                s.prev = "__noaccess__"; s.cnt = 0; saveState(app, s); continue
            case .notFound:
                // Значок спрятан/выключен: перезапуск при падении работает, зависания не видны (1.8 — сообщаем один раз)
                if s.prev != "__nostatus__" {
                    log(L("\(app): значок в строке меню не найден", "\(app): menu bar icon not found"))
                    s.cnt = 0; s.warned = false
                } else { s.cnt += 1 }
                if s.cnt >= POROG_NOICON - 1 && !s.warned {
                    log(L("\(app): значка нет ~\((s.cnt + 1) * 10) мин — уведомил: зависания не видны", "\(app): no icon for ~\((s.cnt + 1) * 10) min — notified: hangs cannot be detected"))
                    notify(L("Сторож не видит значок \(app)", "Sync Watchdog cannot see the \(app) icon"),
                           L("Если \(app) закроется, сторож его запустит, но зависание не заметит. Включите значок \(app) в строке меню.",
                             "If \(app) quits it will be relaunched, but a hang will go unnoticed. Turn the \(app) menu bar icon back on."))
                    s.warned = true
                }
                report.append(L("\(app): значок в строке меню не найден — зависания не вижу", "\(app): menu bar icon not found — cannot detect hangs"))
                s.prev = "__nostatus__"; saveState(app, s); continue
            }
        }

        let busy = classify(st)
        if busy == .unknown && st == s.prev {
            // Держится две проверки подряд — сообщить один раз на такой текст
            let key = unknownKey(app, st)
            if !unknownReported(key) {
                log(L("\(app): незнакомый статус «\(st)» — не понимаю его, зависание с ним не замечу", "\(app): unknown status “\(st)” — cannot tell whether it is a hang"))
                notify(L("Сторож: незнакомый статус \(app)", "Sync Watchdog: unknown \(app) status"),
                       L("«\(st)». Возможно, \(app) обновился. Пока статус непонятен, зависание не ловится — проверьте обновление сторожа.",
                         "“\(st)”. \(app) may have been updated. Hangs are not detected with this status — check for a watchdog update."))
                markUnknownReported(key)
            }
        }
        if (busy == .yes || busy == .attention) && st == s.prev {
            s.cnt += 1
        } else {
            s.cnt = 0
            if st != s.prev { s.doneRestart = false; s.warned = false; s.netWaits = 0 }  // новый эпизод
        }

        var netNote = ""
        if busy == .yes && s.cnt >= POROG && internetOK() {
            // Текст стоит, но программа передаёт данные (большой файл, медленная сеть) — подождать, не дольше ~1 ч (1.8)
            let waits = s.netWaits ?? 0
            if !s.doneRestart && waits < NET_MAX_WAITS && activeTransfer(app) == true {
                s.netWaits = waits + 1
                if waits == 0 {
                    log(L("\(app): статус «\(st)» стоит ~\(s.cnt * 10) мин, но данные передаются — жду", "\(app): status “\(st)” unchanged ~\(s.cnt * 10) min, but data is moving — waiting"))
                }
                netNote = L(", данные идут — жду", ", data moving — waiting")
            } else if !s.doneRestart {
                log(L("\(app): статус «\(st)» не менялся ~\(s.cnt * 10) мин — перезапускаю", "\(app): status “\(st)” unchanged for ~\(s.cnt * 10) min — restarting"))
                if restart(app) { s.doneRestart = true }
                s.cnt = 0; s.netWaits = 0
            } else if !s.warned {
                log(L("\(app): после перезапуска снова завис («\(st)») — больше не перезапускаю, нужен человек", "\(app): stuck again after restart (“\(st)”) — not restarting again, needs a human"))
                notify(L("\(app) не синхронизируется", "\(app) is not syncing"),
                       L("Снова завис после перезапуска: \(st)", "Stuck again after restart: \(st)"))
                s.warned = true
            }
        }

        if busy == .attention && s.cnt >= POROG_OSHIBKA - 1 && !s.warned {
            log(L("\(app): «\(st)» уже ~\((s.cnt + 1) * 10) мин — уведомил, нужен человек", "\(app): “\(st)” for ~\((s.cnt + 1) * 10) min — notified, needs a human"))
            notify(L("\(app): нужна помощь", "\(app) needs attention"),
                   L("\(st). Откройте значок \(app) в строке меню.", "\(st). Open the \(app) menu bar icon."))
            s.warned = true
        }

        s.prev = st
        saveState(app, s)
        let state = busy == .yes ? (s.cnt > 0 ? L("занят, без изменений \(s.cnt * 10) мин", "busy, unchanged for \(s.cnt * 10) min")
                                              : L("занят, идёт работа", "busy, working"))
                  : busy == .skip ? L("пауза/нет сети — не трогаю", "paused/offline — leaving alone")
                  : busy == .attention ? L("ошибка, нужен человек (\((s.cnt + 1) * 10) мин)", "error, needs a human (\((s.cnt + 1) * 10) min)")
                  : busy == .unknown ? L("незнакомый статус — зависание не вижу", "unknown status — cannot detect hangs")
                  : L("в порядке", "OK")
        report.append("\(app): «\(st)» — \(state)\(netNote)\(s.doneRestart ? L(", уже перезапускал", ", already restarted") : "")\(s.warned ? L(", ПРЕДУПРЕДИЛ", ", WARNED") : "")")
        if env["STOROZH_VERBOSE"] != nil { print("\(app): «\(st)» busy=\(busy) cnt=\(s.cnt) restart=\(s.doneRestart) warned=\(s.warned)") }
    }

    // «Последняя проверка»: работал ли сторож и что видел
    let df = DateFormatter(); df.dateFormat = "yyyy-MM-dd HH:mm:ss"
    let text = L("Последняя проверка: ", "Last check: ") + "\(df.string(from: Date()))\n" + report.joined(separator: "\n") + "\n"
    if env["FAKE_OneDrive"] == nil && env["FAKE_Dropbox"] == nil {
        try? text.write(to: statusURL, atomically: true, encoding: .utf8)
        dailyUpdateCheck()   // раз в сутки: новая версия на GitHub (1.7) / daily update check
    }
}

// MARK: - Установщик (двойной щелчок по приложению)

let LABEL = "com.anton.storozh-sinhronizacii"
// Расписание — обычный LaunchAgent в ~/Library/LaunchAgents, его пишет само приложение.
// SMAppService (1.1–1.2) отказался: launchd запускает такие агенты только с подписью Apple
// (Developer ID / Apple Development), а с самоподписанной и временной — «spawn failed / CODESIGNING».
// Schedule = plain LaunchAgent written by the app (SMAppService agents need an Apple-issued signature).
let agent = SMAppService.agent(plistName: "\(LABEL).plist")   // только чтобы снять запись 1.1–1.2
let plistURL = home.appendingPathComponent("Library/LaunchAgents/\(LABEL).plist")
let domain = "gui/\(getuid())"

func isScheduled() -> Bool {
    FileManager.default.fileExists(atPath: plistURL.path) && run("/bin/launchctl", ["print", "\(domain)/\(LABEL)"]) == 0
}

struct ScheduleError: Error, LocalizedError {
    let msg: String
    var errorDescription: String? { msg }
}

func schedule() throws {
    if agent.status == .enabled || agent.status == .requiresApproval { try? agent.unregister() }
    run("/bin/launchctl", ["bootout", "\(domain)/\(LABEL)"])
    guard let exe = Bundle.main.executablePath else { throw ScheduleError(msg: "no executable path") }
    let job: [String: Any] = [
        "Label": LABEL,
        "ProgramArguments": [exe, "--check"],
        "EnvironmentVariables": ["STOROZH_MODE": "check"],
        "RunAtLoad": true,
        "StartInterval": 600,
        "ProcessType": "Background",
        "LowPriorityIO": true,
        "AssociatedBundleIdentifiers": [LABEL],
    ]
    try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
    let data = try PropertyListSerialization.data(fromPropertyList: job, format: .xml, options: 0)
    try data.write(to: plistURL)
    for attempt in 0..<5 {
        if run("/bin/launchctl", ["bootstrap", domain, plistURL.path]) == 0 { return }
        Thread.sleep(forTimeInterval: 0.5 * Double(attempt + 1))
    }
    throw ScheduleError(msg: "launchctl bootstrap failed")
}

func unschedule() {
    run("/bin/launchctl", ["bootout", "\(domain)/\(LABEL)"])
    try? FileManager.default.trashItem(at: plistURL, resultingItemURL: nil)
    try? agent.unregister()
}

// Галочки «следить за …» / watch checkboxes
var showWatchBoxes = false
func watchBoxesView() -> (NSView, () -> Void) {
    let stack = NSStackView(); stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 6
    var boxes: [(String, NSButton)] = []
    for app in APPS {
        let inst = isInstalled(app)
        let title = L("Следить за \(app)", "Watch \(app)") + (inst ? "" : L(" (не установлен)", " (not installed)"))
        let b = NSButton(checkboxWithTitle: title, target: nil, action: nil)
        b.state = isWatched(app) ? .on : .off
        b.isEnabled = inst
        stack.addArrangedSubview(b); boxes.append((app, b))
    }
    let upd = NSButton(checkboxWithTitle: L("Обновлять автоматически", "Update automatically"), target: nil, action: nil)
    upd.state = autoUpdateEnabled() ? .on : .off
    let hint = NSTextField(wrappingLabelWithString: L("Снимите галочку, если держите программу выключенной специально — сторож не будет её запускать.",
                                                      "Uncheck an app you keep closed on purpose — the watchdog will not launch it."))
    hint.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize); hint.textColor = .secondaryLabelColor
    hint.preferredMaxLayoutWidth = 260
    stack.addArrangedSubview(hint)
    stack.addArrangedSubview(upd)
    stack.frame = NSRect(x: 0, y: 0, width: 270, height: stack.fittingSize.height)
    let save = {
        var w = loadWatch()
        for (app, b) in boxes where b.isEnabled { w[app] = (b.state == .on) }
        saveWatch(w)
        setAutoUpdate(upd.state == .on)
    }
    return (stack, save)
}

@discardableResult
func alert(_ title: String, _ text: String, _ buttons: [String] = ["OK"]) -> Int {
    let a = NSAlert()
    a.messageText = title; a.informativeText = text
    for b in buttons { a.addButton(withTitle: b) }
    var saveBoxes: (() -> Void)? = nil
    if showWatchBoxes { let (v, sv) = watchBoxesView(); a.accessoryView = v; saveBoxes = sv }
    defer { saveBoxes?() }
    a.window.level = .floating              // не прятаться за другими окнами / stay on top
    NSApp.activate(ignoringOtherApps: true)
    return a.runModal().rawValue - NSApplication.ModalResponse.alertFirstButtonReturn.rawValue
}

// Короткий итог для окна: «OneDrive — в порядке» без сырого текста значка
func lastCheckText() -> String {
    guard let raw = try? String(contentsOf: statusURL, encoding: .utf8) else {
        return L("Первая проверка пройдёт в течение минуты.", "The first check will run within a minute.")
    }
    return raw.split(separator: "\n").map { line -> String in
        var l = String(line)
        if let a = l.range(of: ": «"), let b = l.range(of: "» — ") {
            l = String(l[..<a.lowerBound]) + " — " + String(l[b.upperBound...])
            return "• " + l
        }
        return l.hasPrefix("Последн") || l.hasPrefix("Last") ? l : "• " + l
    }.joined(separator: "\n")
}

func runInstaller() {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory)
    let title = L("Сторож синхронизации", "Sync Watchdog")
    let path = Bundle.main.bundlePath

    // Запущен прямо из .dmg или из «Загрузок» (macOS запускает такие из временной копии)
    if path.hasPrefix("/Volumes/") || path.contains("/AppTranslocation/") {
        alert(title, L("Сначала перетащите «Сторож синхронизации» в папку «Программы», затем откройте его оттуда.",
                       "First drag “Sync Watchdog” into the Applications folder, then open it from there."))
        return
    }

    migrateOldFiles()

    // Расписание переписывается при каждом открытии: после обновления или переноса путь мог смениться
    // Rewrite the schedule on every open (the app path may have changed after an update)
    let wasInstalled = FileManager.default.fileExists(atPath: plistURL.path) || agent.status == .enabled
    do { try schedule() } catch {
        log(L("установка: не удалось поставить на расписание — \(error)", "install: could not schedule — \(error)"))
        alert(title, L("Не удалось поставить сторожа на расписание: ", "Could not schedule the watchdog: ") + error.localizedDescription)
        return
    }

    if wasInstalled {
        let access = AXIsProcessTrusted() ? "" :
            L("\n\n⚠️ Нет разрешения «Универсальный доступ» — зависания не видны.",
              "\n\n⚠️ No Accessibility permission — hangs cannot be detected.")
        showWatchBoxes = true
        let r = alert(title, L("Сторож включён и работает в фоне, окон не показывает. Раз в 10 минут он проверяет OneDrive и Dropbox и перезапускает их, если они закрылись или зависли.",
                               "The watchdog is on and works in the background without windows. Every 10 minutes it checks OneDrive and Dropbox and restarts them if they quit or hang.")
                      + access + "\n\n" + lastCheckText() + "\n\n" + L("Версия ", "Version ") + currentAppVersion(),
                      ["OK", L("Открыть журнал", "Open log"), L("Проверить обновления", "Check for updates"), L("Удалить сторожа", "Uninstall")])
        if r == 1 {
            if !FileManager.default.fileExists(atPath: logURL.path) {
                try? L("Журнал пуст: сторож пока ничего не делал.\n", "The log is empty: the watchdog has not done anything yet.\n")
                    .write(to: logURL, atomically: true, encoding: .utf8)
            }
            NSWorkspace.shared.open(logURL)
        } else if r == 2 {
            showWatchBoxes = false
            alert(title, checkUpdateInteractive())
        } else if r == 3 {
            showWatchBoxes = false
            unschedule()
            alert(title, L("Сторож выключен. Теперь перетащите приложение в Корзину и удалите его из «Универсального доступа».",
                           "The watchdog is turned off. Now drag the app to the Trash and remove it from Accessibility."))
        } else if !AXIsProcessTrusted() {
            askAccess()
        }
        return
    }

    showWatchBoxes = true
    if AXIsProcessTrusted() {
        alert(title, L("Готово. Сторож будет проверять OneDrive и Dropbox раз в 10 минут.",
                       "Done. The watchdog will check OneDrive and Dropbox every 10 minutes."))
    } else {
        alert(title, L("Почти готово. Остался один шаг: в окне «Универсальный доступ» включите переключатель у «Сторож синхронизации».",
                       "Almost done. One step left: in Accessibility, turn on the switch next to “Sync Watchdog”."))
        askAccess()
    }
}

func askAccess() {
    let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    _ = AXIsProcessTrustedWithOptions(opts)
    if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
        NSWorkspace.shared.open(u)
    }
}

// MARK: - Выбор режима

if CommandLine.arguments.contains("--status") {   // диагностика / diagnostics
    if CommandLine.arguments.contains("--register") {      // поставить расписание без окна / schedule without UI
        do { try schedule(); print("schedule: ok") } catch { print("schedule error: \(error)") }
    }
    if CommandLine.arguments.contains("--unregister") { unschedule(); print("unscheduled") }
    if CommandLine.arguments.contains("--update-now") { print(checkUpdateInteractive()) }   // проверка обновления без окна
    print("scheduled: \(isScheduled())  accessibility: \(AXIsProcessTrusted())  path: \(Bundle.main.bundlePath)")
    exit(0)
}

let checkMode = CommandLine.arguments.contains("--check") || env["STOROZH_MODE"] == "check"
    || env["FAKE_OneDrive"] != nil || env["FAKE_Dropbox"] != nil || env["STOROZH_VERBOSE"] != nil
if checkMode { runCheck() } else { runInstaller() }
