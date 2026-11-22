import SwiftUI
import ServiceManagement
import AppKit

// Окно настроек. Ключи — те же, что в Prefs.
struct SettingsView: View {
    @AppStorage("shotFormat") private var shotFormat = "png"
    @AppStorage("shotCursor") private var shotCursor = false
    @AppStorage("copyOnSave") private var copyOnSave = true
    @AppStorage("highlightBlur") private var highlightBlur = false
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
                Picker("Подсветка области", selection: $highlightBlur) {
                    Text("Затемнять вокруг").tag(false)
                    Text("Размывать вокруг").tag(true)
                }
                LabeledContent("Папка") {
                    Button("Открыть") {
                        try? FileManager.default.createDirectory(at: Prefs.shotsDir, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(Prefs.shotsDir)
                    }
                }
            }
            Section("Горячие клавиши") {
                LabeledContent("Скриншот области", value: "⌃⌥S")
                LabeledContent("Скриншот экрана — сразу в папку", value: "⌃⌥F")
                Text("В окне скриншота: щелчок — окно под курсором или весь экран; ⌘C или Enter — скопировать, ⌘S — сохранить, "
                     + "⌘Z — отменить, C — скопировать цвет под курсором, колёсико — толщина / размер шрифта, Esc — закрыть. "
                     + "«Рука»: перетащить пометку, ручки — размер, двойной щелчок по тексту — править, Delete — удалить.")
                    .font(.footnote).foregroundStyle(.secondary)
            }
            Section {
                Toggle("Запускать при входе в систему", isOn: Binding(get: { login }, set: setLogin))
                if !loginError.isEmpty { Text(loginError).font(.footnote).foregroundStyle(.red) }
                LabeledContent("Журнал") {
                    Button("Показать") { NSWorkspace.shared.activateFileViewerSelecting([Log.url]) }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 520)
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
