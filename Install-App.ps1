# Copyright (c) Files Community
# Licensed under the MIT License.

param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Mode = 'Install',
    [string]$InstallDirectory = '',
    [string]$IdentityPackagePath = '',
    [string]$PackageName = 'FilesDev',
    [string]$Publisher = 'CN=Files AV Manager',
    [string]$ProductName = 'Files AV Resource Manager',
    [string]$CertificateFileName = 'FilesAVManager.cer',
    [string]$LogFileName = 'Files-AV-Manager-install.log'
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
    }

    if ($Mode -eq 'Uninstall') {
        $installedPackages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $Publisher })
        foreach ($package in $installedPackages) {
            Write-InstallLog "Removing identity package: $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }

        Write-Host 'Files identity removed successfully.'
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
    if (Test-Path -LiteralPath $certificate) {
        try {
            Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Confirm:$false | Out-Null
        } catch {
            Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' -Confirm:$false | Out-Null
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
