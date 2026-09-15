import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Generates the small, raster UI surfaces used by the WiX theme. Keeping the
// source here makes the assets reproducible without introducing a third-party
// image tool into the Windows build.

struct ThemeColor {
    let red: CGFloat
    let green: CGFloat
    let blue: CGFloat

    init(_ hex: UInt32) {
        red = CGFloat((hex >> 16) & 0xff) / 255
        green = CGFloat((hex >> 8) & 0xff) / 255
        blue = CGFloat(hex & 0xff) / 255
    }

    var cgColor: CGColor {
        CGColor(red: red, green: green, blue: blue, alpha: 1)
    }
}

let outputDirectory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)

func roundedPath(width: CGFloat, height: CGFloat, radius: CGFloat, inset: CGFloat = 0) -> CGPath {
    CGPath(
        roundedRect: CGRect(x: inset, y: inset, width: width - inset * 2, height: height - inset * 2),
        cornerWidth: radius,
        cornerHeight: radius,
        transform: nil)
}

func makeSurface(
    name: String,
    width: Int,
    height: Int,
    radius: CGFloat,
    fill: ThemeColor,
    stroke: ThemeColor? = nil,
    strokeWidth: CGFloat = 0) throws {
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw NSError(domain: "FilesThemeAssets", code: 1, userInfo: [NSLocalizedDescriptionKey: "Could not create bitmap context"])
    }

    context.clear(CGRect(x: 0, y: 0, width: width, height: height))
    let path = roundedPath(width: CGFloat(width), height: CGFloat(height), radius: radius, inset: stroke == nil ? 0 : strokeWidth / 2)
    context.addPath(path)
    context.setFillColor(fill.cgColor)
    context.fillPath()

    if let stroke {
        context.addPath(path)
        context.setStrokeColor(stroke.cgColor)
        context.setLineWidth(strokeWidth)
        context.strokePath()
    }

    guard let image = context.makeImage() else {
        throw NSError(domain: "FilesThemeAssets", code: 2, userInfo: [NSLocalizedDescriptionKey: "Could not create image"])
    }

    let outputURL = outputDirectory.appendingPathComponent(name)
    guard let destination = CGImageDestinationCreateWithURL(outputURL as CFURL, UTType.png.identifier as CFString, 1, nil) else {
        throw NSError(domain: "FilesThemeAssets", code: 3, userInfo: [NSLocalizedDescriptionKey: "Could not create PNG destination"])
    }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        throw NSError(domain: "FilesThemeAssets", code: 4, userInfo: [NSLocalizedDescriptionKey: "Could not write PNG"])
    }
}

let cardFill = ThemeColor(0xffffff)
let cardBorder = ThemeColor(0xd8dee9)
try makeSurface(name: "Files-Setup.Card.png", width: 448, height: 178, radius: 12, fill: cardFill, stroke: cardBorder, strokeWidth: 1)
try makeSurface(name: "Files-Setup.CardTall.png", width: 448, height: 220, radius: 12, fill: cardFill, stroke: cardBorder, strokeWidth: 1)

let primaryNormal = ThemeColor(0x2f67e8)
let primaryHover = ThemeColor(0x2458c9)
let primarySelected = ThemeColor(0x1d4ed8)
let secondaryNormal = ThemeColor(0xffffff)
let secondaryHover = ThemeColor(0xf0f5ff)
let secondarySelected = ThemeColor(0xe4edff)

for (suffix, color) in [("", primaryNormal), ("Hover", primaryHover), ("Selected", primarySelected)] {
    try makeSurface(name: "Files-Setup.ButtonPrimary\(suffix).png", width: 64, height: 30, radius: 8, fill: color)
    try makeSurface(name: "Files-Setup.ButtonPrimaryWide\(suffix).png", width: 140, height: 30, radius: 8, fill: color)
}

for (suffix, color) in [("", secondaryNormal), ("Hover", secondaryHover), ("Selected", secondarySelected)] {
    try makeSurface(name: "Files-Setup.ButtonSecondary\(suffix).png", width: 64, height: 30, radius: 8, fill: color, stroke: cardBorder, strokeWidth: 1)
}

