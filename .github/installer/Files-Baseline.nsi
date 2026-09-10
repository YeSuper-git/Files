Unicode True
ManifestSupportedOS win10
RequestExecutionLevel admin
SetCompressor /SOLID lzma2
SetRegView 64
InstallDir "$PROGRAMFILES64\Files"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "InstallLocation"

Name "Files"
OutFile "${OUTPUT_EXE}"
BrandingText "Files"
ShowInstDetails nevershow
ShowUninstDetails nevershow

VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "Files"
VIAddVersionKey /LANG=2052 "CompanyName" "YeSuper"
VIAddVersionKey /LANG=2052 "FileDescription" "Files installer"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"

!include LogicLib.nsh
!include ShellIntegration.nsh

Var InstallScript
Var IdentityPackage

Section "Install"
    SetShellVarContext all
    SetOutPath "$INSTDIR"

    File /r "${PAYLOAD_DIR}\*"
    File "${IDENTITY_PACKAGE}"
    File "${INSTALL_SCRIPT}"
    File "${CERTIFICATE}"
    File "${VC_REDIST}"

    StrCpy $InstallScript "$INSTDIR\Install-App.ps1"
    StrCpy $IdentityPackage "$INSTDIR\Files.Identity.msix"

    DetailPrint "Registering Files..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Install -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage" -PackageName "FilesDev" -Publisher "CN=Files AV Manager" -ProductName "Files" -CertificateFileName "Files.cer" -LogFileName "Files-install.log"'
    Pop $0
    ${If} $0 != 0
        Abort "Files installation failed. See %TEMP%\Files-install.log for details."
    ${EndIf}

    CreateDirectory "$SMPROGRAMS\Files"
    CreateShortCut "$SMPROGRAMS\Files\Files.lnk" "$INSTDIR\Files.exe"
    !insertmacro InstallFilesShellIntegration

    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayName" "Files"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "Publisher" "YeSuper"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayIcon" "$INSTDIR\Assets\AppTiles\Dev\Logo.ico"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext all
    StrCpy $InstallScript "$INSTDIR\Install-App.ps1"
    StrCpy $IdentityPackage "$INSTDIR\Files.Identity.msix"

    DetailPrint "Unregistering Files..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Uninstall -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage" -PackageName "FilesDev" -Publisher "CN=Files AV Manager" -ProductName "Files" -CertificateFileName "Files.cer" -LogFileName "Files-install.log"'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_ICONEXCLAMATION|MB_OK "Files identity cleanup failed. Close Files and run the uninstaller again. No files were removed."
        Abort
    ${EndIf}

    Delete "$SMPROGRAMS\Files\Files.lnk"
    RMDir "$SMPROGRAMS\Files"
    !insertmacro UninstallFilesShellIntegration
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files"
    RMDir /r "$INSTDIR"
SectionEnd
