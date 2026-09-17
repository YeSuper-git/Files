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
    [string]$ErrorFileName = 'Files max Installer-error.log',
    [string]$LegacyUninstallKeyName = 'Files'
)

$ErrorActionPreference = 'Stop'
$root = if ([string]::IsNullOrWhiteSpace($InstallDirectory)) {
    Split-Path -Parent $MyInvocation.MyCommand.Definition
} else {
    [IO.Path]::GetFullPath($InstallDirectory)
}
$tempDirectory = [IO.Path]::GetTempPath()
$logPath = Join-Path $tempDirectory $LogFileName
$errorPath = Join-Path $tempDirectory $ErrorFileName
$stage = '初始化安装程序'

function Initialize-InstallLogs {
    try {
        $logDirectory = Split-Path -Parent $logPath
        if (-not (Test-Path -LiteralPath $logDirectory)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        Set-Content -LiteralPath $logPath -Value "[$(Get-Date -Format s)] Files max installer started. Mode=$Mode InstallDirectory=$root" -Encoding UTF8
        Set-Content -LiteralPath $errorPath -Value '' -Encoding UTF8
    } catch {
        # Logging must never prevent the actual install from starting.
    }
}

function Write-InstallLog {
    param([string]$Message)
    try {
        Add-Content -LiteralPath $logPath -Value "[$(Get-Date -Format s)] $Message" -Encoding UTF8
    } catch {
        # Logging must never replace the real installation error.
    }
}

function Write-InstallErrorLog {
    param([string]$Message)
    try {
        Set-Content -LiteralPath $errorPath -Value $Message -Encoding UTF8
    } catch {
        # The bootstrapper will still show the Burn error and error code.
    }
}

function Stop-FilesProcesses {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $runningProcesses = @(Get-Process -Name 'Files', 'Files.App.Server' -ErrorAction SilentlyContinue)
        if ($runningProcesses.Count -eq 0) {
            return
        }

        $runningProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    $remainingProcesses = @(Get-Process -Name 'Files', 'Files.App.Server' -ErrorAction SilentlyContinue)
    if ($remainingProcesses.Count -gt 0) {
        throw "Files max is still running: $($remainingProcesses.ProcessName -join ', ')"
    }
}

function Wait-IdentityPackagesAbsent {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ExcludedPublisher = ''
    )

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $packages = @(Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue)
        if (-not [string]::IsNullOrWhiteSpace($ExcludedPublisher)) {
            $packages = @($packages | Where-Object { $_.Publisher -ne $ExcludedPublisher })
        }

        if ($packages.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    $packageNames = @($packages | ForEach-Object PackageFullName) -join ', '
    throw "Identity package removal is still pending: $packageNames"
}

function Register-ExternalLocationIdentity {
    param(
        [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$ExternalLocation
    )

    $lastRegistrationError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Add-AppxPackage -Path $PackagePath -ExternalLocation $ExternalLocation -ForceUpdateFromAnyVersion -ErrorAction Stop
            return
        } catch {
            $lastRegistrationError = $_
            Write-InstallLog "External-location identity registration attempt $attempt failed: $($_.Exception.Message)"
            if ($attempt -lt 3) {
                Start-Sleep -Seconds 2
            }
        }
    }

    throw "External-location identity registration failed after 3 attempts: $($lastRegistrationError.Exception.Message)"
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
    Initialize-InstallLogs
    Write-InstallLog "Resolved install root: $root"

    if ([string]::IsNullOrWhiteSpace($IdentityPackagePath)) {
        $stage = '定位应用身份包'
        $IdentityPackagePath = Join-Path $root 'Files.Identity.msix'
        if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
            $IdentityPackagePath = Join-Path $root 'Installer\Files.Identity.msix'
        }
    }

    if ($Mode -eq 'Uninstall') {
        $stage = '停止 Files max 进程'
        Stop-FilesProcesses
        # Remove every identity package with this package name. This also
        # cleans up installations made by an earlier publisher identity.
        $stage = '移除应用身份包'
        $installedPackages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
        foreach ($package in $installedPackages) {
            Write-InstallLog "Removing identity package: $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }

        Wait-IdentityPackagesAbsent -Name $PackageName

        Write-Host 'Files max identity removed successfully.'
        exit 0
    }

    $stage = '验证安装文件'
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Files.exe'))) {
        throw "Files.exe was not found in '$root'."
    }
    if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
        throw "Identity package was not found: $IdentityPackagePath"
    }

    $stage = '读取应用身份包'
    $identityVersion = Get-IdentityPackageVersion $IdentityPackagePath

    $stage = '验证应用签名证书'
    $certificate = Join-Path $root $CertificateFileName
    $installerCertificate = Join-Path $root "Installer\$CertificateFileName"
    if (-not (Test-Path -LiteralPath $certificate)) {
        if (Test-Path -LiteralPath $installerCertificate) {
            $certificate = $installerCertificate
        }
    }
    if (-not (Test-Path -LiteralPath $certificate)) {
        throw "Identity certificate was not found. Expected '$certificate' or '$installerCertificate'."
    }

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

    $stage = '安装 Visual C++ 运行库'
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
    $stage = '停止 Files max 进程'
    Stop-FilesProcesses
    $stage = '移除旧应用身份包'
    $legacyIdentities = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -ne $Publisher })
    foreach ($legacyIdentity in $legacyIdentities) {
        Write-InstallLog "Removing previous identity package: $($legacyIdentity.PackageFullName)"
        Remove-AppxPackage -Package $legacyIdentity.PackageFullName -ErrorAction Stop
    }
    Wait-IdentityPackagesAbsent -Name $PackageName -ExcludedPublisher $Publisher

    $stage = '注册 Files max 应用身份'
    $existingIdentity = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $Publisher } |
        Select-Object -First 1
    if ($existingIdentity) {
        Write-InstallLog "Refreshing existing external-location identity $($existingIdentity.PackageFullName) for '$root'"
    } else {
        Write-InstallLog "Registering external-location identity for '$root' at version $identityVersion"
    }
    Register-ExternalLocationIdentity -PackagePath $IdentityPackagePath -ExternalLocation $root

    $stage = '验证应用身份注册结果'
    $installedApp = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $Publisher -and $_.Status -eq 'Ok' } |
        Select-Object -First 1
    for ($attempt = 0; -not $installedApp -and $attempt -lt 10; $attempt++) {
        Start-Sleep -Milliseconds 500
        $installedApp = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
            Where-Object { $_.Publisher -eq $Publisher -and $_.Status -eq 'Ok' } |
            Select-Object -First 1
    }
    if (-not $installedApp) {
        throw 'Files identity package was not registered after installation.'
    }

    $stage = '清理旧版卸载注册'
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
    $details = ($_ | Out-String).Trim()
    $diagnostic = @(
        'Files max installer failed.',
        "Mode: $Mode",
        "Stage: $stage",
        "InstallDirectory: $root",
        "IdentityPackagePath: $IdentityPackagePath",
        "Message: $($_.Exception.Message)",
        'Details:',
        $details
    ) -join [Environment]::NewLine
    Write-InstallLog $diagnostic
    Write-InstallErrorLog $diagnostic
    $ErrorActionPreference = 'Continue'
    Write-Error "$ProductName $Mode failed during '$stage': $($_.Exception.Message). Details saved to $errorPath"
    exit 1
}
