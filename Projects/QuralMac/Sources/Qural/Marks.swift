import AppKit
import CoreImage

// Пометки на скриншоте и их рисование. Координаты — точки экрана, начало в левом верхнем углу
// (вид перевёрнут, isFlipped). Один и тот же код рисует и на экране, и в сохраняемую картинку.

enum Tool: Int, CaseIterable {
    case pen, line, arrow, rect, ellipse, marker, text, blur, step

    var symbol: String {
        switch self {
        case .pen: return "pencil"
        case .line: return "line.diagonal"
        case .arrow: return "arrow.up.right"
        case .rect: return "rectangle"
        case .ellipse: return "circle"
        case .marker: return "highlighter"
        case .text: return "textformat"
        case .blur: return "square.grid.3x3"
        case .step: return "1.circle"
        }
    }

    var title: String {
        switch self {
        case .pen: return "Карандаш"
        case .line: return "Линия"
        case .arrow: return "Стрелка"
        case .rect: return "Прямоугольник"
        case .ellipse: return "Овал"
        case .marker: return "Маркер"
        case .text: return "Текст"
        case .blur: return "Размытие"
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
        case text(String, CGPoint, CGFloat)
        case step(Int, CGPoint)
    }
    var kind: Kind
    var color: NSColor
    var width: CGFloat

    /// Есть ли что сохранять (а не случайный щелчок).
    var meaningful: Bool {
        switch kind {
        case .pen(let p), .marker(let p): return p.count > 1
        case .line(let a, let b), .arrow(let a, let b): return hypot(b.x - a.x, b.y - a.y) > 3
        case .rect(let r), .ellipse(let r), .blur(let r): return r.width > 3 && r.height > 3
        case .text(let s, _, _): return !s.isEmpty
        case .step: return true
        }
    }
}

func rectBetween(_ a: CGPoint, _ b: CGPoint) -> CGRect {
    CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
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
            ctx.addPath(CGPath(roundedRect: r, cornerWidth: 3, cornerHeight: 3, transform: nil))
            ctx.strokePath()
        case .ellipse(let r):
            ctx.strokeEllipse(in: r)
        case .blur(let r):
            guard let p = pixelated else { break }
            ctx.clip(to: r)
            p.draw(in: bounds, from: .zero, operation: .copy, fraction: 1, respectFlipped: true, hints: nil)
        case .text(let s, let at, let size):
            let shadow = NSShadow()
            shadow.shadowColor = NSColor(white: 0, alpha: 0.55)
            shadow.shadowBlurRadius = 3
            shadow.shadowOffset = CGSize(width: 0, height: -1)
            (s as NSString).draw(at: at, withAttributes: [
                .font: NSFont.systemFont(ofSize: size, weight: .bold), .foregroundColor: m.color, .shadow: shadow])
        case .step(let n, let c):
            let r: CGFloat = 13 + m.width
            ctx.fillEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
            ctx.setStrokeColor(NSColor.white.cgColor)
            ctx.setLineWidth(2)
            ctx.strokeEllipse(in: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
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
        let ci = CIImage(cgImage: cg)
        guard let f = CIFilter(name: "CIPixellate") else { return nil }
        f.setValue(ci.clampedToExtent(), forKey: kCIInputImageKey)
        f.setValue(12 * scale, forKey: kCIInputScaleKey)
        guard let out = f.outputImage?.cropped(to: ci.extent),
              let res = CIContext().createCGImage(out, from: ci.extent) else { return nil }
        return NSImage(cgImage: res, size: size)
    }
}
