// swift-tools-version:5.10
// Qural для Mac: программа в строке меню — скриншоты с рисованием и запись экрана.
// Сборка: ./build.sh (нужны Xcode или Command Line Tools; macOS 15+, Apple Silicon).
import PackageDescription

let package = Package(
    name: "Qural",
    platforms: [.macOS("15.0")],
    targets: [
        .executableTarget(
            name: "Qural",
            path: "Sources/Qural",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("Carbon"),
                .linkedFramework("CoreImage"),
                .linkedFramework("ServiceManagement"),
            ]
        )
    ]
)
