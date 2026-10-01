param(
    [string]$AssetsRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$devDir = Join-Path $AssetsRoot 'Dev'
$sourcePath = Join-Path $PSScriptRoot 'FilesMaxIconSource.png'
$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
$expectedHashes = @{}

function Assert-TransparentCorners([System.Drawing.Bitmap]$bitmap, [string]$label) {
    foreach ($point in @(
        @(0, 0),
        @(($bitmap.Width - 1), 0),
        @(0, ($bitmap.Height - 1)),
        @(($bitmap.Width - 1), ($bitmap.Height - 1))
    )) {
        if ($bitmap.GetPixel($point[0], $point[1]).A -gt 16) {
            throw "Icon has a filled outer corner in $label at ($($point[0]),$($point[1]))."
        }
    }
}

function Get-ExpectedHash([int]$width, [int]$height, [double]$fill) {
    $key = "$width`x$height`x$fill"
    if (-not $expectedHashes.ContainsKey($key)) {
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
                $expectedHashes[$key] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray()))
            } finally {
                $stream.Dispose()
            }
        } finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
    return $expectedHashes[$key]
}

try {
    Assert-TransparentCorners $source 'FilesMaxIconSource.png'
    if ($source.GetPixel([int]($source.Width * .08), [int]($source.Height * .08)).B -le
        $source.GetPixel([int]($source.Width * .08), [int]($source.Height * .08)).R) {
        throw 'The canonical icon no longer contains the approved blue rounded-square background.'
    }

foreach ($variant in @('Dev', 'Preview', 'Release')) {
    $dir = Join-Path $AssetsRoot $variant
    foreach ($contrast in @('', 'contrast-black', 'contrast-white')) {
        $devFolder = if ($contrast) { Join-Path $devDir $contrast } else { $devDir }
        $folder = if ($contrast) { Join-Path $dir $contrast } else { $dir }
        $devLogos = @(Get-ChildItem -LiteralPath $devFolder -Filter '*.png' -File |
            Where-Object { $_.Name -ne 'FilesSiameseCatLogo.png' })
        if ($devLogos.Count -ne 82) {
            throw "Expected 82 icon images in Dev/$contrast, found $($devLogos.Count)."
        }
        $actualLogos = @(Get-ChildItem -LiteralPath $folder -Filter '*.png' -File |
            Where-Object { $_.Name -ne 'FilesSiameseCatLogo.png' })
        if ($actualLogos.Count -ne $devLogos.Count) {
            throw "Unexpected icon count in ${variant}/${contrast}: $($actualLogos.Count)."
        }
        foreach ($devLogo in $devLogos) {
            $path = Join-Path $folder $devLogo.Name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Missing $variant/$contrast icon: $($devLogo.Name)"
            }
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            if ($hash -ne (Get-FileHash -LiteralPath $devLogo.FullName -Algorithm SHA256).Hash) {
                throw "$variant/$contrast icon differs from Dev: $($devLogo.Name)"
            }
            $bitmap = [System.Drawing.Bitmap]::FromFile($path)
            try {
                if ($variant -eq 'Dev' -and $hash -ne
                    (Get-ExpectedHash $bitmap.Width $bitmap.Height $(if ($devLogo.Name -like 'SplashScreen.*') { 0.5 } else { 1.0 }))) {
                    throw "Icon differs from FilesMaxIconSource.png: $($devLogo.Name)"
                }
                if ($bitmap.Width -eq $bitmap.Height) {
                    Assert-TransparentCorners $bitmap "$variant/$contrast/$($devLogo.Name)"
                }
            } finally {
                $bitmap.Dispose()
            }
        }
    }
    $ico = Join-Path $dir 'Logo.ico'
    if ((Get-FileHash -LiteralPath $ico -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $devDir 'Logo.ico') -Algorithm SHA256).Hash) {
        throw "$variant ICO differs from Dev."
    }
    $icoStream = [System.IO.File]::OpenRead($ico)
    $reader = [System.IO.BinaryReader]::new($icoStream)
    try {
        if ($reader.ReadUInt16() -ne 0 -or $reader.ReadUInt16() -ne 1) {
            throw "$variant/Logo.ico is not a valid ICO."
        }
        $count = $reader.ReadUInt16()
        if ($count -ne 15) {
            throw "$variant/Logo.ico has $count sizes; expected 15."
        }
        for ($index = 0; $index -lt $count; $index++) {
            $size = [int]$reader.ReadByte()
            if ($size -eq 0) { $size = 256 }
            $null = $reader.ReadBytes(7)
            $length = $reader.ReadUInt32()
            $offset = $reader.ReadUInt32()
            $returnPosition = $icoStream.Position
            $icoStream.Position = $offset
            $pngBytes = $reader.ReadBytes($length)
            $icoStream.Position = $returnPosition
            if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pngBytes)) -ne
                (Get-ExpectedHash $size $size 1.0)) {
                throw "$variant/Logo.ico ${size}px image differs from FilesMaxIconSource.png."
            }
        }
    } finally {
        $reader.Dispose()
        $icoStream.Dispose()
    }
    foreach ($size in @(16, 32, 48, 256)) {
        $icon = [System.Drawing.Icon]::new($ico, $size, $size)
        try {
            $bitmap = $icon.ToBitmap()
            try {
                Assert-TransparentCorners $bitmap "$variant/Logo.ico ($size px)"
            } finally {
                $bitmap.Dispose()
            }
        } finally {
            $icon.Dispose()
        }
    }
}

    if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $devDir 'FilesSiameseCatLogo.png') -Algorithm SHA256).Hash) {
        throw 'Dev/FilesSiameseCatLogo.png differs from the canonical icon source.'
    }
} finally {
    $source.Dispose()
}

Write-Host 'All Dev, Preview, and Release icons derive from the approved rounded icon, with transparent outer corners.'
