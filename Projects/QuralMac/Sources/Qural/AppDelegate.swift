import AppKit
import SwiftUI
import Carbon

// Значок в строке меню, меню и горячие клавиши.
//   ⌃⌥S — скриншот области (рисование, подсветка, «Рука»)
//   ⌃⌥F — скриншот экрана под курсором сразу в папку и буфер
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var status: NSStatusItem!
    private var settingsWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        Prefs.register()
        status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        status.button?.image = NSImage(systemSymbolName: "camera.viewfinder", accessibilityDescription: "Qural")
        let menu = NSMenu()
        menu.delegate = self
        status.menu = menu

        let hk = HotKeys.shared, m = HotKeys.ctrlOpt
        var failed: [String] = []
        if !hk.register(id: 1, keyCode: kVK_ANSI_S, modifiers: m, { [weak self] in self?.shotArea() }) { failed.append("⌃⌥S") }
        if !hk.register(id: 2, keyCode: kVK_ANSI_F, modifiers: m, { [weak self] in self?.shotScreen() }) { failed.append("⌃⌥F") }

        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        Log.write("запуск \(version), macOS \(ProcessInfo.processInfo.operatingSystemVersionString), значок: \(status.button != nil), "
                  + "клавиши: \(failed.isEmpty ? "все заняты Qural" : "не удалось " + failed.joined(separator: ", "))")
        if !Prefs.d.bool(forKey: "launchedBefore") {
            Prefs.d.set(true, forKey: "launchedBefore")
            openSettings()
        }
        if failed.isEmpty {
            Toast.show("Qural работает — значок в строке меню · ⌃⌥S область · ⌃⌥F весь экран")
        } else {
            Toast.show("Клавиши \(failed.joined(separator: ", ")) заняты другой программой — скриншот есть в меню Qural")
        }
    }

    /// Qural уже запущен и его открыли ещё раз (Finder, Launchpad, Spotlight) — показать настройки.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openSettings()
        return false
    }

    // ── меню (собирается заново при каждом открытии) ──
    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.addItem(item("Скриншот области", "crop", #selector(shotArea), "s"))
        menu.addItem(item("Скриншот экрана", "macwindow", #selector(shotScreen), "f"))
        menu.addItem(.separator())
        menu.addItem(item("Папка скриншотов", "folder", #selector(openShots), nil))
        menu.addItem(.separator())
        let s = item("Настройки…", "gearshape", #selector(openSettings), nil)
        s.keyEquivalent = ","; s.keyEquivalentModifierMask = [.command]
        menu.addItem(s)
        menu.addItem(NSMenuItem(title: "Выйти из Qural", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
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
        Log.write("⌃⌥S / меню: скриншот области")
        if ShotSession.current != nil { return }
        Task { @MainActor in await ShotSession.start() }
    }

    @objc func shotScreen() {
        Log.write("⌃⌥F / меню: скриншот экрана")
        Task { @MainActor in await Snapshot.screenUnderMouse() }
    }

    @objc func openShots() {
        try? FileManager.default.createDirectory(at: Prefs.shotsDir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(Prefs.shotsDir)
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
