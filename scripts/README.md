# Desktop build and local distribution

Use PowerShell 7 (`pwsh`) from the repository root:

```powershell
.\native\Kabelaar.CadWorker\Get-NativeDependencies.ps1
.\native\Kabelaar.CadWorker\Build-Native.ps1
.\scripts\Build-Kabelaar.ps1
.\scripts\Package-Kabelaar.ps1 -BuildDirectory 'C:\Dev\Kabelaar\artifacts\builds\<build>\app'
```

Native dependencies are downloaded only when explicitly requested. The native
build requires x64 MSVC, Windows SDK, CMake and Ninja; see the native worker's
README for developer-environment and portable-toolchain options.

`Build-Kabelaar` runs the C# tests in Release, publishes a self-contained Windows
x64 desktop application with the native CAD runtime, and runs the published
application's smoke/performance acceptance checks. It records the commit,
dependency licenses, test evidence and hashes of all published files. Development
builds may use `-SkipTests` or `-SkipSmokeTest`; these cannot be packaged.

`Package-Kabelaar` requires a clean working tree at the exact tested commit, a
passing smoke report, unchanged test evidence and unchanged published files.
It creates the local Velopack installer, portable ZIP and update feed. It does
not upload or publish anything. Version 0.9.0 remains an acceptance release.
There is no claim of code signing unless a signing certificate is explicitly
configured in a later release process.

## Local update

For an installed application, choose **Help → Lokale update…** and select the
complete generated release folder containing `releases.win.json` and the full
`.nupkg`. Kabelaar displays the current and target versions, prepares the local
package, then asks about unsaved changes before restarting. Cancelling that
dialog keeps the document open; pending updates are not applied automatically
at the next startup. No internet feed is contacted.

For a portable application, close Kabelaar and extract the new portable ZIP into
a new folder. Project files remain where the user saved them. User libraries,
preferences/logs and recovery files are under `%LOCALAPPDATA%\Kabelaar.UserData`,
outside the Velopack installation root. When running an external installer
directly, save work and close Kabelaar first: the app cannot promise that an
external installation process will respect its unsaved-document prompt.

## Installed acceptance

`Test-KabelaarInstallation.ps1` tests a real per-user installation against the
exact clean, tested package manifest. Close installed Kabelaar windows first.
Use PowerShell 7 in the actual Windows user session, with a fresh output folder:

```powershell
.\scripts\Test-KabelaarInstallation.ps1 `
    -ReleaseDirectory 'C:\Dev\Kabelaar\artifacts\releases\0.9.0' `
    -ExpectedVersion '0.9.0' `
    -OutputDirectory 'C:\Dev\Kabelaar\artifacts\installed-acceptance-final' `
    -Mode Application
```

`Application` requires an older installed build containing this acceptance
harness, such as internal version 0.8.100. It creates a synthetic unsaved
document, waits for the actual 20-second autosave timer, terminates only that
owned test process, and performs the real local `UpdateManager` download and
restart. The new version verifies that the separate update document was saved
before restart and that the crash-recovery archive remains readable. `Updater`
instead applies the full package using the installed `Update.exe`; `Validate`
checks an already-installed target version without applying an update.

All modes compare installed runtime hashes, preserve pre-existing user-data
bytes (allowing log appends), test Cancel/Save and other-instance guards with
real processes, then launch both actual shortcuts and a `.kabelaar` file through
Windows shell association. If no user catalog exists, the test creates a
synthetic catalog with an embedded source asset; cleanup removes it only if its
identity and hash still match. Existing catalogs are read and hashed only.
Recovery cleanup removes only this run's synthetic recovery entry. A failed
run retains its evidence and may leave that clearly identified test data for
inspection.

Acceptance windows use `ShowActivated=false` and stay out of the taskbar.
The normal `--no-activate` argument also prevents activation for ordinary
launches. No mouse or keyboard input is generated. Confirmation answers are
scripted, while saving, process checks, updating and restarting use production
code. These checks establish automated installed behavior, not visual
acceptance or operation on a second PC. Results are in
`installed-acceptance-result.json` and the per-stage JSON files.
