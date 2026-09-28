Unicode True
ManifestSupportedOS win10
RequestExecutionLevel admin
SetCompressor /SOLID lzma
InstallDir "$PROGRAMFILES64\Files"
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "InstallLocation"

!define MUI_ABORTWARNING
!define MUI_UNABORTWARNING
!define MUI_ICON "${PAYLOAD_DIR}\Assets\AppTiles\Dev\Logo.ico"
!define MUI_UNICON "${PAYLOAD_DIR}\Assets\AppTiles\Dev\Logo.ico"
!define MUI_WELCOMEPAGE_TITLE "欢迎使用文件资源管理器安装向导"
!define MUI_WELCOMEPAGE_TEXT "此向导将引导您完成文件资源管理器的安装。\r\n\r\n建议在开始安装前关闭其他应用。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "请选择文件资源管理器的安装位置。"
!define MUI_DIRECTORYPAGE_TEXT_DESTINATION "安装文件夹："
!define MUI_FINISHPAGE_TITLE "文件资源管理器安装完成"
!define MUI_FINISHPAGE_TEXT "文件资源管理器已经安装到您的电脑。"
!define MUI_UNCONFIRMPAGE_TEXT_TOP "请选择是否要从电脑中卸载文件资源管理器。"

!include MUI2.nsh

Name "文件资源管理器"
OutFile "${OUTPUT_EXE}"
BrandingText "文件资源管理器"
ShowInstDetails nevershow
ShowUninstDetails nevershow

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "文件资源管理器卸载完成"
!define MUI_FINISHPAGE_TEXT "文件资源管理器已经从您的电脑中卸载。"
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "SimpChinese"

VIProductVersion "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "文件资源管理器"
VIAddVersionKey /LANG=2052 "CompanyName" "YeSuper"
VIAddVersionKey /LANG=2052 "FileDescription" "文件资源管理器安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"

!ifndef APP_DISPLAY_VERSION
!define APP_DISPLAY_VERSION "${APP_VERSION}"
!endif

!include LogicLib.nsh
!include ShellIntegration.nsh

Var InstallScript
Var IdentityPackage

Section "Install"
    SetShellVarContext all
    SetRegView 64
    SetOutPath "$INSTDIR"

    File /r "${PAYLOAD_DIR}\*"
    File "${IDENTITY_PACKAGE}"
    File "${INSTALL_SCRIPT}"
    File "${CERTIFICATE}"
    File "${VC_REDIST}"

    StrCpy $InstallScript "$INSTDIR\Install-App.ps1"
    StrCpy $IdentityPackage "$INSTDIR\Files.Identity.msix"

    DetailPrint "正在注册文件资源管理器..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Install -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage"'
    Pop $0
    ${If} $0 != 0
        Abort "文件资源管理器安装失败。详情请查看 %TEMP%\Files-Installer-install.log。"
    ${EndIf}

    CreateDirectory "$SMPROGRAMS\Files"
    CreateShortCut "$SMPROGRAMS\Files\Files.lnk" "$INSTDIR\Files.exe"
    !insertmacro InstallFilesShellIntegration

    WriteUninstaller "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayName" "文件资源管理器"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayVersion" "${APP_DISPLAY_VERSION}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "Publisher" "YeSuper"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "DisplayIcon" "$INSTDIR\Assets\AppTiles\Dev\Logo.ico"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "NoModify" 1
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files" "NoRepair" 1
SectionEnd

Section "Uninstall"
    SetShellVarContext all
    SetRegView 64
    StrCpy $InstallScript "$INSTDIR\Install-App.ps1"
    StrCpy $IdentityPackage "$INSTDIR\Files.Identity.msix"

    DetailPrint "正在注销文件资源管理器..."
    nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$InstallScript" -Mode Uninstall -IdentityVersion "${APP_VERSION}" -InstallDirectory "$INSTDIR" -IdentityPackagePath "$IdentityPackage"'
    Pop $0
    ${If} $0 != 0
        MessageBox MB_ICONEXCLAMATION|MB_OK "文件资源管理器身份清理失败。请关闭文件资源管理器后重新运行卸载程序。未删除任何文件。"
        Abort
    ${EndIf}

    Delete "$SMPROGRAMS\Files\Files.lnk"
    RMDir "$SMPROGRAMS\Files"
    !insertmacro UninstallFilesShellIntegration
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\Files"
    RMDir /r "$INSTDIR"
SectionEnd
