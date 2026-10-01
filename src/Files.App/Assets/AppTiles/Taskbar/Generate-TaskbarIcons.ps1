param(
    [string]$AssetsRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = Join-Path $PSScriptRoot 'FilesMaxIconSource.png'
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
$iconSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)
$logoNames = @('BadgeLogo', 'Large310x310Logo', 'Small71x71Logo', 'SplashScreen', 'Square150x150Logo', 'Square44x44Logo', 'StoreLogo', 'Wide310x150Logo')

function Get-RenderedPng([int]$width, [int]$height, [double]$fill = 1.0) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $size = [int]([Math]::Min($width, $height) * $fill)
        $graphics.DrawImage($source, [int](($width - $size) / 2), [int](($height - $size) / 2), $size, $size)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            return ,$stream.ToArray()
        } finally {
            $stream.Dispose()
        }
    } finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

try {
    $pngByDimensions = @{}
    foreach ($size in $iconSizes) {
        $pngByDimensions["${size}x${size}"] = Get-RenderedPng $size $size
    }

    foreach ($variant in @('Dev', 'Preview', 'Release')) {
        $output = Join-Path $AssetsRoot $variant
        foreach ($folder in @($output, (Join-Path $output 'contrast-black'), (Join-Path $output 'contrast-white'))) {
            foreach ($file in (Get-ChildItem -LiteralPath $folder -Filter '*.png' -File |
                Where-Object { $logoNames -contains ($_.Name -split '\.')[0] })) {
                $original = [System.Drawing.Image]::FromFile($file.FullName)
                try {
                    $fill = if ($file.Name -like 'SplashScreen.*') { 0.5 } else { 1.0 }
                    $key = "$($original.Width)x$($original.Height)x$fill"
                    if (-not $pngByDimensions.ContainsKey($key)) {
                        $pngByDimensions[$key] = Get-RenderedPng $original.Width $original.Height $fill
                    }
                } finally {
                    $original.Dispose()
                }
                [System.IO.File]::WriteAllBytes($file.FullName, $pngByDimensions[$key])
            }
        }

        $stream = [System.IO.MemoryStream]::new()
        $writer = [System.IO.BinaryWriter]::new($stream)
        try {
            $writer.Write([uint16]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]$iconSizes.Count)
            $offset = [uint32](6 + 16 * $iconSizes.Count)
            foreach ($size in $iconSizes) {
                $writer.Write([byte]($size % 256))
                $writer.Write([byte]($size % 256))
                $writer.Write([byte]0)
                $writer.Write([byte]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]32)
                $writer.Write([uint32]$pngByDimensions["${size}x${size}"].Length)
                $writer.Write($offset)
                $offset += [uint32]$pngByDimensions["${size}x${size}"].Length
            }
            foreach ($size in $iconSizes) {
                $writer.Write([byte[]]$pngByDimensions["${size}x${size}"])
            }
            [System.IO.File]::WriteAllBytes((Join-Path $output 'Logo.ico'), $stream.ToArray())
        } finally {
            $writer.Dispose()
            $stream.Dispose()
        }
    }
    [System.IO.File]::WriteAllBytes((Join-Path $AssetsRoot 'Dev\FilesSiameseCatLogo.png'), [System.IO.File]::ReadAllBytes($sourcePath))
} finally {
    $source.Dispose()
}
