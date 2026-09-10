; Windows Explorer integration owned by the traditional installer.
; A private key name is used so uninstall cannot remove another application's
; generic "Files" registration.
!ifndef FILES_SHELL_INTEGRATION_NSH
!define FILES_SHELL_INTEGRATION_NSH

!macro InstallFilesShellIntegration
    WriteRegStr HKLM "Software\Classes\Directory\shell\FilesYeSuper" "MUIVerb" "Open in Files"
    WriteRegStr HKLM "Software\Classes\Directory\shell\FilesYeSuper" "Icon" "$INSTDIR\Files.exe"
    WriteRegStr HKLM "Software\Classes\Directory\shell\FilesYeSuper\command" "" '"$INSTDIR\Files.exe" "%1"'

    WriteRegStr HKLM "Software\Classes\Drive\shell\FilesYeSuper" "MUIVerb" "Open in Files"
    WriteRegStr HKLM "Software\Classes\Drive\shell\FilesYeSuper" "Icon" "$INSTDIR\Files.exe"
    WriteRegStr HKLM "Software\Classes\Drive\shell\FilesYeSuper\command" "" '"$INSTDIR\Files.exe" "%1"'

    WriteRegStr HKLM "Software\Classes\Directory\Background\shell\FilesYeSuper" "MUIVerb" "Open in Files"
    WriteRegStr HKLM "Software\Classes\Directory\Background\shell\FilesYeSuper" "Icon" "$INSTDIR\Files.exe"
    WriteRegStr HKLM "Software\Classes\Directory\Background\shell\FilesYeSuper\command" "" '"$INSTDIR\Files.exe" "%V"'
!macroend

!macro UninstallFilesShellIntegration
    DeleteRegKey HKLM "Software\Classes\Directory\shell\FilesYeSuper"
    DeleteRegKey HKLM "Software\Classes\Drive\shell\FilesYeSuper"
    DeleteRegKey HKLM "Software\Classes\Directory\Background\shell\FilesYeSuper"
!macroend

!endif
