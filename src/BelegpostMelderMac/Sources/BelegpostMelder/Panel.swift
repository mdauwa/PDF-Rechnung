import SwiftUI
import AppKit

/// Fenster des Menüleisten-Symbols: Kopf, Status, Verhalten bei neuen Belegen, Verbindung.
struct MelderPanel: View {
    @ObservedObject var model: MelderModel
    @ObservedObject var client: MelderClient
    @ObservedObject var cfg: MelderConfig
    @State private var loginItem = LoginItem.enabled
    @State private var showPair = false

    private let accent = Color(red: 0x1F / 255, green: 0x5F / 255, blue: 0xBF / 255)
    private let dark = Color(red: 0x16 / 255, green: 0x20 / 255, blue: 0x2E / 255)

    var body: some View {
        VStack(spacing: 0) {
            header
            VStack(alignment: .leading, spacing: 12) {
                statusCard
                if cfg.paired && !showPair { optionsCard }
                connectCard
                HStack {
                    Spacer()
                    Button("Beenden") { NSApp.terminate(nil) }.buttonStyle(.link).foregroundStyle(.secondary)
                }
            }
            .padding(14)
        }
        .frame(width: 360)
        .background(Color(nsColor: .windowBackgroundColor))
    }

    private var header: some View {
        HStack(spacing: 12) {
            ZStack {
                RoundedRectangle(cornerRadius: 9).fill(.white).frame(width: 40, height: 40)
                Image(systemName: "doc.text").font(.system(size: 20, weight: .semibold)).foregroundStyle(accent)
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("Belegpost Melder").font(.headline).foregroundStyle(.white)
                Text(cfg.portalTitle.flatMap { $0 == "Belegpost" ? nil : $0 } ?? "Neue Belege sofort öffnen")
                    .font(.subheadline).foregroundStyle(.white.opacity(0.75))
            }
            Spacer()
        }
        .padding(16)
        .background(dark)
    }

    private var pill: (String, Color) {
        switch client.state {
        case .connected: return ("verbunden", .green)
        case .connecting: return ("verbinde …", accent)
        case .offline: return ("keine Verbindung", .orange)
        case .rejected: return ("nicht mehr gekoppelt", .red)
        case .notPaired: return ("nicht verbunden", .secondary)
        }
    }

    private var statusCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 6) {
                    Circle().fill(pill.1).frame(width: 7, height: 7)
                    Text(pill.0).font(.caption.weight(.medium)).foregroundStyle(pill.1)
                }
                .padding(.horizontal, 10).padding(.vertical, 4)
                .background(Capsule().fill(pill.1.opacity(0.12)))
                Text(cfg.paired ? "Angemeldet als \(cfg.userName ?? "")" : "Noch nicht mit dem Portal verbunden").font(.headline)
                if cfg.paired, let s = cfg.server { Text(s).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
                Text(client.statusText).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                if cfg.paired {
                    HStack {
                        Button("Portal öffnen") { model.open("/") }.frame(maxWidth: .infinity)
                        Button("Letzten Beleg") { if let p = cfg.lastDocumentPath { model.open(p) } }
                            .disabled(cfg.lastDocumentPath == nil).frame(maxWidth: .infinity)
                    }
                    .controlSize(.large)
                    if cfg.outlookAllowed, let id = cfg.lastDocumentId {
                        Button("Letzten Beleg in Outlook") { model.openOutlook(id, title: nil) }.buttonStyle(.link)
                    }
                }
            }
        }
    }

    private var optionsCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                Text("Wenn ein neuer Beleg von dir kommt").font(.subheadline.weight(.medium))
                Picker("", selection: Binding(get: { cfg.action }, set: { model.setAction($0) })) {
                    Text("Portal").tag("portal")
                    if cfg.outlookAllowed { Text("Outlook").tag("outlook") }
                    Text("Nur Hinweis").tag("hinweis")
                }
                .pickerStyle(.segmented).labelsHidden()
                Text(hint).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Divider()
                Toggle(isOn: Binding(get: { loginItem }, set: { LoginItem.enable($0); loginItem = LoginItem.enabled })) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Bei der Anmeldung starten")
                        Text("Melder läuft unauffällig in der Menüleiste").font(.caption).foregroundStyle(.secondary)
                    }
                }
                .toggleStyle(.switch)
            }
        }
    }

    private var hint: String {
        switch cfg.action {
        case "outlook": return "Öffnet die E-Mail mit Text und Anhängen als Entwurf in Outlook – du prüfst und sendest selbst. Kein automatischer Versand."
        case "hinweis": return "Zeigt nur eine Mitteilung mit „Öffnen“\(cfg.outlookAllowed ? " und „In Outlook“" : "")."
        default: return "Öffnet die Ansicht des Belegs im Browser."
        }
    }

    private var connectCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 10) {
                if !cfg.paired || showPair {
                    Text("Mit dem Portal verbinden").font(.headline)
                    Text("Im Portal links „Melder & Hinweise“ öffnen und auf „Melder verbinden“ klicken. Dann „Im Melder übernehmen“ – oder den Code hier einfügen.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    TextField("Kopplungscode", text: $model.pairCode, axis: .vertical)
                        .lineLimit(3...4).font(.system(.caption, design: .monospaced)).textFieldStyle(.roundedBorder)
                    HStack {
                        Button("Einfügen") { model.pairCode = NSPasteboard.general.string(forType: .string) ?? "" }
                        if cfg.paired { Button("Abbrechen") { showPair = false } }
                        Spacer()
                        Button(model.busy ? "Verbinde …" : "Verbinden") { model.pair(model.pairCode); showPair = false }
                            .buttonStyle(.borderedProminent).tint(accent)
                            .disabled(model.busy || PairingData.parse(model.pairCode) == nil)
                    }
                } else {
                    Text("Verbindung").font(.headline)
                    Text("Dieser Mac ist mit deinem Portal-Konto verbunden.").font(.caption).foregroundStyle(.secondary)
                    HStack {
                        Button("Neu verbinden") { showPair = true }.buttonStyle(.link)
                        Spacer()
                        Button("Trennen", role: .destructive) { model.disconnect() }.buttonStyle(.link)
                    }
                }
                if let m = model.message { Text(m).font(.caption).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true) }
            }
        }
    }
}

/// Weiße Karte mit feinem Rand – wie im Portal.
struct Card<Content: View>: View {
    @ViewBuilder var content: Content
    var body: some View {
        content
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(14)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .controlBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.primary.opacity(0.08)))
    }
}
