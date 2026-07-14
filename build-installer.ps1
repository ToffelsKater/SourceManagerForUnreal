# Builds the distributable installer for Source Manager for Unreal Engine.
#   .\build-installer.ps1        -> dist\SourceManagerSetup.exe  (Inno Setup)
#   .\build-installer.ps1 -Msi   -> also builds dist\SourceManagerSetup.msi (WiX)
# Requires: .NET SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
#           and for -Msi the WiX tool (dotnet tool install --global wix --version 5.0.2)

param([switch]$Msi)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host '1/2 Publishing self-contained exe...'
dotnet publish "$root\UnrealManager.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o "$root\dist" -v quiet -nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '2/2 Building Inno Setup installer...'
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }

& $iscc /Q "$root\installer\setup.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Get-Item "$root\dist\SourceManagerSetup.exe"
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))

if ($Msi) {
    Write-Host 'Extra: building MSI (WiX)...'
    wix build "$root\installer\Product.wxs" -bindpath "dist=$root\dist" -o "$root\dist\SourceManagerSetup.msi"
    if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
    $msiFile = Get-Item "$root\dist\SourceManagerSetup.msi"
    Write-Host ("Done: {0} ({1:N1} MB)" -f $msiFile.FullName, ($msiFile.Length / 1MB))
}
