param(
    [Parameter(Mandatory = $true)][string]$RimWorld,
    [Parameter(Mandatory = $true)][string]$RimWorldManagedDir,
    [Parameter(Mandatory = $true)][string]$HarmonyAssembly,
    [Parameter(Mandatory = $true)][string]$RimBridgeSdkDir,
    [string]$DotNet = 'dotnet',
    [string]$Go = 'go'
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskGame = (Resolve-Path -LiteralPath $RimWorld).Path
$taskConfig = Get-Content -Raw -LiteralPath (Join-Path $taskRepo '.rimgovernor/bridge/config/config.json') | ConvertFrom-Json
$taskConfiguredGame = [IO.Path]::GetFullPath($taskConfig.games.'rimgovernor-trial'.workingDir)
if (-not $taskConfiguredGame.Equals($taskGame, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RimWorld must match the existing bridge trial workingDir'
}
$taskInstalled = Join-Path $taskGame 'Mods/RimGovernor'
$taskOutput = Join-Path $taskRepo ('.rimgovernor/n0103-acceptance-' + [guid]::NewGuid().ToString('N'))
$taskProdPackage = & "$PSScriptRoot/build_native_mod.ps1" -RimWorldManagedDir $RimWorldManagedDir `
    -HarmonyAssembly $HarmonyAssembly -RimBridgeSdkDir $RimBridgeSdkDir -DotNet $DotNet `
    -OutputRoot (Join-Path $taskOutput 'build-production')
# researchaccept exercises test/research_observation_fingerprint, a private fixture
# tool (scripts/fixtures/ResearchObservationFixture.cs) that build_native_mod.ps1 only
# compiles in when -Fixture names it. roomsaccept/suppliesaccept assert the installed
# package carries NO fixture-only tools (a production-shape discovery invariant), so
# research needs its own fixture package instead of sharing the other three's build.
$taskFixturePackage = & "$PSScriptRoot/build_native_mod.ps1" -RimWorldManagedDir $RimWorldManagedDir `
    -HarmonyAssembly $HarmonyAssembly -RimBridgeSdkDir $RimBridgeSdkDir -DotNet $DotNet `
    -Fixture ResearchObservationFixture -OutputRoot (Join-Path $taskOutput 'build-research-fixture')
if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') { throw 'Close every RimWorld instance before this run.' }
$taskBackup = Join-Path $taskOutput 'original-package'
$taskHadPackage = Test-Path -LiteralPath $taskInstalled
# Retain the original whole package so this run cannot leave the real install swapped.
if ($taskHadPackage) { Move-Item -LiteralPath $taskInstalled -Destination $taskBackup }
$taskResults = Join-Path $taskOutput 'results'
New-Item -ItemType Directory -Path $taskResults | Out-Null
$taskFailed = @()
$taskBinaries = Join-Path $taskOutput 'go-bin'
New-Item -ItemType Directory -Path $taskBinaries | Out-Null
Push-Location (Join-Path $taskRepo 'go')
try {
    foreach ($taskCmd in @('pawnaccept', 'researchaccept', 'roomsaccept', 'suppliesaccept')) {
        & $Go build -o (Join-Path $taskBinaries "$taskCmd.exe") "./internal/nativeaccept/cmd/$taskCmd"
        if ($LASTEXITCODE) { throw "go build failed for $taskCmd" }
    }
} finally { Pop-Location }
$taskPackageFor = @{
    pawnaccept     = $taskProdPackage
    researchaccept = $taskFixturePackage
    roomsaccept    = $taskProdPackage
    suppliesaccept = $taskProdPackage
}
try {
    Push-Location $taskRepo
    try {
        foreach ($taskScript in @('pawnaccept', 'researchaccept', 'roomsaccept', 'suppliesaccept')) {
            Write-Host "=== $taskScript ==="
            # Copy (never move) the pristine build output so it can be reused for
            # every binary that shares it, and this binary's installed copy can be
            # discarded independently of the others.
            Copy-Item -LiteralPath $taskPackageFor[$taskScript] -Destination $taskInstalled -Recurse
            try {
                & (Join-Path $taskBinaries "$taskScript.exe") -root ".rimgovernor/bridge" -output (Join-Path $taskResults $taskScript)
                if ($LASTEXITCODE) { $taskFailed += $taskScript }
            } finally {
                if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') {
                    throw "Game still running after $taskScript; close it before continuing."
                }
                if (Test-Path -LiteralPath $taskInstalled) {
                    Move-Item -LiteralPath $taskInstalled -Destination (Join-Path $taskOutput "tested-package-$taskScript")
                }
            }
        }
    }
    finally { Pop-Location }
} finally {
    if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') {
        throw "Game still running; close it before restoring the retained package at $taskBackup"
    }
    if ($taskHadPackage) { Move-Item -LiteralPath $taskBackup -Destination $taskInstalled }
}
Write-Host "Results in $taskResults"
if ($taskFailed.Count) { throw "Failed: $($taskFailed -join ', ')" }
