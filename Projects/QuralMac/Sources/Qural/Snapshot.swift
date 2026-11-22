import AppKit
import ScreenCaptureKit

// Снимок экрана и сохранение в папку. Каждый шаг — в журнал (~/Library/Logs/Qural/qural.log).
@MainActor
enum Snapshot {
    /// Экран целиком, в точных пикселях.
    static func capture(_ d: SCDisplay, screen: NSScreen) async throws -> CGImage {
        let cfg = SCStreamConfiguration()
        cfg.width = Int(screen.frame.width * screen.backingScaleFactor)
        cfg.height = Int(screen.frame.height * screen.backingScaleFactor)
        cfg.showsCursor = Prefs.shotCursor
        cfg.captureResolution = .best
        return try await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(display: d, excludingWindows: []), configuration: cfg)
    }

    /// Сохранить в ~/Pictures/Qural (PNG или JPEG). nil — не вышло (уже показано уведомление).
    static func save(_ img: CGImage) -> URL? {
        let dir = Prefs.shotsDir
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let ext = Prefs.shotFormat
            let url = dir.appendingPathComponent("Qural \(Prefs.stamp()).\(ext)")
            let rep = NSBitmapImageRep(cgImage: img)
            guard let data = ext == "jpg" ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
                                          : rep.representation(using: .png, properties: [:]) else {
                throw NSError(domain: "Qural", code: 1, userInfo: [NSLocalizedDescriptionKey: "не удалось закодировать картинку"])
            }
            try data.write(to: url)
            Log.write("сохранено: \(url.path)")
            return url
        } catch {
            Log.write("сохранение: ошибка \(error)")
            Toast.show("Не сохранилось: \(error.localizedDescription)")
            return nil
        }
    }

    /// ⌃⌥F — экран под курсором сразу в папку (и в буфер, если включено).
    static func screenUnderMouse() async {
        let screen = NSScreen.underMouse
        Log.write("скриншот экрана: экран \(screen.displayID), \(Int(screen.frame.width))×\(Int(screen.frame.height))")
        let content: SCShareableContent
        do {
            content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        } catch {
            Log.write("скриншот экрана: нет доступа \(error)")
            Permissions.screenDenied()
            return
        }
        guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) ?? content.displays.first else {
            Log.write("скриншот экрана: экран не найден")
            Toast.show("Не нашёл экран для снимка")
            return
        }
        do {
            let img = try await capture(d, screen: screen)
            guard let url = save(img) else { return }
            if Prefs.copyOnSave { Clipboard.put(img) }
            Toast.show("Экран сохранён: \(url.lastPathComponent)", action: "Показать") {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        } catch {
            Log.write("скриншот экрана: ошибка снимка \(error)")
            Toast.show("Скриншот не получился: \(error.localizedDescription)")
        }
    }
}
