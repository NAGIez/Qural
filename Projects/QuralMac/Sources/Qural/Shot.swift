import AppKit
import ScreenCaptureKit

// Скриншот области: снимаем все экраны, поверх каждого — окно с застывшим снимком.
// Тянем рамку (щелчок без рамки — весь экран), рисуем, ⌘C / Enter — в буфер, ⌘S — сохранить, Esc — выйти.
// Тот же экран выбора области — для записи (pick): красная рамка и кнопка «Начать запись».

@MainActor
final class ShotSession {
    static private(set) var current: ShotSession?

    typealias Pick = (CGRect, NSScreen) -> Void
    let pick: Pick?
    private var windows: [NSWindow] = []
    private(set) var views: [OverlayView] = []
    private(set) var focused: OverlayView?
    private lazy var toolbar = Toolbar(session: self)

    var tool: Tool? { didSet { toolbar.refresh() } }
    var color: NSColor = .systemRed { didSet { toolbar.refresh() } }

    static func start(pick: Pick? = nil) async {
        guard current == nil else { return }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            var shots: [(NSScreen, CGImage)] = []
            for screen in NSScreen.screens {
                guard let d = content.displays.first(where: { $0.displayID == screen.displayID }) else { continue }
                let cfg = SCStreamConfiguration()
                let sc = screen.backingScaleFactor
                cfg.width = Int(screen.frame.width * sc)
                cfg.height = Int(screen.frame.height * sc)
                cfg.showsCursor = pick == nil && Prefs.shotCursor
                cfg.captureResolution = .best
                let img = try await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(display: d, excludingWindows: []), configuration: cfg)
                shots.append((screen, img))
            }
            guard !shots.isEmpty, current == nil else { return }
            let s = ShotSession(shots: shots, pick: pick)
            current = s
            s.show()
        } catch {
            Permissions.screenDenied()
        }
    }

    private init(shots: [(NSScreen, CGImage)], pick: Pick?) {
        self.pick = pick
        for (screen, img) in shots {
            let w = OverlayWindow(screen: screen)
            let v = OverlayView(frame: CGRect(origin: .zero, size: screen.frame.size), image: img, scale: screen.backingScaleFactor, session: self)
            v.screen = screen
            w.contentView = v
            windows.append(w)
            views.append(v)
        }
    }

    private func show() {
        NSApp.activate()
        let under = NSScreen.underMouse
        for (w, v) in zip(windows, views) {
            if v.screen === under { w.makeKeyAndOrderFront(nil); w.makeFirstResponder(v) } else { w.orderFrontRegardless() }
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

    // ── действия ──
    func undo() { focused?.undo() }

    func confirmPick() {
        guard let pick, let v = focused, let s = v.selection, let screen = v.screen else { return }
        close()
        pick(s, screen)
    }

    func finish(save: Bool) {
        guard let v = focused, let img = v.export() else { return }
        if save {
            let dir = Prefs.shotsDir
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let ext = Prefs.shotFormat
            let url = dir.appendingPathComponent("Qural \(Prefs.stamp()).\(ext)")
            let rep = NSBitmapImageRep(cgImage: img)
            let data = ext == "jpg" ? rep.representation(using: .jpeg, properties: [.compressionFactor: 0.92])
                                    : rep.representation(using: .png, properties: [:])
            do {
                try data?.write(to: url)
            } catch {
                Toast.show("Не сохранилось: \(error.localizedDescription)")
                return
            }
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
    weak var screen: NSScreen?
    let image: NSImage
    let cg: CGImage
    let scale: CGFloat
    private var pixelated: NSImage?
    private var pixelatedMade = false

    private(set) var selection: CGRect?
    private var marks: [Mark] = []
    private var current: Mark?
    private var editor: NSTextField?
    private var editorAt = CGPoint.zero
    private let textSize: CGFloat = 22
    private var mouse: CGPoint?

    private enum Drag { case none, select(CGPoint), move(CGPoint, CGRect), resize(Int, CGRect), draw(CGPoint) }
    private var drag: Drag = .none
    var isSelecting: Bool { if case .select = drag { return true }; return false }

    private var pickMode: Bool { session?.pick != nil }

    init(frame: CGRect, image cg: CGImage, scale: CGFloat, session: ShotSession) {
        self.cg = cg
        self.scale = scale
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
        selection = nil; marks.removeAll(); current = nil; drag = .none
        needsDisplay = true
    }

    func undo() {
        if !marks.isEmpty { marks.removeLast(); needsDisplay = true }
    }

    // ── мышь ──
    override func mouseMoved(with event: NSEvent) {
        mouse = convert(event.locationInWindow, from: nil)
        if selection == nil { needsDisplay = true }
    }

    override func mouseDown(with event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        commitText()
        session?.focus(self)
        if let s = selection {
            if let h = handle(at: p, in: s) { drag = .resize(h, s); return }
            if s.contains(p) {
                if let t = session?.tool, !pickMode {
                    switch t {
                    case .text: startText(at: p); return
                    case .step:
                        let n = marks.filter { if case .step = $0.kind { return true } else { return false } }.count + 1
                        marks.append(Mark(kind: .step(n, p), color: color, width: 3))
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
        marks.removeAll()
        selection = nil
        drag = .select(p)
        session?.layoutToolbar()
    }

    override func mouseDragged(with event: NSEvent) {
        let p = clamp(convert(event.locationInWindow, from: nil))
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
        }
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        switch drag {
        case .select:
            if let s = selection, s.width < 4 || s.height < 4 { selection = bounds }   // щелчок — весь экран
            if selection == nil { selection = bounds }
        case .draw:
            if let c = current, c.meaningful { marks.append(c) }
            current = nil
        default: break
        }
        drag = .none
        session?.layoutToolbar()
        needsDisplay = true
    }

    private var color: NSColor { session?.color ?? .systemRed }

    private func newMark(_ t: Tool, at p: CGPoint) -> Mark {
        let kind: Mark.Kind
        switch t {
        case .pen: kind = .pen([p])
        case .marker: kind = .marker([p])
        case .line: kind = .line(p, p)
        case .arrow: kind = .arrow(p, p)
        case .rect: kind = .rect(CGRect(origin: p, size: .zero))
        case .ellipse: kind = .ellipse(CGRect(origin: p, size: .zero))
        case .blur:
            if !pixelatedMade { pixelatedMade = true; pixelated = Painter.pixelate(cg, size: bounds.size, scale: scale) }
            kind = .blur(CGRect(origin: p, size: .zero))
        case .text, .step: kind = .pen([p])
        }
        return Mark(kind: kind, color: color, width: 4)
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
        default: break
        }
        return m
    }

    private func clamp(_ p: CGPoint) -> CGPoint {
        CGPoint(x: min(max(0, p.x), bounds.width), y: min(max(0, p.y), bounds.height))
    }

    // ручки рамки: 0 — левый верх, по часовой стрелке
    private func handles(_ s: CGRect) -> [CGPoint] {
        [CGPoint(x: s.minX, y: s.minY), CGPoint(x: s.midX, y: s.minY), CGPoint(x: s.maxX, y: s.minY), CGPoint(x: s.maxX, y: s.midY),
         CGPoint(x: s.maxX, y: s.maxY), CGPoint(x: s.midX, y: s.maxY), CGPoint(x: s.minX, y: s.maxY), CGPoint(x: s.minX, y: s.midY)]
    }

    private func handle(at p: CGPoint, in s: CGRect) -> Int? {
        handles(s).firstIndex { abs($0.x - p.x) <= 7 && abs($0.y - p.y) <= 7 }
    }

    private func resized(_ s: CGRect, handle h: Int, to p: CGPoint) -> CGRect {
        var l = s.minX, t = s.minY, r = s.maxX, b = s.maxY
        if [0, 6, 7].contains(h) { l = p.x }
        if [2, 3, 4].contains(h) { r = p.x }
        if [0, 1, 2].contains(h) { t = p.y }
        if [4, 5, 6].contains(h) { b = p.y }
        return rectBetween(CGPoint(x: l, y: t), CGPoint(x: r, y: b))
    }

    // ── текст ──
    private func startText(at p: CGPoint) {
        let f = NSTextField(frame: CGRect(x: p.x - 2, y: p.y - 2, width: 320, height: textSize + 12))
        f.isBordered = false
        f.drawsBackground = false
        f.focusRingType = .none
        f.font = .systemFont(ofSize: textSize, weight: .bold)
        f.textColor = color
        f.placeholderString = "Текст"
        f.delegate = self
        addSubview(f)
        window?.makeFirstResponder(f)
        editor = f
        editorAt = p
    }

    func controlTextDidEndEditing(_ obj: Notification) { commitText() }

    private func commitText() {
        guard let f = editor else { return }
        editor = nil
        let s = f.stringValue
        f.removeFromSuperview()
        if !s.isEmpty { marks.append(Mark(kind: .text(s, editorAt, textSize), color: color, width: 4)) }
        window?.makeFirstResponder(self)
        needsDisplay = true
    }

    // ── клавиши ──
    override func keyDown(with event: NSEvent) {
        let cmd = event.modifierFlags.contains(.command)
        let ch = event.charactersIgnoringModifiers?.lowercased() ?? ""
        switch event.keyCode {
        case 53: session?.close(); return                                  // Esc
        case 36, 76:                                                       // Enter
            if pickMode { session?.confirmPick() } else if selection != nil { session?.finish(save: false) }
            return
        default: break
        }
        if cmd && (ch == "c" || ch == "с") && !pickMode && selection != nil { session?.finish(save: false); return }
        if cmd && (ch == "s" || ch == "ы") && !pickMode && selection != nil { session?.finish(save: true); return }
        if cmd && (ch == "z" || ch == "я") { undo(); return }
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
            let accent: NSColor = pickMode ? .systemRed : .white
            ctx.setStrokeColor(accent.cgColor)
            ctx.setLineWidth(pickMode ? 2 : 1)
            if pickMode { ctx.setLineDash(phase: 0, lengths: [6, 4]) }
            ctx.stroke(s.insetBy(dx: -0.5, dy: -0.5))
            ctx.setLineDash(phase: 0, lengths: [])
            for h in handles(s) {
                let r = CGRect(x: h.x - 4, y: h.y - 4, width: 8, height: 8)
                ctx.setFillColor(NSColor.white.cgColor); ctx.fill(r)
                ctx.setStrokeColor(NSColor(white: 0, alpha: 0.5).cgColor); ctx.setLineWidth(1); ctx.stroke(r)
            }
            sizeLabel(ctx, s)
        } else {
            hint(ctx)
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
        for m in marks { Painter.draw(m, ctx: ctx, pixelated: pixelated, bounds: bounds) }
        if let c = current { Painter.draw(c, ctx: ctx, pixelated: pixelated, bounds: bounds) }
        ctx.restoreGState()
    }

    private func sizeLabel(_ ctx: CGContext, _ s: CGRect) {
        let text = "\(Int((s.width * scale).rounded())) × \(Int((s.height * scale).rounded()))" as NSString
        let a: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium), .foregroundColor: NSColor.white]
        let sz = text.size(withAttributes: a)
        var r = CGRect(x: s.minX, y: s.minY - sz.height - 10, width: sz.width + 14, height: sz.height + 6)
        if r.minY < 4 { r.origin.y = s.minY + 6; r.origin.x = s.minX + 6 }
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: 6, cornerHeight: 6, transform: nil))
        ctx.setFillColor(NSColor(white: 0, alpha: 0.65).cgColor)
        ctx.fillPath()
        text.draw(at: CGPoint(x: r.minX + 7, y: r.minY + 3), withAttributes: a)
    }

    private func hint(_ ctx: CGContext) {
        let text = (pickMode ? "●  Запись экрана — выделите область · щелчок — весь экран · Esc — отмена"
                             : "Выделите область · щелчок — весь экран · Esc — отмена") as NSString
        let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 15, weight: .medium), .foregroundColor: NSColor.white]
        let sz = text.size(withAttributes: a)
        let r = CGRect(x: bounds.midX - sz.width / 2 - 18, y: bounds.midY - sz.height / 2 - 10, width: sz.width + 36, height: sz.height + 20)
        ctx.addPath(CGPath(roundedRect: r, cornerWidth: 12, cornerHeight: 12, transform: nil))
        ctx.setFillColor((pickMode ? NSColor.systemRed.withAlphaComponent(0.85) : NSColor(white: 0, alpha: 0.6)).cgColor)
        ctx.fillPath()
        text.draw(at: CGPoint(x: r.minX + 18, y: r.minY + 10), withAttributes: a)
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
    private var colorButton: NSButton?
    private var built = false

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
        guard !built, let session else { return }
        built = true
        if session.pick != nil {
            let start = NSButton(title: "  Начать запись  ", target: self, action: #selector(startRec))
            start.bezelStyle = .rounded
            start.bezelColor = .systemRed
            stack.addArrangedSubview(start)
            stack.addArrangedSubview(button("xmark", "Отмена (Esc)", #selector(closeIt)))
            return
        }
        for t in Tool.allCases {
            let b = button(t.symbol, t.title, #selector(pickTool(_:)))
            b.tag = t.rawValue
            toolButtons.append(b)
            stack.addArrangedSubview(b)
        }
        stack.addArrangedSubview(separator())
        let c = button("circle.fill", "Цвет", #selector(pickColor(_:)))
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

    func refresh() {
        build()
        guard let session else { return }
        for b in toolButtons {
            let on = session.tool?.rawValue == b.tag
            b.contentTintColor = on ? .controlAccentColor : .white
            b.wantsLayer = true
            b.layer?.cornerRadius = 6
            b.layer?.backgroundColor = on ? NSColor(white: 1, alpha: 0.18).cgColor : nil
        }
        colorButton?.contentTintColor = session.color
    }

    @objc private func pickTool(_ b: NSButton) {
        guard let session, let t = Tool(rawValue: b.tag) else { return }
        session.tool = session.tool == t ? nil : t
    }

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
    @objc private func startRec() { session?.confirmPick() }
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
