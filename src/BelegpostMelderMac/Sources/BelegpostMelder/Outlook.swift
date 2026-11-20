import AppKit

/// Öffnet die Mail eines Belegs als Entwurf in Outlook für Mac – mit Empfängern, Betreff, HTML-Text und Anhängen.
///
/// Weg 1: klassisches Outlook per AppleScript (macOS fragt beim ersten Mal einmalig, ob der Melder Outlook steuern darf).
/// Weg 2: .eml-Entwurf in Outlook öffnen (neues Outlook ohne AppleScript-Unterstützung).
/// Weg 3: kein Outlook installiert → Apple Mail per AppleScript.
@MainActor
enum OutlookBridge {
    static let outlookBundle = "com.microsoft.Outlook"

    struct Result {
        let ok: Bool; let message: String
        /// Über Outlook (AppleScript) geöffnet → Versand lässt sich in „Gesendet“ wiederfinden
        var viaOutlook = false
        var subject = ""
    }

    static func openDraft(documentId: String) async -> Result {
        let cfg = MelderConfig.shared
        guard let token = cfg.token, let server = cfg.server else { return Result(ok: false, message: "Melder ist nicht verbunden.") }
        let base = server + "/melder/api/vorgang/" + (documentId.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? documentId)
        let s = MelderClient.session(thumbprint: cfg.thumbprint, timeout: 60)

        // Outlook im Web: Belegpost legt den Entwurf über Microsoft 365 an, hier nur den Link im Browser öffnen
        if cfg.mailClient == "outlookweb" {
            do {
                let (data, res) = try await s.data(for: MelderClient.request(base + "/webentwurf", token: token, method: "POST", json: [String: Any]()))
                let code = (res as? HTTPURLResponse)?.statusCode ?? 0
                guard code == 200, let o = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                      let link = o["url"] as? String, let url = URL(string: link) else {
                    return Result(ok: false, message: MelderClient.errorText(data) ?? "Server meldet \(code).")
                }
                NSWorkspace.shared.open(url)
                return Result(ok: true, message: "Entwurf in Outlook im Web geöffnet.")
            } catch { return Result(ok: false, message: "Server nicht erreichbar: \(error.localizedDescription)") }
        }

        let mail: [String: Any]
        do {
            let (data, res) = try await s.data(for: MelderClient.request(base + "/mail", token: token))
            let code = (res as? HTTPURLResponse)?.statusCode ?? 0
            guard code == 200, let o = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                return Result(ok: false, message: MelderClient.errorText(data) ?? "Server meldet \(code).")
            }
            mail = o
        } catch { return Result(ok: false, message: "Server nicht erreichbar: \(error.localizedDescription)") }

        // Anhänge in einen Ordner im Temp-Verzeichnis
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("Belegpost/\(documentId)", isDirectory: true)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var files: [URL] = []
        var html = mail["html"] as? String ?? ""
        for a in mail["attachments"] as? [[String: Any]] ?? [] {
            guard let b64 = a["data"] as? String, let data = Data(base64Encoded: b64) else { continue }
            if let cid = a["contentId"] as? String, !cid.isEmpty {
                // Logo direkt ins HTML (AppleScript kann keine Content-ID setzen)
                let type = a["contentType"] as? String ?? "image/png"
                html = html.replacingOccurrences(of: "cid:" + cid, with: "data:\(type);base64,\(b64)")
                continue
            }
            let url = folder.appendingPathComponent(safe(a["name"] as? String ?? "Anhang"))
            try? data.write(to: url)
            files.append(url)
        }

        let to = mail["to"] as? [String] ?? [], cc = mail["cc"] as? [String] ?? [], bcc = mail["bcc"] as? [String] ?? []
        let subject = mail["subject"] as? String ?? ""
        let text = mail["text"] as? String ?? ""
        if html.isEmpty { html = htmlFromText(text) } // normale E-Mail (nur Text): Outlook für Mac erwartet HTML

        var result: Result
        if cfg.mailClient == "standard" {
            // Standard-Mailprogramm des Macs (Systemeinstellung „Standard-E-Mail-Programm“) über den Teilen-Dienst
            if let service = NSSharingService(named: .composeEmail) {
                service.recipients = to + cc + bcc
                service.subject = subject
                let items: [Any] = [text] + files.map { $0 as Any }
                if service.canPerform(withItems: items) {
                    service.perform(withItems: items)
                    result = Result(ok: true, message: "Im Mailprogramm geöffnet.")
                } else { result = Result(ok: false, message: "Das Standard-Mailprogramm kann keinen Entwurf mit Anhängen öffnen.") }
            } else { result = Result(ok: false, message: "Kein Standard-Mailprogramm eingerichtet.") }
        } else if NSWorkspace.shared.urlForApplication(withBundleIdentifier: outlookBundle) != nil {
            if let err = runOutlookScript(subject: subject, html: html, to: to, cc: cc, bcc: bcc, files: files) {
                // neues Outlook (kaum AppleScript) → .eml-Entwurf
                result = await openEml(session: s, base: base, token: token, folder: folder, documentId: documentId,
                                       note: "Outlook-Steuerung nicht möglich (\(err)) – ")
            } else { result = Result(ok: true, message: "In Outlook geöffnet.", viaOutlook: true, subject: subject) }
        } else if let err = runMailScript(subject: subject, text: text, to: to, cc: cc, bcc: bcc, files: files) {
            result = Result(ok: false, message: "Weder Outlook noch Apple Mail konnten geöffnet werden: \(err)")
        } else { result = Result(ok: true, message: "In Apple Mail geöffnet (Outlook ist nicht installiert).") }

        if result.ok { _ = try? await s.data(for: MelderClient.request(base + "/outlook", token: token, method: "POST")) }
        cleanup()
        return result
    }

    // MARK: Versandbestätigung

    /// true, wenn in „Gesendet“ eine Nachricht mit diesem Betreff liegt, die nach der Übergabe gesendet wurde.
    /// Outlook wird dafür nie gestartet – nur geprüft, wenn es läuft.
    static func wasSent(subject: String, since: Date) -> Bool {
        guard !subject.isEmpty,
              NSRunningApplication.runningApplications(withBundleIdentifier: outlookBundle).isEmpty == false else { return false }
        let seconds = max(60, Int(Date().timeIntervalSince(since)) + 60)
        let src = """
        tell application "Microsoft Outlook"
          set cutoff to (current date) - \(seconds)
          set n to 0
          try
            set n to count of (messages of sent mail folder whose subject is \(q(subject)) and time sent > cutoff)
          end try
          return n
        end tell
        """
        var error: NSDictionary?
        guard let script = NSAppleScript(source: src) else { return false }
        let r = script.executeAndReturnError(&error)
        return error == nil && r.int32Value > 0
    }

    // MARK: AppleScript

    private static func q(_ s: String) -> String {
        "\"" + s.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"") + "\""
    }

    /// nil = erfolgreich, sonst Fehlermeldung
    private static func run(_ source: String) -> String? {
        var error: NSDictionary?
        guard let script = NSAppleScript(source: source) else { return "Skript ungültig" }
        script.executeAndReturnError(&error)
        if let e = error {
            let num = e[NSAppleScript.errorNumber] as? Int ?? 0
            if num == -1743 { return "Zugriff verweigert – in Systemeinstellungen → Datenschutz & Sicherheit → Automation dem Belegpost Melder Outlook erlauben" }
            return e[NSAppleScript.errorMessage] as? String ?? "Fehler \(num)"
        }
        return nil
    }

    /// Reinen Text für Outlook als HTML (Zeilenumbrüche bleiben erhalten).
    static func htmlFromText(_ text: String) -> String {
        let escaped = text
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\n", with: "<br>")
        return "<html><body style=\"font-family:-apple-system,Helvetica,Arial,sans-serif;font-size:13px\">" + escaped + "</body></html>"
    }

    private static func runOutlookScript(subject: String, html: String, to: [String], cc: [String], bcc: [String], files: [URL]) -> String? {
        var s = "tell application \"Microsoft Outlook\"\n"
        s += "set m to make new outgoing message with properties {subject:\(q(subject)), content:\(q(html))}\n"
        for a in to { s += "make new to recipient at m with properties {email address:{address:\(q(a))}}\n" }
        for a in cc { s += "make new cc recipient at m with properties {email address:{address:\(q(a))}}\n" }
        for a in bcc { s += "make new bcc recipient at m with properties {email address:{address:\(q(a))}}\n" }
        for f in files { s += "make new attachment at m with properties {file:(POSIX file \(q(f.path)))}\n" }
        s += "open m\nactivate\nend tell"
        return run(s)
    }

    private static func runMailScript(subject: String, text: String, to: [String], cc: [String], bcc: [String], files: [URL]) -> String? {
        var s = "tell application \"Mail\"\n"
        s += "set m to make new outgoing message with properties {subject:\(q(subject)), content:\(q(text)), visible:true}\n"
        s += "tell m\n"
        for a in to { s += "make new to recipient at end of to recipients with properties {address:\(q(a))}\n" }
        for a in cc { s += "make new cc recipient at end of cc recipients with properties {address:\(q(a))}\n" }
        for a in bcc { s += "make new bcc recipient at end of bcc recipients with properties {address:\(q(a))}\n" }
        for f in files { s += "make new attachment with properties {file name:(POSIX file \(q(f.path)))} at after the last paragraph\n" }
        s += "end tell\nactivate\nend tell"
        return run(s)
    }

    // MARK: .eml-Entwurf

    private static func openEml(session: URLSession, base: String, token: String, folder: URL, documentId: String, note: String) async -> Result {
        do {
            let (data, res) = try await session.data(for: MelderClient.request(base + "/eml", token: token))
            guard (res as? HTTPURLResponse)?.statusCode == 200 else { return Result(ok: false, message: note + (MelderClient.errorText(data) ?? "Entwurf nicht verfügbar.")) }
            let url = folder.appendingPathComponent("Entwurf-\(safe(documentId)).eml")
            try data.write(to: url)
            if let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: outlookBundle) {
                let c = NSWorkspace.OpenConfiguration()
                try await NSWorkspace.shared.open([url], withApplicationAt: app, configuration: c)
            } else { NSWorkspace.shared.open(url) }
            return Result(ok: true, message: note + "Entwurf geöffnet.")
        } catch { return Result(ok: false, message: note + error.localizedDescription) }
    }

    private static func safe(_ n: String) -> String {
        let bad = CharacterSet(charactersIn: "/:\\?%*|\"<>")
        return String(n.unicodeScalars.map { bad.contains($0) ? "_" : Character($0) }.prefix(120))
    }

    /// Alte Entwurfsdateien (älter als 2 Tage) entfernen.
    private static func cleanup() {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("Belegpost", isDirectory: true)
        guard let items = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.contentModificationDateKey]) else { return }
        for i in items {
            if let d = try? i.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate, d < Date().addingTimeInterval(-2 * 86400) {
                try? FileManager.default.removeItem(at: i)
            }
        }
    }
}
