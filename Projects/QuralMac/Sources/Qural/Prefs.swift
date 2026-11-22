import Foundation

// Настройки (UserDefaults). Те же ключи читает окно настроек через @AppStorage.
enum Prefs {
    static let d = UserDefaults.standard

    static func register() {
        d.register(defaults: [
            "shotFormat": "png",      // png | jpg
            "shotCursor": false,      // курсор на скриншоте
            "copyOnSave": true,       // при сохранении — ещё и в буфер
            "sysAudio": true,         // звук системы в записи
            "mic": false,             // микрофон в записи
            "codec": "hevc",          // hevc | h264
            "fps": 30,
            "cursor": true,           // курсор в записи
            "countdown": true,        // отсчёт 3-2-1 перед записью
        ])
    }

    static var shotFormat: String { d.string(forKey: "shotFormat") == "jpg" ? "jpg" : "png" }
    static var shotCursor: Bool { d.bool(forKey: "shotCursor") }
    static var copyOnSave: Bool { d.bool(forKey: "copyOnSave") }
    static var sysAudio: Bool { d.bool(forKey: "sysAudio") }
    static var mic: Bool { d.bool(forKey: "mic") }
    static var codec: String { d.string(forKey: "codec") == "h264" ? "h264" : "hevc" }
    static var fps: Int { [24, 30, 60].contains(d.integer(forKey: "fps")) ? d.integer(forKey: "fps") : 30 }
    static var cursor: Bool { d.bool(forKey: "cursor") }
    static var countdown: Bool { d.bool(forKey: "countdown") }

    static var shotsDir: URL {
        FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask)[0].appendingPathComponent("Qural", isDirectory: true)
    }
    static var videosDir: URL {
        FileManager.default.urls(for: .moviesDirectory, in: .userDomainMask)[0].appendingPathComponent("Qural", isDirectory: true)
    }

    /// «2026-11-22 в 21.50.12» — для имён файлов.
    static func stamp() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "ru_RU")
        f.dateFormat = "yyyy-MM-dd 'в' HH.mm.ss"
        return f.string(from: Date())
    }
}
