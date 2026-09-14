# WiX installer

The resource manager installer is built as a WiX Toolset 7 MSI wrapped by a Burn
bootstrapper:

- `Files-Installer.msi` owns the application files, shortcuts, Explorer
  integration, identity-registration custom actions, repair, and uninstall.
- `Files-Setup.exe` owns the user-facing setup, prerequisite chain,
  upgrade detection, caching, and bundle uninstall entry.

The GitHub Actions workflow supplies the publish directory and the signed
external-location identity package at build time. The Burn `InstallFolder`
variable is persisted and passed to the MSI as `INSTALLFOLDER`, so the folder
chosen in the setup options page is the folder used by the MSI and by the
external-location registration script.

The legacy NSIS scripts remain in `.github/installer` temporarily as a rollback
reference. They are no longer used by the resource manager workflow.
