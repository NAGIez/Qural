import AppKit

// Точка входа: программа без значка в Dock, только в строке меню.
@main
enum QuralMain {
    // NSApplication держит делегата слабой ссылкой — храним его сами, иначе в оптимизированной сборке
    // он освобождается сразу после присваивания и программа запускается «пустой» (без значка и клавиш).
    @MainActor static var delegate: AppDelegate?

    @MainActor static func main() {
        let app = NSApplication.shared
        let d = AppDelegate()
        delegate = d
        app.delegate = d
        app.setActivationPolicy(.accessory)
        withExtendedLifetime(d) { app.run() }
    }
}

extension NSScreen {
    var displayID: CGDirectDisplayID {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
    }

    /// Экран, на котором сейчас курсор.
    static var underMouse: NSScreen {
        let p = NSEvent.mouseLocation
        return NSScreen.screens.first { NSMouseInRect(p, $0.frame, false) } ?? NSScreen.main ?? NSScreen.screens[0]
    }
}

// Журнал: ~/Library/Logs/Qural/qural.log (виден и в «Консоли»).
enum Log {
    static let url: URL = {
        let dir = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs/Qural", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent("qural.log")
    }()

    static func write(_ text: String) {
        let line = ISO8601DateFormatter().string(from: Date()) + " " + text + "\n"
        NSLog("Qural: %@", text)
        guard let data = line.data(using: .utf8) else { return }
        if let h = try? FileHandle(forWritingTo: url) {
            defer { try? h.close() }
            _ = try? h.seekToEnd()
            try? h.write(contentsOf: data)
        } else {
            try? data.write(to: url)
        }
    }
}
