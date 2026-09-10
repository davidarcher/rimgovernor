$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Set-Location $repo
if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before this fixture.' }
$env:DOTNET_ROOT = Join-Path $repo 'tmp/dotnet'
$dll = 'C:/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/RimGovernorObservations/BridgeTools/Observations/RimGovernor.Observations.BridgeTools.dll'
$backup = Join-Path $repo ('.rimgovernor/install-backup-' + [guid]::NewGuid().ToString() + '.dll')
Copy-Item -LiteralPath $dll -Destination $backup
try {
    & tmp/dotnet/dotnet.exe build integrations/colony-bridge/src/ColonyObservations.csproj -c Release -v quiet -p:InstallFixture=true
    if ($LASTEXITCODE) { throw 'Fixture build failed' }
    Copy-Item -LiteralPath integrations/colony-bridge/BridgeTools/Observations/RimGovernor.Observations.BridgeTools.dll -Destination $dll -Force
    & .venv/Scripts/python.exe -X utf8 scripts/install_smoke.py
    if ($LASTEXITCODE) { throw 'Installation fixture failed' }
} finally {
    if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Game still running; fixture DLL restoration requires it to close.' }
    Copy-Item -LiteralPath $backup -Destination $dll -Force
    & powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build_observation_bridge.ps1 -Install
    if ($LASTEXITCODE) { throw 'Normal companion build/install failed; prior DLL was restored.' }
}
