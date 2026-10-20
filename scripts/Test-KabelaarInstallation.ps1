#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$ReleaseDirectory,
    [Parameter(Mandatory=$true)][string]$ExpectedVersion,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('Validate','Updater','Application')][string]$Mode = 'Validate',
    [string]$InstalledRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kabelaar')
)
$ErrorActionPreference = 'Stop'
$ReleaseDirectory = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$InstalledRoot = (Resolve-Path -LiteralPath $InstalledRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count) {
    throw 'Choose a fresh acceptance output directory.'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$installedPrefix = $InstalledRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$current = Join-Path $InstalledRoot 'current'
$executable = Join-Path $current 'Kabelaar.exe'
$userData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Kabelaar.UserData'
$expected = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'tested-build-info.json') -Raw | ConvertFrom-Json
if ($expected.version -ne $ExpectedVersion -or !$expected.cleanSource -or !$expected.testsPassed -or !$expected.smokePassed) {
    throw 'The target release does not have the requested clean tested build metadata.'
}
$packageManifest = Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'package-verification.json') -Raw | ConvertFrom-Json
if ($packageManifest.commit -ne $expected.commit -or $packageManifest.version -ne $ExpectedVersion) { throw 'Release metadata disagree.' }
foreach ($asset in $packageManifest.assets) {
    $assetPath = [IO.Path]::GetFullPath((Join-Path $ReleaseDirectory $asset.name))
    if (!$assetPath.StartsWith($ReleaseDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Release asset mismatch: $($asset.name)" }
}
$fullPackages = @(Get-ChildItem -LiteralPath $ReleaseDirectory -Filter '*-full.nupkg' -File)
if ($fullPackages.Count -ne 1) { throw 'Acceptance requires exactly one full package in the release folder.' }

function Assert-NoInstalledProcesses {
    foreach ($candidate in [Diagnostics.Process]::GetProcessesByName('Kabelaar')) {
        try {
            if (!$candidate.HasExited) {
                $candidatePath = $candidate.MainModule.FileName
                if (!$candidatePath -or [IO.Path]::GetFullPath($candidatePath).StartsWith($installedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'An installed Kabelaar window is still running. Save and close it before this controlled test.'
                }
            }
        } finally { $candidate.Dispose() }
    }
}
function Start-OwnedProcess([string]$File, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT') | Out-Null
    $start.Environment.Remove('KABELAAR_ACCEPTANCE_NONCE') | Out-Null
    return [Diagnostics.Process]::Start($start)
}
function Wait-OwnedExit($Process, [int]$Seconds = 60) {
    try {
        if (!$Process.WaitForExit($Seconds * 1000)) {
            # Only the exact process handle created by this test can be terminated here.
            $Process.Kill($true)
            $Process.WaitForExit()
            throw "Owned acceptance process exceeded $Seconds seconds."
        }
        return $Process.ExitCode
    } finally { $Process.Dispose() }
}
function Read-Success([string]$Path) {
    $report = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if (!$report.success) { throw "Acceptance stage failed: $Path" }
    return $report
}
function Invoke-Stage([string]$Stage, [string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $process = Start-OwnedProcess $executable @('--installed-acceptance','--stage',$Stage,'--output',$Directory)
    $exitCode = Wait-OwnedExit $process
    if ($Stage -eq 'autosave-crash') {
        if ($exitCode -eq 0 -or !(Test-Path -LiteralPath (Join-Path $Directory 'autosave-before-crash.json'))) {
            throw 'The timed autosave/forced-termination stage did not complete as expected.'
        }
    } elseif ($exitCode -ne 0) { throw "Installed acceptance stage $Stage exited with $exitCode." }
}
function Snapshot-UserData {
    if (!(Test-Path -LiteralPath $userData)) { return @() }
    return @(Get-ChildItem -LiteralPath $userData -File -Recurse | ForEach-Object {
        [pscustomobject]@{ path=[IO.Path]::GetRelativePath($userData,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
function Verify-UserData($Snapshot) {
    foreach ($file in $Snapshot) {
        $path = Join-Path $userData $file.path
        if (!(Test-Path -LiteralPath $path)) { throw "User data disappeared: $($file.path)" }
        if ($file.path -like '*kabelaar.log') {
            # Logging may append; every pre-existing byte must remain intact.
            $stream = [IO.File]::OpenRead($path)
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
            try {
                $remaining = [long]$file.bytes
                $buffer = [byte[]]::new(65536)
                while ($remaining -gt 0) {
                    $read = $stream.Read($buffer,0,[int][Math]::Min($remaining,$buffer.Length))
                    if ($read -eq 0) { throw 'Existing user log was truncated.' }
                    $hash.AppendData($buffer,0,$read)
                    $remaining -= $read
                }
                if ([Convert]::ToHexString($hash.GetHashAndReset()) -ne $file.sha256) { throw 'Existing user log content changed.' }
            } finally { $stream.Dispose(); $hash.Dispose() }
        } elseif ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
            throw "User data changed across update: $($file.path)"
        }
    }
}
function Verify-InstalledFiles {
    $actual = Get-Content -LiteralPath (Join-Path $current 'build-info.json') -Raw | ConvertFrom-Json
    if ($actual.version -ne $ExpectedVersion -or $actual.commit -ne $expected.commit) { throw 'Installed version/commit differs from the tested release.' }
    $checked = 0
    foreach ($file in $expected.files) {
        # vpk's documented default excludes PDB debug symbols, not runtime binaries.
        if ($file.path -like '*.pdb') { continue }
        $path = [IO.Path]::GetFullPath((Join-Path $current $file.path))
        if (!$path.StartsWith($current.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Installed runtime mismatch: $($file.path)" }
        $checked++
    }
    return $checked
}
function Wait-Report([string]$Path, [int]$Seconds = 120) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path -LiteralPath $Path) {
            # The application publishes complete JSON by an atomic rename.
            return Read-Success $Path
        }
        $errorPath = $Path -replace '-result\.json$', '-error.json'
        if ($errorPath -ne $Path -and (Test-Path -LiteralPath $errorPath)) { throw (Get-Content -LiteralPath $errorPath -Raw) }
        Start-Sleep -Milliseconds 200
    }
    throw "Timed out waiting for acceptance report: $Path"
}
function Invoke-ShellProbe([string]$File, [string]$Name) {
    $directory = Join-Path $OutputDirectory $Name
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $nonce = [Guid]::NewGuid().ToString('N')
    $priorOutput = $env:KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT
    $priorNonce = $env:KABELAAR_ACCEPTANCE_NONCE
    try {
        $env:KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT = $directory
        $env:KABELAAR_ACCEPTANCE_NONCE = $nonce
        # Launch the real .lnk/document through Windows; no shortcut rewriting or keyboard automation.
        $process = Start-Process -FilePath $File -WindowStyle Hidden -PassThru
        $report = Wait-Report (Join-Path $directory 'launch-result.json') 30
        if ($report.nonce -ne $nonce -or $report.identity.version -ne $ExpectedVersion) { throw 'Shell launch did not acknowledge this test and version.' }
        # ShellExecute may return no process handle, or a launcher. The opt-in nonce
        # identifies the actual owned application, which waits for this acknowledgement.
        if ($null -ne $process) { $process.Dispose() }
        $launched = [Diagnostics.Process]::GetProcessById([int]$report.identity.processId)
        Set-Content -LiteralPath (Join-Path $directory 'launch.ack') -Value $nonce -Encoding utf8
        if ((Wait-OwnedExit $launched 15) -ne 0) { throw 'Shell-launched acceptance process failed.' }
        return $report
    } finally {
        $env:KABELAAR_ACCEPTANCE_LAUNCH_OUTPUT = $priorOutput
        $env:KABELAAR_ACCEPTANCE_NONCE = $priorNonce
    }
}

Assert-NoInstalledProcesses
$before = @(Snapshot-UserData)
ConvertTo-Json -InputObject $before -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'user-data-before.json') -Encoding utf8
$fromVersion = (Get-Content -LiteralPath (Join-Path $current 'build-info.json') -Raw | ConvertFrom-Json).version
$profileDirectory = Join-Path $OutputDirectory 'profile'
$crashDirectory = Join-Path $OutputDirectory 'crash-recovery'
if ($Mode -eq 'Application') {
    Invoke-Stage 'seed-profile' $profileDirectory
    Invoke-Stage 'autosave-crash' $crashDirectory
    $acrossUpdate = @(Snapshot-UserData)
    ConvertTo-Json -InputObject $acrossUpdate -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'user-data-before-transition.json') -Encoding utf8
    $transition = Join-Path $OutputDirectory 'application-transition'
    New-Item -ItemType Directory -Path $transition -Force | Out-Null
    $process = Start-OwnedProcess $executable @('--installed-acceptance','--stage','update','--output',$transition,
        '--release-directory',$ReleaseDirectory,'--expected-version',$ExpectedVersion)
    if ((Wait-OwnedExit $process 120) -ne 0) { throw 'The application update stage failed before applying.' }
    $transitionReport = Wait-Report (Join-Path $transition 'verify-update-result.json')
    $restart = [Diagnostics.Process]::GetProcessById([int]$transitionReport.identity.processId)
    Set-Content -LiteralPath (Join-Path $transition 'verify-update.ack') -Value 'result received' -Encoding utf8
    if ((Wait-OwnedExit $restart 15) -ne 0) { throw 'Restart verification did not exit successfully.' }
    Verify-UserData $acrossUpdate
} elseif ($Mode -eq 'Updater') {
    $process = Start-OwnedProcess (Join-Path $InstalledRoot 'Update.exe') @('apply','--silent','--norestart',
        '--package',$fullPackages[0].FullName,'--log',(Join-Path $OutputDirectory 'updater.log'))
    if ((Wait-OwnedExit $process 120) -ne 0) { throw 'The actual installed updater failed.' }
}
Assert-NoInstalledProcesses
$verifiedFiles = Verify-InstalledFiles
Verify-UserData $before
if ($Mode -ne 'Application') {
    Invoke-Stage 'seed-profile' $profileDirectory
    Invoke-Stage 'autosave-crash' $crashDirectory
}
Invoke-Stage 'recovery' $crashDirectory
$recoveryReport = Read-Success (Join-Path $crashDirectory 'recovery-result.json')
$guardDirectory = Join-Path $OutputDirectory 'update-guards'
Invoke-Stage 'guards' $guardDirectory
$guardReport = Read-Success (Join-Path $guardDirectory 'guards-result.json')
$closeDirectory = Join-Path $OutputDirectory 'normal-clean-close'
Invoke-Stage 'clean-close' $closeDirectory
$closeReport = Read-Success (Join-Path $closeDirectory 'clean-close-result.json')
Invoke-Stage 'cleanup-profile' $profileDirectory
Verify-UserData $before

$association = (Get-Item -LiteralPath 'HKCU:\Software\Classes\Kabelaar.Project\shell\open\command').GetValue('')
$expectedAssociation = '"' + $executable + '" "%1"'
if ($association -ne $expectedAssociation) { throw 'The installed project association does not target the installed current executable.' }
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Kabelaar.lnk'
$menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kabelaar.lnk'
$shellReports = @()
foreach ($item in @(@{File=$desktopLink;Name='desktop-shortcut'},@{File=$menuLink;Name='start-menu-shortcut'},
    @{File=$guardReport.projectPath;Name='project-association'})) {
    if (!(Test-Path -LiteralPath $item.File)) { throw "Actual shell entry is missing: $($item.File)" }
    $shellReports += Invoke-ShellProbe $item.File $item.Name
}
Verify-UserData $before
[ordered]@{
    success=$true; fromVersion=$fromVersion; toVersion=$ExpectedVersion; mode=$Mode; commit=$expected.commit
    installedRoot=$InstalledRoot; userDataRoot=$userData; verifiedRuntimeFiles=$verifiedFiles
    existingUserFilesPreserved=@($before).Count; actualShellLaunches=$shellReports.Count
    timedAutosaveSeconds=$recoveryReport.timedAutosaveSeconds; updateGuardChecks=$guardReport.checks
    normalCleanClose=$closeReport.closed
    evidenceScope='Actual installed processes and shell launches; scripted confirmation answers, no mouse/keyboard visual acceptance.'
} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'installed-acceptance-result.json') -Encoding utf8
"Installed acceptance passed: $OutputDirectory"
