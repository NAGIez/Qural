import SwiftUI
import ServiceManagement
import AppKit

// Окно настроек. Ключи — те же, что в Prefs.
struct SettingsView: View {
    @AppStorage("shotFormat") private var shotFormat = "png"
    @AppStorage("shotCursor") private var shotCursor = false
    @AppStorage("copyOnSave") private var copyOnSave = true
    @AppStorage("sysAudio") private var sysAudio = true
    @AppStorage("mic") private var mic = false
    @AppStorage("codec") private var codec = "hevc"
    @AppStorage("fps") private var fps = 30
    @AppStorage("cursor") private var cursor = true
    @AppStorage("countdown") private var countdown = true
    @State private var login = SMAppService.mainApp.status == .enabled
    @State private var loginError = ""

    var body: some View {
        Form {
            Section("Скриншоты") {
                Picker("Формат", selection: $shotFormat) {
                    Text("PNG — без потерь").tag("png")
                    Text("JPEG — меньше файл").tag("jpg")
                }
                Toggle("Курсор на скриншоте", isOn: $shotCursor)
                Toggle("При сохранении ещё и копировать в буфер", isOn: $copyOnSave)
                LabeledContent("Папка") {
                    Button("Открыть") { openDir(Prefs.shotsDir) }
                }
            }
            Section("Запись экрана") {
                Toggle("Звук системы", isOn: $sysAudio)
                Toggle("Микрофон", isOn: $mic)
                Picker("Кодек", selection: $codec) {
                    Text("HEVC (H.265) — меньше файл").tag("hevc")
                    Text("H.264 — открывается везде").tag("h264")
                }
                Picker("Кадров в секунду", selection: $fps) {
                    Text("24").tag(24)
                    Text("30").tag(30)
                    Text("60").tag(60)
                }
                Toggle("Курсор на записи", isOn: $cursor)
                Toggle("Отсчёт 3-2-1 перед записью", isOn: $countdown)
                LabeledContent("Папка") {
                    Button("Открыть") { openDir(Prefs.videosDir) }
                }
            }
            Section("Горячие клавиши") {
                LabeledContent("Скриншот области", value: "⌃⌥S")
                LabeledContent("Скриншот экрана", value: "⌃⌥F")
                LabeledContent("Записать экран / остановить", value: "⌃⌥R")
                LabeledContent("Записать область", value: "⌃⌥A")
                LabeledContent("Записать окно", value: "⌃⌥W")
                Text("В окне скриншота: ⌘C или Enter — скопировать, ⌘S — сохранить, ⌘Z — отменить, Esc — закрыть.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            Section {
                Toggle("Запускать при входе в систему", isOn: Binding(get: { login }, set: setLogin))
                if !loginError.isEmpty { Text(loginError).font(.footnote).foregroundStyle(.red) }
            }
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 620)
    }

    private func openDir(_ dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        NSWorkspace.shared.open(dir)
    }

    private func setLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            loginError = ""
        } catch {
            loginError = "Не получилось: \(error.localizedDescription)"
        }
        login = SMAppService.mainApp.status == .enabled
    }
}
