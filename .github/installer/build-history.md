# Files max installer build history

## 2026-09-24 — 1.0.12

- Workflow: [Build Files max run 35944331577](https://github.com/YeSuper-git/Files/actions/runs/35944331577)
- Result: succeeded on attempt 2. The verified installer passed the Windows smoke test for silent install, startup, and uninstall.
- Time: 14m37s end-to-end (12m49s build job + 1m39s smoke-test job). The first attempt added 4m58s before failing, for 19m35s of runner time across both attempts.
- Artifact: `Files max Setup 1.0.12.exe`, 205,348,749 bytes (196.5 MiB), SHA-256 `e4e7b17ca46d22a7e2ada134e0166df7e0aa4f827d6b27e0916aa16dcbc5783d`.
- Local copy: `artifacts/installer-1.0.12/Files max Setup 1.0.12.exe` (ignored build output; not committed).
- Version state advanced by the workflow from 1.0.12 to 1.0.13 after the smoke test passed.

### Failed attempt 1

Run [35943784883](https://github.com/YeSuper-git/Files/actions/runs/35943784883) failed after 4m58s during **Publish unpackaged application**. Installer preflight, dependency restore, external-tool preflight, and launcher build all passed. The application compile then found:

- `ActorPosterPreview.xaml.cs`: missing `System.IO` for `File.Exists` (two CS0103 errors), plus the nullable main-poster path was not flow-checked at insertion (CS8604).
- `ResourceLibraryPage.xaml.cs`: the `(_, args)` event parameter named `_` shadowed the discard in two `out _` date-parser calls, producing two CS1503 type errors.

These were corrected and pushed in `dd6e1128b` before starting attempt 2. This was a source compile failure, not a WiX, signing, installation, or uninstall failure.

### Successful attempt 2: timing and warnings

- The publish-output cache did not hit. The main app publish took 6m33.7s, the identity/MSI/Burn installer stage took about 2m43s, and the end-to-end workflow took 14m37s.
- Two new CsWinRT1028 warnings identified assistant view-model classes that should be `partial`; fixed after the successful build. This source change intentionally invalidates the source-keyed publish cache for the next build.
- Existing warnings remain for the obsolete `NativeStorageLegacyService` registration (CS0618), trimming annotations in third-party `TagLibSharp` and `FluentFTP` (IL2104), and deep generic-recursion AOT analysis in nested `BulkConcurrentObservableCollection<GroupedCollection<...>>` usages (IL3054). The latter is a potentially meaningful compatibility/performance warning; restructuring that shared collection path needs separate impact analysis, not a warning suppression.
- WiX emitted ICE61/WIX1076 for `WIX_UPGRADE_DETECTED` because no maximum version is authored. The actual install/upgrade/uninstall smoke test passed; review this authoring warning separately before changing upgrade semantics.

The successful smoke-test result validates the produced installer, but the post-build warning cleanup was not rebuilt by design; it is staged for the next explicitly requested installer build.
