import AppKit

// Плашки вверху экрана: уведомление (Toast) и отсчёт перед записью (Countdown).
// Обе не попадают на скриншоты и в запись (sharingType = .none) и не забирают фокус.

@MainActor
private func makePanel(size: CGSize, on screen: NSScreen, top: CGFloat = 14) -> NSPanel {
    let f = screen.visibleFrame
    let rect = CGRect(x: f.midX - size.width / 2, y: f.maxY - size.height - top, width: size.width, height: size.height)
    let p = NSPanel(contentRect: rect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
    p.isReleasedWhenClosed = false
    p.level = .statusBar
    p.isOpaque = false
    p.backgroundColor = .clear
    p.hasShadow = true
    p.sharingType = .none
    p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
    return p
}

@MainActor
private func pill(_ ctx: CGContext, _ r: CGRect) {
    let path = CGPath(roundedRect: r, cornerWidth: r.height / 2.8, cornerHeight: r.height / 2.8, transform: nil)
    ctx.addPath(path)
    ctx.setFillColor(NSColor(calibratedRed: 0.09, green: 0.1, blue: 0.12, alpha: 0.96).cgColor)
    ctx.fillPath()
    ctx.addPath(path)
    ctx.setStrokeColor(NSColor(white: 1, alpha: 0.1).cgColor)
    ctx.setLineWidth(1)
    ctx.strokePath()
}

// ── уведомление: текст и (необязательно) действие по щелчку ──
@MainActor
enum Toast {
    private static var panel: NSPanel?
    private static var timer: Timer?

    static func show(_ text: String, action: String? = nil, _ handler: (() -> Void)? = nil) {
        hide()
        let font = NSFont.systemFont(ofSize: 13, weight: .medium)
        let full = action != nil ? text + "   " : text
        let w1 = (full as NSString).size(withAttributes: [.font: font]).width
        let w2 = action.map { ($0 as NSString).size(withAttributes: [.font: font]).width } ?? 0
        let size = CGSize(width: min(640, ceil(w1 + w2 + 40)), height: 40)
        let p = makePanel(size: size, on: NSScreen.underMouse)
        let v = ToastView(frame: CGRect(origin: .zero, size: size))
        v.text = full; v.action = action; v.font = font
        v.onClick = { hide(); handler?() }
        p.contentView = v
        p.orderFrontRegardless()
        panel = p
        timer = Timer.scheduledTimer(withTimeInterval: action != nil ? 6 : 3, repeats: false) { _ in
            MainActor.assumeIsolated { hide() }
        }
    }

    static func hide() {
        timer?.invalidate(); timer = nil
        panel?.orderOut(nil); panel = nil
    }
}

private final class ToastView: NSView {
    var text = "", action: String?, font = NSFont.systemFont(ofSize: 13)
    var onClick: (() -> Void)?

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        pill(ctx, bounds.insetBy(dx: 0.5, dy: 0.5))
        let a: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.white]
        let h = (text as NSString).size(withAttributes: a).height
        (text as NSString).draw(at: CGPoint(x: 20, y: (bounds.height - h) / 2), withAttributes: a)
        if let action {
            let w = (text as NSString).size(withAttributes: a).width
            (action as NSString).draw(at: CGPoint(x: 20 + w, y: (bounds.height - h) / 2),
                                      withAttributes: [.font: font, .foregroundColor: NSColor.systemBlue])
        }
    }

    override func mouseDown(with event: NSEvent) { onClick?() }
}

// ── отсчёт перед записью: «● Запись через 3», полоска тает за 3 секунды; щелчок — отмена ──
@MainActor
final class Countdown {
    private static var current: Countdown?
    private let panel: NSPanel
    private let view: CountdownView
    private var timer: Timer?
    private let start = Date()
    private let done: () -> Void

    static var active: Bool { current != nil }

    static func show(on screen: NSScreen, _ done: @escaping () -> Void) {
        current?.cancel()
        current = Countdown(screen: screen, done: done)
    }

    static func cancelActive() { current?.cancel() }

    private init(screen: NSScreen, done: @escaping () -> Void) {
        self.done = done
        let size = CGSize(width: 300, height: 58)
        panel = makePanel(size: size, on: screen, top: 10)
        view = CountdownView(frame: CGRect(origin: .zero, size: size))
        panel.contentView = view
        view.onClick = { [weak self] in self?.cancel() }
        panel.orderFrontRegardless()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        tick()
    }

    private func tick() {
        let t = Date().timeIntervalSince(start)
        if t >= 3 { finish(run: true); return }
        view.t = t
        view.needsDisplay = true
    }

    private func cancel() { finish(run: false) }

    private func finish(run: Bool) {
        timer?.invalidate(); timer = nil
        panel.orderOut(nil)
        if Countdown.current === self { Countdown.current = nil }
        if run { done() }
    }
}

private final class CountdownView: NSView {
    var t: TimeInterval = 0
    var onClick: (() -> Void)?

    override func draw(_ dirtyRect: NSRect) {
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let card = bounds.insetBy(dx: 0.5, dy: 0.5)
        pill(ctx, card)
        // полоска снизу тает справа налево
        ctx.saveGState()
        ctx.addPath(CGPath(roundedRect: card, cornerWidth: card.height / 2.8, cornerHeight: card.height / 2.8, transform: nil))
        ctx.clip()
        ctx.setFillColor(NSColor(white: 1, alpha: 0.15).cgColor)
        ctx.fill(CGRect(x: card.minX, y: card.minY, width: card.width, height: 3))
        ctx.setFillColor(NSColor.systemRed.cgColor)
        ctx.fill(CGRect(x: card.minX, y: card.minY, width: card.width * CGFloat(max(0, 1 - t / 3)), height: 3))
        ctx.restoreGState()
        // пульсирующая точка
        let ph = CGFloat((sin(t * .pi * 2 - .pi / 2) + 1) / 2)
        let c = CGPoint(x: 26, y: bounds.midY + 1)
        let halo = 9 + 6 * ph
        ctx.setFillColor(NSColor.systemRed.withAlphaComponent(0.08 + 0.27 * (1 - ph)).cgColor)
        ctx.fillEllipse(in: CGRect(x: c.x - halo, y: c.y - halo, width: halo * 2, height: halo * 2))
        ctx.setFillColor(NSColor.systemRed.cgColor)
        ctx.fillEllipse(in: CGRect(x: c.x - 6, y: c.y - 6, width: 12, height: 12))
        // подписи
        ("Запись через" as NSString).draw(at: CGPoint(x: 46, y: 28), withAttributes: [
            .font: NSFont.systemFont(ofSize: 15, weight: .semibold), .foregroundColor: NSColor.white])
        ("щелчок — отмена" as NSString).draw(at: CGPoint(x: 47, y: 11), withAttributes: [
            .font: NSFont.systemFont(ofSize: 12), .foregroundColor: NSColor(white: 1, alpha: 0.6)])
        // цифра справа «подпрыгивает» на каждой секунде
        let n = max(1, 3 - Int(t))
        let inSec = CGFloat(t - floor(t))
        let k = 1 - min(1, inSec / 0.35)
        let pop: CGFloat = t < 1 ? 1 : 1 + 0.28 * k * k
        let font = NSFont.systemFont(ofSize: 28 * pop, weight: .bold)
        let s = "\(n)" as NSString
        let a: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor(white: 1, alpha: min(1, 0.55 + inSec * 3))]
        let sz = s.size(withAttributes: a)
        s.draw(at: CGPoint(x: bounds.maxX - 40 - sz.width / 2, y: bounds.midY - sz.height / 2 + 1), withAttributes: a)
    }

    override func mouseDown(with event: NSEvent) { onClick?() }
}
