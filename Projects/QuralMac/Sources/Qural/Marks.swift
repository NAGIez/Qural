import AppKit
import CoreImage

// Пометки на скриншоте и их рисование. Координаты — точки экрана, начало в левом верхнем углу
// (вид перевёрнут, isFlipped). Один и тот же код рисует и на экране, и в сохраняемую картинку.

enum Tool: Int, CaseIterable {
    case hand, pen, line, arrow, rect, ellipse, marker, text, blur, highlight, step

    var symbol: String {
        switch self {
        case .hand: return "hand.raised"
        case .pen: return "pencil"
        case .line: return "line.diagonal"
        case .arrow: return "arrow.up.right"
        case .rect: return "rectangle"
        case .ellipse: return "circle"
        case .marker: return "highlighter"
        case .text: return "textformat"
        case .blur: return "eye.slash"
        case .highlight: return "square.dashed.inset.filled"
        case .step: return "1.circle"
        }
    }

    var title: String {
        switch self {
        case .hand: return "Рука — двигать, менять размер, править текст (двойной щелчок), Delete — удалить"
        case .pen: return "Карандаш"
        case .line: return "Линия"
        case .arrow: return "Стрелка"
        case .rect: return "Прямоугольник"
        case .ellipse: return "Овал"
        case .marker: return "Маркер"
        case .text: return "Текст"
        case .blur: return "Размытие — спрятать (пиксели)"
        case .highlight: return "Подсветка области — всё вокруг затемнить или размыть"
        case .step: return "Номер шага"
        }
    }
}

struct Mark {
    enum Kind {
        case pen([CGPoint])
        case marker([CGPoint])
        case line(CGPoint, CGPoint)
        case arrow(CGPoint, CGPoint)
        case rect(CGRect)
        case ellipse(CGRect)
        case blur(CGRect)
        case highlight(CGRect)
        case text(String, CGPoint, CGFloat)   // текст, левый верхний угол, размер шрифта
        case step(Int, CGPoint, CGFloat)      // номер, центр, радиус
    }
    var kind: Kind
    var color: NSColor
    var width: CGFloat

    /// Есть ли что сохранять (а не случайный щелчок).
    var meaningful: Bool {
        switch kind {
        case .pen(let p), .marker(let p): return p.count > 1
        case .line(let a, let b), .arrow(let a, let b): return hypot(b.x - a.x, b.y - a.y) > 3
        case .rect(let r), .ellipse(let r), .blur(let r), .highlight(let r): return r.width > 3 && r.height > 3
        case .text(let s, _, _): return !s.isEmpty
        case .step: return true
        }
    }

    var isHighlight: Bool { if case .highlight = kind { return true } else { return false } }
    /// Надпись и номер растут без искажений.
    var uniform: Bool {
        switch kind { case .text, .step: return true; default: return false }
    }

    static func font(_ size: CGFloat) -> NSFont { .systemFont(ofSize: size, weight: .bold) }

    /// Рамка вокруг пометки.
    var box: CGRect {
        switch kind {
        case .pen(let p), .marker(let p):
            guard let f = p.first else { return .zero }
            var r = CGRect(origin: f, size: .zero)
            for q in p { r = r.union(CGRect(origin: q, size: .zero)) }
            return r
        case .line(let a, let b), .arrow(let a, let b): return rectBetween(a, b)
        case .rect(let r), .ellipse(let r), .blur(let r), .highlight(let r): return r
        case .text(let s, let at, let size):
            let sz = (s as NSString).size(withAttributes: [.font: Mark.font(size)])
            return CGRect(origin: at, size: sz)
        case .step(_, let c, let r): return CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)
        }
    }

    /// Попала ли точка в пометку (для «Руки»).
    func hit(_ p: CGPoint) -> Bool {
        let tol = max(7, width / 2 + 5)
        switch kind {
        case .pen(let pts), .marker(let pts):
            let t = isMarker ? max(tol, width * 2 + 4) : tol
            if pts.count == 1 { return hypot(pts[0].x - p.x, pts[0].y - p.y) <= t }
            for i in 1..<pts.count where segDist(p, pts[i - 1], pts[i]) <= t { return true }
            return false
        case .line(let a, let b), .arrow(let a, let b): return segDist(p, a, b) <= tol
        case .rect(let r), .ellipse(let r):
            // по контуру (внутри можно рисовать другое), маленькие — целиком
            if r.width < 30 || r.height < 30 { return r.insetBy(dx: -tol, dy: -tol).contains(p) }
            return r.insetBy(dx: -tol, dy: -tol).contains(p) && !r.insetBy(dx: tol, dy: tol).contains(p)
        case .blur(let r), .highlight(let r): return r.insetBy(dx: -4, dy: -4).contains(p)
        case .text, .step: return box.insetBy(dx: -6, dy: -6).contains(p)
        }
    }

    private var isMarker: Bool { if case .marker = kind { return true } else { return false } }

    func moved(_ dx: CGFloat, _ dy: CGFloat) -> Mark {
        var m = self
        let f = { (q: CGPoint) in CGPoint(x: q.x + dx, y: q.y + dy) }
        switch kind {
        case .pen(let p): m.kind = .pen(p.map(f))
        case .marker(let p): m.kind = .marker(p.map(f))
        case .line(let a, let b): m.kind = .line(f(a), f(b))
        case .arrow(let a, let b): m.kind = .arrow(f(a), f(b))
        case .rect(let r): m.kind = .rect(r.offsetBy(dx: dx, dy: dy))
        case .ellipse(let r): m.kind = .ellipse(r.offsetBy(dx: dx, dy: dy))
        case .blur(let r): m.kind = .blur(r.offsetBy(dx: dx, dy: dy))
        case .highlight(let r): m.kind = .highlight(r.offsetBy(dx: dx, dy: dy))
        case .text(let s, let at, let size): m.kind = .text(s, f(at), size)
        case .step(let n, let c, let r): m.kind = .step(n, f(c), r)
        }
        return m
    }

    /// Растянуть из рамки `from` в рамку `to` (у надписи и номера — равномерно, размер шрифта / круга вслед).
    func scaled(from o: CGRect, to n: CGRect) -> Mark {
        var m = self
        let sx = o.width > 0.5 ? n.width / o.width : 1, sy = o.height > 0.5 ? n.height / o.height : 1
        let f = { (q: CGPoint) in CGPoint(x: n.minX + (q.x - o.minX) * sx, y: n.minY + (q.y - o.minY) * sy) }
        switch kind {
        case .pen(let p): m.kind = .pen(p.map(f))
        case .marker(let p): m.kind = .marker(p.map(f))
        case .line(let a, let b): m.kind = .line(f(a), f(b))
        case .arrow(let a, let b): m.kind = .arrow(f(a), f(b))
        case .rect: m.kind = .rect(n)
        case .ellipse: m.kind = .ellipse(n)
        case .blur: m.kind = .blur(n)
        case .highlight: m.kind = .highlight(n)
        case .text(let s, _, let size): m.kind = .text(s, n.origin, max(8, min(400, size * sx)))
        case .step(let k, _, let r): m.kind = .step(k, CGPoint(x: n.midX, y: n.midY), max(8, min(200, r * sx)))
        }
        return m
    }

    /// Крупнее / мельче (колёсико для надписи и номера), от левого верхнего угла / центра.
    func resized(by k: CGFloat) -> Mark {
        var m = self
        switch kind {
        case .text(let s, let at, let size): m.kind = .text(s, at, max(8, min(400, (size * k).rounded())))
        case .step(let n, let c, let r): m.kind = .step(n, c, max(8, min(200, r * k)))
        default: break
        }
        return m
    }
}

func rectBetween(_ a: CGPoint, _ b: CGPoint) -> CGRect {
    CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
}

private func segDist(_ p: CGPoint, _ a: CGPoint, _ b: CGPoint) -> CGFloat {
    let dx = b.x - a.x, dy = b.y - a.y
    let l2 = dx * dx + dy * dy
    if l2 < 0.01 { return hypot(p.x - a.x, p.y - a.y) }
    let t = max(0, min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / l2))
    return hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy))
}

@MainActor
enum Painter {
    /// pixelated — вся картинка «пикселями» (для размытия), bounds — где она лежит.
    static func draw(_ m: Mark, ctx: CGContext, pixelated: NSImage?, bounds: CGRect) {
        ctx.saveGState()
        defer { ctx.restoreGState() }
        ctx.setLineCap(.round)
        ctx.setLineJoin(.round)
        ctx.setStrokeColor(m.color.cgColor)
        ctx.setFillColor(m.color.cgColor)
        ctx.setLineWidth(m.width)
        switch m.kind {
        case .pen(let pts):
            stroke(pts, ctx)
        case .marker(let pts):
            ctx.setStrokeColor(m.color.withAlphaComponent(0.38).cgColor)
            ctx.setLineWidth(max(14, m.width * 4))
            ctx.setBlendMode(.multiply)
            stroke(pts, ctx)
        case .line(let a, let b):
            ctx.move(to: a); ctx.addLine(to: b); ctx.strokePath()
        case .arrow(let a, let b):
            arrow(a, b, m.width, ctx)
        case .rect(let r):
            ctx.addPath(CGPath(roundedRect: r, cornerWidth: min(3, r.width / 2), cornerHeight: min(3, r.height / 2), transform: nil))
            ctx.strokePath()
        case .ellipse(let r):
            ctx.strokeEllipse(in: r)
        case .blur(let r):
            guard let p = pixelated else { break }
            ctx.clip(to: r)
            p.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        case .highlight:
            break   // рисуется отдельно, одним слоем (OverlayView.drawHighlight)
        case .text(let s, let at, let size):
            let shadow = NSShadow()
            shadow.shadowColor = NSColor(white: 0, alpha: 0.55)
            shadow.shadowBlurRadius = 3
            shadow.shadowOffset = CGSize(width: 0, height: -1)
            (s as NSString).draw(at: at, withAttributes: [.font: Mark.font(size), .foregroundColor: m.color, .shadow: shadow])
        case .step(let n, let c, let r):
            let box = CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)
            ctx.fillEllipse(in: box)
            ctx.setStrokeColor(NSColor.white.cgColor)
            ctx.setLineWidth(max(1.5, r / 8))
            ctx.strokeEllipse(in: box)
            let s = "\(n)" as NSString
            let a: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: r * 1.05, weight: .bold), .foregroundColor: NSColor.white]
            let sz = s.size(withAttributes: a)
            s.draw(at: CGPoint(x: c.x - sz.width / 2, y: c.y - sz.height / 2), withAttributes: a)
        }
    }

    private static func stroke(_ pts: [CGPoint], _ ctx: CGContext) {
        guard let f = pts.first else { return }
        ctx.move(to: f)
        if pts.count == 1 { ctx.addLine(to: CGPoint(x: f.x + 0.1, y: f.y)) }
        for p in pts.dropFirst() { ctx.addLine(to: p) }
        ctx.strokePath()
    }

    private static func arrow(_ a: CGPoint, _ b: CGPoint, _ w: CGFloat, _ ctx: CGContext) {
        let ang = atan2(b.y - a.y, b.x - a.x)
        let head = max(16, w * 4.2)
        let base = CGPoint(x: b.x - head * 0.75 * cos(ang), y: b.y - head * 0.75 * sin(ang))
        ctx.move(to: a); ctx.addLine(to: base); ctx.strokePath()
        ctx.move(to: b)
        ctx.addLine(to: CGPoint(x: b.x - head * cos(ang - .pi / 7), y: b.y - head * sin(ang - .pi / 7)))
        ctx.addLine(to: CGPoint(x: b.x - head * cos(ang + .pi / 7), y: b.y - head * sin(ang + .pi / 7)))
        ctx.closePath()
        ctx.fillPath()
    }

    /// Вся картинка крупными «пикселями» — для инструмента «Размытие».
    static func pixelate(_ cg: CGImage, size: CGSize, scale: CGFloat) -> NSImage? {
        filtered(cg, size: size) { ci in
            let f = CIFilter(name: "CIPixellate")
            f?.setValue(ci.clampedToExtent(), forKey: kCIInputImageKey)
            f?.setValue(12 * scale, forKey: kCIInputScaleKey)
            return f?.outputImage
        }
    }

    /// Вся картинка размыта (подсветка области: «размыть вокруг»), level 1…10.
    static func blurred(_ cg: CGImage, size: CGSize, scale: CGFloat, level: Int) -> NSImage? {
        filtered(cg, size: size) { ci in
            let f = CIFilter(name: "CIGaussianBlur")
            f?.setValue(ci.clampedToExtent(), forKey: kCIInputImageKey)
            f?.setValue(CGFloat(2 + level * 3) * scale, forKey: kCIInputRadiusKey)
            return f?.outputImage
        }
    }

    private static func filtered(_ cg: CGImage, size: CGSize, _ apply: (CIImage) -> CIImage?) -> NSImage? {
        let ci = CIImage(cgImage: cg)
        guard let out = apply(ci)?.cropped(to: ci.extent),
              let res = CIContext().createCGImage(out, from: ci.extent) else { return nil }
        return NSImage(cgImage: res, size: size)
    }
}
