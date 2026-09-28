param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Mode = 'Install',
    [string]$IdentityVersion = '',
    [string]$AttemptId = '',
    [string]$LogFileName = 'Files max Installer-script.log',
    [string]$ErrorFileName = 'Files max Installer-error.log'
)

$ErrorActionPreference = 'Stop'
$safeAttemptId = [regex]::Replace($AttemptId, '[^A-Za-z0-9_-]', '_')
if ([string]::IsNullOrWhiteSpace($safeAttemptId)) {
    $safeAttemptId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $PID
}

$scriptBaseName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileName($LogFileName))
$scriptExtension = [IO.Path]::GetExtension($LogFileName)
$scriptLogName = '{0}-{1}{2}' -f $scriptBaseName, $safeAttemptId, $scriptExtension
$errorBaseName = [IO.Path]::GetFileNameWithoutExtension([IO.Path]::GetFileName($ErrorFileName))
$errorExtension = [IO.Path]::GetExtension($ErrorFileName)
$errorLogName = '{0}-{1}{2}' -f $errorBaseName, $safeAttemptId, $errorExtension
$tempRoots = @([IO.Path]::GetTempPath(), $env:TEMP)
if (-not [string]::IsNullOrWhiteSpace($env:SystemRoot)) {
    $tempRoots += Join-Path $env:SystemRoot 'Temp'
}
$tempRoots = @($tempRoots | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
$scriptLogPath = $null
$errorLogPath = $null
$logWriteWarnings = @()
$failureStage = '初始化启动日志'
$parseErrors = @()

foreach ($tempRoot in $tempRoots) {
    try {
        if (-not (Test-Path -LiteralPath $tempRoot -PathType Container)) {
            New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
        }

        $candidateScriptLog = Join-Path $tempRoot $scriptLogName
        Set-Content -LiteralPath $candidateScriptLog -Value "[$(Get-Date -Format o)] Installer launcher started. AttemptId=$safeAttemptId Mode=$Mode PID=$PID" -Encoding UTF8
        $scriptLogPath = $candidateScriptLog
        $errorLogPath = Join-Path $tempRoot $errorLogName
        break
    } catch {
        $logWriteWarnings += "${tempRoot}: $($_.Exception.Message)"
    }
}

try {
    $failureStage = '验证安装脚本语法'
    $installScriptPath = Join-Path $PSScriptRoot 'Install-App.ps1'
    if (-not (Test-Path -LiteralPath $installScriptPath -PathType Leaf)) {
        throw "Install-App.ps1 was not found beside the launcher: $installScriptPath"
    }

    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $installScriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        $parseDetails = @($parseErrors | ForEach-Object {
            $tokenText = $_.Extent.Text
            if ($tokenText.Length -gt 240) {
                $tokenText = $tokenText.Substring(0, 240)
            }
            "Line=$($_.Extent.StartLineNumber), Column=$($_.Extent.StartColumnNumber), Token='$tokenText', Message=$($_.Message)"
        })
        throw "Install-App.ps1 contains Windows PowerShell syntax errors: $($parseDetails -join ' | ')"
    }

    $failureStage = '运行安装脚本'
    if ($scriptLogPath) {
        Add-Content -LiteralPath $scriptLogPath -Value "[$(Get-Date -Format o)] Windows PowerShell $($PSVersionTable.PSVersion); validated $installScriptPath" -Encoding UTF8
    }
    & $installScriptPath -Mode $Mode -IdentityVersion $IdentityVersion -AttemptId $safeAttemptId -LogFileName $LogFileName -ErrorFileName $ErrorFileName
    exit 0
} catch {
    $errorRecord = $_
    $positionMessage = 'Unavailable'
    if ($errorRecord.InvocationInfo) {
        $positionMessage = $errorRecord.InvocationInfo.PositionMessage
    }
    $stackTrace = 'Unavailable'
    if ($errorRecord.ScriptStackTrace) {
        $stackTrace = $errorRecord.ScriptStackTrace
    }
    $exceptionType = 'unknown'
    $exceptionHResult = 'unknown'
    $exceptionMessage = 'unavailable'
    $exceptionDetails = 'unavailable'
    if ($errorRecord.Exception) {
        $exceptionType = $errorRecord.Exception.GetType().FullName
        $exceptionHResult = '0x{0:X8}' -f [BitConverter]::ToUInt32(
            [BitConverter]::GetBytes([int]$errorRecord.Exception.HResult),
            0)
        $exceptionMessage = $errorRecord.Exception.Message
        $exceptionDetails = $errorRecord.Exception.ToString()
    }
    $diagnostic = @(
        'Files max installer launcher failed.',
        "Timestamp: $(Get-Date -Format o)",
        "AttemptId: $safeAttemptId",
        "Mode: $Mode",
        "Stage: $failureStage",
        "PowerShellVersion: $($PSVersionTable.PSVersion)",
        "InstallScript: $(Join-Path $PSScriptRoot 'Install-App.ps1')",
        "ExceptionType: $exceptionType",
        "HResult: $exceptionHResult",
        "ExceptionDetails: $exceptionDetails",
        "FullyQualifiedErrorId: $($errorRecord.FullyQualifiedErrorId)",
        "CategoryInfo: $($errorRecord.CategoryInfo)",
        "Message: $exceptionMessage",
        "Position: $positionMessage",
        "ScriptStackTrace: $stackTrace",
        "LogWriteWarnings: $($logWriteWarnings -join ' | ')"
    )

    if ($parseErrors.Count -gt 0) {
        $diagnostic += 'ParserErrors:'
        foreach ($parseError in $parseErrors) {
            $tokenText = $parseError.Extent.Text
            if ($tokenText.Length -gt 240) {
                $tokenText = $tokenText.Substring(0, 240)
            }
            $diagnostic += "Line=$($parseError.Extent.StartLineNumber), Column=$($parseError.Extent.StartColumnNumber), Token='$tokenText', Message=$($parseError.Message)"
        }
    }

    if ($scriptLogPath) {
        try {
            Add-Content -LiteralPath $scriptLogPath -Value ($diagnostic -join [Environment]::NewLine) -Encoding UTF8
        } catch {
            $logWriteWarnings += "Script log append failed: $($_.Exception.Message)"
        }
    }

    $errorText = $diagnostic -join [Environment]::NewLine
    foreach ($tempRoot in $tempRoots) {
        try {
            if (-not (Test-Path -LiteralPath $tempRoot -PathType Container)) {
                New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
            }

            $candidateErrorLog = Join-Path $tempRoot $errorLogName
            if (Test-Path -LiteralPath $candidateErrorLog -PathType Leaf) {
                $previousErrorText = Get-Content -LiteralPath $candidateErrorLog -Raw
                if (-not [string]::IsNullOrWhiteSpace($previousErrorText)) {
                    $errorText = $previousErrorText.TrimEnd() + [Environment]::NewLine + [Environment]::NewLine + $errorText
                }
            }
            Set-Content -LiteralPath $candidateErrorLog -Value $errorText -Encoding UTF8
            $errorLogPath = $candidateErrorLog
            break
        } catch {
            $logWriteWarnings += "${tempRoot}: $($_.Exception.Message)"
        }
    }

    [Console]::Error.WriteLine("Files max installer launcher failed. AttemptId=$safeAttemptId ErrorLog=$errorLogPath")
    exit 1
}
