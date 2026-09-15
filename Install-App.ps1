# Copyright (c) Files Community
# Licensed under the MIT License.

param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Mode = 'Install',
    [string]$InstallDirectory = '',
    [string]$IdentityPackagePath = '',
    [string]$PackageName = 'FilesDev',
    [string]$Publisher = 'CN=Files',
    [string]$ProductName = 'Files max',
    [string]$CertificateFileName = 'Files.cer',
    [string]$LogFileName = 'Files max Installer-install.log',
    [string]$LegacyUninstallKeyName = 'Files'
)

$ErrorActionPreference = 'Stop'
$root = if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
} else {
    [IO.Path]::GetFullPath($InstallDirectory)
}
$logPath = Join-Path $env:TEMP $LogFileName

function Write-InstallLog {
    param([string]$Message)
    Add-Content -Path $logPath -Value "[$(Get-Date -Format s)] $Message" -Encoding UTF8
}

function Stop-FilesProcesses {
    Get-Process -Name 'Files', 'Files.App.Server' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Invoke-ProcessChecked {
    param(
        [Parameter(Mandatory)][string]$FileName,
        [string]$Arguments = '',
        [int]$TimeoutMilliseconds = 300000
    )

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FileName
    $startInfo.Arguments = $Arguments
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
        throw "$FileName timed out after $([math]::Round($TimeoutMilliseconds / 60000)) minutes."
    }

    $process.Refresh()
    Write-InstallLog "$FileName exited with code $($process.ExitCode)"
    return $process.ExitCode
}

function Get-IdentityPackageVersion {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries | Where-Object FullName -eq 'AppxManifest.xml' | Select-Object -First 1
        if (-not $entry) {
            throw "Identity package has no AppxManifest.xml: $PackagePath"
        }

        $reader = New-Object System.IO.StreamReader($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        return [version]$manifest.Package.Identity.Version
    } finally {
        $archive.Dispose()
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($IdentityPackagePath)) {
        $IdentityPackagePath = Join-Path $root 'Files.Identity.msix'
        if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
            $IdentityPackagePath = Join-Path $root 'Installer\Files.Identity.msix'
        }
    }

    if ($Mode -eq 'Uninstall') {
        Stop-FilesProcesses
        # Remove every identity package with this package name. This also
        # cleans up installations made by an earlier publisher identity.
        $installedPackages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
        foreach ($package in $installedPackages) {
            Write-InstallLog "Removing identity package: $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }

        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $remainingPackages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
            if ($remainingPackages.Count -eq 0) {
                break
            }
            Start-Sleep -Milliseconds 500
        }
        if ($remainingPackages.Count -gt 0) {
            throw "Identity package removal is still pending: $($remainingPackages.PackageFullName -join ', ')"
        }

        Write-Host 'Files max identity removed successfully.'
        exit 0
    }

    if (-not (Test-Path -LiteralPath (Join-Path $root 'Files.exe'))) {
        throw "Files.exe was not found in '$root'."
    }
    if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
        throw "Identity package was not found: $IdentityPackagePath"
    }

    $identityVersion = Get-IdentityPackageVersion $IdentityPackagePath

    $certificate = Join-Path $root $CertificateFileName
    if (-not (Test-Path -LiteralPath $certificate)) {
        $installerCertificate = Join-Path $root "Installer\$CertificateFileName"
        if (Test-Path -LiteralPath $installerCertificate) {
            $certificate = $installerCertificate
        }
    }
    if (Test-Path -LiteralPath $certificate) {
        $certUtil = Join-Path $env:SystemRoot 'System32\certutil.exe'
        if (-not (Test-Path -LiteralPath $certUtil)) {
            throw "Windows certificate utility was not found: $certUtil"
        }

        Write-InstallLog "Trusting identity certificate with certutil: $certificate"
        $certificateExitCode = Invoke-ProcessChecked `
            -FileName $certUtil `
            -Arguments "-addstore -f TrustedPeople `"$certificate`""
        if ($certificateExitCode -ne 0) {
            throw "Identity certificate import failed with exit code $certificateExitCode"
        }
    }

    $vcRedist = Join-Path $root 'VC_redist.x64.exe'
    if (Test-Path -LiteralPath $vcRedist) {
        Write-InstallLog 'Installing Microsoft Visual C++ Redistributable'
        $exitCode = Invoke-ProcessChecked -FileName $vcRedist -Arguments '/install /quiet /norestart'
        if ($exitCode -notin @(0, 1638, 3010)) {
            throw "Microsoft Visual C++ Redistributable installation failed with exit code $exitCode"
        }
    }

    # Remove an older identity package before registering the current one.
    # The package name is stable, while the signing identity may change when
    # moving from a development build to the standard Files identity.
    Stop-FilesProcesses
    $legacyIdentities = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -ne $Publisher })
    foreach ($legacyIdentity in $legacyIdentities) {
        Write-InstallLog "Removing previous identity package: $($legacyIdentity.PackageFullName)"
        Remove-AppxPackage -Package $legacyIdentity.PackageFullName -ErrorAction Stop
    }

    $existingIdentity = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $Publisher } |
        Select-Object -First 1
    if ($existingIdentity) {
        Write-InstallLog "Refreshing existing external-location identity $($existingIdentity.PackageFullName) for '$root'"
    } else {
        Write-InstallLog "Registering external-location identity for '$root' at version $identityVersion"
    }
    Add-AppxPackage -Path $IdentityPackagePath -ExternalLocation $root -ForceUpdateFromAnyVersion -ErrorAction Stop

    $installedApp = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $Publisher -and $_.Status -eq 'Ok' } |
        Select-Object -First 1
    if (-not $installedApp) {
        throw 'Files identity package was not registered after installation.'
    }

    if (-not [string]::IsNullOrWhiteSpace($LegacyUninstallKeyName)) {
        $legacyUninstallKey = Join-Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall' $LegacyUninstallKeyName
        if (Test-Path -LiteralPath $legacyUninstallKey) {
            Write-InstallLog "Removing legacy installer registration: $LegacyUninstallKeyName"
            Remove-Item -LiteralPath $legacyUninstallKey -Recurse -Force -ErrorAction Stop
        }
    }

    Write-InstallLog "Files external-location installation completed: $($installedApp.PackageFullName)"
    Write-Host "$ProductName installed successfully."
    exit 0
}
catch {
    $details = $_ | Out-String
    Add-Content -Path $logPath -Value $details -Encoding UTF8
    Write-Error "$_ Details saved to $logPath"
    exit 1
}
