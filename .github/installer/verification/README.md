# Installer UI review

The installer uses a fixed 720 × 560 logical-pixel window. The title bar only
offers minimize and close. All pages share the same typography, card, controls
and footer; DWM remains responsible for the outer window border and corners.

On Windows with .NET 10, run:

```powershell
dotnet run --project .github/installer/verification/FilesMax.Installer.Verification.csproj --configuration Release -- artifacts/installer-ui-review
```

This compiles the real WPF bootstrapper and opens it with sample data, without
starting Burn or installing/uninstalling any software. It checks consent and
path gating, navigation callbacks, progress labels, completion actions, fixed
window dimensions, and visible button text bounds. It also renders ten actual
WPF client-area images for visual review, including long paths and long errors.
Open `index.html` in the review artifact to switch between the captured pages.

`Validate installer UI` runs these focused checks for bootstrapper UI changes.
It does not compile the Files app, package an installer, or publish a release.
The full installer workflow remains manually triggered.

The PNGs do not include DWM borders/shadows and are not evidence of native frame
rendering or real per-monitor DPI switching. Those still need interactive
Windows 11 verification. Browser mockups are not a substitute for these WPF
renders or for testing the native installer.

Design references:
- https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics
- https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-rounded-corners
