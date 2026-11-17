// swift-tools-version:5.9
// HB AI · Smart DNS для macOS. Сборка: bash mac/build.sh (на Mac или в GitHub Actions, .github/workflows/mac.yml).
import PackageDescription

let package = Package(
    name: "HBAISmartDNS",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "HBAISmartDNS",
            path: "Sources/HBAISmartDNS"
        ),
        .testTarget(
            name: "HBAISmartDNSTests",
            dependencies: ["HBAISmartDNS"],
            path: "Tests/HBAISmartDNSTests"
        )
    ]
)
