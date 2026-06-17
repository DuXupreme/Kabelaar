<#
.SYNOPSIS
    Bouwt Kabelboom Tekenstudio en pakt het als Velopack-release.

.DESCRIPTION
    Stappen:
      1. PyInstaller one-dir build  -> dist/Kabelboom Tekenstudio/
      2. vpk pack (icoon + splash)  -> Releases/
      3. (optioneel) vpk upload github  -> publiceert naar GitHub Releases

.PARAMETER Version
    Semver-versie van deze release, bijv. 1.0.0.

.PARAMETER Publish
    Indien opgegeven: upload de release naar GitHub Releases.

.PARAMETER Token
    GitHub OAuth-token. Standaard $env:GITHUB_TOKEN.

.EXAMPLE
    ./tools/build_release.ps1 -Version 1.0.0
    ./tools/build_release.ps1 -Version 1.0.1 -Publish
#>
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [switch] $Publish,
    [string] $Token = $env:GITHUB_TOKEN
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- app-identiteit -------------------------------------------------------
$PackId   = 'KabelboomTekenstudio'
$Title    = 'Kabelboom Tekenstudio'
$Authors  = 'DuXupreme'
$MainExe  = 'Kabelboom Tekenstudio.exe'
$PackDir  = Join-Path $root 'dist/Kabelboom Tekenstudio'
$Icon     = Join-Path $root 'assets/icon.ico'
$Splash   = Join-Path $root 'assets/splash.png'
$RepoUrl  = 'https://github.com/DuXupreme/Kabelaar'
$OutDir   = Join-Path $root 'Releases'

# --- 0. versie in de app bakken ------------------------------------------
# updater.py leest APP_VERSION hieruit om met de release-feed te vergelijken.
Set-Content -Path (Join-Path $root '_version.py') -Encoding utf8 -Value @"
# Automatisch gegenereerd door tools/build_release.ps1 -- niet handmatig wijzigen.
APP_VERSION = "$Version"
"@

# --- 1. PyInstaller -------------------------------------------------------
# Eerst zelf opruimen met -Force; PyInstaller's eigen --clean faalt op
# read-only/door OneDrive gelockte .pyc-bestanden onder build/.
function Remove-Tree([string] $p) {
    if (-not (Test-Path $p)) { return }
    for ($i = 0; $i -lt 3; $i++) {
        try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop; return }
        catch { Start-Sleep -Milliseconds 600 }
    }
    Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop
}
Remove-Tree (Join-Path $root 'build')
Remove-Tree (Join-Path $root 'dist')

Write-Host "==> PyInstaller build ($Version)..." -ForegroundColor Cyan
python -m PyInstaller --noconfirm "Kabelboom Tekenstudio.spec"
if ($LASTEXITCODE -ne 0) { throw "PyInstaller faalde (exit $LASTEXITCODE)" }
if (-not (Test-Path (Join-Path $PackDir $MainExe))) {
    throw "Verwachte exe niet gevonden: $(Join-Path $PackDir $MainExe)"
}

# --- 2. vpk pack ----------------------------------------------------------
Write-Host "==> Velopack pack..." -ForegroundColor Cyan
vpk pack `
    --packId      $PackId `
    --packVersion $Version `
    --packDir     $PackDir `
    --mainExe     $MainExe `
    --packTitle   $Title `
    --packAuthors $Authors `
    --icon        $Icon `
    --splashImage $Splash `
    --outputDir   $OutDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack faalde (exit $LASTEXITCODE)" }

Write-Host "==> Release klaar in: $OutDir" -ForegroundColor Green

# --- 3. publiceren --------------------------------------------------------
if ($Publish) {
    if ([string]::IsNullOrWhiteSpace($Token)) {
        throw "Geen GitHub-token. Geef -Token of zet `$env:GITHUB_TOKEN."
    }
    Write-Host "==> Uploaden naar GitHub Releases..." -ForegroundColor Cyan
    vpk upload github `
        --outputDir $OutDir `
        --repoUrl   $RepoUrl `
        --token     $Token `
        --publish   true `
        --merge     true
    if ($LASTEXITCODE -ne 0) { throw "vpk upload faalde (exit $LASTEXITCODE)" }
    Write-Host "==> Gepubliceerd op $RepoUrl/releases" -ForegroundColor Green
} else {
    Write-Host "Tip: voeg -Publish toe om naar GitHub te uploaden." -ForegroundColor Yellow
}
