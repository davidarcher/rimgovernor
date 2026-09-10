param(
    [switch]$Install,
    [string]$RimWorld = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [string]$DotNet = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $DotNet) { $DotNet = Join-Path $repoRoot 'tmp/dotnet/dotnet.exe' }
$env:DOTNET_ROOT = Split-Path $DotNet -Parent
$sourceRoot = Join-Path $repoRoot 'integrations/colony-bridge'
& $DotNet build (Join-Path $sourceRoot 'src/ColonyObservations.csproj') -c Release -v quiet "-p:RimWorldManagedDir=$RimWorld/RimWorldWin64_Data/Managed" "-p:RimBridgeSdkDir=$RimWorld/Mods/RimBridgeServer/1.6/Assemblies"
if ($LASTEXITCODE -ne 0) { throw 'Observation companion build failed' }
if ($Install) {
    if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before replacing its companion DLL' }
    $destination = Join-Path $RimWorld 'Mods/RimGovernorObservations'
    New-Item -ItemType Directory -Force $destination | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Assemblies') -Destination $destination -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'About') -Destination $destination -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'BridgeTools') -Destination $destination -Recurse -Force
    Write-Output "Installed observation companion: $destination"
}
