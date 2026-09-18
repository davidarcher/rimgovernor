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
# research/reads exercises test/research_observation_fingerprint, a private fixture
# tool (scripts/fixtures/ResearchObservationFixture.cs) that build_native_mod.ps1 only
# compiles in when -Fixture names it. rooms/reads and supplies/reads assert the installed
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
# The four cases run through the shared `acceptance` runner (#135); one build.
$taskRunner = Join-Path $taskBinaries 'acceptance.exe'
Push-Location (Join-Path $taskRepo 'go')
try {
    & $Go build -o $taskRunner ./internal/nativeaccept/cmd/acceptance
    if ($LASTEXITCODE) { throw 'go build failed for acceptance' }
} finally { Pop-Location }
$taskCases = @('pawn/reads', 'research/reads', 'rooms/reads', 'supplies/reads')
$taskPackageFor = @{
    'pawn/reads'     = $taskProdPackage
    'research/reads' = $taskFixturePackage
    'rooms/reads'    = $taskProdPackage
    'supplies/reads' = $taskProdPackage
}
try {
    Push-Location $taskRepo
    try {
        foreach ($taskScript in $taskCases) {
            Write-Host "=== $taskScript ==="
            $taskLabel = $taskScript.Replace('/', '-')
            # Copy (never move) the pristine build output so it can be reused for
            # every case that shares it, and this case's installed copy can be
            # discarded independently of the others.
            Copy-Item -LiteralPath $taskPackageFor[$taskScript] -Destination $taskInstalled -Recurse
            try {
                & $taskRunner run $taskScript -root (Join-Path $taskRepo '.rimgovernor/bridge') -output (Join-Path $taskResults $taskLabel)
                if ($LASTEXITCODE) { $taskFailed += $taskScript }
            } finally {
                if (Get-Process | Where-Object ProcessName -Like 'RimWorld*') {
                    throw "Game still running after $taskScript; close it before continuing."
                }
                if (Test-Path -LiteralPath $taskInstalled) {
                    Move-Item -LiteralPath $taskInstalled -Destination (Join-Path $taskOutput "tested-package-$taskLabel")
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
