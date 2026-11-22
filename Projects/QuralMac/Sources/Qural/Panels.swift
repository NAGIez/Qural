import AppKit

// Плашка-уведомление вверху экрана (Toast). Не попадает на скриншоты (sharingType = .none) и не забирает фокус.

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
