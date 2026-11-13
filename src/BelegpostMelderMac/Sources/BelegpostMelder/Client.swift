import Foundation
import CryptoKit

struct BelegEvent {
    let seq: Int64, id: String, title: String, status: String, text: String?, path: String, kind: String, mail: Bool
}

enum MelderState { case notPaired, connecting, connected, offline, rejected }

/// Nimmt ein selbst signiertes Server-Zertifikat an, wenn sein SHA-256-Fingerabdruck zur Kopplung passt.
final class PinningDelegate: NSObject, URLSessionDelegate {
    let thumbprint: String?
    init(thumbprint: String?) { self.thumbprint = thumbprint }

    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        guard challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust,
              let trust = challenge.protectionSpace.serverTrust else { completionHandler(.performDefaultHandling, nil); return }
        if SecTrustEvaluateWithError(trust, nil) { completionHandler(.useCredential, URLCredential(trust: trust)); return }
        if let tp = thumbprint, !tp.isEmpty,
           let chain = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = chain.first {
            let der = SecCertificateCopyData(leaf) as Data
            let hex = SHA256.hash(data: der).map { String(format: "%02X", $0) }.joined()
            if hex.caseInsensitiveCompare(tp) == .orderedSame { completionHandler(.useCredential, URLCredential(trust: trust)); return }
        }
        completionHandler(.cancelAuthenticationChallenge, nil)
    }
}

enum MelderError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let m) = self { return m }; return nil }
}

/// Verbindung zum Portal: Server-Sent Events mit automatischem Neuaufbau, dazu einfache API-Aufrufe.
@MainActor
final class MelderClient: ObservableObject {
    @Published private(set) var state: MelderState = .notPaired
    @Published private(set) var statusText = "Nicht verbunden"
    var onEvent: ((BelegEvent) -> Void)?
    private var task: Task<Void, Never>?
    private let cfg = MelderConfig.shared

    static func session(thumbprint: String?, timeout: TimeInterval) -> URLSession {
        let c = URLSessionConfiguration.default
        c.timeoutIntervalForRequest = timeout
        c.timeoutIntervalForResource = 7 * 24 * 3600
        c.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: c, delegate: PinningDelegate(thumbprint: thumbprint), delegateQueue: nil)
    }

    static func request(_ url: String, token: String, method: String = "GET", json: Any? = nil) -> URLRequest {
        var r = URLRequest(url: URL(string: url)!)
        r.httpMethod = method
        r.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        if let json { r.httpBody = try? JSONSerialization.data(withJSONObject: json); r.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        return r
    }

    static func errorText(_ data: Data) -> String? {
        (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["error"] as? String
    }

    /// Beim Koppeln: Verbindung prüfen, liefert Benutzer, Portal-Titel und Outlook-Freigabe.
    static func hello(server: String, token: String, thumbprint: String?) async throws -> (String, String, Bool) {
        let s = session(thumbprint: thumbprint, timeout: 15)
        do {
            let (data, res) = try await s.data(for: request(server + "/melder/api/hallo", token: token))
            let code = (res as? HTTPURLResponse)?.statusCode ?? 0
            if code == 401 { throw MelderError.message("Der Code ist nicht (mehr) gültig – im Portal bitte neu verbinden.") }
            guard code == 200, let o = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw MelderError.message("Server antwortet mit \(code).")
            }
            return (o["user"] as? String ?? "", o["title"] as? String ?? "Belegpost", o["outlook"] as? Bool ?? false)
        } catch let e as MelderError { throw e }
        catch { throw MelderError.message("Server \(server) nicht erreichbar: \(error.localizedDescription)") }
    }

    func start() {
        stop()
        guard cfg.paired else { set(.notPaired, "Nicht verbunden – im Portal unter „Melder & Hinweise“ verbinden."); return }
        task = Task { await loop() }
    }

    func stop() { task?.cancel(); task = nil }

    private func set(_ s: MelderState, _ text: String) { state = s; statusText = text }

    private func loop() async {
        var delay: UInt64 = 3
        while !Task.isCancelled {
            guard let token = cfg.token, let server = cfg.server else { set(.notPaired, "Zugangsschlüssel fehlt – bitte neu verbinden."); return }
            set(.connecting, "Verbinde mit \(server) …")
            do {
                let s = Self.session(thumbprint: cfg.thumbprint, timeout: 90) // Lebenszeichen kommt alle 25 s
                var url = server + "/melder/api/ereignisse"
                if let last = cfg.lastSeq { url += "?after=\(last)" }
                var req = Self.request(url, token: token)
                req.setValue("text/event-stream", forHTTPHeaderField: "Accept")
                let (bytes, res) = try await s.bytes(for: req)
                let code = (res as? HTTPURLResponse)?.statusCode ?? 0
                if code == 401 {
                    set(.rejected, "Dieser Mac ist im Portal nicht mehr verbunden – bitte neu verbinden.")
                    try await Task.sleep(nanoseconds: 300 * 1_000_000_000)
                    continue
                }
                guard code == 200 else { throw MelderError.message("Server antwortet mit \(code).") }
                set(.connected, "Wartet auf neue Belege – druckst du im ERP, erscheint der Beleg hier.")
                delay = 3
                Task { await self.sendSettings() }
                var eventName = ""
                // Hinweis: „lines“ überspringt Leerzeilen – jedes Ereignis hat genau eine data-Zeile.
                for try await line in bytes.lines {
                    if Task.isCancelled { return }
                    if line.hasPrefix("event:") { eventName = line.dropFirst(6).trimmingCharacters(in: .whitespaces) }
                    else if line.hasPrefix("data:") {
                        if eventName == "beleg" { handle(String(line.dropFirst(5)).trimmingCharacters(in: .whitespaces)) }
                        eventName = ""
                    }
                }
                set(.offline, "Verbindung beendet – verbinde neu …")
            } catch is CancellationError { return }
            catch {
                if Task.isCancelled { return }
                set(.offline, "Keine Verbindung (\(error.localizedDescription)) – neuer Versuch in \(delay) s")
            }
            try? await Task.sleep(nanoseconds: delay * 1_000_000_000)
            delay = min(60, delay * 2)
        }
    }

    private func handle(_ json: String) {
        guard let data = json.data(using: .utf8), let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let seq = (o["seq"] as? NSNumber)?.int64Value else { return }
        let e = BelegEvent(seq: seq, id: o["id"] as? String ?? "", title: o["title"] as? String ?? "", status: o["status"] as? String ?? "",
                           text: o["text"] as? String, path: o["path"] as? String ?? "/", kind: o["kind"] as? String ?? "neu",
                           mail: o["mail"] as? Bool ?? true)
        cfg.lastSeq = e.seq
        cfg.lastDocumentPath = e.path
        cfg.lastDocumentId = e.id
        onEvent?(e)
    }

    /// Dem Server melden, ob dieser Mac Belege in Outlook öffnet; liefert die Outlook-Freigabe zurück.
    func sendSettings() async {
        guard let token = cfg.token, let server = cfg.server else { return }
        let s = Self.session(thumbprint: cfg.thumbprint, timeout: 15)
        guard let (data, res) = try? await s.data(for: Self.request(server + "/melder/api/einstellungen", token: token, method: "POST",
                                                                    json: ["outlook": cfg.onNewDocument == "outlook"])),
              (res as? HTTPURLResponse)?.statusCode == 200,
              let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        let allowed = o["outlookAllowed"] as? Bool ?? false
        if allowed != cfg.outlookAllowed { cfg.outlookAllowed = allowed }
    }
}
