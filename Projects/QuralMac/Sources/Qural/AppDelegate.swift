import AppKit
import SwiftUI
import Carbon
import ScreenCaptureKit

// Значок в строке меню, меню и горячие клавиши.
//   ⌃⌥S — скриншот области      ⌃⌥F — скриншот экрана (сразу в папку и буфер)
//   ⌃⌥R — запись экрана / стоп  ⌃⌥A — запись области   ⌃⌥W — запись окна
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var status: NSStatusItem!
    private var tick: Timer?
    private var settingsWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Prefs.register()
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu
        updateStatus()

        let hk = HotKeys.shared, m = HotKeys.ctrlOpt
        hk.register(id: 1, keyCode: kVK_ANSI_S, modifiers: m) { [weak self] in self?.shotArea() }
        hk.register(id: 2, keyCode: kVK_ANSI_F, modifiers: m) { [weak self] in self?.shotScreen() }
        hk.register(id: 3, keyCode: kVK_ANSI_R, modifiers: m) { [weak self] in self?.toggleRecord() }
        hk.register(id: 4, keyCode: kVK_ANSI_A, modifiers: m) { [weak self] in self?.recordArea() }
        hk.register(id: 5, keyCode: kVK_ANSI_W, modifiers: m) { [weak self] in self?.recordWindow() }

        Recorder.shared.onChange = { [weak self] in self?.updateStatus() }
    }

    // ── значок: обычный — видоискатель; идёт запись — красная точка и время ──
    private func updateStatus() {
        guard let b = status.button else { return }
        if Recorder.shared.isRecording {
            b.image = NSImage(systemSymbolName: "record.circle.fill", accessibilityDescription: "Идёт запись")
            b.contentTintColor = .systemRed
            b.imagePosition = .imageLeading
            b.title = " " + elapsed()
            if tick == nil {
                tick = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
                    MainActor.assumeIsolated {
                        guard let self, let b = self.status.button else { return }
                        b.title = " " + self.elapsed()
                    }
                }
            }
        } else {
            tick?.invalidate(); tick = nil
            b.image = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "Qural")
            b.contentTintColor = nil
            b.title = ""
        }
    }

    private func elapsed() -> String {
        let t = Int(Date().timeIntervalSince(Recorder.shared.startedAt ?? Date()))
        return t >= 3600 ? String(format: "%d:%02d:%02d", t / 3600, t / 60 % 60, t % 60) : String(format: "%02d:%02d", t / 60, t % 60)
    }

    // ── меню (собирается заново при каждом открытии) ──
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let rec = Recorder.shared.isRecording
        if rec {
            menu.addItem(item("Остановить запись", "stop.circle", #selector(toggleRecord), "r"))
            menu.addItem(.separator())
        }
        menu.addItem(item("Скриншот области", "crop", #selector(shotArea), "s"))
        menu.addItem(item("Скриншот экрана", "macwindow", #selector(shotScreen), "f"))
        menu.addItem(.separator())
        if !rec {
            menu.addItem(item("Записать экран", "record.circle", #selector(toggleRecord), "r"))
            menu.addItem(item("Записать область…", "rectangle.dashed", #selector(recordArea), "a"))
            menu.addItem(item("Записать окно…", "macwindow.on.rectangle", #selector(recordWindow), "w"))
            menu.addItem(.separator())
        }
        menu.addItem(item("Папка скриншотов", "folder", #selector(openShots), nil))
        menu.addItem(item("Папка записей", "film", #selector(openVideos), nil))
        menu.addItem(.separator())
        let s = item("Настройки…", "gearshape", #selector(openSettings), nil)
        s.keyEquivalent = ","; s.keyEquivalentModifierMask = [.command]
        menu.addItem(s)
        let q = NSMenuItem(title: "Выйти из Qural", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(q)
    }

    private func item(_ title: String, _ symbol: String, _ action: Selector, _ key: String?) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key ?? "")
        i.target = self
        i.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)
        if key != nil { i.keyEquivalentModifierMask = [.control, .option] }   // подсказка: та же горячая клавиша
        return i
    }

    // ── действия ──
    @objc func shotArea() {
        if ShotSession.current != nil { return }
        Task { @MainActor in await ShotSession.start() }
    }

    @objc func shotScreen() {
        Task { @MainActor in await Snapshot.screenUnderMouse() }
    }

    @objc func toggleRecord() {
        if Countdown.active { Countdown.cancelActive(); return }
        if Recorder.shared.isRecording { Recorder.shared.stop() } else { Recorder.shared.recordScreen() }
    }

    @objc func recordArea() {
        if Recorder.shared.isRecording { Recorder.shared.stop() } else { Recorder.shared.recordArea() }
    }

    @objc func recordWindow() {
        if Recorder.shared.isRecording { Recorder.shared.stop() } else { Recorder.shared.recordWindow() }
    }

    @objc func openShots() { open(Prefs.shotsDir) }
    @objc func openVideos() { open(Prefs.videosDir) }

    private func open(_ dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(dir)
    }

    @objc func openSettings() {
        if settingsWindow == nil {
            let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
            w.title = "Qural — настройки"
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.center()
            settingsWindow = w
        }
        NSApp.activate()
        settingsWindow?.makeKeyAndOrderFront(nil)
    }
}

// Скриншот всего экрана под курсором — сразу в папку (и в буфер, если включено).
@MainActor
enum Snapshot {
    static func screenUnderMouse() async {
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let screen = NSScreen.underMouse
            guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) else { return }
            let cfg = SCStreamConfiguration()
            cfg.width = Int(screen.frame.width * screen.backingScaleFactor)
            cfg.height = Int(screen.frame.height * screen.backingScaleFactor)
            cfg.showsCursor = Prefs.shotCursor
            cfg.captureResolution = .best
            let img = try await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(display: d, excludingWindows: []), configuration: cfg)
            let dir = Prefs.shotsDir
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let ext = Prefs.shotFormat
            let url = dir.appendingPathComponent("Qural \(Prefs.stamp()).\(ext)")
            let rep = NSBitmapImageRep(cgImage: img)
            let data = ext == "jpg" ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
                                    : rep.representation(using: .png, properties: [:])
            try data?.write(to: url)
            if Prefs.copyOnSave { Clipboard.put(img) }
            Toast.show("Экран сохранён: \(url.lastPathComponent)", action: "Показать") {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        } catch {
            Permissions.screenDenied()
        }
    }
}
