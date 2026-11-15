import SwiftUI
import AppKit
import UserNotifications
import ServiceManagement

/// Belegpost Melder für macOS – lebt in der Menüleiste (kein Dock-Symbol, LSUIElement).
@main
struct BelegpostMelderApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate

    var body: some Scene {
        MenuBarExtra {
            MelderPanel(model: delegate.model, client: delegate.model.client, cfg: delegate.model.cfg)
        } label: {
            MenuIcon(client: delegate.model.client)
        }
        .menuBarExtraStyle(.window)
    }
}

@MainActor
final class MelderModel: ObservableObject {
    let cfg = MelderConfig.shared
    let client = MelderClient()
    @Published var pairCode = ""
    @Published var busy = false
    @Published var message: String?

    private var sentTimer: Timer?
    private var tasksRunning = false

    init() {
        client.onEvent = { [weak self] e in self?.onBeleg(e) }
        client.onConnected = { [weak self] in Task { await self?.runTasks() } }
        if cfg.paired { client.start() }
        // Versandbestätigung aus Outlook: jede Minute prüfen, solange Entwürfe offen sind
        sentTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in
            Task { @MainActor in await self?.checkSent() }
        }
    }

    func setMapi(_ mode: String) {
        Task { if !(await client.setMapiMode(mode)) { message = "Einstellung konnte nicht gespeichert werden – keine Verbindung?" } }
    }

    /// Aufträge vom Server (z. B. E-Mails aus dem ERP über MAPI) abholen und ausführen.
    func runTasks() async {
        guard !tasksRunning, cfg.paired else { return }
        tasksRunning = true
        defer { tasksRunning = false }
        for t in await client.pendingTasks() {
            guard await client.claimTask(t.id) else { continue }
            if t.kind == "outlook", let id = t.vorgangId {
                if cfg.outlookAllowed { await openOutlookNow(id, title: "E-Mail aus dem ERP") }
                else { open("/#/vorgang?id=\(id)") }
            }
            await client.completeTask(t.id)
        }
    }

    private func checkSent() async {
        var pending = cfg.pendingSent
        guard !pending.isEmpty, cfg.paired else { return }
        for (id, info) in pending {
            let since = info["at"] as? Date ?? Date()
            if Date().timeIntervalSince(since) > 7 * 24 * 3600 { pending[id] = nil; continue }
            if OutlookBridge.wasSent(subject: info["subject"] as? String ?? "", since: since),
               await client.reportSent(id, at: Date()) { pending[id] = nil }
        }
        cfg.pendingSent = pending
    }

    func open(_ path: String) {
        guard let server = cfg.server, let url = URL(string: server + (path.hasPrefix("/") ? path : "/" + path)) else { return }
        NSWorkspace.shared.open(url)
    }

    func setAction(_ a: String) {
        cfg.onNewDocument = a
        Task { await client.sendSettings() }
    }

    func openOutlook(_ id: String, title: String?) {
        Task { await openOutlookNow(id, title: title) }
    }

    func openOutlookNow(_ id: String, title: String?) async {
        let r = await OutlookBridge.openDraft(documentId: id)
        if !r.ok {
            Notifier.show(title: title ?? "Outlook", body: r.message, id: id, path: "/#/vorgang?id=\(id)", mail: false)
            await client.reportOutlookError(id, r.message)
        } else if r.viaOutlook {
            var p = cfg.pendingSent
            p[id] = ["subject": r.subject, "at": Date()]
            cfg.pendingSent = p
        }
    }

    private func onBeleg(_ e: BelegEvent) {
        if e.kind == "outlook" { openOutlook(e.id, title: e.title); return } // aus dem Portal: „In Outlook öffnen“
        if e.kind == "auftrag" { Task { await runTasks() }; return }       // z. B. MAPI-Mail aus dem ERP
        if e.kind == "einstellung" {                                       // z. B. vom Administrator überrollt
            Task { await client.refreshInfo() }
            if let t = e.text, !t.isEmpty { Notifier.show(title: "Einstellung geändert", body: t, id: nil, path: nil, mail: false) }
            return
        }
        let title = e.title.isEmpty ? "Neuer Beleg" : e.title
        let text = e.text ?? [
            "bereit": "Bereit zum Versand.", "fehler": "Bitte prüfen – der Beleg hat einen Fehler.",
            "warnung": "Bitte prüfen.", "ohneVersand": "Ohne E-Mail-Versand."
        ][e.status] ?? "Neuer Beleg."
        let outlookOk = e.mail && cfg.outlookAllowed
        switch cfg.action {
        case "outlook" where outlookOk:
            openOutlook(e.id, title: title)
            Notifier.show(title: title, body: text + "\nAls E-Mail in Outlook geöffnet.", id: e.id, path: e.path, mail: false)
        case "portal":
            open(e.path)
            Notifier.show(title: title, body: text + "\nIm Browser geöffnet.", id: e.id, path: e.path, mail: outlookOk)
        default:
            Notifier.show(title: title, body: text, id: e.id, path: e.path, mail: outlookOk)
        }
    }

    func pair(_ input: String) {
        guard let p = PairingData.parse(input) else { message = "Der Code ist ungültig. Bitte im Portal neu erzeugen."; return }
        busy = true; message = "Prüfe Verbindung zu \(p.server) …"
        Task {
            defer { busy = false }
            do {
                let (user, title, outlook) = try await MelderClient.hello(server: p.server, token: p.token, thumbprint: p.thumbprint)
                client.stop()
                cfg.server = p.server; cfg.token = p.token; cfg.thumbprint = p.thumbprint
                cfg.userName = user; cfg.portalTitle = title; cfg.outlookAllowed = outlook
                cfg.lastSeq = nil; cfg.lastDocumentPath = nil; cfg.lastDocumentId = nil
                pairCode = ""; message = nil
                LoginItem.enable(true)
                client.start()
                Notifier.show(title: "Melder verbunden", body: "Angemeldet als \(user). Neu gedruckte Belege erscheinen ab jetzt hier.", id: nil, path: nil, mail: false)
            } catch { message = error.localizedDescription }
        }
    }

    func disconnect() {
        client.stop()
        cfg.token = nil; cfg.lastSeq = nil; cfg.lastDocumentPath = nil; cfg.lastDocumentId = nil
        client.start() // meldet „nicht verbunden“
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    @MainActor lazy var model = MelderModel()

    func applicationWillFinishLaunching(_ notification: Notification) {
        // Link „belegpost-melder:koppeln?c=…“ aus dem Portal
        NSAppleEventManager.shared().setEventHandler(self, andSelector: #selector(handleURL(_:reply:)),
                                                     forEventClass: AEEventClass(kInternetEventClass), andEventID: AEEventID(kAEGetURL))
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        Notifier.setup(delegate: self)
        Task { @MainActor in _ = self.model }
    }

    @objc func handleURL(_ event: NSAppleEventDescriptor, reply: NSAppleEventDescriptor) {
        guard let s = event.paramDescriptor(forKeyword: keyDirectObject)?.stringValue else { return }
        Task { @MainActor in self.model.pair(s) }
    }

    // Mitteilungen auch anzeigen, wenn die App „vorne“ ist
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .sound, .list])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        let info = response.notification.request.content.userInfo
        let id = info["id"] as? String, path = info["path"] as? String
        Task { @MainActor in
            switch response.actionIdentifier {
            case Notifier.outlookAction: if let id { self.model.openOutlook(id, title: response.notification.request.content.title) }
            default: if let path { self.model.open(path) }
            }
            completionHandler()
        }
    }
}

/// macOS-Mitteilungen mit „Öffnen“ und „In Outlook“.
enum Notifier {
    static let openAction = "OPEN", outlookAction = "OUTLOOK"

    static func setup(delegate: UNUserNotificationCenterDelegate) {
        let c = UNUserNotificationCenter.current()
        c.delegate = delegate
        let open = UNNotificationAction(identifier: openAction, title: "Öffnen", options: [.foreground])
        let outlook = UNNotificationAction(identifier: outlookAction, title: "In Outlook", options: [.foreground])
        c.setNotificationCategories([
            UNNotificationCategory(identifier: "beleg", actions: [open], intentIdentifiers: []),
            UNNotificationCategory(identifier: "belegMail", actions: [open, outlook], intentIdentifiers: [])
        ])
        c.requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    static func show(title: String, body: String, id: String?, path: String?, mail: Bool) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.subtitle = "Belegpost"
        content.body = body
        content.sound = .default
        content.categoryIdentifier = mail ? "belegMail" : "beleg"
        var info: [String: String] = [:]
        if let id { info["id"] = id }
        if let path { info["path"] = path }
        content.userInfo = info
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
}

/// Start bei der Anmeldung (macOS 13+: SMAppService).
enum LoginItem {
    static var enabled: Bool { SMAppService.mainApp.status == .enabled }
    static func enable(_ on: Bool) {
        do { if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } } catch { }
    }
}

/// Symbol in der Menüleiste: gefüllt = verbunden, Ausrufezeichen = Problem.
struct MenuIcon: View {
    @ObservedObject var client: MelderClient
    var body: some View {
        switch client.state {
        case .connected: Image(systemName: "doc.text.fill")
        case .rejected, .offline: Image(systemName: "exclamationmark.triangle")
        default: Image(systemName: "doc.text")
        }
    }
}
