// Draws the app icon (1024×1024 PNG): a rounded square in the app's accent blue, a house outline,
// and a flood line across it. Usage: swift make_icon.swift out.png
import AppKit

let size: CGFloat = 1024
let out = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon.png"
let image = NSImage(size: NSSize(width: size, height: size))
image.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext

// macOS icon grid: 824×824 body, 100 px margins, ~185 px corner radius.
let body = CGRect(x: 100, y: 100, width: 824, height: 824)
let bodyPath = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)
ctx.addPath(bodyPath)
ctx.clip()
let colors = [CGColor(red: 0.16, green: 0.42, blue: 0.78, alpha: 1), CGColor(red: 0.08, green: 0.27, blue: 0.55, alpha: 1)] as CFArray
let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(), colors: colors, locations: [0, 1])!
ctx.drawLinearGradient(gradient, start: CGPoint(x: 0, y: 924), end: CGPoint(x: 0, y: 100), options: [])

// House outline
ctx.setStrokeColor(CGColor(gray: 1, alpha: 1))
ctx.setLineWidth(46)
ctx.setLineJoin(.round)
ctx.setLineCap(.round)
let house = CGMutablePath()
house.move(to: CGPoint(x: 300, y: 300))
house.addLine(to: CGPoint(x: 300, y: 560))
house.addLine(to: CGPoint(x: 512, y: 740))
house.addLine(to: CGPoint(x: 724, y: 560))
house.addLine(to: CGPoint(x: 724, y: 300))
house.closeSubpath()
ctx.addPath(house)
ctx.strokePath()

// Water: a filled wave covering the lower part of the house
let water = CGMutablePath()
water.move(to: CGPoint(x: 100, y: 100))
water.addLine(to: CGPoint(x: 100, y: 430))
var x: CGFloat = 100
while x <= 924 {
	let y = 430 + 26 * sin((x - 100) / 824 * .pi * 4)
	water.addLine(to: CGPoint(x: x, y: y))
	x += 8
}
water.addLine(to: CGPoint(x: 924, y: 100))
water.closeSubpath()
ctx.addPath(water)
ctx.setFillColor(CGColor(red: 0.55, green: 0.80, blue: 1.0, alpha: 0.85))
ctx.fillPath()

// 50% line
ctx.setStrokeColor(CGColor(red: 1, green: 0.78, blue: 0.30, alpha: 1))
ctx.setLineWidth(18)
ctx.setLineDash(phase: 0, lengths: [40, 26])
ctx.move(to: CGPoint(x: 210, y: 520))
ctx.addLine(to: CGPoint(x: 814, y: 520))
ctx.strokePath()

image.unlockFocus()
let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: out))
