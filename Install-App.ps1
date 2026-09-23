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
    # The MSI passes its own product version during a real uninstall. Older
    # cached MSI custom actions do not know this parameter; treating a missing
    # version as a no-op prevents their late major-upgrade cleanup from
    # removing the identity package just installed by the newer MSI.
    [string]$IdentityVersion = '',
    [string]$CertificateFileName = 'Files.cer',
    [string]$AttemptId = '',
    [string]$LogFileName = 'Files max Installer-script.log',
    [string]$ErrorFileName = 'Files max Installer-error.log',
    [string]$LegacyUninstallKeyName = 'Files'
)

$ErrorActionPreference = 'Stop'
$script:root = ''
$script:stage = '初始化安装程序'
$script:logPath = $null
$script:errorPath = $null
$script:loggingFailures = @()

if ([string]::IsNullOrWhiteSpace($AttemptId)) {
    $AttemptId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID, ([guid]::NewGuid().ToString('N').Substring(0, 8))
}
$AttemptId = [regex]::Replace($AttemptId, '[^A-Za-z0-9_-]', '_')
$script:attemptId = $AttemptId
$logBaseName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileName($LogFileName))
$errorBaseName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileName($ErrorFileName))
$script:logFileName = '{0}-{1}.log' -f $logBaseName, $AttemptId
$script:errorFileName = '{0}-{1}.log' -f $errorBaseName, $AttemptId

function Initialize-InstallLogs {
    $tempRoots = @([IO.Path]::GetTempPath(), $env:TEMP)
    if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
        $tempRoots += Join-Path $env:SystemRoot 'Temp'
    }

    $uniqueRoots = @($tempRoots |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique)
    foreach ($logDirectory in $uniqueRoots) {
        try {
            if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
                New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
            }

            $candidateLogPath = Join-Path $logDirectory $script:logFileName
            $candidateErrorPath = Join-Path $logDirectory $script:errorFileName
            Set-Content -LiteralPath $candidateLogPath -Value "[$(Get-Date -Format s)] Files max installer started. AttemptId=$script:attemptId Mode=$Mode PID=$PID" -Encoding UTF8
            Set-Content -LiteralPath $candidateErrorPath -Value '' -Encoding UTF8
            $script:logPath = $candidateLogPath
            $script:errorPath = $candidateErrorPath

            if ($script:loggingFailures.Count -gt 0) {
                Write-InstallLog "Log directory fallback used. Earlier locations failed: $($script:loggingFailures -join ' | ')"
            }
            return
        } catch {
            $script:loggingFailures += "${logDirectory}: $($_.Exception.Message)"
        }
    }

    Write-Host "WARNING: Could not initialize installer log files. AttemptId=$script:attemptId. Tried: $($uniqueRoots -join '; ')"
}

function Write-InstallLog {
    param([string]$Message)
    if ([string]::IsNullOrWhiteSpace($script:logPath)) { return }

    try {
        Add-Content -LiteralPath $script:logPath -Value "[$(Get-Date -Format s)] [AttemptId=$script:attemptId] $Message" -Encoding UTF8
    } catch {
        $script:loggingFailures += "Writing script log failed: $($_.Exception.Message)"
        $script:logPath = $null
        Write-Host "WARNING: Installer script log became unavailable: $($_.Exception.Message)"
    }
}

function Write-InstallErrorLog {
    param([string]$Message)
    if ([string]::IsNullOrWhiteSpace($script:errorPath)) { return }

    try {
        Set-Content -LiteralPath $script:errorPath -Value $Message -Encoding UTF8
    } catch {
        $script:loggingFailures += "Writing script error log failed: $($_.Exception.Message)"
        $script:errorPath = $null
        Write-Host "WARNING: Installer error log became unavailable: $($_.Exception.Message)"
    }
}

function Set-InstallStage {
    param([Parameter(Mandatory)][string]$Name)
    $script:stage = $Name
    Write-InstallLog "Stage started: $Name"
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
        [string]$ExcludedPublisher = '',
        [string]$ExpectedVersion = ''
    )

    $expectedVersionValue = $null
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
        try {
            $expectedVersionValue = [version]$ExpectedVersion
        } catch {
            throw "Invalid expected identity package version '$ExpectedVersion'."
        }
    }

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $packages = @(Get-AppxPackage -Name $Name -ErrorAction SilentlyContinue)
        if (-not [string]::IsNullOrWhiteSpace($ExcludedPublisher)) {
            $packages = @($packages | Where-Object { $_.Publisher -ne $ExcludedPublisher })
        }
        if ($expectedVersionValue) {
            $packages = @($packages | Where-Object {
                try {
                    ([version]$_.Version) -eq $expectedVersionValue
                } catch {
                    $false
                }
            })
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

function Remove-LegacyUninstallRegistration {
    param([Parameter(Mandatory)][string]$KeyName)

    $subKeyPath = "Software\Microsoft\Windows\CurrentVersion\Uninstall\$KeyName"
    foreach ($registryView in @(
        [Microsoft.Win32.RegistryView]::Registry64,
        [Microsoft.Win32.RegistryView]::Registry32
    )) {
        $baseKey = $null
        $existingKey = $null
        try {
            $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
                [Microsoft.Win32.RegistryHive]::LocalMachine,
                $registryView)
            $existingKey = $baseKey.OpenSubKey($subKeyPath)
            if ($existingKey) {
                $existingKey.Dispose()
                $existingKey = $null
                $baseKey.DeleteSubKeyTree($subKeyPath)
                Write-InstallLog "Removed legacy uninstall registration from $registryView view: $KeyName"
            }
        } finally {
            if ($existingKey) { $existingKey.Dispose() }
            if ($baseKey) { $baseKey.Dispose() }
        }
    }
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
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    Write-InstallLog "Launching process: $FileName $Arguments (timeout=${TimeoutMilliseconds}ms)"
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        try { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue } catch { }
        $process.WaitForExit()
        Write-InstallLog "Process timed out: $FileName (pid=$($process.Id))"
        throw "$FileName timed out after $([math]::Round($TimeoutMilliseconds / 60000)) minutes."
    }

    $standardOutput = $standardOutputTask.GetAwaiter().GetResult().Trim()
    $standardError = $standardErrorTask.GetAwaiter().GetResult().Trim()
    $process.Refresh()
    Write-InstallLog "$FileName exited with code $($process.ExitCode)"
    if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
        Write-InstallLog "Process stdout ($FileName): $standardOutput"
    }
    if (-not [string]::IsNullOrWhiteSpace($standardError)) {
        Write-InstallLog "Process stderr ($FileName): $standardError"
    }
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
    Set-InstallStage '解析安装路径'
    $scriptDirectory = [IO.Path]::GetFullPath($PSScriptRoot)
    $defaultInstallDirectory = $scriptDirectory
    if ((Split-Path -Leaf $scriptDirectory) -ieq 'Installer') {
        $defaultInstallDirectory = Split-Path -Parent $scriptDirectory
    }
    $root = [IO.Path]::GetFullPath($defaultInstallDirectory)
    if (-not [string]::IsNullOrWhiteSpace($InstallDirectory)) {
        $root = [IO.Path]::GetFullPath($InstallDirectory)
    }
    $script:root = $root
    Write-InstallLog "Resolved install root: $root"
    Write-InstallLog "PowerShell=$($PSVersionTable.PSVersion), identity package input='$IdentityPackagePath'"

    if ([string]::IsNullOrWhiteSpace($IdentityPackagePath)) {
        Set-InstallStage '定位应用身份包'
        $IdentityPackagePath = Join-Path $root 'Files.Identity.msix'
        if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
            $IdentityPackagePath = Join-Path $root 'Installer\Files.Identity.msix'
        }
    }

    if ($Mode -eq 'Uninstall') {
        if ([string]::IsNullOrWhiteSpace($IdentityVersion)) {
            Set-InstallStage '保护新版身份包'
            Write-InstallLog 'Skipping identity removal because this uninstall action did not specify its owning product version. This is expected for a cached pre-version-scoped MSI during major upgrade.'
            Write-Host 'Skipped identity removal: the uninstall action did not specify an owning product version.'
            return
        }

        try {
            $identityVersionToRemove = [version]$IdentityVersion
        } catch {
            throw "Invalid uninstall identity version '$IdentityVersion'; refusing to remove any identity package."
        }

        # Remove only the package version owned by this MSI. During a major
        # upgrade the previous MSI may run after the new MSI has registered
        # its identity package, so an unscoped removal can break the upgrade.
        Set-InstallStage '查找待移除身份包'
        $installedPackages = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
        $packagesToRemove = @()
        foreach ($package in $installedPackages) {
            $packageVersion = $null
            try {
                $packageVersion = [version]$package.Version
            } catch {
                Write-InstallLog "Skipping identity package with unreadable version '$($package.Version)': $($package.PackageFullName)"
                continue
            }

            if ($packageVersion -ne $identityVersionToRemove) {
                Write-InstallLog "Preserving identity package version $packageVersion; uninstall owns version $($identityVersionToRemove): $($package.PackageFullName)"
                continue
            }

            $packagesToRemove += $package
        }

        if ($packagesToRemove.Count -eq 0) {
            Write-InstallLog "No identity package owned by version $identityVersionToRemove was found; preserving all installed identity packages."
            Write-Host "Skipped identity removal: version $identityVersionToRemove is not installed."
            return
        }

        Set-InstallStage '停止 Files max 进程'
        Stop-FilesProcesses
        Set-InstallStage '移除应用身份包'
        foreach ($package in $packagesToRemove) {
            Write-InstallLog "Removing identity package: $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
        }

        Wait-IdentityPackagesAbsent -Name $PackageName -ExpectedVersion $identityVersionToRemove.ToString()

        Write-Host 'Files max identity removed successfully.'
        return
    }

    Set-InstallStage '验证安装文件'
    if (-not (Test-Path -LiteralPath (Join-Path $root 'Files.exe'))) {
        throw "Files.exe was not found in '$root'."
    }
    if (-not (Test-Path -LiteralPath $IdentityPackagePath)) {
        throw "Identity package was not found: $IdentityPackagePath"
    }

    Set-InstallStage '读取应用身份包'
    $identityVersion = Get-IdentityPackageVersion $IdentityPackagePath
    Write-InstallLog "Identity package version: $identityVersion"

    Set-InstallStage '验证应用签名证书'
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

    Set-InstallStage '安装 Visual C++ 运行库'
    $vcRedist = Join-Path $root 'VC_redist.x64.exe'
    if (Test-Path -LiteralPath $vcRedist) {
        Write-InstallLog 'Installing Microsoft Visual C++ Redistributable'
        $exitCode = Invoke-ProcessChecked -FileName $vcRedist -Arguments '/install /quiet /norestart'
        if (@(0, 1638, 3010) -notcontains $exitCode) {
            throw "Microsoft Visual C++ Redistributable installation failed with exit code $exitCode"
        }
    }

    # Remove an older identity package before registering the current one.
    # The package name is stable, while the signing identity may change when
    # moving from a development build to the standard Files identity.
    Set-InstallStage '停止 Files max 进程'
    Stop-FilesProcesses
    Set-InstallStage '移除旧应用身份包'
    $legacyIdentities = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -ne $Publisher })
    foreach ($legacyIdentity in $legacyIdentities) {
        Write-InstallLog "Removing previous identity package: $($legacyIdentity.PackageFullName)"
        Remove-AppxPackage -Package $legacyIdentity.PackageFullName -ErrorAction Stop
    }
    Wait-IdentityPackagesAbsent -Name $PackageName -ExcludedPublisher $Publisher

    Set-InstallStage '注册 Files max 应用身份'
    $existingIdentity = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Where-Object { $_.Publisher -eq $Publisher } |
        Select-Object -First 1
    if ($existingIdentity) {
        Write-InstallLog "Refreshing existing external-location identity $($existingIdentity.PackageFullName) for '$root'"
    } else {
        Write-InstallLog "Registering external-location identity for '$root' at version $identityVersion"
    }
    Register-ExternalLocationIdentity -PackagePath $IdentityPackagePath -ExternalLocation $root

    Set-InstallStage '验证应用身份注册结果'
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

    $expectedRoot = [IO.Path]::GetFullPath($root)
    $registeredVersion = [version]$installedApp.Version
    if ($registeredVersion -ne $identityVersion) {
        throw "Files identity version mismatch: expected $identityVersion, registered $registeredVersion."
    }

    # Get-AppxPackage.InstallLocation is the protected WindowsApps identity
    # directory for an external-location package, not the external payload
    # directory passed to Add-AppxPackage. The payload location was already
    # validated before registration; here we verify the identity version and
    # record both locations without confusing them.
    $identityStoreLocation = [string]$installedApp.InstallLocation
    Write-InstallLog "Verified Files identity registration: version=$registeredVersion identityStore=$identityStoreLocation externalLocation=$expectedRoot"

    Set-InstallStage '清理旧版卸载注册'
    if (-not [string]::IsNullOrWhiteSpace($LegacyUninstallKeyName)) {
        Remove-LegacyUninstallRegistration -KeyName $LegacyUninstallKeyName
    }

    Write-InstallLog "Files external-location installation completed: $($installedApp.PackageFullName)"
    Write-Host "$ProductName installed successfully."
    return
}
catch {
    $errorRecord = $_
    $exception = $errorRecord.Exception
    $hresult = '未知'
    if ($null -ne $exception) {
        try {
            $hresultValue = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exception.HResult), 0)
            $hresult = '0x{0:X8}' -f $hresultValue
        } catch { }
    }
    $positionMessage = '不可用'
    if ($errorRecord.InvocationInfo) {
        $positionMessage = $errorRecord.InvocationInfo.PositionMessage
    }
    $stackTrace = '不可用'
    if ($errorRecord.ScriptStackTrace) {
        $stackTrace = $errorRecord.ScriptStackTrace
    }
    $exceptionType = 'unknown'
    $exceptionDetails = 'unavailable'
    if ($exception) {
        $exceptionType = $exception.GetType().FullName
        $exceptionDetails = $exception.ToString()
    }
    $recordDetails = ($errorRecord | Format-List * -Force | Out-String -Width 4096).Trim()
    $diagnostic = @(
        'Files max installer failed.',
        "Timestamp: $(Get-Date -Format o)",
        "AttemptId: $script:attemptId",
        "Mode: $Mode",
        "Stage: $script:stage",
        "InstallDirectory: $root",
        "IdentityPackagePath: $IdentityPackagePath",
        "ExceptionType: $exceptionType",
        "HResult: $hresult",
        "ExceptionDetails: $exceptionDetails",
        "FullyQualifiedErrorId: $($errorRecord.FullyQualifiedErrorId)",
        "CategoryInfo: $($errorRecord.CategoryInfo)",
        "Message: $($errorRecord.Exception.Message)",
        "Position: $positionMessage",
        "ScriptStackTrace: $stackTrace",
        'ErrorRecord:',
        $recordDetails,
        "LogWriteWarnings: $($script:loggingFailures -join ' | ')"
    ) -join [Environment]::NewLine
    Write-InstallLog $diagnostic
    Write-InstallErrorLog $diagnostic
    $ErrorActionPreference = 'Continue'
    $errorLogLocation = '错误日志写入失败；请从 Windows Installer 日志中搜索本次操作编号'
    if ($script:errorPath) {
        $errorLogLocation = $script:errorPath
    }
    Write-Error "$ProductName $Mode failed during '$script:stage': $($errorRecord.Exception.Message). AttemptId=$script:attemptId. ErrorLog=$errorLogLocation"
    if (-not $script:errorPath) {
        Write-Error $diagnostic
    }
    throw
}
