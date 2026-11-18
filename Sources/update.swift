// macOS updater. Compiled alongside main.swift; no top-level actions.
import Foundation
import Security
import AppKit

let UPDATE_API = env["STOROZH_UPDATE_API"] ?? "https://api.github.com/repos/bruska8888/storozh-sinhronizacii/releases?per_page=30"
private let updateMaxBytes = 60 * 1024 * 1024
private let updateModulus = "sOrH8bW6LdMEuUkT9a/INnECyCMzLrmMgffS/ScG52TlaHBBz86hvnCu5b3m/geWLXQWAGW125bXemwO/iUaAJTTOlVkQPzoJFC/fzQX3+7dbDzLCn5mVWBG52RcfOo4zDb3junbPQKqh2E9iVFe7SgRCN3Cj4zklHIFZWjqa8qq9wyplC+AeGMsJfeEObLfU14IxqCCbmSfzyYMFEqXVxWRohZyGiXy+bZY809lthlfhIitjJj3gTEKcJlFez6jXIhDWKbHI05yiyNqr94fQUJBuW1NViyJTDavFsumOnmHa8yaOY4WMDDGAB7EPSvNrLS3gHm0MqijqH3YnY+4g5AQhifC4Wof3Qx3Z8Z0sNT33Coci6+5VTfHgUdDzr7yds6mn8eJE11ZmFVYw+LmlMpDRsW6b+5ZOuypeetw5fGUCQ8UYaN22T4Di4gwaVwV+PIXFQ9cr4TNXVEdpN9SkNHB2beaBlM2SW30UmqwJ2EzrPWp1fcQVI+CQVFoAZ3x"

private struct UpdateError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
    init(_ ru: String, _ en: String) { message = L(ru, en) }
}

func currentAppVersion() -> String {
    (Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String)
        ?? env["STOROZH_FAKE_VERSION"] ?? "0"
}

// Compare arbitrary-length numeric components without integer overflow; 1.7 == 1.7.0.
private func updateVersionCompare(_ a: String, _ b: String) -> ComparisonResult {
    let aa = a.split(separator: ".").map(String.init)
    let bb = b.split(separator: ".").map(String.init)
    for i in 0..<max(aa.count, bb.count) {
        let x = i < aa.count ? String(aa[i].drop(while: { $0 == "0" })) : ""
        let y = i < bb.count ? String(bb[i].drop(while: { $0 == "0" })) : ""
        if x.count != y.count { return x.count > y.count ? .orderedDescending : .orderedAscending }
        if x != y { return x > y ? .orderedDescending : .orderedAscending }
    }
    return .orderedSame
}

private func updateDER(_ tag: UInt8, _ bytes: Data) -> Data {
    var length = bytes.count
    var header = Data([tag])
    if length < 128 { header.append(UInt8(length)) }
    else {
        var octets: [UInt8] = []
        while length > 0 { octets.insert(UInt8(length & 255), at: 0); length >>= 8 }
        header.append(0x80 | UInt8(octets.count)); header.append(contentsOf: octets)
    }
    header.append(bytes)
    return header
}

func verifyUpdateSignature(_ data: Data, _ sigBase64: String) -> Bool {
    let modulus = updateModulus   // ключ вшит, подмены нет / key is built in, no override
    guard var n = Data(base64Encoded: modulus), !n.isEmpty else { return false }
    // Only base64 and whitespace are accepted (not arbitrary ignored characters).
    let clean = sigBase64.components(separatedBy: .whitespacesAndNewlines).joined()
    guard let sig = Data(base64Encoded: clean), !sig.isEmpty else { return false }
    while n.count > 1 && n.first == 0 { n.removeFirst() }
    if let first = n.first, first & 0x80 != 0 { n.insert(0, at: 0) }
    let der = updateDER(0x30, updateDER(0x02, n) + updateDER(0x02, Data([1, 0, 1])))
    let attrs = [kSecAttrKeyType: kSecAttrKeyTypeRSA, kSecAttrKeyClass: kSecAttrKeyClassPublic] as CFDictionary
    guard let key = SecKeyCreateWithData(der as CFData, attrs, nil) else { return false }
    return SecKeyVerifySignature(key, .rsaSignatureMessagePKCS1v15SHA256, data as CFData, sig as CFData, nil)
}

// Delegate bounds received bytes during transfer, even for chunked responses.
private final class UpdateDownload: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    let done = DispatchSemaphore(value: 0)
    var data = Data()
    var error: Error?
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                    didReceive response: URLResponse, completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            error = UpdateError("Сервер вернул HTTP \(http.statusCode)", "Server returned HTTP \(http.statusCode)")
        } else if response.expectedContentLength > Int64(updateMaxBytes) {
            error = UpdateError("Файл больше 60 МБ", "File exceeds 60 MB")
        }
        completionHandler(error == nil ? .allow : .cancel)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive bytes: Data) {
        guard error == nil else { return }
        guard bytes.count <= updateMaxBytes - data.count else {
            error = UpdateError("Файл больше 60 МБ", "File exceeds 60 MB")
            dataTask.cancel(); return
        }
        data.append(bytes)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError failure: Error?) {
        if error == nil { error = failure }
        done.signal()
    }
}

private func updateFetch(_ url: URL, api: Bool = false) throws -> Data {
    if url.isFileURL && env["STOROZH_UPDATE_API"] != nil {   // file:// — только в тестах / tests only
        let h = try FileHandle(forReadingFrom: url)
        defer { try? h.close() }
        let bytes = try h.read(upToCount: updateMaxBytes + 1) ?? Data()
        guard bytes.count <= updateMaxBytes else { throw UpdateError("Файл больше 60 МБ", "File exceeds 60 MB") }
        return bytes
    }
    guard url.scheme?.lowercased() == "https" else {
        throw UpdateError("Неподдерживаемый адрес обновления", "Unsupported update URL")
    }
    let receiver = UpdateDownload()
    let config = URLSessionConfiguration.ephemeral
    config.timeoutIntervalForRequest = 30; config.timeoutIntervalForResource = 30
    let session = URLSession(configuration: config, delegate: receiver, delegateQueue: nil)
    defer { session.invalidateAndCancel() }
    var request = URLRequest(url: url, timeoutInterval: 30)
    request.setValue("SyncWatchdog/\(currentAppVersion())", forHTTPHeaderField: "User-Agent")
    if api { request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept") }
    session.dataTask(with: request).resume()
    guard receiver.done.wait(timeout: .now() + 30) == .success else {
        throw UpdateError("Время ожидания сервера истекло", "Server request timed out")
    }
    if let failure = receiver.error { throw failure }
    return receiver.data
}

private struct UpdateRelease: Decodable {
    struct Asset: Decodable { let name: String; let browser_download_url: URL }
    let draft: Bool
    let prerelease: Bool
    let assets: [Asset]
}

private func newerMacRelease() throws -> (version: String, zip: URL, sig: URL)? {
    guard let api = URL(string: UPDATE_API) else { throw UpdateError("Неверный адрес API", "Invalid API URL") }
    let releases = try JSONDecoder().decode([UpdateRelease].self, from: updateFetch(api, api: true))
    let regex = try NSRegularExpression(pattern: "^SyncWatchdog-mac-([0-9]+(\\.[0-9]+)*)\\.zip$")
    var best: (version: String, zip: URL, sig: URL)?
    for release in releases where !release.draft && !release.prerelease {
        for asset in release.assets {
            let name = asset.name as NSString
            guard let match = regex.firstMatch(in: asset.name, range: NSRange(location: 0, length: name.length)),
                  let sig = release.assets.first(where: { $0.name == asset.name + ".sig" }) else { continue }
            let version = name.substring(with: match.range(at: 1))
            guard updateVersionCompare(version, currentAppVersion()) == .orderedDescending else { continue }
            if best == nil || updateVersionCompare(version, best!.version) == .orderedDescending {
                best = (version, asset.browser_download_url, sig.browser_download_url)
            }
        }
    }
    return best
}

func findNewerMacRelease() -> (version: String, zip: URL, sig: URL)? {
    try? newerMacRelease()
}

// Read stderr concurrently with the process to avoid a full-pipe deadlock.
private func updateCodeSignDetails(_ app: URL) throws -> String {
    let p = Process(), pipe = Pipe()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
    p.arguments = ["-dvv", app.path]
    p.standardOutput = FileHandle.nullDevice; p.standardError = pipe
    try p.run()
    let output = pipe.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    guard p.terminationStatus == 0 else { throw UpdateError("Не удалось прочитать подпись приложения", "Could not read app code signature") }
    return String(decoding: output, as: UTF8.self)
}

private func performUpdate(zipURL: URL, sigURL: URL, version: String) throws {
    let fm = FileManager.default
    let rawTarget = env["STOROZH_UPDATE_TARGET"] ?? Bundle.main.bundlePath
    let target = URL(fileURLWithPath: rawTarget).standardizedFileURL
    let realTarget = target.resolvingSymlinksInPath()
    func allowed(_ path: String) -> Bool {
        path.hasSuffix("/Sync Watchdog.app") && !path.hasPrefix("/Volumes/") && !path.contains("AppTranslocation")
    }
    guard allowed(rawTarget), allowed(target.path), allowed(realTarget.path), target.path == realTarget.path else {
        throw UpdateError("Обновление невозможно по этому пути: \(rawTarget)", "Cannot update at this path: \(rawTarget)")
    }
    var isDirectory: ObjCBool = false
    guard fm.fileExists(atPath: target.path, isDirectory: &isDirectory), isDirectory.boolValue else {
        throw UpdateError("Целевое приложение не найдено", "Target app was not found")
    }
    let tmp = fm.temporaryDirectory.appendingPathComponent("SyncWatchdog-update-\(UUID().uuidString)", isDirectory: true)
    try fm.createDirectory(at: tmp, withIntermediateDirectories: true)
    var keepRecoveryFiles = false
    defer { if !keepRecoveryFiles { try? fm.removeItem(at: tmp) } }
    let zip = try updateFetch(zipURL)
    let sig = try updateFetch(sigURL)
    guard let sigText = String(data: sig, encoding: .utf8), verifyUpdateSignature(zip, sigText) else {
        throw UpdateError("Обновление \(version) не прошло проверку подписи — НЕ установлено",
                          "Update \(version) failed signature verification — NOT installed")
    }
    let archive = tmp.appendingPathComponent("update.zip")
    let unpacked = tmp.appendingPathComponent("unpacked", isDirectory: true)
    try zip.write(to: archive)
    try fm.createDirectory(at: unpacked, withIntermediateDirectories: true)
    guard run("/usr/bin/ditto", ["-x", "-k", archive.path, unpacked.path]) == 0 else {
        throw UpdateError("Не удалось распаковать обновление", "Could not unpack update")
    }
    let entries = try fm.contentsOfDirectory(at: unpacked, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])
    guard entries.count == 1, let app = entries.first, app.lastPathComponent == "Sync Watchdog.app",
          try app.resourceValues(forKeys: [.isDirectoryKey]).isDirectory == true,
          try app.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink != true else {
        throw UpdateError("В архиве должен быть только Sync Watchdog.app", "Archive must contain only Sync Watchdog.app")
    }
    guard run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path]) == 0 else {
        throw UpdateError("Подпись приложения повреждена", "App code signature is invalid")
    }
    let details = try updateCodeSignDetails(app).components(separatedBy: .newlines)
    guard details.contains("Authority=Sync Watchdog"), details.contains("Identifier=\(LABEL)") else {
        throw UpdateError("Неверный издатель или идентификатор приложения", "Unexpected app signer or identifier")
    }
    let infoURL = app.appendingPathComponent("Contents/Info.plist")
    let info = try PropertyListSerialization.propertyList(from: Data(contentsOf: infoURL), options: [], format: nil) as? [String: Any]
    guard info?["CFBundleShortVersionString"] as? String == version else {
        throw UpdateError("Версия приложения не совпадает с релизом", "App version does not match release")
    }
    // Keep all working files in temporaryDirectory, including an independent old-app backup.
    let backup = tmp.appendingPathComponent("previous.app", isDirectory: true)
    try fm.copyItem(at: target, to: backup)
    do {
        _ = try fm.replaceItemAt(target, withItemAt: app, backupItemName: nil, options: [])
    } catch {
        // If replacement removed the destination, restore the independently copied old app.
        // Never discard the recovery copy when the filesystem cannot complete restoration.
        if !fm.fileExists(atPath: target.path) {
            do { try fm.copyItem(at: backup, to: target) }
            catch { keepRecoveryFiles = true }
        } else {
            // The API may fail after moving items; retain the original rather than guess
            // which version is now at the destination.
            keepRecoveryFiles = true
        }
        if keepRecoveryFiles {
            log(L("Замена приложения не выполнена; резервная копия: \(backup.path)",
                  "App replacement failed; backup: \(backup.path)"))
        }
        throw error
    }
    log(L("Сторож обновлён: \(currentAppVersion()) → \(version)", "Sync Watchdog updated: \(currentAppVersion()) → \(version)"))
}

func installUpdate(zipURL: URL, sigURL: URL, version: String) -> Bool {
    do { try performUpdate(zipURL: zipURL, sigURL: sigURL, version: version); return true }
    catch { log(error.localizedDescription); return false }
}

func autoUpdateEnabled() -> Bool { loadWatch()["__autoUpdate"] ?? true }
func setAutoUpdate(_ on: Bool) {
    var watch = loadWatch(); watch["__autoUpdate"] = on; saveWatch(watch)
}

private struct UpdateState: Codable {
    var lastCheck: Date = .distantPast
    var latestSeen = ""
    var notifiedVersion = ""
}
private func loadUpdateState() -> UpdateState {
    guard let data = try? Data(contentsOf: stateDir.appendingPathComponent("update.json")),
          let state = try? JSONDecoder().decode(UpdateState.self, from: data) else { return UpdateState() }
    return state
}
private func saveUpdateState(_ state: UpdateState) {
    do {
        try FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
        try JSONEncoder().encode(state).write(to: stateDir.appendingPathComponent("update.json"), options: .atomic)
    } catch {
        log(L("Не удалось сохранить состояние обновлений: \(error.localizedDescription)",
              "Could not save update state: \(error.localizedDescription)"))
    }
}

func dailyUpdateCheck(force: Bool = false) {
    var state = loadUpdateState()
    let now = Date()
    guard force || env["STOROZH_UPDATE_NOW"] != nil || now.timeIntervalSince(state.lastCheck) >= 24 * 3600 else { return }
    state.lastCheck = now
    saveUpdateState(state)
    do {
        guard let release = try newerMacRelease() else { return }
        state.latestSeen = release.version
        if autoUpdateEnabled() {
            if installUpdate(zipURL: release.zip, sigURL: release.sig, version: release.version) {
                notify(L("Сторож синхронизации обновлён", "Sync Watchdog updated"),
                       L("Установлена версия \(release.version)", "Installed version \(release.version)"))
            }
        } else if state.notifiedVersion != release.version {
            notify(L("Есть новая версия сторожа: \(release.version)", "New Sync Watchdog version: \(release.version)"),
                   L("Откройте сторожа и нажмите «Обновить»", "Open Sync Watchdog and click “Update”"))
            state.notifiedVersion = release.version
        }
        saveUpdateState(state)
    } catch {
        log(L("Не удалось проверить обновления: \(error.localizedDescription)", "Could not check for updates: \(error.localizedDescription)"))
    }
}

func checkUpdateInteractive() -> String {
    do {
        guard let release = try newerMacRelease() else {
            return L("У вас последняя версия (\(currentAppVersion()))", "You have the latest version (\(currentAppVersion()))")
        }
        try performUpdate(zipURL: release.zip, sigURL: release.sig, version: release.version)
        return L("Обновлено до \(release.version) — откройте сторожа ещё раз", "Updated to \(release.version) — reopen Sync Watchdog")
    } catch {
        log(error.localizedDescription)
        return L("Не удалось проверить обновления: \(error.localizedDescription)", "Could not check for updates: \(error.localizedDescription)")
    }
}
