[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RimWorldDir,
    [Parameter(Mandatory = $true)][string]$HarmonyAssembly,
    [string]$DotNet = 'dotnet',
    [string]$OutputRoot = '',
    [ValidateSet('HomeCoverageFixture', 'SleepingFixture', 'MedicineFixture', 'AnimalContainmentFixture', 'ResearchObservationFixture', 'RoundsSleepingFixture', 'RoundsProductionFixture', 'GuardedConstructionFixture', 'StorageHaulFixture', 'RefillHysteresisFixture', 'ThroughputFixture', 'WasteFixture', 'BurnableFilterFixture', 'GearFixture',
        'MoodFixture', 'PopulationFixture', 'HusbandryFixture', 'MedicalManagementFixture',
        'DeepResourcesFixture', 'FoodChannelFixture', 'FishingFixture', 'LedgerFixture', 'HuntLedgerFixture', 'UpkeepFixture', 'TradeFixture',
        'EmergencyDevelopmentFixture',
        'PawnControlFixture', 'PawnActivityFixture', 'ForecastFixture', 'DraftFaultFixture', 'RuntimeFaultFixture', 'MapScopeFixture', 'BuildingTemperatureFixture', 'CaravanDepartureFixture', 'RecoveryServiceFixture', 'QuestAcceptFixture', 'RecoveryAreaFixture', 'BedAssignFixture', 'ZoneDeleteFixture', 'ApplyRefusalFixture', 'RefrigerationFixture', 'PowerFixture', 'DefenseFixture', 'LightingFixture', 'FlooringFixture', 'RoutesFixture', 'HutShellFixture', 'QuietStorytellerFixture', 'DebugStartFixture', 'LetterFixture', 'FreezeNeedsFixture', 'WallUpgradeFixture', 'ShutdownFixture', 'FarmEnvironmentFixture', 'DialogFixture', 'ProductionLadderFixture', 'BlightFixture', 'PlantationFixture', 'LatticeSowFixture', 'NonFoodFieldFixture','WinterFixture', 'ShrineFixture', 'SubdueFixture', 'TrainingFixture','SparringFixture', 'SparringWatchFixture','ArrestFixture', 'LayoutGridFixture', 'HazardFixture', 'StartupLaborFixture', 'BuriedSteelFixture', 'ArtFixture', 'ShelterFixture', 'ColonyReviewFixture', 'BabyCareFixture', 'MechGestationFixture', 'FirstRitualFixture', 'HorrorIncidentFixture', 'EntityHoldFixture', 'PlanStageFixture', 'MonolithAwakenFixture')]
    [string[]]$Fixture = @()
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskSource = Join-Path $taskRepo 'integrations/rimgovernor-native'
$taskProject = Join-Path $taskSource 'src/Bridge/RimGovernor.Bridge.csproj'
$taskHostSource = Join-Path $taskRepo 'integrations/rimgovernor-host'
$taskHostProject = Join-Path $taskHostSource 'src/Host/RimGovernor.Host.csproj'
$taskFixtures = @($Fixture | Sort-Object -Unique)
# Every fixture build carries test/quiet_storyteller: the acceptance harnesses
# quiet the debug colony through it by default .
if ($taskFixtures.Count) { $taskFixtures = @(($taskFixtures + 'QuietStorytellerFixture' + 'DebugStartFixture' + 'LetterFixture' + 'FreezeNeedsFixture' + 'ShutdownFixture') | Sort-Object -Unique) }
$taskRole = if ($taskFixtures.Count) { 'fixture' } else { 'production' }
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $taskRepo ('.rimgovernor/native-builds/' + $taskRole + '-' + [guid]::NewGuid().ToString('N'))
}
$taskOutput = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $taskOutput) { throw "Build output must be fresh: $taskOutput" }
$RimWorldDir = (Resolve-Path -LiteralPath $RimWorldDir).Path
$HarmonyAssembly = (Resolve-Path -LiteralPath $HarmonyAssembly).Path
foreach ($taskRequired in @($taskProject, (Join-Path $RimWorldDir 'Assembly-CSharp.dll'),
        $HarmonyAssembly, $taskHostProject,
        (Join-Path $taskSource 'Notices/host/PROVENANCE.md'),
        (Join-Path $taskSource 'Notices/headless/LICENSE'),
        (Join-Path $taskSource 'Notices/headless/PROVENANCE.md'),
        (Join-Path $taskSource 'Notices/companion/PROVENANCE.md'))) {
    if (-not (Test-Path -LiteralPath $taskRequired -PathType Leaf)) { throw "Required native build input missing: $taskRequired" }
}
$taskCompiler = (Get-Command $DotNet -ErrorAction Stop).Source
$taskSdkVersion = (& $taskCompiler --version | Out-String).Trim()
if ($LASTEXITCODE) { throw 'Could not read .NET SDK version' }
if ([version]$taskSdkVersion.Split('-')[0] -lt [version]'10.0') { throw "The native build needs .NET SDK 10 (net10.0); found $taskSdkVersion" }
# Copies a directory tree with robocopy (many small files: Copy-Item per file is several times slower), keeping
# timestamps, skipping build output directories and, when given, copying only the matching file patterns.
function Copy-TaskTree([string]$From, [string]$To, [string[]]$Patterns = @(), [string[]]$ExcludeDirs = @('obj', 'bin'), [string[]]$ExcludeFiles = @()) {
    $taskCopyArgs = @($From, $To) + $Patterns + @('/S', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/MT:16')
    if ($ExcludeDirs.Count) { $taskCopyArgs += '/XD'; $taskCopyArgs += $ExcludeDirs }
    if ($ExcludeFiles.Count) { $taskCopyArgs += '/XF'; $taskCopyArgs += $ExcludeFiles }
    & robocopy @taskCopyArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $From" }
    $global:LASTEXITCODE = 0
}
# SHA-256 of each file as lowercase hex, hashed on several threads; returns @{ path = hash }.
function Get-TaskHashes([string[]]$Paths) {
    $taskHashes = @{}
    if ($Paths.Count -eq 0) { return $taskHashes }
    $taskChunkSize = [Math]::Max(1, [int][Math]::Ceiling($Paths.Count / 8.0))
    $taskChunks = for ($taskAt = 0; $taskAt -lt $Paths.Count; $taskAt += $taskChunkSize) { , @($Paths[$taskAt..([Math]::Min($taskAt + $taskChunkSize, $Paths.Count) - 1)]) }
    $taskHashOne = { param($taskChunk) foreach ($taskPath in $taskChunk) { [pscustomobject]@{ Path = $taskPath; Hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($taskPath))).ToLowerInvariant() } } }
    $taskRows = if ($PSVersionTable.PSVersion.Major -ge 7) {
        $taskChunks | ForEach-Object -Parallel { foreach ($taskPath in $_) { [pscustomobject]@{ Path = $taskPath; Hash = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.IO.File]::ReadAllBytes($taskPath))).ToLowerInvariant() } } } -ThrottleLimit 8
    } else { foreach ($taskChunk in $taskChunks) { & $taskHashOne $taskChunk } }
    foreach ($taskRow in $taskRows) { $taskHashes[$taskRow.Path] = $taskRow.Hash }
    return $taskHashes
}
$taskBuild = Join-Path $taskOutput 'build'
$taskPackage = Join-Path $taskOutput 'RimGovernor'
$taskCompiled = Join-Path $taskBuild 'compiled'
New-Item -ItemType Directory -Path $taskBuild, $taskPackage -Force | Out-Null
# Build a private source copy: concurrent fixture and production builds must not
# share obj/restore state or write binaries under the source checkout.
$taskCopyRoot = Join-Path $taskBuild 'source'
$taskCopyNative = Join-Path $taskCopyRoot 'integrations/rimgovernor-native'
New-Item -ItemType Directory -Path $taskCopyNative -Force | Out-Null
foreach ($taskDirectory in @('src', 'About', 'Defs', 'Textures', 'Notices')) {
    Copy-TaskTree (Join-Path $taskSource $taskDirectory) (Join-Path $taskCopyNative $taskDirectory) -ExcludeDirs @('obj', 'bin', 'Assemblies', 'BridgeTools')
}
# The vendored host (fork of RimBridgeServer) builds from the same private copy.
$taskCopyHost = Join-Path $taskCopyRoot 'integrations/rimgovernor-host'
Copy-TaskTree (Join-Path $taskHostSource 'src') (Join-Path $taskCopyHost 'src')
New-Item -ItemType Directory -Path $taskCopyHost -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $taskHostSource 'Directory.Build.props') -Destination $taskCopyHost
# The brain assemblies the mod shares with the in-process brain (#3080): Domain, Policy, Contracts (the wire records, #3100), Bridge (the definition catalog over the game's Defs, #3116) with Bridge.Contracts,
# NativeFacade and HttpChannel (the local control channel, #3083) with the Telemetry
# it reports access rows through, and the host (#3115) with its graph: Snapshot, Observation, Store (native SQLite),
# Executor, BuildingRuntime, PlayerApi and Simulation, with the shared build props and package versions. Policy embeds its
# thought trigger table (brain/src/RimGovernor.Brain.Policy/thought_triggers.tsv).
foreach ($taskBrainItem in @('brain/Directory.Build.props', 'brain/Directory.Packages.props', 'brain/Game.props', 'brain/src/RimGovernor.Brain.Domain', 'brain/src/RimGovernor.Brain.Policy', 'brain/src/RimGovernor.Brain.Contracts', 'brain/src/RimGovernor.Brain.Bridge', 'brain/src/RimGovernor.Brain.Bridge.Contracts', 'brain/src/RimGovernor.Brain.NativeFacade', 'brain/src/RimGovernor.Brain.HttpChannel', 'brain/src/RimGovernor.Brain.Telemetry', 'brain/src/RimGovernor.Brain.Host', 'brain/src/RimGovernor.Brain.Snapshot', 'brain/src/RimGovernor.Brain.Observation', 'brain/src/RimGovernor.Brain.Store', 'brain/src/RimGovernor.Brain.Executor', 'brain/src/RimGovernor.Brain.BuildingRuntime', 'brain/src/RimGovernor.Brain.PlayerApi', 'brain/src/RimGovernor.Brain.Simulation')) {
    $taskBrainFrom = Get-Item -LiteralPath (Join-Path $taskRepo $taskBrainItem)
    $taskBrainTo = Join-Path $taskCopyRoot $taskBrainItem
    if ($taskBrainFrom.PSIsContainer) { Copy-TaskTree $taskBrainFrom.FullName $taskBrainTo }
    else {
        New-Item -ItemType Directory -Path (Split-Path $taskBrainTo -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $taskBrainFrom.FullName -Destination $taskBrainTo
    }
}
$taskScripts = Join-Path $taskCopyRoot 'scripts'
New-Item -ItemType Directory -Path $taskScripts -Force | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination $taskScripts
Copy-TaskTree (Join-Path $taskRepo 'scripts/fixtures') (Join-Path $taskScripts 'fixtures') -Patterns @('*.cs', '*.csproj')
Copy-Item -LiteralPath (Join-Path $taskRepo 'THIRD_PARTY.md') -Destination $taskCopyRoot
Copy-Item -LiteralPath (Join-Path $taskSource 'README.md') -Destination $taskCopyNative
# sourceTree hashes the copied inputs (before the build writes obj/bin into
# them) so an acceptance harness can tell, without git, whether the installed
# package matches its worktree (the acceptance harness's SourceTreeHash reproduces this
# list from the copy rules above): sorted ordinal by copy-relative path with
# forward slashes, one "<path>`t<sha256>`n" line each, SHA-256 of the lines.
$taskSourceLines = [System.Collections.Generic.List[string]]::new()
$taskCopied = @(Get-ChildItem -LiteralPath $taskCopyRoot -Recurse -File)
$taskCopiedHashes = Get-TaskHashes @($taskCopied.FullName)
foreach ($taskFile in $taskCopied) {
    $taskRelative = $taskFile.FullName.Substring($taskCopyRoot.Length + 1).Replace('\', '/')
    $taskSourceLines.Add($taskRelative + "`t" + $taskCopiedHashes[$taskFile.FullName] + "`n")
}
$taskSourceLines.Sort([System.StringComparer]::Ordinal)
$taskSourceHasher = [System.Security.Cryptography.SHA256]::Create()
$taskSourceTree = [BitConverter]::ToString($taskSourceHasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(-join $taskSourceLines))).Replace('-', '').ToLowerInvariant()
$taskSourceHasher.Dispose()
# Compile in a work directory that persists between builds, so a rebuild recompiles only what changed (a fresh copy
# every time made every build a cold compile of the whole brain). It is shared by every worktree of this repository
# (under the main checkout's .rimgovernor): MSBuild's incremental state holds absolute paths, so only the same
# directory lets a new worktree start from the cache an earlier build left, and recompile just what differs. One work
# directory per fixture set: the fixture flags are compile-time properties that MSBuild's up-to-date check does not
# see. The copy above stays pristine, so the sourceTree hash and the source snapshot never see build output; the work
# copy is synced from it, and a file the sync rewrote is touched so its timestamp is newer than the outputs built
# from the previous one.
$taskCommonGit = (& git -C $taskRepo rev-parse --path-format=absolute --git-common-dir | Out-String).Trim()
$taskMainRepo = if ($LASTEXITCODE -eq 0 -and $taskCommonGit) { Split-Path ([IO.Path]::GetFullPath($taskCommonGit)) -Parent } else { $taskRepo }
$global:LASTEXITCODE = 0
$taskFixtureKey = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes(($taskFixtures -join ',')))).Substring(0, 8).ToLowerInvariant()
$taskWork = Join-Path $taskMainRepo (".rimgovernor/native-builds/_work/$taskRole-$taskFixtureKey")
# The compiled output goes to a stable path in the work directory too: OutputPath feeds the compiler's up-to-date
# inputs, so a per-build path would recompile every project each time.
$taskWorkCompiled = Join-Path $taskWork '_out/compiled'
$taskWorkHostCompiled = Join-Path $taskWork '_out/host-compiled'
$taskHostCompiled = Join-Path $taskBuild 'host-compiled'
$taskLockName = 'Global\rimgovernor-native-build-' + [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($taskWork.ToLowerInvariant()))).Substring(0, 24)
$taskLock = [System.Threading.Mutex]::new($false, $taskLockName)
try {
    try { $null = $taskLock.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
    New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
    (Get-Item -LiteralPath $taskWork).LastWriteTimeUtc = [DateTime]::UtcNow
    $taskSynced = & robocopy $taskCopyRoot $taskWork /MIR /FP /NDL /NJH /NJS /NP /NS /MT:16 /XD obj bin _out
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): sync to $taskWork" }
    $global:LASTEXITCODE = 0
    $taskNow = [DateTime]::UtcNow
    foreach ($taskLine in $taskSynced) {
        # Only a copied file is listed with one of these classes; unchanged files are listed as 'same'.
        if ($taskLine -notmatch '^\s*(New File|Newer|Older|Changed|Modified)') { continue }
        $taskTouched = $taskLine.Substring($taskLine.LastIndexOf("`t") + 1).Trim()
        if ($taskTouched.StartsWith($taskCopyRoot, [StringComparison]::OrdinalIgnoreCase)) { $taskTouched = $taskWork + $taskTouched.Substring($taskCopyRoot.Length) }
        if ($taskTouched.StartsWith($taskWork, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $taskTouched -PathType Leaf)) {
            [System.IO.File]::SetLastWriteTimeUtc($taskTouched, $taskNow)
        }
    }
    $taskHostArgs = @('build', (Join-Path $taskWork 'integrations/rimgovernor-host/src/Host/RimGovernor.Host.csproj'),
        '-c', 'Release', '-v', 'minimal', '-p:RestoreLockedMode=true', "-p:OutputPath=$taskWorkHostCompiled/",
        "-p:RimWorldDir=$RimWorldDir", "-p:HarmonyAssembly=$HarmonyAssembly")
    & $taskCompiler @taskHostArgs *> (Join-Path $taskBuild 'host-build.log')
    if ($LASTEXITCODE) { throw "Host build failed; inspect $taskBuild/host-build.log" }
    $taskArgs = @('build', (Join-Path $taskWork 'integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj'),
        '-c', 'Release', '-v', 'minimal', '-p:RestoreLockedMode=true', "-p:OutputPath=$taskWorkCompiled/",
        "-p:RimWorldDir=$RimWorldDir", "-p:HarmonyAssembly=$HarmonyAssembly")
    foreach ($taskFlag in $taskFixtures) { $taskArgs += "-p:${taskFlag}=true" }
    & $taskCompiler @taskArgs *> (Join-Path $taskBuild 'build.log')
    if ($LASTEXITCODE) { throw "Native build failed; inspect $taskBuild/build.log" }
    # Another worktree's build may reuse this directory the moment the lock is released: take this build's output first.
    Copy-TaskTree $taskWorkCompiled $taskCompiled -ExcludeDirs @()
    Copy-TaskTree $taskWorkHostCompiled $taskHostCompiled -ExcludeDirs @()
    # Work directories of fixture sets nobody has built for a while would otherwise pile up.
    foreach ($taskStale in Get-ChildItem -LiteralPath (Split-Path $taskWork -Parent) -Directory | Where-Object { $_.FullName -ne $taskWork -and $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-7) }) {
        Remove-Item -LiteralPath $taskStale.FullName -Recurse -Force -ErrorAction Continue
    }
} finally {
    $taskLock.ReleaseMutex()
    $taskLock.Dispose()
}
foreach ($taskItem in @(
    @{ Name = 'RimGovernor.Runtime.dll'; Directory = 'Assemblies' },
    @{ Name = 'RimGovernor.Bridge.dll'; Directory = 'BridgeTools/RimGovernor' }
)) {
    $taskDll = Join-Path $taskCompiled $taskItem.Name
    if (-not (Test-Path -LiteralPath $taskDll -PathType Leaf)) { throw "Expected native output missing: $taskDll" }
    $taskDestination = Join-Path $taskPackage $taskItem.Directory
    New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
    Copy-Item -LiteralPath $taskDll -Destination $taskDestination
}
# The host ships in the mod's general Assemblies/ (the game loads it as a mod assembly): its own
# assemblies plus the resolved NuGet runtime DLLs, each with a retained notice.
$taskHostAssemblies = Join-Path $taskPackage 'Assemblies'
foreach ($taskDll in Get-ChildItem -LiteralPath $taskHostCompiled -Filter 'RimGovernor.Host*.dll' -File) {
    Copy-Item -LiteralPath $taskDll.FullName -Destination $taskHostAssemblies
}
if (-not (Test-Path -LiteralPath (Join-Path $taskHostAssemblies 'RimGovernor.Host.dll') -PathType Leaf)) { throw 'Expected host output missing: RimGovernor.Host.dll' }
$taskHostDependencies = @()
foreach ($taskLine in Get-Content -LiteralPath (Join-Path $taskHostCompiled 'runtime-dependencies.tsv')) {
    $taskFields = $taskLine.Split('|')
    if ($taskFields.Count -ne 3) { throw "Malformed host runtime dependency: $taskLine" }
    if (-not $taskFields[0]) { continue }
    $taskNotice = Join-Path $taskCopyNative ('Notices/host/' + $taskFields[0].ToLowerInvariant() + '/' + $taskFields[1])
    if (-not (Test-Path -LiteralPath (Join-Path $taskNotice 'LICENSE'))) { throw "Missing host dependency notice: $taskLine" }
    Copy-Item -LiteralPath (Join-Path $taskHostCompiled $taskFields[2]) -Destination $taskHostAssemblies
    $taskHostDependencies += [ordered]@{ package = $taskFields[0]; version = $taskFields[1]; file = $taskFields[2] }
}
# Only resolved NuGet runtime DLLs are redistributed, beside their requesting
# Bridge assembly where the host's scoped resolver searches first.
$taskRuntimeDependencies = @()
foreach ($taskLine in Get-Content -LiteralPath (Join-Path $taskCompiled 'runtime-dependencies.tsv')) {
    $taskFields = $taskLine.Split('|')
    if ($taskFields.Count -ne 3 -or -not $taskFields[0]) { throw "Unidentified runtime dependency: $taskLine" }
    $taskNotice = Join-Path $taskCopyNative ('Notices/bridge/' + $taskFields[0].ToLowerInvariant() + '/' + $taskFields[1])
    if (-not (Test-Path -LiteralPath (Join-Path $taskNotice 'LICENSE'))) { throw "Missing runtime dependency notice: $taskLine" }
    # Native SQLite (Brain.Store, #3115) is a RID-specific asset: it builds to runtimes/<rid>/native and is listed once
    # per RID. The game is win-x64, and the runtime probes the requesting assembly's own directory for a native
    # library, not runtimes/, so only the win-x64 binary ships, flat beside the Bridge.
    $taskDll = Join-Path $taskCompiled $taskFields[2]
    if (-not (Test-Path -LiteralPath $taskDll -PathType Leaf)) { $taskDll = Join-Path $taskCompiled ('runtimes/win-x64/native/' + $taskFields[2]) }
    if (-not (Test-Path -LiteralPath $taskDll -PathType Leaf)) { throw "Runtime dependency output missing: $taskLine" }
    if ($taskRuntimeDependencies | Where-Object { $_.package -eq $taskFields[0] -and $_.file -eq $taskFields[2] }) { continue }
    Copy-Item -LiteralPath $taskDll -Destination (Join-Path $taskPackage 'BridgeTools/RimGovernor')
    $taskRuntimeDependencies += [ordered]@{ package = $taskFields[0]; version = $taskFields[1]; file = $taskFields[2] }
}
# The brain assemblies ride beside the Bridge: the host resolves a Bridge reference by simple name from this directory.
foreach ($taskDll in Get-ChildItem -LiteralPath $taskCompiled -Filter 'RimGovernor.Brain.*.dll' -File) {
    Copy-Item -LiteralPath $taskDll.FullName -Destination (Join-Path $taskPackage 'BridgeTools/RimGovernor')
}
foreach ($taskBrain in @('RimGovernor.Brain.Domain.dll', 'RimGovernor.Brain.Policy.dll', 'RimGovernor.Brain.Contracts.dll', 'RimGovernor.Brain.Bridge.dll', 'RimGovernor.Brain.Bridge.Contracts.dll', 'RimGovernor.Brain.NativeFacade.dll', 'RimGovernor.Brain.HttpChannel.dll', 'RimGovernor.Brain.Telemetry.dll', 'RimGovernor.Brain.Host.dll', 'RimGovernor.Brain.Snapshot.dll', 'RimGovernor.Brain.Observation.dll', 'RimGovernor.Brain.Store.dll', 'RimGovernor.Brain.Executor.dll', 'RimGovernor.Brain.BuildingRuntime.dll', 'RimGovernor.Brain.PlayerApi.dll', 'RimGovernor.Brain.Simulation.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskPackage "BridgeTools/RimGovernor/$taskBrain") -PathType Leaf)) { throw "Expected brain output missing: $taskBrain" }
}Copy-Item -LiteralPath (Join-Path $taskCopyNative 'About'), (Join-Path $taskCopyNative 'Defs'), (Join-Path $taskCopyNative 'Textures'), (Join-Path $taskCopyNative 'Notices') -Destination $taskPackage -Recurse
Copy-Item -LiteralPath (Join-Path $taskCopyNative 'README.md') -Destination $taskPackage
$taskSnapshot = Join-Path $taskPackage 'Source'
Copy-TaskTree $taskCopyRoot $taskSnapshot -ExcludeFiles @('*.dll', '*.pdb', '*.exe')
foreach ($taskFile in Get-ChildItem -LiteralPath $taskSnapshot -Recurse -File -Filter 'Defs.cs' | Where-Object { $_.FullName -match '[\\/]csharp[\\/]Defs\.cs$' }) {
    Remove-Item -LiteralPath $taskFile.FullName
}
$taskInputs = @($HarmonyAssembly) + @('Assembly-CSharp.dll', 'UnityStubs.dll', 'Waylith.dll' | ForEach-Object { Join-Path $RimWorldDir $_ })
$taskPackageFiles = @(Get-ChildItem -LiteralPath $taskPackage -Recurse -File | Sort-Object FullName)
$taskPackageHashes = Get-TaskHashes @($taskPackageFiles.FullName)
$taskManifest = [ordered]@{
    formatVersion = 1
    packageId = 'davidarcher.rimgovernor.native'
    role = $taskRole
    fixtures = $taskFixtures
    runtimeDependencies = $taskRuntimeDependencies
    hostDependencies = $taskHostDependencies
    sourceRevision = (& git -C $taskRepo rev-parse HEAD)
    sourceDirty = [bool](& git -C $taskRepo status --porcelain)
    sourceTree = $taskSourceTree
    dotnetSdk = $taskSdkVersion
    inputs = @($taskInputs | Sort-Object -Unique | ForEach-Object {
        [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    files = @($taskPackageFiles | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($taskPackage.Length + 1).Replace('\', '/'); sha256 = $taskPackageHashes[$_.FullName] }
    })
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPackage 'native-manifest.json') -Encoding UTF8
Write-Output $taskPackage
