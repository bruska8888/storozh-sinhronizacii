// Сторож синхронизации / Sync Watchdog
// Запускается launchd раз в 10 минут, делает одну проверку и выходит.
// 1) OneDrive/Dropbox закрылся — запускает его в фоне.
// 2) Клиент жив, но статус значка в строке меню («Отправка файлов (2%)», «Соединение...»)
//    не меняется POROG проверок подряд — один штатный перезапуск.
//    Снова завис — уведомление, больше не трогает, пока статус не сменится.
// Нужен доступ «Универсальный доступ» для этого приложения (читает текст значков).

import AppKit
import ApplicationServices
import Foundation

let APPS = ["OneDrive", "Dropbox"]
let POROG = 4  // проверок по 10 мин без изменений = зависание
let home = FileManager.default.homeDirectoryForCurrentUser
let logURL = home.appendingPathComponent("Library/Logs/storozh_sinhronizacii.log")
let stateDir = home.appendingPathComponent("Library/Application Support/storozh-sinhronizacii")
let env = ProcessInfo.processInfo.environment
let statusURL = home.appendingPathComponent("Library/Logs/storozh_sinhronizacii_status.txt")
var report: [String] = []   // «последняя проверка» — перезаписывается каждый запуск

// MARK: - Строки (RU/EN)

let isRU = (Locale.preferredLanguages.first ?? "ru").hasPrefix("ru")
func L(_ ru: String, _ en: String) -> String { isRU ? ru : en }

// MARK: - Журнал

func log(_ msg: String) {
    let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm"
    let line = "\(f.string(from: Date())) — \(msg)\n"
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
}

func stateURL(_ app: String) -> URL { stateDir.appendingPathComponent("\(app).json") }
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
        log("\(app): завис, штатно не закрылся — оставил как есть")
        return false
    }
    Thread.sleep(forTimeInterval: 3)
    launch(app)
    return true
}

func internetOK() -> Bool {
    run("/usr/bin/curl", ["-s", "-o", "/dev/null", "-m", "10", "https://captive.apple.com"]) == 0
}

func notify(_ title: String, _ text: String) {
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

enum Busy { case yes, no, skip }

func classify(_ st: String) -> Busy {
    let s = st.lowercased()
    let idle = ["выполнено", "обновлено", "up to date", "files are up to date", "синхронизировано"]
    if idle.contains(where: s.contains) { return .no }
    let skip = ["приостановл", "пауз", "нет подключения", "не в сети", "ошибк", "требуется", "войдите",
                "paused", "offline", "not connected", "error", "sign in", "signed out"]
    if skip.contains(where: s.contains) { return .skip }
    let busy = ["отправк", "скачиван", "загрузк", "соединени", "подключени", "синхрониз", "обработк",
                "индекс", "подготовк", "проверк",
                "upload", "download", "connecting", "syncing", "processing", "indexing", "preparing"]
    if busy.contains(where: s.contains) { return .yes }
    return .no
}

// MARK: - Запрос доступа (install.sh запускает с --ask)

if CommandLine.arguments.contains("--ask") {
    let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    _ = AXIsProcessTrustedWithOptions(opts)
    exit(0)
}

// MARK: - Проверка

for app in APPS {
    guard FileManager.default.fileExists(atPath: "/Applications/\(app).app") else { continue }

    guard let r = running(app) else {
        launch(app)
        log("\(app) не работал, запущен")
        report.append("\(app): не работал — запущен")
        try? FileManager.default.removeItem(at: stateURL(app))
        continue
    }

    var s = loadState(app)
    var st: String
    if let fake = env["FAKE_\(app)"] {          // для проверки: подменить статус
        st = fake
    } else {
        switch menuBarStatus(r) {
        case .text(let t): st = t
        case .noAccess:
            if s.prev != "__noaccess__" {
                log("\(app): нет доступа «Универсальный доступ» у Сторожа синхронизации — зависания не вижу")
            }
            report.append("\(app): НЕТ ДОСТУПА «Универсальный доступ» — зависания не вижу")
            s.prev = "__noaccess__"; s.cnt = 0; saveState(app, s); continue
        case .notFound:
            if s.prev != "__nostatus__" { log("\(app): значок в строке меню не найден") }
            report.append("\(app): значок в строке меню не найден")
            s.prev = "__nostatus__"; s.cnt = 0; saveState(app, s); continue
        }
    }

    let busy = classify(st)
    if busy == .yes && st == s.prev {
        s.cnt += 1
    } else {
        s.cnt = 0
        if st != s.prev { s.doneRestart = false; s.warned = false }  // новый эпизод
    }

    if busy == .yes && s.cnt >= POROG && internetOK() {
        if !s.doneRestart {
            log("\(app): статус «\(st)» не менялся ~\(s.cnt * 10) мин — перезапускаю")
            if restart(app) { s.doneRestart = true }
            s.cnt = 0
        } else if !s.warned {
            log("\(app): после перезапуска снова завис («\(st)») — больше не перезапускаю, нужен человек")
            notify(L("\(app) не синхронизируется", "\(app) is not syncing"),
                   L("Снова завис после перезапуска: \(st)", "Stuck again after restart: \(st)"))
            s.warned = true
        }
    }

    s.prev = st
    saveState(app, s)
    let state = busy == .yes ? (s.cnt > 0 ? "занят, без изменений \(s.cnt * 10) мин" : "занят, идёт работа")
              : busy == .skip ? "пауза/нет сети — не трогаю" : "в порядке"
    report.append("\(app): «\(st)» — \(state)\(s.doneRestart ? ", уже перезапускал" : "")\(s.warned ? ", ПРЕДУПРЕДИЛ" : "")")
    if env["STOROZH_VERBOSE"] != nil { print("\(app): «\(st)» busy=\(busy) cnt=\(s.cnt) restart=\(s.doneRestart) warned=\(s.warned)") }
}

// «Последняя проверка»: работал ли сторож и что видел
let df = DateFormatter(); df.dateFormat = "yyyy-MM-dd HH:mm:ss"
let text = "Последняя проверка: \(df.string(from: Date()))\n" + report.joined(separator: "\n") + "\n"
if env["FAKE_OneDrive"] == nil && env["FAKE_Dropbox"] == nil {
    try? text.write(to: statusURL, atomically: true, encoding: .utf8)
}
