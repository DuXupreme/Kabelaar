$scriptPath = Join-Path $PSScriptRoot "kabelboom_tekenstudio.py"

function Get-LocalPythonCandidates {
    $candidates = @()
    $localPythonRoot = Join-Path $env:LocalAppData "Programs\Python"

    if (Test-Path $localPythonRoot) {
        $candidates += Get-ChildItem -Path $localPythonRoot -Directory -Filter "Python*" -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "python.exe" }
    }

    return $candidates | Select-Object -Unique
}

foreach ($candidate in Get-LocalPythonCandidates) {
    if (Test-Path $candidate) {
        & $candidate $scriptPath
        exit $LASTEXITCODE
    }
}

if (Get-Command python -ErrorAction SilentlyContinue) {
    & python $scriptPath
    exit $LASTEXITCODE
}

$pyCommand = Get-Command py -ErrorAction SilentlyContinue
if ($pyCommand) {
    $pyListOutput = & $pyCommand.Source -0p 2>&1 | Out-String

    if ($pyListOutput -match "python\.exe") {
        & $pyCommand.Source $scriptPath
        exit $LASTEXITCODE
    }
}

Write-Error @"
Geen Python-installatie gevonden voor Kabelboom Tekenstudio.

Installeer Python 3.10 of nieuwer en vink 'Add python.exe to PATH' aan,
of installeer Python onder %LocalAppData%\Programs\Python.

Start daarna opnieuw via start_kabelboom_tekenstudio.bat.
"@
exit 1
