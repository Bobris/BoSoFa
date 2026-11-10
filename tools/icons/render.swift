// Renders brands/icons/icon.svg into the app and web icons, with macOS's own SVG support (no other tool needed):
//   swift tools/icons/render.swift brands/icons web/apps/web/public
// writes, next to the SVG: icon-<size>.png, the macOS app icon AppIcon.icns (drawn inside Apple's icon grid, with its shadow),
// and icon.ico for Windows; and into the web page's public folder the favicons and the touch icon.
import AppKit

let folder = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "brands/icons")
guard let svg = NSImage(contentsOf: folder.appendingPathComponent("icon.svg")) else { fatalError("icon.svg cannot be read") }

func png(_ size: Int, macGrid: Bool) -> Data {
    let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
                                  hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSGraphicsContext.current?.imageInterpolation = .high
    let full = CGFloat(size)
    var rect = NSRect(x: 0, y: 0, width: full, height: full)
    if macGrid {
        // Apple's grid: the shape is 824 of 1024, centred a little high, with a soft shadow below.
        let side = full * 824 / 1024
        rect = NSRect(x: (full - side) / 2, y: (full - side) / 2 + full * 10 / 1024, width: side, height: side)
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
        shadow.shadowBlurRadius = full * 28 / 1024
        shadow.shadowOffset = NSSize(width: 0, height: -full * 12 / 1024)
        shadow.set()
    }
    svg.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1)
    NSGraphicsContext.restoreGraphicsState()
    return bitmap.representation(using: .png, properties: [:])!
}

for size in [16, 32, 48, 180, 192, 256, 512] {
    try png(size, macGrid: false).write(to: folder.appendingPathComponent("icon-\(size).png"))
}

// macOS: an iconset folder made into AppIcon.icns by iconutil.
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("AppIcon.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
for base in [16, 32, 128, 256, 512] {
    try png(base, macGrid: true).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try png(base * 2, macGrid: true).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", folder.appendingPathComponent("AppIcon.icns").path]
try iconutil.run()
iconutil.waitUntilExit()

// Windows: an .ico holding PNG images, which Windows Vista and later read.
let sizes = [16, 24, 32, 48, 64, 256]
let images = sizes.map { png($0, macGrid: false) }
var ico = Data([0, 0, 1, 0, UInt8(sizes.count), 0])
var offset = 6 + 16 * sizes.count
for (size, image) in zip(sizes, images) {
    let side = UInt8(size >= 256 ? 0 : size)
    ico.append(contentsOf: [side, side, 0, 0, 1, 0, 32, 0])
    withUnsafeBytes(of: UInt32(image.count).littleEndian) { ico.append(contentsOf: $0) }
    withUnsafeBytes(of: UInt32(offset).littleEndian) { ico.append(contentsOf: $0) }
    offset += image.count
}
for image in images { ico.append(image) }
try ico.write(to: folder.appendingPathComponent("icon.ico"))
// The web page: the SVG for browsers that take it, PNGs for the rest and for home screens.
if CommandLine.arguments.count > 2 {
    let web = URL(fileURLWithPath: CommandLine.arguments[2])
    try FileManager.default.createDirectory(at: web, withIntermediateDirectories: true)
    for (source, target) in [("icon.svg", "icon.svg"), ("icon-32.png", "icon-32.png"), ("icon-180.png", "apple-touch-icon.png"), ("icon-192.png", "icon-192.png"), ("icon-512.png", "icon-512.png")] {
        let destination = web.appendingPathComponent(target)
        try? FileManager.default.removeItem(at: destination)
        try FileManager.default.copyItem(at: folder.appendingPathComponent(source), to: destination)
    }
}
print("Rendered \(folder.path)")
