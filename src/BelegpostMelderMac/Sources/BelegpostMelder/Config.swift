import Foundation
import Security

/// Einstellungen des Melders (UserDefaults); der Zugangsschlüssel liegt im Schlüsselbund.
final class MelderConfig: ObservableObject {
    static let shared = MelderConfig()
    private let d = UserDefaults.standard

    @Published var server: String? { didSet { d.set(server, forKey: "server") } }
    @Published var thumbprint: String? { didSet { d.set(thumbprint, forKey: "thumbprint") } }
    @Published var portalTitle: String? { didSet { d.set(portalTitle, forKey: "portalTitle") } }
    @Published var userName: String? { didSet { d.set(userName, forKey: "userName") } }
    /// "portal", "outlook" oder "hinweis"
    @Published var onNewDocument: String { didSet { d.set(onNewDocument, forKey: "onNewDocument") } }
    @Published var outlookAllowed: Bool { didSet { d.set(outlookAllowed, forKey: "outlookAllowed") } }
    @Published var lastDocumentPath: String? { didSet { d.set(lastDocumentPath, forKey: "lastDocumentPath") } }
    @Published var lastDocumentId: String? { didSet { d.set(lastDocumentId, forKey: "lastDocumentId") } }
    var lastSeq: Int64? {
        get { d.object(forKey: "lastSeq") as? Int64 }
        set { d.set(newValue, forKey: "lastSeq") }
    }

    private init() {
        server = d.string(forKey: "server")
        thumbprint = d.string(forKey: "thumbprint")
        portalTitle = d.string(forKey: "portalTitle")
        userName = d.string(forKey: "userName")
        onNewDocument = d.string(forKey: "onNewDocument") ?? "portal"
        outlookAllowed = d.bool(forKey: "outlookAllowed")
        lastDocumentPath = d.string(forKey: "lastDocumentPath")
        lastDocumentId = d.string(forKey: "lastDocumentId")
    }

    var paired: Bool { server != nil && token != nil }

    /// Wirksame Aktion (Outlook nur mit Freigabe im Portal).
    var action: String { onNewDocument == "outlook" && !outlookAllowed ? "portal" : onNewDocument }

    // MARK: Schlüsselbund

    private let service = "at.belegpost.melder"

    var token: String? {
        get {
            let q: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: "token", kSecReturnData as String: true]
            var out: AnyObject?
            guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess, let data = out as? Data else { return nil }
            return String(data: data, encoding: .utf8)
        }
        set {
            let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                       kSecAttrAccount as String: "token"]
            SecItemDelete(base as CFDictionary)
            if let v = newValue, let data = v.data(using: .utf8) {
                var add = base
                add[kSecValueData as String] = data
                add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
                SecItemAdd(add as CFDictionary, nil)
            }
            objectWillChange.send()
        }
    }
}

/// Kopplungscode aus dem Portal (Base64url-JSON) oder Link „belegpost-melder:koppeln?c=…“.
struct PairingData {
    let server: String, token: String, thumbprint: String?, title: String?, user: String?

    static func parse(_ input: String?) -> PairingData? {
        guard var s = input?.trimmingCharacters(in: .whitespacesAndNewlines), !s.isEmpty else { return nil }
        if s.lowercased().hasPrefix("belegpost-melder:"), let r = s.range(of: "c=") { s = String(s[r.upperBound...]) }
        s = (s.removingPercentEncoding ?? s).trimmingCharacters(in: CharacterSet(charactersIn: "/ \n"))
        var b64 = s.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while b64.count % 4 != 0 { b64 += "=" }
        guard let data = Data(base64Encoded: b64),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let server = obj["s"] as? String, let token = obj["t"] as? String else { return nil }
        return PairingData(server: server.trimmingCharacters(in: CharacterSet(charactersIn: "/")), token: token,
                           thumbprint: obj["f"] as? String, title: obj["n"] as? String, user: obj["u"] as? String)
    }
}
