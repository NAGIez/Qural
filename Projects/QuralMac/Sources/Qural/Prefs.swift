import Foundation

// Настройки (UserDefaults). Те же ключи читает окно настроек через @AppStorage.
enum Prefs {
    static let d = UserDefaults.standard

    static func register() {
        d.register(defaults: [
            "shotFormat": "png",      // png | jpg
            "shotCursor": false,      // курсор на скриншоте
            "copyOnSave": true,       // при сохранении — ещё и в буфер
            "highlightBlur": false,   // подсветка области: вокруг размыть, а не затемнить
            "blurLevel": 5,           // сила размытия вокруг, 1…10
        ])
    }

    static var shotFormat: String { d.string(forKey: "shotFormat") == "jpg" ? "jpg" : "png" }
    static var shotCursor: Bool { d.bool(forKey: "shotCursor") }
    static var copyOnSave: Bool { d.bool(forKey: "copyOnSave") }
    static var highlightBlur: Bool { d.bool(forKey: "highlightBlur") }
    static var blurLevel: Int { max(1, min(10, d.integer(forKey: "blurLevel"))) }

    static var shotsDir: URL {
        FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0].appendingPathComponent("Qural", isDirectory: true)
    }

    /// «2026-11-22 в 21.50.12» — для имён файлов.
    static func stamp() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "ru_RU")
        f.dateFormat = "yyyy-MM-dd 'в' HH.mm.ss"
        return f.string(from: Date())
    }
}
