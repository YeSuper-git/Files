Unicode True
ManifestSupportedOS win10
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetRegView 64
InstallDir "$PROGRAMFILES64\Files AV Resource Manager"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "InstallLocation"

Name "Files AV Resource Manager"
OutFile "${OUTPUT_EXE}"
BrandingText "Files AV Resource Manager"
ShowInstDetails nevershow
ShowUninstDetails nevershow

VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "Files AV Resource Manager"
VIAddVersionKey /LANG=2052 "CompanyName" "YeSuper"
VIAddVersionKey /LANG=2052 "FileDescription" "Files AV Resource Manager installer"
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
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Install -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage"'
    Pop $0
    ${If} $0 != 0
        Abort "Files installation failed. See %TEMP%\Files-AV-Manager-install.log for details."
    ${EndIf}

    CreateDirectory "$SMPROGRAMS\Files AV Resource Manager"
    CreateShortCut "$SMPROGRAMS\Files AV Resource Manager\Files AV Resource Manager.lnk" "$INSTDIR\Files.exe"
    !insertmacro InstallFilesShellIntegration

    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "DisplayName" "Files AV Resource Manager"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "Publisher" "YeSuper"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "DisplayIcon" "$INSTDIR\Assets\AppTiles\Dev\Logo.ico"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext all
    StrCpy $InstallScript "$INSTDIR\Install-App.ps1"
    StrCpy $IdentityPackage "$INSTDIR\Files.Identity.msix"

    DetailPrint "Unregistering Files..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Uninstall -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage"'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_ICONEXCLAMATION|MB_OK "Files identity cleanup failed. Close Files and run the uninstaller again. No files were removed."
        Abort
    ${EndIf}

    Delete "$SMPROGRAMS\Files AV Resource Manager\Files AV Resource Manager.lnk"
    RMDir "$SMPROGRAMS\Files AV Resource Manager"
    !insertmacro UninstallFilesShellIntegration
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files AV Resource Manager"
    RMDir /r "$INSTDIR"
SectionEnd
