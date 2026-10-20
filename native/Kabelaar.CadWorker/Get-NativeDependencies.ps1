param([string]$DependencyDirectory = "$PSScriptRoot\..\..\artifacts\native-deps")
$ErrorActionPreference = 'Stop'
$DependencyDirectory = [IO.Path]::GetFullPath($DependencyDirectory)
New-Item -ItemType Directory -Path $DependencyDirectory -Force | Out-Null
$downloads = @(
    @{Name='opencascade-release-no-pch.zip'; Hash='307F694F1D4A280C7F58EE2DDB69A7F7E2B78D82749339EFD40CE8B8B116D76C'; Folder='occt-8.0.1'; Inner='opencascade-8.0.1-vc14-64.zip'},
    @{Name='3rdparty-vc14-64.zip'; Hash='12D04F688C583B8E896758F65CD7012458FEE921CEDB792EC6CAAF469B372598'; Folder='occt-thirdparty'; Inner='3rdparty-vc14-64.zip'}
)
foreach ($item in $downloads) {
    $path = Join-Path $DependencyDirectory $item.Name
    if (!(Test-Path -LiteralPath $path)) {
        Invoke-WebRequest "https://github.com/Open-Cascade-SAS/OCCT/releases/download/V8.0.1/$($item.Name)" -OutFile $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item.Hash) { throw "Native dependency checksum mismatch: $($item.Name)" }
    $unpacked = Join-Path $DependencyDirectory $item.Folder
    Expand-Archive -LiteralPath $path -DestinationPath $unpacked -Force
    $inner = Join-Path $unpacked $item.Inner
    Expand-Archive -LiteralPath $inner -DestinationPath (Join-Path $unpacked 'sdk') -Force
}
'Verified and extracted OCCT 8.0.1 dependencies. No compiler or system settings were changed.'
