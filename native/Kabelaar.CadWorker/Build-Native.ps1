param(
    [string]$MsvcRoot = "$PSScriptRoot\..\..\artifacts\native-deps\msvc",
    [string]$OcctRoot = "$PSScriptRoot\..\..\artifacts\native-deps\occt-8.0.1\sdk\opencascade-8.0.1-vc14-64",
    [string]$ThirdPartyRoot = "$PSScriptRoot\..\..\artifacts\native-deps\occt-thirdparty",
    [string]$OutputDirectory = "$PSScriptRoot\..\..\artifacts\native-worker"
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath("$PSScriptRoot\..\..")
$OcctRoot = (Resolve-Path -LiteralPath $OcctRoot).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$portable = Test-Path -LiteralPath $MsvcRoot
if ($portable) {
    $MsvcRoot = (Resolve-Path -LiteralPath $MsvcRoot).Path
    $compilerVersion = Get-ChildItem -LiteralPath "$MsvcRoot\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $sdkVersion = Get-ChildItem -LiteralPath "$MsvcRoot\Windows Kits\10\Include" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    $compilerBin = "$($compilerVersion.FullName)\bin\Hostx64\x64"
} else {
    $compilerBin = Split-Path -Parent (Get-Command cl.exe -ErrorAction Stop).Source
    if (!$env:INCLUDE -or !$env:LIB) { throw 'Start from an x64 Visual Studio Developer PowerShell, or pass a portable -MsvcRoot.' }
}
$oldPath = $env:PATH
$oldInclude = $env:INCLUDE
$oldLib = $env:LIB
try {
    if ($portable) {
        $env:PATH = "$env:PATH;$compilerBin;$MsvcRoot\Windows Kits\10\bin\$($sdkVersion.Name)\x64"
        $env:INCLUDE = "$($compilerVersion.FullName)\include;$($sdkVersion.FullName)\ucrt;$($sdkVersion.FullName)\shared;$($sdkVersion.FullName)\um"
        $env:LIB = "$($compilerVersion.FullName)\lib\x64;$MsvcRoot\Windows Kits\10\Lib\$($sdkVersion.Name)\ucrt\x64;$MsvcRoot\Windows Kits\10\Lib\$($sdkVersion.Name)\um\x64"
    }
    $buildDirectory = Join-Path $repository 'artifacts\native-build'
    & cmake -S $PSScriptRoot -B $buildDirectory -G Ninja "-DCMAKE_BUILD_TYPE=Release" "-DCMAKE_TRY_COMPILE_CONFIGURATION=Release" "-DCMAKE_CXX_COMPILER=$compilerBin\cl.exe" "-DOCCT_ROOT=$OcctRoot"
    if ($LASTEXITCODE -ne 0) { throw 'Native CMake configure failed.' }
    & cmake --build $buildDirectory --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Native compilation failed.' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    Copy-Item -LiteralPath "$buildDirectory\Kabelaar.CadWorker.exe" -Destination $OutputDirectory -Force
    # Resolve only actual PE runtime dependencies. Do not ship Draw/VTK/Python or entire SDKs.
    $runtimeSources = @("$OcctRoot\win64\vc14\bin", $ThirdPartyRoot, $compilerBin, "$env:SystemRoot\System32")
    $available = @{}
    foreach ($root in $runtimeSources) {
        if (Test-Path -LiteralPath $root) {
            $recursive = $root -ne "$env:SystemRoot\System32"
            foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.dll' -File -Recurse:$recursive) {
                if (!$available.ContainsKey($file.Name.ToLowerInvariant())) { $available[$file.Name.ToLowerInvariant()] = $file.FullName }
            }
        }
    }
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue("$OutputDirectory\Kabelaar.CadWorker.exe")
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($queue.Count -gt 0) {
        $binary = $queue.Dequeue()
        $dependencies = & "$compilerBin\dumpbin.exe" /DEPENDENTS $binary
        foreach ($line in $dependencies) {
            if ($line -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') {
                $name = $Matches[1]
                if (!$seen.Add($name)) { continue }
                if ($name -match '^(api-ms-|ext-ms-)') { continue }
                $source = $available[$name.ToLowerInvariant()]
                if (!$source) { throw "Missing native runtime dependency: $name" }
                if ($source.StartsWith("$env:SystemRoot\System32", [StringComparison]::OrdinalIgnoreCase) -and $name -notmatch '^(msvcp|vcruntime|concrt)') { continue }
                Copy-Item -LiteralPath $source -Destination (Join-Path $OutputDirectory $name) -Force
                $queue.Enqueue((Join-Path $OutputDirectory $name))
            }
        }
    }
    $licenseRoot = Join-Path $OutputDirectory 'licenses'
    New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $OcctRoot -File | Where-Object Name -Match 'LICENSE|COPYING|OCCT_LGPL' | Copy-Item -Destination $licenseRoot -Force
    foreach ($component in @('freetype-2.13.3-x64', 'freeimage-3.18.0-x64', 'tbb-2021.13.0-x64', 'jemalloc-vc14-64')) {
        $componentPath = Get-ChildItem -LiteralPath $ThirdPartyRoot -Directory -Recurse | Where-Object Name -eq $component | Select-Object -First 1
        if ($componentPath) {
            $componentLicense = Join-Path $licenseRoot $component
            New-Item -ItemType Directory -Path $componentLicense -Force | Out-Null
            Get-ChildItem -LiteralPath $componentPath.FullName -File -Recurse |
                Where-Object Name -Match '^(LICENSE|COPYING|copyright)' |
                Copy-Item -Destination $componentLicense -Force
        }
    }
    if (Test-Path -LiteralPath "$PSScriptRoot\licenses") {
        Copy-Item -LiteralPath "$PSScriptRoot\licenses" -Destination $OutputDirectory -Recurse -Force
    }
    Copy-Item -LiteralPath "$PSScriptRoot\THIRD-PARTY-NOTICES.txt" -Destination $OutputDirectory -Force
    $buildInfo = [ordered]@{
        workerVersion = '0.9.0'; protocolVersion = 1; occtVersion = '8.0.1'
        sourceSha256 = (Get-FileHash -LiteralPath "$PSScriptRoot\main.cpp" -Algorithm SHA256).Hash
        sources = @('main.cpp','CMakeLists.txt','Build-Native.ps1') | ForEach-Object {
            [ordered]@{ name=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash }
        }
        files = @(Get-ChildItem -LiteralPath $OutputDirectory -File |
            Where-Object Extension -In '.exe','.dll' |
            ForEach-Object { [ordered]@{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
    }
    $buildInfo | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$OutputDirectory\native-build-info.json" -Encoding utf8
    "Built native STEP worker: $OutputDirectory"
} finally {
    $env:PATH = $oldPath
    $env:INCLUDE = $oldInclude
    $env:LIB = $oldLib
}
