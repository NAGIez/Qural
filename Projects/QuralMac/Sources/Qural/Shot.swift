import AppKit
import ScreenCaptureKit

// Скриншот области: снимаем все экраны, поверх каждого — окно с застывшим снимком.
// Наведите на окно — щелчок выделит его; щелчок по пустому месту — весь экран; можно тянуть рамку.
// Рисование, «Рука» (двигать, менять размер, править текст), подсветка области, лупа с цветом (C — скопировать).
// ⌘C / Enter — в буфер, ⌘S — сохранить, ⌘Z — отменить, Esc — выйти.

@MainActor
final class ShotSession {
    static private(set) var current: ShotSession?

    private var windows: [NSWindow] = []
    private(set) var views: [OverlayView] = []
    private(set) var focused: OverlayView?
    private lazy var toolbar = Toolbar(session: self)

    var tool: Tool? { didSet { toolbar.refresh(); focused?.toolChanged() } }
    var color: NSColor = .systemRed { didSet { toolbar.refresh() } }
    var width: CGFloat = 4
    var highlightBlur = Prefs.highlightBlur { didSet { Prefs.d.set(highlightBlur, forKey: "highlightBlur"); toolbar.refresh(); focused?.needsDisplay = true } }
    var blurLevel = Prefs.blurLevel { didSet { Prefs.d.set(blurLevel, forKey: "blurLevel"); focused?.needsDisplay = true } }

    static func start() async {
        guard current == nil else { return }
        Log.write("скриншот области: снимаю экраны")
        do {
            let wins = WindowList.onScreen()
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            var shots: [(NSScreen, CGImage)] = []
            for screen in NSScreen.screens {
                guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) else { continue }
                shots.append((screen, try await Snapshot.capture(d, screen: screen)))
            }
            guard !shots.isEmpty, current == nil else { Log.write("скриншот: нет экранов"); return }
            let s = ShotSession(shots: shots, windows: wins)
            current = s
            s.show()
        } catch {
            Log.write("скриншот: ошибка \(error)")
            Permissions.screenDenied()
        }
    }

    private init(shots: [(NSScreen, CGImage)], windows wins: [CGRect]) {
        for (screen, img) in shots {
            let w = OverlayWindow(screen: screen)
            let disp = CGDisplayBounds(screen.displayID)   // экран в общих координатах окон (y вниз)
            let local = wins.compactMap { r -> CGRect? in
                let l = r.offsetBy(dx: -disp.minX, dy: -disp.minY).intersection(CGRect(origin: .zero, size: screen.frame.size))
                return l.width > 40 && l.height > 40 ? l : nil
            }
            let v = OverlayView(frame: CGRect(origin: .zero, size: screen.frame.size), image: img,
                                scale: screen.backingScaleFactor, windows: local, session: self)
            w.contentView = v
            windows.append(w)
            views.append(v)
            v.screenRef = screen
        }
    }

    private func show() {
        NSApp.activate()
        let under = NSScreen.underMouse
        for (w, v) in zip(windows, views) {
            if v.screenRef === under { w.makeKeyAndOrderFront(nil); w.makeFirstResponder(v); focused = v }
            else { w.orderFrontRegardless() }
        }
    }

    /// На этом экране начали выделять — на остальных снять рамку; панель — сюда.
    func focus(_ v: OverlayView) {
        if focused !== v {
            for o in views where o !== v { o.reset() }
            focused = v
            v.window?.makeKey()
        }
        layoutToolbar()
    }

    func layoutToolbar() {
        guard let v = focused, let s = v.selection, !v.isSelecting else { toolbar.view.removeFromSuperview(); return }
        if toolbar.view.superview !== v { v.addSubview(toolbar.view) }
        toolbar.refresh()
        let sz = toolbar.view.fittingSize
        var x = s.maxX - sz.width
        var y = s.maxY + 8
        if y + sz.height > v.bounds.maxY - 6 { y = s.minY - sz.height - 8 }       // снизу не влезает — сверху
        if y < 6 { y = s.maxY - sz.height - 8 }                                 // и сверху нет — внутри рамки
        x = min(max(6, x), v.bounds.maxX - sz.width - 6)
        toolbar.view.frame = CGRect(x: x, y: y, width: sz.width, height: sz.height)
    }

    func close() {
        for w in windows { w.orderOut(nil) }
        windows.removeAll(); views.removeAll(); focused = nil
        if ShotSession.current === self { ShotSession.current = nil }
    }

    func undo() { focused?.undo() }

    func finish(save: Bool) {
        guard let v = focused, let img = v.export() else { return }
        if save {
            guard let url = Snapshot.save(img) else { return }
            if Prefs.copyOnSave { Clipboard.put(img) }
            close()
            Toast.show("Сохранено: \(url.lastPathComponent)", action: "Показать") {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        } else {
            Clipboard.put(img)
            close()
            Toast.show("Скриншот скопирован")
        }
    }

    /// Ширина панели поменялась (появились кнопки подсветки) — переставить на следующем шаге.
    func layoutToolbarLater() {
        DispatchQueue.main.async { [weak self] in MainActor.assumeIsolated { self?.layoutToolbar() } }
    }
}

enum Clipboard {
    @MainActor static func put(_ img: CGImage) {
        let pb = NSPasteboard.general
        pb.clearContents()
        if let png = NSBitmapImageRep(cgImage: img).representation(using: .png, properties: [:]) {
            pb.setData(png, forType: .png)
        }
        pb.writeObjects([NSImage(cgImage: img, size: .zero)])
    }

    @MainActor static func put(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }
}

/// Окна на экране сверху вниз (для «наведите на окно — щелчок выделит его»), в общих координатах, y вниз.
enum WindowList {
    static func onScreen() -> [CGRect] {
        let me = ProcessInfo.processInfo.processIdentifier
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        return list.compactMap { w -> CGRect? in
            guard (w[kCGWindowLayer as String] as? Int) == 0,
                  (w[kCGWindowOwnerPID as String] as? Int32) != me,
                  ((w[kCGWindowAlpha as String] as? Double) ?? 1) > 0.05,
                  let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: b as CFDictionary) else { return nil }
            return r
        }
    }
}

final class OverlayWindow: NSWindow {
    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        isReleasedWhenClosed = false
        setFrame(screen.frame, display: false)
        level = .screenSaver
        isOpaque = true
        backgroundColor = .black
        hasShadow = false
        sharingType = .none
        acceptsMouseMovedEvents = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
    }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

@MainActor
final class OverlayView: NSView, NSTextFieldDelegate {
    weak var session: ShotSession?
    weak var screenRef: NSScreen?
    let image: NSImage
    let cg: CGImage
    let scale: CGFloat
    private let windowRects: [CGRect]
    private lazy var bitmap = NSBitmapImageRep(cgImage: cg)   // цвет точки под курсором
    private var pixelated: NSImage?
    private var pixelatedMade = false
    private var blurImage: NSImage?
    private var blurImageLevel = 0

    private(set) var selection: CGRect?
    private var marks: [Mark] = []
    private var history: [[Mark]] = []       // для ⌘Z — состояния до каждого изменения
    private var current: Mark?
    private var picked: Int?                  // выбранная «Рукой» пометка
    private var editor: NSTextField?
    private var editorAt = CGPoint.zero
    private var editorSize: CGFloat = 22
    private var editorColor: NSColor = .systemRed
    private var mouse: CGPoint?
    private var flash: String?
    private var flashTimer: Timer?
    private var wheelAt = Date.distantPast    // несколько щелчков колёсика подряд — один шаг отмены

    private enum Drag {
        case none, select(CGPoint), move(CGPoint, CGRect), resize(Int, CGRect), draw(CGPoint)
        case moveMark(CGPoint, Mark), scaleMark(Int, CGRect, Mark, CGPoint)
    }
    private var drag: Drag = .none
    private var dragged = false
    var isSelecting: Bool { if case .select = drag { return true } else { return false } }

    init(frame: CGRect, image cg: CGImage, scale: CGFloat, windows: [CGRect], session: ShotSession) {
        self.cg = cg
        self.scale = scale
        self.windowRects = windows
        self.image = NSImage(cgImage: cg, size: frame.size)
        self.session = session
        super.init(frame: frame)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect], owner: self))
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .crosshair) }

    func reset() {
        commitText()
        selection = nil; marks.removeAll(); history.removeAll(); current = nil; picked = nil; drag = .none
        needsDisplay = true
    }

    func toolChanged() {
        if session?.tool != .hand { picked = nil }
        needsDisplay = true
    }

    private var color: NSColor { session?.color ?? .systemRed }
    private var tool: Tool? { session?.tool }
    private var lineWidth: CGFloat { session?.width ?? 4 }

    private func remember() { history.append(marks); if history.count > 100 { history.removeFirst() } }

    func undo() {
        commitText()
        guard let last = history.popLast() else { return }
        marks = last; picked = nil
        needsDisplay = true
    }

    /// Окно под точкой (до выделения): щелчок выделит его.
    private func windowAt(_ p: CGPoint) -> CGRect? { windowRects.first { $0.contains(p) } }

    // ── мышь ──
    override func mouseMoved(with event: NSEvent) {
        mouse = convert(event.locationInWindow, from: nil)
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        mouse = p
        dragged = false
        commitText()
        session?.focus(self)
        if let s = selection {
            if tool == .hand {
                if let i = picked, i < marks.count, let h = handle(at: p, in: pickBox(marks[i])) {
                    remember(); drag = .scaleMark(h, marks[i].box, marks[i], p); return
                }
                if let i = marks.lastIndex(where: { $0.hit(p) }) {
                    picked = i
                    needsDisplay = true
                    if event.clickCount == 2, case .text = marks[i].kind { editText(i); return }
                    remember(); drag = .moveMark(p, marks[i])
                    return
                }
                picked = nil
                needsDisplay = true
            }
            if let h = handle(at: p, in: s) { drag = .resize(h, s); return }
            if s.contains(p) {
                if let t = tool, t != .hand {
                    switch t {
                    case .text:
                        if let i = marks.lastIndex(where: { m in
                            if case .text = m.kind { return m.hit(p) } else { return false }
                        }) { editText(i); return }
                        startText(at: p, size: 22, color: color)
                        return
                    case .step:
                        let n = marks.filter { m in if case .step = m.kind { return true } else { return false } }.count + 1
                        remember()
                        marks.append(Mark(kind: .step(n, p, 12 + lineWidth), color: color, width: lineWidth))
                        needsDisplay = true
                        return
                    default:
                        drag = .draw(p)
                        current = newMark(t, at: p)
                        return
                    }
                }
                drag = .move(p, s)
                return
            }
        }
        reset()
        drag = .select(p)
        session?.layoutToolbar()
    }

    override func mouseDragged(with event: NSEvent) {
        let p = clamp(convert(event.locationInWindow, from: nil))
        mouse = p
        dragged = true
        switch drag {
        case .none: return
        case .select(let o):
            selection = rectBetween(o, p)
        case .move(let o, let s):
            var r = s.offsetBy(dx: p.x - o.x, dy: p.y - o.y)
            r.origin.x = min(max(0, r.origin.x), bounds.width - r.width)
            r.origin.y = min(max(0, r.origin.y), bounds.height - r.height)
            selection = r
            session?.layoutToolbar()
        case .resize(let h, let s):
            selection = resized(s, handle: h, to: p)
            session?.layoutToolbar()
        case .draw(let o):
            current = update(current, from: o, to: p)
        case .moveMark(let o, let m):
            if let i = picked, i < marks.count { marks[i] = m.moved(p.x - o.x, p.y - o.y) }
        case .scaleMark(let h, let box, let m, let o):
            if let i = picked, i < marks.count {
                marks[i] = m.scaled(from: box, to: scaledBox(box, handle: h, dx: p.x - o.x, dy: p.y - o.y, uniform: m.uniform))
            }
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        switch drag {
        case .select(let o):
            if let s = selection, s.width >= 4, s.height >= 4 { break }
            selection = windowAt(o) ?? bounds                  // щелчок без рамки: окно под курсором или весь экран
        case .draw:
            if let c = current, c.meaningful { remember(); marks.append(c) }
            current = nil
        case .moveMark, .scaleMark:
            if !dragged { _ = history.popLast() }               // просто выбрали — шаг отмены не нужен
        default: break
        }
        drag = .none
        session?.layoutToolbar()
        needsDisplay = true
    }

    override func scrollWheel(with event: NSEvent) {
        guard abs(event.scrollingDeltaY) > 0.1 else { return }
        let up = event.scrollingDeltaY > 0
        if let f = editor {                                          // правка текста — размер шрифта
            editorSize = max(8, min(400, (up ? editorSize * 1.1 + 1 : editorSize / 1.1 - 1).rounded()))
            f.font = Mark.font(editorSize)
            f.setFrameSize(CGSize(width: max(320, f.frame.width), height: editorSize + 12))
            showFlash("Шрифт: \(Int(editorSize))")
            return
        }
        if tool == .hand, let i = picked, i < marks.count, marks[i].uniform {   // надпись / номер — крупнее, мельче
            if Date().timeIntervalSince(wheelAt) > 0.9 { remember() }
            wheelAt = Date()
            marks[i] = marks[i].resized(by: up ? 1.1 : 1 / 1.1)
            needsDisplay = true
            return
        }
        guard let s = session else { return }
        if tool == .highlight && s.highlightBlur {
            let n = max(1, min(10, s.blurLevel + (up ? 1 : -1)))
            if n != s.blurLevel { s.blurLevel = n; showFlash("Размытие: \(n)") }
            return
        }
        let w = max(1, min(30, s.width + (up ? 1 : -1)))
        if w != s.width { s.width = w; showFlash("Толщина: \(Int(w))") }
    }

    private func showFlash(_ text: String) {
        flash = text
        needsDisplay = true
        flashTimer?.invalidate()
        flashTimer = Timer.scheduledTimer(withTimeInterval: 1.0, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.flash = nil; self?.needsDisplay = true }
        }
    }

    private func newMark(_ t: Tool, at p: CGPoint) -> Mark {
        let kind: Mark.Kind
        switch t {
        case .pen, .hand, .text, .step: kind = .pen([p])
        case .marker: kind = .marker([p])
        case .line: kind = .line(p, p)
        case .arrow: kind = .arrow(p, p)
        case .rect: kind = .rect(CGRect(origin: p, size: .zero))
        case .ellipse: kind = .ellipse(CGRect(origin: p, size: .zero))
        case .highlight: kind = .highlight(CGRect(origin: p, size: .zero))
        case .blur:
            if !pixelatedMade { pixelatedMade = true; pixelated = Painter.pixelate(cg, size: bounds.size, scale: scale) }
            kind = .blur(CGRect(origin: p, size: .zero))
        }
        return Mark(kind: kind, color: color, width: lineWidth)
    }

    private func update(_ m: Mark?, from o: CGPoint, to p: CGPoint) -> Mark? {
        guard var m else { return nil }
        switch m.kind {
        case .pen(let pts): m.kind = .pen(pts + [p])
        case .marker(let pts): m.kind = .marker(pts + [p])
        case .line: m.kind = .line(o, p)
        case .arrow: m.kind = .arrow(o, p)
        case .rect: m.kind = .rect(rectBetween(o, p))
        case .ellipse: m.kind = .ellipse(rectBetween(o, p))
        case .blur: m.kind = .blur(rectBetween(o, p))
        case .highlight: m.kind = .highlight(rectBetween(o, p))
        default: break
        }
        return m
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(0, p.x), bounds.width), y: min(max(0, p.y), bounds.height))
    }

    // ручки: 0 — левый верх, по часовой стрелке (как у рамки выделения)
    private func handles(_ s: CGRect) -> [CGPoint] {
        [CGPoint(x: s.minX, y: s.minY), CGPoint(x: s.midX, y: s.minY), CGPoint(x: s.maxX, y: s.minY), CGPoint(x: s.maxX, y: s.midY),
         CGPoint(x: s.maxX, y: s.maxY), CGPoint(x: s.midX, y: s.maxY), CGPoint(x: s.minX, y: s.maxY), CGPoint(x: s.minX, y: s.midY)]
    }

    private func handle(at p: CGPoint, in s: CGRect) -> Int? {
        handles(s).firstIndex { abs($0.x - p.x) <= 7 && abs($0.y - p.y) <= 7 }
    }

    private func pickBox(_ m: Mark) -> CGRect { m.box.insetBy(dx: -6, dy: -6) }

    private func resized(_ s: CGRect, handle h: Int, to p: CGPoint) -> CGRect {
        var l = s.minX, t = s.minY, r = s.maxX, b = s.maxY
        if [0, 6, 7].contains(h) { l = p.x }
        if [2, 3, 4].contains(h) { r = p.x }
        if [0, 1, 2].contains(h) { t = p.y }
        if [4, 5, 6].contains(h) { b = p.y }
        return rectBetween(CGPoint(x: l, y: t), CGPoint(x: r, y: b))
    }

    /// Новая рамка пометки при перетаскивании ручки; у надписи и номера — без искажений, от противоположного угла.
    private func scaledBox(_ o: CGRect, handle h: Int, dx: CGFloat, dy: CGFloat, uniform: Bool) -> CGRect {
        var l = o.minX, t = o.minY, r = o.maxX, b = o.maxY
        if [0, 6, 7].contains(h) { l += dx }
        if [2, 3, 4].contains(h) { r += dx }
        if [0, 1, 2].contains(h) { t += dy }
        if [4, 5, 6].contains(h) { b += dy }
        if !uniform { return CGRect(x: min(l, r), y: min(t, b), width: max(2, abs(r - l)), height: max(2, abs(b - t))) }
        let fw = (r - l) / max(1, o.width), fh = (b - t) / max(1, o.height)
        let f = max(0.1, min(20, abs(fw - 1) > abs(fh - 1) ? fw : fh))
        let nw = o.width * f, nh = o.height * f
        let x = [0, 6, 7].contains(h) ? o.maxX - nw : o.minX
        let y = [0, 1, 2].contains(h) ? o.maxY - nh : o.minY
        return CGRect(x: x, y: y, width: nw, height: nh)
    }

    // ── текст ──
    private func startText(at p: CGPoint, size: CGFloat, color c: NSColor, text: String = "") {
        editorSize = size
        editorColor = c
        let f = NSTextField(frame: CGRect(x: p.x - 2, y: p.y - 2, width: 320, height: size + 12))
        f.isBordered = false
        f.drawsBackground = false
        f.focusRingType = .none
        f.font = Mark.font(size)
        f.textColor = c
        f.stringValue = text
        f.placeholderString = "Текст"
        f.delegate = self
        addSubview(f)
        window?.makeFirstResponder(f)
        editor = f
        editorAt = p
    }

    /// Править готовую надпись: убрать её и открыть поле с тем же текстом, цветом и размером.
    private func editText(_ i: Int) {
        guard i < marks.count, case .text(let s, let at, let size) = marks[i].kind else { return }
        remember()
        let c = marks[i].color
        marks.remove(at: i)
        picked = nil
        needsDisplay = true
        startText(at: at, size: size, color: c, text: s)
    }

    func controlTextDidEndEditing(_ obj: Notification) { commitText() }

    private func commitText() {
        guard let f = editor else { return }
        editor = nil
        let s = f.stringValue
        f.removeFromSuperview()
        if !s.isEmpty { remember(); marks.append(Mark(kind: .text(s, editorAt, editorSize), color: editorColor, width: 4)) }
        window?.makeFirstResponder(self)
        needsDisplay = true
    }

    // ── клавиши ──
    override func keyDown(with event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        let ch = event.charactersIgnoringModifiers?.lowercased() ?? ""
        switch event.keyCode {
        case 53:                                                           // Esc
            if picked != nil { picked = nil; needsDisplay = true } else { session?.close() }
            return
        case 36, 76:                                                       // Enter
            if selection != nil { session?.finish(save: false) }
            return
        case 51, 117:                                                      // Delete — убрать выбранную пометку
            if let i = picked, i < marks.count { remember(); marks.remove(at: i); picked = nil; needsDisplay = true }
            return
        case 123, 124, 125, 126:                                           // стрелки — сдвинуть выбранную пометку
            if let i = picked, i < marks.count {
                let d: CGFloat = event.modifierFlags.contains(.shift) ? 10 : 1
                let dx: CGFloat = event.keyCode == 123 ? -d : event.keyCode == 124 ? d : 0
                let dy: CGFloat = event.keyCode == 126 ? -d : event.keyCode == 125 ? d : 0
                remember(); marks[i] = marks[i].moved(dx, dy); needsDisplay = true
            }
            return
        default: break
        }
        if cmd && (ch == "c" || ch == "с") && selection != nil { session?.finish(save: false); return }
        if cmd && (ch == "s" || ch == "ы") && selection != nil { session?.finish(save: true); return }
        if cmd && (ch == "z" || ch == "я") { undo(); return }
        if !cmd && (ch == "c" || ch == "с"), let p = mouse {             // C — скопировать цвет под курсором
            let hex = hexAt(p)
            Clipboard.put(hex)
            showFlash("Скопирован цвет \(hex)")
            return
        }
        super.keyDown(with: event)
    }

    // ── рисование ──
    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        drawPicture(ctx)
        // затемнение вокруг рамки
        ctx.saveGState()
        ctx.setFillColor(NSColor(white: 0, alpha: 0.42).cgColor)
        if let s = selection {
            ctx.addRect(bounds); ctx.addRect(s)
            ctx.fillPath(using: .evenOdd)
        } else {
            ctx.fill(bounds)
        }
        ctx.restoreGState()

        if let s = selection {
            ctx.setStrokeColor(NSColor.white.cgColor)
            ctx.setLineWidth(1)
            ctx.stroke(s.insetBy(dx: -0.5, dy: -0.5))
            drawHandles(ctx, s, color: .white)
            sizeLabel(ctx, s)
            if let i = picked, i < marks.count {                           // выбранная «Рукой» пометка
                let b = pickBox(marks[i])
                ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
                ctx.setLineWidth(1)
                ctx.setLineDash(phase: 0, lengths: [4, 3])
                ctx.stroke(b)
                ctx.setLineDash(phase: 0, lengths: [])
                drawHandles(ctx, b, color: .controlAccentColor, corners: marks[i].uniform)
            }
        } else {
            if let p = mouse, let w = windowAt(p) {                        // окно под курсором — щелчок выделит его
                ctx.saveGState()
                ctx.clip(to: w)
                image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
                ctx.restoreGState()
                ctx.setStrokeColor(NSColor.controlAccentColor.cgColor)
                ctx.setLineWidth(2)
                ctx.stroke(w.insetBy(dx: 1, dy: 1))
            }
            hint(ctx)
        }
        if selection == nil || isSelecting { magnifier(ctx) }
        if let f = flash, let p = mouse { pillText(ctx, f, at: CGPoint(x: p.x + 18, y: p.y + 18)) }
    }

    private func drawHandles(_ ctx: CGContext, _ s: CGRect, color: NSColor, corners: Bool = false) {
        for (i, h) in handles(s).enumerated() where !corners || i % 2 == 0 {
            let r = CGRect(x: h.x - 4, y: h.y - 4, width: 8, height: 8)
            ctx.setFillColor(color.cgColor); ctx.fill(r)
            ctx.setStrokeColor(NSColor(white: 0, alpha: 0.5).cgColor); ctx.setLineWidth(1); ctx.stroke(r)
        }
    }

    /// Снимок и пометки (без затемнения и рамки) — общее для экрана и сохранения.
    private func drawPicture(_ ctx: CGContext) {
        ctx.interpolationQuality = .none
        image.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        guard let s = selection else { return }
        ctx.saveGState()
        ctx.clip(to: s)
        ctx.interpolationQuality = .high
        drawHighlight(ctx, s)
        for m in marks where !m.isHighlight { Painter.draw(m, ctx: ctx, pixelated: pixelated, bounds: bounds) }
        if let c = current, !c.isHighlight { Painter.draw(c, ctx: ctx, pixelated: pixelated, bounds: bounds) }
        ctx.restoreGState()
    }

    /// Подсветка — одним слоем: всё в рамке, кроме подсвеченных мест, затемнено или размыто.
    /// Пересекающиеся подсветки сливаются в одно чёткое место.
    private func drawHighlight(_ ctx: CGContext, _ s: CGRect) {
        var spots = marks.filter { $0.isHighlight }.map { $0.box }
        if let c = current, c.isHighlight { spots.append(c.box) }
        spots = spots.filter { $0.width >= 2 && $0.height >= 2 }
        guard !spots.isEmpty, let session else { return }
        var holes: CGPath?
        for r in spots {
            let rr = CGPath(roundedRect: r, cornerWidth: min(8, r.width / 2), cornerHeight: min(8, r.height / 2), transform: nil)
            holes = holes.map { $0.union(rr) } ?? rr
        }
        guard let holes else { return }
        let dim = CGPath(rect: s, transform: nil).subtracting(holes)
        ctx.saveGState()
        ctx.addPath(dim)
        ctx.clip()
        if session.highlightBlur {
            if blurImage == nil || blurImageLevel != session.blurLevel {
                blurImage = Painter.blurred(cg, size: bounds.size, scale: scale, level: session.blurLevel)
                blurImageLevel = session.blurLevel
            }
            blurImage?.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
            ctx.setFillColor(NSColor(white: 0, alpha: 0.2).cgColor)
        } else {
            ctx.setFillColor(NSColor(white: 0, alpha: 0.58).cgColor)
        }
        ctx.fill(s)
        ctx.restoreGState()
    }

    private func sizeLabel(_ ctx: CGContext, _ s: CGRect) {
        let text = "\(Int((s.width * scale).rounded())) × \(Int((s.height * scale).rounded()))"
        var p = CGPoint(x: s.minX, y: s.minY - 26)
        if p.y < 4 { p = CGPoint(x: s.minX + 6, y: s.minY + 6) }
        pillText(ctx, text, at: p, mono: true)
    }

    private func pillText(_ ctx: CGContext, _ text: String, at p: CGPoint, mono: Bool = false) {
        let a: [NSAttributedString.Key: Any] = [
            .font: mono ? NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium) : NSFont.systemFont(ofSize: 12, weight: .medium),
            .foregroundColor: NSColor.white]
        let sz = (text as NSString).size(withAttributes: a)
        let r = CGRect(x: p.x, y: p.y, width: sz.width + 14, height: sz.height + 6)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: 6, cornerHeight: 6, transform: nil))
        ctx.setFillColor(NSColor(white: 0, alpha: 0.7).cgColor)
        ctx.fillPath()
        (text as NSString).draw(at: CGPoint(x: r.minX + 7, y: r.minY + 3), withAttributes: a)
    }

    private func hint(_ ctx: CGContext) {
        let text = "Выделите область · щелчок — окно под курсором или весь экран · Esc — отмена" as NSString
        let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 15, weight: .medium), .foregroundColor: NSColor.white]
        let sz = text.size(withAttributes: a)
        let r = CGRect(x: bounds.midX - sz.width / 2 - 18, y: 40, width: sz.width + 36, height: sz.height + 20)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: 12, cornerHeight: 12, transform: nil))
        ctx.setFillColor(NSColor(white: 0, alpha: 0.6).cgColor)
        ctx.fillPath()
        text.draw(at: CGPoint(x: r.minX + 18, y: r.minY + 10), withAttributes: a)
    }

    // ── лупа: точки под курсором крупно, цвет и координаты; C — скопировать цвет ──
    private func pixelColor(_ p: CGPoint) -> NSColor {
        let x = Int(p.x * scale), y = Int(p.y * scale)
        guard x >= 0, y >= 0, x < bitmap.pixelsWide, y < bitmap.pixelsHigh else { return .black }
        return bitmap.colorAt(x: x, y: y)?.usingColorSpace(.sRGB) ?? .black
    }

    private func hexAt(_ p: CGPoint) -> String {
        let c = pixelColor(p)
        return String(format: "#%02X%02X%02X", Int((c.redComponent * 255).rounded()), Int((c.greenComponent * 255).rounded()), Int((c.blueComponent * 255).rounded()))
    }

    private func magnifier(_ ctx: CGContext) {
        guard let p = mouse, bounds.contains(p) else { return }
        let cells = 15, zoom: CGFloat = 8
        let side = CGFloat(cells) * zoom
        var box = CGRect(x: p.x + 22, y: p.y + 22, width: side, height: side)
        if box.maxX > bounds.maxX - 4 { box.origin.x = p.x - 22 - side }
        if box.maxY + 46 > bounds.maxY - 4 { box.origin.y = p.y - 22 - side - 46 }
        // кусок снимка 15×15 точек экрана вокруг курсора, без сглаживания
        let n = Int(CGFloat(cells) * scale)
        let src = CGRect(x: Int(p.x * scale) - n / 2, y: Int(p.y * scale) - n / 2, width: n, height: n)
        ctx.saveGState()
        ctx.setFillColor(NSColor.black.cgColor)
        ctx.fill(box)
        let inside = src.intersection(CGRect(x: 0, y: 0, width: cg.width, height: cg.height))
        if !inside.isNull, let part = cg.cropping(to: inside) {
            ctx.interpolationQuality = .none
            let k = side / CGFloat(n)
            let dst = CGRect(x: box.minX + (inside.minX - src.minX) * k, y: box.minY + (inside.minY - src.minY) * k,
                             width: inside.width * k, height: inside.height * k)
            NSImage(cgImage: part, size: CGSize(width: part.width, height: part.height))
                .draw(in: dst, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        }
        ctx.setStrokeColor(NSColor(white: 1, alpha: 0.85).cgColor)
        ctx.setLineWidth(1)
        ctx.stroke(box.insetBy(dx: 0.5, dy: 0.5))
        let mid = CGFloat(cells / 2) * zoom
        let cell = CGRect(x: box.minX + mid, y: box.minY + mid, width: zoom, height: zoom)
        ctx.setStrokeColor(NSColor.black.cgColor); ctx.stroke(cell)
        ctx.setStrokeColor(NSColor.white.cgColor); ctx.stroke(cell.insetBy(dx: -1, dy: -1))
        ctx.restoreGState()
        // цвет и координаты
        let info = CGRect(x: box.minX, y: box.maxY + 4, width: side, height: 40)
        ctx.addPath(CGPath(roundedRect: info, cornerWidth: 8, cornerHeight: 8, transform: nil))
        ctx.setFillColor(NSColor(white: 0, alpha: 0.75).cgColor)
        ctx.fillPath()
        let sw = CGRect(x: info.minX + 8, y: info.minY + 7, width: 12, height: 12)
        ctx.setFillColor(pixelColor(p).cgColor); ctx.fill(sw)
        ctx.setStrokeColor(NSColor(white: 1, alpha: 0.6).cgColor); ctx.stroke(sw)
        (hexAt(p) as NSString).draw(at: CGPoint(x: info.minX + 26, y: info.minY + 4), withAttributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 12, weight: .semibold), .foregroundColor: NSColor.white])
        ("X \(Int(p.x))  Y \(Int(p.y))  · C — цвет" as NSString).draw(at: CGPoint(x: info.minX + 8, y: info.minY + 22), withAttributes: [
            .font: NSFont.systemFont(ofSize: 10.5), .foregroundColor: NSColor(white: 1, alpha: 0.7)])
    }

    // ── сохранение: выделенная часть в точных пикселях экрана ──
    func export() -> CGImage? {
        commitText()
        guard let s = selection else { return nil }
        let pw = max(1, Int((s.width * scale).rounded())), ph = max(1, Int((s.height * scale).rounded()))
        let space = cg.colorSpace ?? CGColorSpace(name: CGColorSpace.sRGB)!
        guard let ctx = CGContext(data: nil, width: pw, height: ph, bitsPerComponent: 8, bytesPerRow: 0, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return nil }
        // перевернуть (как у вида) и перейти в точки выделенной области
        ctx.translateBy(x: 0, y: CGFloat(ph))
        ctx.scaleBy(x: scale, y: -scale)
        ctx.translateBy(x: -s.minX, y: -s.minY)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        drawPicture(ctx)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()
    }
}

// ── панель инструментов под рамкой ──
@MainActor
final class Toolbar: NSObject {
    weak var session: ShotSession?
    let view = NSVisualEffectView()
    private let stack = NSStackView()
    private var toolButtons: [NSButton] = []
    private var hlButtons: [NSButton] = []     // «затемнить / размыть» — видны, пока выбрана подсветка
    private var colorButton: NSButton?
    private var built = false
    private var lastHL: Bool?

    static let palette: [(String, NSColor)] = [
        ("Красный", .systemRed), ("Оранжевый", .systemOrange), ("Жёлтый", .systemYellow), ("Зелёный", .systemGreen),
        ("Синий", .systemBlue), ("Фиолетовый", .systemPurple), ("Белый", .white), ("Чёрный", .black),
    ]

    init(session: ShotSession) {
        self.session = session
        super.init()
        view.material = .hudWindow
        view.blendingMode = .withinWindow
        view.state = .active
        view.appearance = NSAppearance(named: .vibrantDark)
        view.wantsLayer = true
        view.layer?.cornerRadius = 10
        view.layer?.masksToBounds = true
        stack.orientation = .horizontal
        stack.spacing = 2
        stack.edgeInsets = NSEdgeInsets(top: 4, left: 6, bottom: 4, right: 6)
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor), stack.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            stack.topAnchor.constraint(equalTo: view.topAnchor), stack.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }

    private func build() {
        guard !built else { return }
        built = true
        for t in Tool.allCases {
            let b = button(t.symbol, t.title, #selector(pickTool(_:)))
            b.tag = t.rawValue
            toolButtons.append(b)
            stack.addArrangedSubview(b)
            if t == .highlight {
                let dark = button("circle.lefthalf.filled", "Затемнить вокруг", #selector(hlDark))
                let blur = button("drop.fill", "Размыть вокруг (колёсико — сила)", #selector(hlBlur))
                hlButtons = [dark, blur]
                for h in hlButtons { stack.addArrangedSubview(h) }
            }
        }
        stack.addArrangedSubview(separator())
        let c = button("circle.fill", "Цвет (колёсико — толщина)", #selector(pickColor(_:)))
        colorButton = c
        stack.addArrangedSubview(c)
        stack.addArrangedSubview(button("arrow.uturn.backward", "Отменить (⌘Z)", #selector(undoIt)))
        stack.addArrangedSubview(separator())
        stack.addArrangedSubview(button("doc.on.doc", "Скопировать (⌘C, Enter)", #selector(copyIt)))
        stack.addArrangedSubview(button("square.and.arrow.down", "Сохранить (⌘S)", #selector(saveIt)))
        stack.addArrangedSubview(button("xmark", "Закрыть (Esc)", #selector(closeIt)))
    }

    private func button(_ symbol: String, _ tip: String, _ action: Selector) -> NSButton {
        let img = NSImage(systemSymbolName: symbol, accessibilityDescription: tip) ?? NSImage()
        let b = NSButton(image: img, target: self, action: action)
        b.isBordered = false
        b.imageScaling = .scaleProportionallyDown
        b.toolTip = tip
        b.contentTintColor = .white
        b.wantsLayer = true
        b.layer?.cornerRadius = 6
        b.widthAnchor.constraint(equalToConstant: 30).isActive = true
        b.heightAnchor.constraint(equalToConstant: 28).isActive = true
        return b
    }

    private func separator() -> NSView {
        let v = NSBox()
        v.boxType = .separator
        v.widthAnchor.constraint(equalToConstant: 1).isActive = true
        v.heightAnchor.constraint(equalToConstant: 20).isActive = true
        return v
    }

    private func mark(_ b: NSButton, _ on: Bool) {
        b.contentTintColor = on ? .controlAccentColor : .white
        b.layer?.backgroundColor = on ? NSColor(white: 1, alpha: 0.18).cgColor : nil
    }

    func refresh() {
        build()
        guard let session else { return }
        for b in toolButtons { mark(b, session.tool?.rawValue == b.tag) }
        let hl = session.tool == .highlight
        for b in hlButtons { b.isHidden = !hl }
        if hlButtons.count == 2 { mark(hlButtons[0], !session.highlightBlur); mark(hlButtons[1], session.highlightBlur) }
        colorButton?.contentTintColor = session.color
        if lastHL != nil && lastHL != hl && view.superview != nil { session.layoutToolbarLater() }   // панель стала шире / уже
        lastHL = hl
    }

    @objc private func pickTool(_ b: NSButton) {
        guard let session, let t = Tool(rawValue: b.tag) else { return }
        session.tool = session.tool == t ? nil : t
    }

    @objc private func hlDark() { session?.highlightBlur = false }
    @objc private func hlBlur() { session?.highlightBlur = true }

    @objc private func pickColor(_ b: NSButton) {
        let menu = NSMenu()
        for (i, (name, c)) in Toolbar.palette.enumerated() {
            let item = NSMenuItem(title: name, action: #selector(setColor(_:)), keyEquivalent: "")
            item.target = self
            item.tag = i
            item.image = dot(c)
            item.state = session?.color == c ? .on : .off
            menu.addItem(item)
        }
        _ = menu.popUp(positioning: nil, at: CGPoint(x: 0, y: b.bounds.height + 4), in: b)
    }

    private func dot(_ c: NSColor) -> NSImage {
        NSImage(size: CGSize(width: 14, height: 14), flipped: false) { r in
            c.setFill(); NSBezierPath(ovalIn: r.insetBy(dx: 1, dy: 1)).fill()
            NSColor(white: 0.5, alpha: 0.6).setStroke(); NSBezierPath(ovalIn: r.insetBy(dx: 1, dy: 1)).stroke()
            return true
        }
    }

    @objc private func setColor(_ item: NSMenuItem) { session?.color = Toolbar.palette[item.tag].1 }
    @objc private func undoIt() { session?.undo() }
    @objc private func copyIt() { session?.finish(save: false) }
    @objc private func saveIt() { session?.finish(save: true) }
    @objc private func closeIt() { session?.close() }
}

@MainActor
enum Permissions {
    static func screenDenied() {
        Toast.show("Нет доступа к экрану — разрешите Qural в «Запись экрана и системного звука»", action: "Открыть") {
            if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") {
                NSWorkspace.shared.open(url)
            }
        }
    }
}
