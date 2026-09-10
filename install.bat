@echo off
setlocal
set "SCRIPT=%~dp0Install-App.ps1"

if not exist "%SCRIPT%" (
    echo Install-App.ps1 was not found next to this file.
    echo Please use the generated Files-Setup.exe or Files-AV-Manager-Setup.exe.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
set "EXITCODE=%ERRORLEVEL%"
if not "%EXITCODE%" == "0" pause
exit /b %EXITCODE%
