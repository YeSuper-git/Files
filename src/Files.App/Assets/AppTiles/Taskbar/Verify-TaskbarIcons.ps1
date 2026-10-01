param(
    [string]$AssetsRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$devDir = Join-Path $AssetsRoot 'Dev'

function Assert-NoBlue([System.Drawing.Bitmap]$bitmap, [string]$label) {
    foreach ($yStep in 0..16) {
        $y = [int](($bitmap.Height - 1) * $yStep / 16)
        foreach ($xStep in 0..16) {
            $x = [int](($bitmap.Width - 1) * $xStep / 16)
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.A -gt 16 -and $pixel.B -gt ($pixel.R * 1.15) -and
                $pixel.B -gt ($pixel.G * 1.05)) {
                throw "Blue icon background found in $label at ($x,$y)."
            }
        }
    }
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
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne
                (Get-FileHash -LiteralPath $devLogo.FullName -Algorithm SHA256).Hash) {
                throw "$variant/$contrast icon differs from Dev: $($devLogo.Name)"
            }
            $bitmap = [System.Drawing.Bitmap]::FromFile($path)
            try {
                Assert-NoBlue $bitmap "$variant/$contrast/$($devLogo.Name)"
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
    foreach ($size in @(16, 32, 48, 256)) {
        $icon = [System.Drawing.Icon]::new($ico, $size, $size)
        try {
            $bitmap = $icon.ToBitmap()
            try {
                Assert-NoBlue $bitmap "$variant/Logo.ico ($size px)"
            } finally {
                $bitmap.Dispose()
            }
        } finally {
            $icon.Dispose()
        }
    }
}

$source = [System.Drawing.Bitmap]::FromFile((Join-Path $devDir 'FilesSiameseCatLogo.png'))
try {
    Assert-NoBlue $source 'Dev/FilesSiameseCatLogo.png'
} finally {
    $source.Dispose()
}

Write-Host 'All normal and high-contrast Dev, Preview, and Release icons are identical and free of blue backplates.'
