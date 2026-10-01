# Files max icon source

`CatTransparent.png` is the shared transparent mascot source for the Dev, Preview, and Release icons. After changing it, run `Generate-TaskbarIcons.ps1` to update the ICO, package logos, splash art, and normal/high-contrast variants in all three directories.

`Verify-TaskbarIcons.ps1` checks that all variants match and contain no blue backplate. It runs in PR validation and both installer workflows so a future build cannot silently restore the old blue-corner icon.
