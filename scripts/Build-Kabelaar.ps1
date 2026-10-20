#requires -Version 7.0
param(
    [string]$Version = '0.9.0',
    [string]$ArtifactDirectory = '',
    [switch]$BuildNative,
    [switch]$SkipTests,
    [switch]$SkipSmokeTest,
    [switch]$InteractiveSmoke
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath("$PSScriptRoot\..")
Set-Location -LiteralPath $repository
if (!$ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $repository ("artifacts\builds\" + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
if (Test-Path -LiteralPath $ArtifactDirectory) {
    if (@(Get-ChildItem -LiteralPath $ArtifactDirectory -Force).Count -gt 0) { throw 'Choose a new empty build artifact directory.' }
}
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$publishDirectory = Join-Path $ArtifactDirectory 'app'
$testDirectory = Join-Path $ArtifactDirectory 'test-results'
$commit = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
$dirtyBefore = @(& git status --porcelain --untracked-files=normal)
$nativeDirectory = Join-Path $repository 'artifacts\native-worker'
$nativeMetadata = Join-Path $nativeDirectory 'native-build-info.json'
$sourceHash = (Get-FileHash -LiteralPath 'native\Kabelaar.CadWorker\main.cpp' -Algorithm SHA256).Hash
$nativeCurrent = Test-Path -LiteralPath $nativeMetadata
if ($nativeCurrent) {
    $previousNative = Get-Content -LiteralPath $nativeMetadata -Raw | ConvertFrom-Json
    $nativeCurrent = $previousNative.sourceSha256 -eq $sourceHash -and @($previousNative.sources).Count -eq 3
    if ($nativeCurrent) {
        foreach ($source in $previousNative.sources) {
            $sourcePath = Join-Path "$repository\native\Kabelaar.CadWorker" $source.name
            if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ne $source.sha256) { $nativeCurrent = $false }
        }
    }
}
if ($BuildNative -or !$nativeCurrent) {
    if (!(Test-Path -LiteralPath 'artifacts\native-deps\occt-8.0.1\sdk')) {
        throw 'Native SDK missing. Run native/Kabelaar.CadWorker/Get-NativeDependencies.ps1 once, then provide an x64 MSVC developer environment.'
    }
    & "$repository\native\Kabelaar.CadWorker\Build-Native.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
}
$nativeInfo = Get-Content -LiteralPath $nativeMetadata -Raw | ConvertFrom-Json
foreach ($file in $nativeInfo.files) {
    $path = Join-Path $nativeDirectory $file.name
    if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) {
        throw "Native runtime changed: $($file.name). Rebuild with -BuildNative."
    }
}
$oldWorker = $env:KABELAAR_CAD_WORKER
try {
    $env:KABELAAR_CAD_WORKER = Join-Path $nativeDirectory 'Kabelaar.CadWorker.exe'
    & dotnet restore "$repository\Kabelaar.slnx" -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    $testProjects = @(Get-ChildItem -LiteralPath "$repository\tests" -Filter '*.csproj' -Recurse)
    if (!$SkipTests) {
        New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
        foreach ($project in $testProjects) {
            & dotnet test $project.FullName -c Release --nologo --logger "trx;LogFileName=$($project.BaseName).trx" --results-directory $testDirectory "-p:Version=$Version"
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.BaseName)" }
        }
    }
    $sourceRevision = if ($dirtyBefore.Count -eq 0) { $commit } else { "$commit-dirty" }
    & dotnet publish "$repository\src\Kabelaar.App\Kabelaar.App.csproj" -c Release -r win-x64 --self-contained true -o $publishDirectory "-p:Version=$Version" "-p:SourceRevisionId=$sourceRevision" "-p:ContinuousIntegrationBuild=true"
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained desktop publish failed.' }

    # Retain exact dependency versions and the license notices present in each restored NuGet package.
    $licenseDirectory = Join-Path $publishDirectory 'licenses'
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    $assets = Get-Content -LiteralPath 'src\Kabelaar.App\obj\project.assets.json' -Raw | ConvertFrom-Json
    $dependencyInfo = @()
    foreach ($property in $assets.libraries.PSObject.Properties) {
        if ($property.Value.type -ne 'package') { continue }
        $packagePath = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $property.Value.path
            if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
        }
        if (!$packagePath) { throw "Restored dependency missing: $($property.Name)" }
        $nuspecFile = Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File | Select-Object -First 1
        [xml]$nuspec = Get-Content -LiteralPath $nuspecFile.FullName -Raw
        $metadata = $nuspec.package.metadata
        $dependencyInfo += [ordered]@{
            package = $property.Name; authors = [string]$metadata.authors
            license = [string]$metadata.license.InnerText; licenseUrl = [string]$metadata.licenseUrl
            repository = [string]$metadata.repository.url; commit = [string]$metadata.repository.commit
        }
        $destination = Join-Path $licenseDirectory ($property.Name.Replace('/','-'))
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $packagePath -File |
            Where-Object Name -Match '^(LICENSE|COPYING|NOTICE|THIRD.PARTY)' |
            Copy-Item -Destination $destination -Force
    }
    # Self-contained runtime packs are not listed as ordinary NuGet libraries.
    # Derive their exact versions from the published runtime config, not the SDK installed on this PC.
    $runtimeConfig = Get-Content -LiteralPath "$publishDirectory\Kabelaar.runtimeconfig.json" -Raw | ConvertFrom-Json
    $runtimeFrameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    if ($runtimeFrameworks.Count -lt 2) { throw 'Published self-contained runtime framework metadata is incomplete.' }
    foreach ($framework in $runtimeFrameworks) {
        $packageId = "$($framework.name).Runtime.win-x64"
        $packagePath = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder ($packageId.ToLowerInvariant() + '/' + $framework.version)
            if (Test-Path -LiteralPath $candidate) { $packagePath = $candidate; break }
        }
        if (!$packagePath) { throw "Runtime license source missing: $packageId/$($framework.version)" }
        $destination = Join-Path $licenseDirectory ($packageId + '-' + $framework.version)
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $runtimeNotices = @(Get-ChildItem -LiteralPath $packagePath -File |
            Where-Object Name -Match '^(LICENSE|COPYING|NOTICE|THIRD.PARTY)')
        if ($runtimeNotices.Count -eq 0) { throw "Runtime license files missing: $packageId" }
        $runtimeNotices | Copy-Item -Destination $destination -Force
        $dependencyInfo += [ordered]@{ package="$packageId/$($framework.version)"; source='Published includedFrameworks'; licenseFiles=@($runtimeNotices.Name) }
    }
    $dependencyInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$licenseDirectory\managed-dependencies.json" -Encoding utf8
    if (Test-Path -LiteralPath "$repository\scripts\licenses") {
        Get-ChildItem -LiteralPath "$repository\scripts\licenses" -File | Copy-Item -Destination $licenseDirectory -Force
    }
    if (!$SkipSmokeTest) {
        $smokeDirectory = Join-Path $ArtifactDirectory 'smoke'
        $smokeArguments = @('--smoke-test','--output',"`"$smokeDirectory`"")
        $windowStyle = 'Hidden'
        $smokeTimeout = 180000
        if ($InteractiveSmoke) {
            $smokeArguments += '--interactive-smoke'
            $windowStyle = 'Normal'
            $smokeTimeout = 115000
        }
        $smokeWatch = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process -FilePath "$publishDirectory\Kabelaar.exe" -ArgumentList $smokeArguments -WindowStyle $windowStyle -PassThru
        $remaining = [int][Math]::Max(0, $smokeTimeout - $smokeWatch.ElapsedMilliseconds)
        if (!$process.WaitForExit($remaining)) { $process.Kill($true); throw 'Published app smoke test timed out.' }
        if ($process.ExitCode -ne 0) { throw "Published app smoke test failed: exit $($process.ExitCode)." }
        $smokeFile = Join-Path $smokeDirectory 'smoke-result.json'
        $smokeResult = if (Test-Path -LiteralPath $smokeFile) { Get-Content -LiteralPath $smokeFile -Raw | ConvertFrom-Json } else { $null }
        if (!$smokeResult.success -or $smokeResult.scope -ne 'full' -or $smokeResult.performanceMeasured -ne $true) {
            throw 'Published app smoke acceptance report is missing or failed.'
        }
    }
    $dirtyAfter = @(& git status --porcelain --untracked-files=normal)
    $finalCommit = (& git rev-parse HEAD).Trim()
    if ($finalCommit -ne $commit) { throw 'HEAD changed during build. Repeat the build from stable source.' }
    $clean = $dirtyBefore.Count -eq 0 -and $dirtyAfter.Count -eq 0
    $files = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($publishDirectory, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $testFiles = if (Test-Path -LiteralPath $testDirectory) { @(Get-ChildItem -LiteralPath $testDirectory -File -Recurse | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($ArtifactDirectory, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    }) } else { @() }
    if (!$SkipSmokeTest) {
        $testFiles += @(Get-ChildItem -LiteralPath $smokeDirectory -Filter '*.json' -File | ForEach-Object {
            [ordered]@{ path = [IO.Path]::GetRelativePath($ArtifactDirectory, $_.FullName); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    }
    [ordered]@{
        version=$Version; commit=$commit; cleanSource=$clean; configuration='Release'; runtime='win-x64'
        testsPassed=(!$SkipTests -and $testProjects.Count -gt 0); testProjectCount=$testProjects.Count
        smokePassed=(!$SkipSmokeTest); builtUtc=[DateTimeOffset]::UtcNow.ToString('O')
        acceptanceStatus='Awaiting user production acceptance'; files=$files; evidence=$testFiles
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$publishDirectory\build-info.json" -Encoding utf8
    "Build ready: $publishDirectory"
    if (!$clean) { 'Development build: source is not clean; packaging is intentionally unavailable.' }
} finally {
    $env:KABELAAR_CAD_WORKER = $oldWorker
}
