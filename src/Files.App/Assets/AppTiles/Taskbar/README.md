# Files max icon source

`FilesMaxIconSource.png` is the only artwork source for the Dev, Preview, and Release icons. It contains the original cat on a blue rounded-square background, with transparent pixels outside the rounded corners. After changing it, run `Generate-TaskbarIcons.ps1` to update the ICO, package logos, splash art, and normal/high-contrast variants in all three directories.

`Verify-TaskbarIcons.ps1` checks that all variants match the source artwork and retain transparent outer corners. It runs in PR validation and both installer workflows so a future build cannot silently add a second blue square outside the rounded icon.

Package manifests must reference `Square44x44Logo.png` without a scale qualifier. The identity package includes the generated `.scale-*` and `.targetsize-*_altform-unplated` variants. Referencing `.scale-100.png` directly prevents Windows from selecting the unplated taskbar resource and adds an accent-color square behind the icon.

Local external-location previews must use `.github/scripts/New-LocalPreviewIdentity.ps1` after publishing. It creates a fresh identity from the current installer manifest and icon assets, rebuilds and inspects the PRI, updates the executable identity, and records provenance. Never copy an identity directory or PRI from an older preview.
