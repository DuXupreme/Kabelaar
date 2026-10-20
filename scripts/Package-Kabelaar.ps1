#requires -Version 7.0
param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath("$PSScriptRoot\..")
Set-Location -LiteralPath $repository
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$metadataPath = Join-Path $BuildDirectory 'build-info.json'
if (!(Test-Path -LiteralPath $metadataPath)) { throw 'Build metadata missing. Use Build-Kabelaar.ps1 first.' }
$build = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$commit = (& git rev-parse HEAD).Trim()
if (@(& git status --porcelain --untracked-files=normal).Count -ne 0) { throw 'Packaging requires a clean working tree. Preserve and commit reviewed source changes first.' }
if (!$build.cleanSource -or $build.commit -ne $commit) { throw 'Build was not produced from the current clean commit. Rebuild before packaging.' }
if (!$build.testsPassed -or !$build.smokePassed -or $build.configuration -ne 'Release' -or $build.runtime -ne 'win-x64') {
    throw 'Packaging requires passing Release tests and the published desktop smoke test.'
}
foreach ($file in $build.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $BuildDirectory $file.path))
    if (!$path.StartsWith($BuildDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid build manifest path.' }
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Published file changed after tests: $($file.path)"
    }
}
$actualFiles = @(Get-ChildItem -LiteralPath $BuildDirectory -File -Recurse |
    Where-Object FullName -ne $metadataPath)
if ($actualFiles.Count -ne @($build.files).Count) { throw 'Files were added or removed from the tested publish directory.' }
$artifactDirectory = Split-Path -Parent $BuildDirectory
foreach ($test in $build.evidence) {
    $path = [IO.Path]::GetFullPath((Join-Path $artifactDirectory $test.path))
    if (!$path.StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid test manifest path.' }
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $test.sha256) { throw 'Test results changed or are missing.' }
}
foreach ($required in @('Kabelaar.exe','CadWorker\Kabelaar.CadWorker.exe','CadWorker\THIRD-PARTY-NOTICES.txt','CadWorker\licenses\LICENSE_LGPL_21.txt','licenses\managed-dependencies.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $BuildDirectory $required))) { throw "Required runtime/license file missing: $required" }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repository ("artifacts\releases\" + $build.version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
    throw 'Choose a new empty release directory to avoid mixing old and new packages.'
}
$vpk = Get-Command vpk.exe -ErrorAction SilentlyContinue
if (!$vpk) {
    $candidate = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dotnet\tools\vpk.exe'
    if (!(Test-Path -LiteralPath $candidate)) { throw 'Velopack vpk tool is missing.' }
    $vpkPath = $candidate
} else { $vpkPath = $vpk.Source }
& $vpkPath pack --packId Kabelaar --packTitle Kabelaar --packAuthors Kabelaar --packVersion $build.version --packDir $BuildDirectory --mainExe Kabelaar.exe --runtime win-x64 --icon "$repository\assets\icon.ico" --outputDir $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
foreach ($required in @('Kabelaar-win-Setup.exe','Kabelaar-win-Portable.zip','releases.win.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Velopack output missing: $required" }
}
# Check actual payloads, not only the pre-packaging build directory. Velopack has
# built-in exclusions for the .NET dump collector as well as the default PDB filter.
$fullPackages = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*-full.nupkg' -File)
if ($fullPackages.Count -ne 1) { throw 'Expected exactly one full update package.' }
$payloadCounts = [ordered]@{}
foreach ($archive in @(
    @{ Path=$fullPackages[0].FullName; Prefix='lib/app/' },
    @{ Path=(Join-Path $OutputDirectory 'Kabelaar-win-Portable.zip'); Prefix='current/' }
)) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.Path)
    try {
        $verified = 0
        foreach ($file in $build.files) {
            if ($file.path -like '*.pdb' -or $file.path -eq 'createdump.exe') { continue }
            $entryName = $archive.Prefix + $file.path.Replace('\','/')
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry) { throw "Package payload is missing: $entryName" }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if ($hash -ne $file.sha256) { throw "Packaged file differs from tested build: $entryName" }
            $verified++
        }
        $payloadCounts[[IO.Path]::GetFileName($archive.Path)] = $verified
    } finally { $zip.Dispose() }
}
Copy-Item -LiteralPath $metadataPath -Destination "$OutputDirectory\tested-build-info.json"
$hashes = @(Get-ChildItem -LiteralPath $OutputDirectory -File | ForEach-Object {
    [ordered]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; bytes=$_.Length }
})
[ordered]@{ version=$build.version; commit=$commit; published=$false; acceptanceStatus=$build.acceptanceStatus;
    verifiedPayloadFiles=$payloadCounts; excludedBuildFiles=@($build.files.path | Where-Object { $_ -like '*.pdb' -or $_ -eq 'createdump.exe' }); assets=$hashes } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$OutputDirectory\package-verification.json" -Encoding utf8
"Local installer and portable package ready: $OutputDirectory"
