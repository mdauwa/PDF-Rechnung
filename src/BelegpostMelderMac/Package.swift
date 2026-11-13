// swift-tools-version:5.9
// Belegpost Melder für macOS: Menüleisten-App, meldet neue eigene Belege und öffnet sie im Browser oder als Mail-Entwurf in Outlook.
import PackageDescription

let package = Package(
    name: "BelegpostMelder",
    defaultLocalization: "de",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "BelegpostMelder", path: "Sources/BelegpostMelder")
    ]
)
