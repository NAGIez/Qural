import AppKit
import ScreenCaptureKit
import AVFoundation

// Запись экрана через ScreenCaptureKit (macOS 15+): система сама кодирует видео на аппаратном кодировщике
// (HEVC или H.264) и пишет файл вместе со звуком системы и микрофоном (SCRecordingOutput).
// Что записывать: весь экран под курсором, область (выбор рамкой) или окно (системный выбор окна).
@MainActor
final class Recorder: NSObject {
    static let shared = Recorder()

    private(set) var startedAt: Date?
    private var stream: SCStream?
    private var output: SCRecordingOutput?
    private var url: URL?
    private var stopping = false
    var onChange: (() -> Void)?

    var isRecording: Bool { stream != nil }

    // ── что записывать ──
    func recordScreen() {
        guard !isRecording else { return }
        Task { @MainActor in
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                let screen = NSScreen.underMouse
                guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) else { return }
                let filter = SCContentFilter(display: d, excludingApplications: ownApps(content), exceptingWindows: [])
                let sc = screen.backingScaleFactor
                begin(filter: filter, rect: nil, pixels: CGSize(width: CGFloat(d.width) * sc, height: CGFloat(d.height) * sc), screen: screen)
            } catch {
                Permissions.screenDenied()
            }
        }
    }

    func recordArea() {
        guard !isRecording else { return }
        Task { @MainActor in
            await ShotSession.start { [weak self] rect, screen in
                self?.startArea(rect, on: screen)
            }
        }
    }

    private func startArea(_ rect: CGRect, on screen: NSScreen) {
        Task { @MainActor in
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) else { return }
                let filter = SCContentFilter(display: d, excludingApplications: ownApps(content), exceptingWindows: [])
                let sc = screen.backingScaleFactor
                // область в точках экрана, начало — левый верхний угол (так и ждёт sourceRect)
                let r = rect.integral
                begin(filter: filter, rect: r, pixels: CGSize(width: r.width * sc, height: r.height * sc), screen: screen)
            } catch {
                Permissions.screenDenied()
            }
        }
    }

    private let picker = WindowPicker()

    func recordWindow() {
        guard !isRecording else { return }
        picker.pick { [weak self] filter in
            let r = filter.contentRect
            let sc = CGFloat(filter.pointPixelScale)
            self?.begin(filter: filter, rect: nil, pixels: CGSize(width: r.width * sc, height: r.height * sc), screen: NSScreen.underMouse)
        }
    }

    private func ownApps(_ content: SCShareableContent) -> [SCRunningApplication] {
        content.applications.filter { $0.bundleIdentifier == Bundle.main.bundleIdentifier }
    }

    /// Отсчёт (если включён) и запись.
    private func begin(filter: SCContentFilter, rect: CGRect?, pixels: CGSize, screen: NSScreen) {
        let go = { [weak self] in
            guard let self else { return }
            Task { @MainActor in await self.start(filter: filter, rect: rect, pixels: pixels) }
        }
        if Prefs.countdown { Countdown.show(on: screen, go) } else { go() }
    }

    // ── запись ──
    private func start(filter: SCContentFilter, rect: CGRect?, pixels: CGSize) async {
        guard stream == nil else { return }
        let cfg = SCStreamConfiguration()
        cfg.width = max(2, Int(pixels.width.rounded()) / 2 * 2)       // кодеку нужны чётные размеры
        cfg.height = max(2, Int(pixels.height.rounded()) / 2 * 2)
        if let rect { cfg.sourceRect = rect }
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(Prefs.fps))
        cfg.showsCursor = Prefs.cursor
        cfg.capturesAudio = Prefs.sysAudio
        cfg.excludesCurrentProcessAudio = true
        cfg.sampleRate = 48_000
        cfg.channelCount = 2
        cfg.captureMicrophone = Prefs.mic
        cfg.queueDepth = 6

        let dir = Prefs.videosDir
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let file = dir.appendingPathComponent("Qural \(Prefs.stamp()).mov")
        let rc = SCRecordingOutputConfiguration()
        rc.outputURL = file
        rc.outputFileType = .mov
        rc.videoCodecType = Prefs.codec == "h264" ? .h264 : .hevc

        let s = SCStream(filter: filter, configuration: cfg, delegate: self)
        let out = SCRecordingOutput(configuration: rc, delegate: self)
        do {
            try s.addRecordingOutput(out)
            try await s.startCapture()
        } catch {
            Toast.show("Запись не началась: \(error.localizedDescription)")
            return
        }
        stream = s; output = out; url = file; startedAt = Date(); stopping = false
        onChange?()
    }

    func stop() {
        guard let s = stream, !stopping else { return }
        stopping = true
        Task { @MainActor in
            try? await s.stopCapture()
            // файл дописывается асинхронно; сообщение — в recordingOutputDidFinishRecording,
            // а если оно не придёт — через 3 секунды всё равно сказать, где файл
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            self.finished(s)
        }
    }

    /// s — какая запись закончилась (старый таймер не должен трогать уже новую запись).
    fileprivate func finished(_ s: SCStream? = nil) {
        guard let cur = stream, s == nil || s === cur else { return }
        let file = url
        stream = nil; output = nil; url = nil; startedAt = nil; stopping = false
        onChange?()
        if let file, FileManager.default.fileExists(atPath: file.path) {
            Toast.show("Запись сохранена: \(file.lastPathComponent)", action: "Показать") {
                NSWorkspace.shared.activateFileViewerSelecting([file])
            }
        } else {
            Toast.show("Запись не сохранилась")
        }
    }

    fileprivate func failed(_ error: Error) {
        guard stream != nil else { return }
        let had = url
        stream = nil; output = nil; url = nil; startedAt = nil; stopping = false
        onChange?()
        if let had, FileManager.default.fileExists(atPath: had.path) {
            Toast.show("Запись прервалась — сохранено, что успело: \(had.lastPathComponent)", action: "Показать") {
                NSWorkspace.shared.activateFileViewerSelecting([had])
            }
        } else {
            Toast.show("Запись прервалась: \(error.localizedDescription)")
        }
    }
}

extension Recorder: SCStreamDelegate, SCRecordingOutputDelegate {
    nonisolated func stream(_ stream: SCStream, didStopWithError error: Error) {
        Task { @MainActor in Recorder.shared.failed(error) }
    }

    nonisolated func recordingOutput(_ recordingOutput: SCRecordingOutput, didFailWithError error: Error) {
        Task { @MainActor in Recorder.shared.failed(error) }
    }

    nonisolated func recordingOutputDidFinishRecording(_ recordingOutput: SCRecordingOutput) {
        Task { @MainActor in Recorder.shared.finished() }
    }
}

// Системное окно выбора окна для записи (SCContentSharingPicker): пользователь щёлкает по окну.
@MainActor
final class WindowPicker: NSObject, SCContentSharingPickerObserver {
    private var done: ((SCContentFilter) -> Void)?

    func pick(_ done: @escaping (SCContentFilter) -> Void) {
        self.done = done
        let p = SCContentSharingPicker.shared
        var cfg = SCContentSharingPickerConfiguration()
        cfg.allowedPickerModes = [.singleWindow]
        p.defaultConfiguration = cfg
        p.add(self)
        p.isActive = true
        p.present(using: .window)
    }

    private func end() {
        let p = SCContentSharingPicker.shared
        p.remove(self)
        p.isActive = false
    }

    nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {
        Task { @MainActor in self.end(); self.done = nil }
    }

    nonisolated func contentSharingPicker(_ picker: SCContentSharingPicker, didUpdateWith filter: SCContentFilter, for stream: SCStream?) {
        Task { @MainActor in
            self.end()
            let d = self.done
            self.done = nil
            d?(filter)
        }
    }

    nonisolated func contentSharingPickerStartDidFailWithError(_ error: Error) {
        Task { @MainActor in self.end(); Toast.show("Не удалось открыть выбор окна: \(error.localizedDescription)") }
    }
}
