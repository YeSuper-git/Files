# Files max icon source

`FilesMaxIconSource.png` is the only artwork source for the Dev, Preview, and Release icons. It contains the original cat on a blue rounded-square background, with transparent pixels outside the rounded corners. After changing it, run `Generate-TaskbarIcons.ps1` to update the ICO, package logos, splash art, and normal/high-contrast variants in all three directories.

`Verify-TaskbarIcons.ps1` checks that all variants match the source artwork and retain transparent outer corners. It runs in PR validation and both installer workflows so a future build cannot silently add a second blue square outside the rounded icon.
