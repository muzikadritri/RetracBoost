# Baut Retrac Boost und legt die fertige RetracBoost.exe eine Ebene höher ab.
param([string]$Config = 'Release')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

if (-not (Test-Path (Join-Path $here 'app.ico'))) { & (Join-Path $here 'make-icon.ps1') }

dotnet build (Join-Path $here 'RetracBoost.csproj') -c $Config -o (Join-Path $here 'bin') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen ($LASTEXITCODE)" }

$target = Join-Path (Split-Path $here -Parent) 'RetracBoost.exe'
$prev = Join-Path (Split-Path $here -Parent) 'RetracBoost.vorherige-Version.exe'
try {
    Copy-Item (Join-Path $here 'bin\RetracBoost.exe') $target -Force
} catch {
    if (Test-Path $target) {
        try { Move-Item $target $prev -Force -ErrorAction Stop }
        catch { Move-Item $target (Join-Path $env:TEMP ("RetracBoost.old." + [Guid]::NewGuid().ToString("N") + ".exe")) -Force }
    }
    Copy-Item (Join-Path $here 'bin\RetracBoost.exe') $target -Force
}
"Fertig: $target"

$setupProj = Join-Path (Split-Path $here -Parent) 'RetracBoostSetup\RetracBoostSetup.csproj'
if (Test-Path $setupProj) {
    $setupDir = Split-Path $setupProj -Parent
    dotnet build $setupProj -c $Config -o (Join-Path $setupDir 'bin') --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Setup-Build fehlgeschlagen ($LASTEXITCODE)" }
    $setupTarget = Join-Path (Split-Path $here -Parent) 'RetracBoost-Setup.exe'
    Copy-Item (Join-Path $setupDir 'bin\RetracBoost-Setup.exe') $setupTarget -Force
    "Fertig: $setupTarget"
}
