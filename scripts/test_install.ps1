param(
    [Parameter(Mandatory = $true)][string]$RimWorld,
    [Parameter(Mandatory = $true)][string]$RimWorldManagedDir,
    [Parameter(Mandatory = $true)][string]$HarmonyAssembly,
    [Parameter(Mandatory = $true)][string]$RimBridgeSdkDir,
    [string]$DotNet = 'dotnet',
    [string]$Python = 'python'
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskGame = (Resolve-Path -LiteralPath $RimWorld).Path
$taskConfig = Get-Content -Raw -LiteralPath (Join-Path $taskRepo '.rimgovernor/bridge/config/config.json') | ConvertFrom-Json
$taskConfiguredGame = [IO.Path]::GetFullPath($taskConfig.games.'rimgovernor-trial'.workingDir)
if (-not $taskConfiguredGame.Equals($taskGame, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RimWorld must match the existing bridge trial workingDir used by install_smoke.py'
}
$taskInstalled = Join-Path $taskGame 'Mods/RimGovernor'
$taskOutput = Join-Path $taskRepo ('.rimgovernor/install-fixture-' + [guid]::NewGuid().ToString('N'))
$taskPackage = & "$PSScriptRoot/build_native_mod.ps1" -RimWorldManagedDir $RimWorldManagedDir `
    -HarmonyAssembly $HarmonyAssembly -RimBridgeSdkDir $RimBridgeSdkDir -DotNet $DotNet `
    -OutputRoot $taskOutput -Fixture InstallFixture
if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') { throw 'Close every RimWorld instance before this fixture.' }
$taskBackup = Join-Path $taskOutput 'original-package'
$taskHadPackage = Test-Path -LiteralPath $taskInstalled
# Both move targets are explicit children of the selected game/artifact roots;
# retain the original whole package so fixture source/manifest cannot survive rollback.
if ($taskHadPackage) { Move-Item -LiteralPath $taskInstalled -Destination $taskBackup }
try {
    Move-Item -LiteralPath $taskPackage -Destination $taskInstalled
    Push-Location $taskRepo
    try { & $Python -X utf8 scripts/install_smoke.py }
    finally { Pop-Location }
    if ($LASTEXITCODE) { throw 'Installation fixture failed' }
} finally {
    if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') {
        throw "Game still running; close it before restoring the retained package at $taskBackup"
    }
    if (Test-Path -LiteralPath $taskInstalled) {
        Move-Item -LiteralPath $taskInstalled -Destination (Join-Path $taskOutput 'fixture-package')
    }
    if ($taskHadPackage) { Move-Item -LiteralPath $taskBackup -Destination $taskInstalled }
}
