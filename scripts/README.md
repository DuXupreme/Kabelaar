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
