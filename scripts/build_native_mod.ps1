[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RimWorldManagedDir,
    [Parameter(Mandatory = $true)][string]$HarmonyAssembly,
    [string]$DotNet = 'dotnet',
    [string]$OutputRoot = '',
    [ValidateSet('HomeCoverageFixture', 'SleepingFixture', 'MedicineFixture', 'AnimalContainmentFixture', 'ResearchObservationFixture', 'RoundsSleepingFixture', 'RoundsProductionFixture', 'GuardedConstructionFixture', 'StorageHaulFixture', 'RefillHysteresisFixture', 'ThroughputFixture', 'WasteFixture', 'BurnableFilterFixture', 'GearFixture',
        'MoodFixture', 'PopulationFixture', 'HusbandryFixture', 'MedicalManagementFixture',
        'DeepResourcesFixture', 'FoodChannelFixture', 'FishingFixture', 'LedgerFixture', 'HuntLedgerFixture', 'HuntChainRuleFixture', 'UpkeepFixture', 'TradeFixture',
        'EmergencyDevelopmentFixture',
        'ForecastFixture', 'DraftFaultFixture', 'RuntimeFaultFixture', 'MapScopeFixture', 'BuildingTemperatureFixture', 'CaravanDepartureFixture', 'RecoveryServiceFixture', 'QuestAcceptFixture', 'RecoveryAreaFixture', 'BedAssignFixture', 'ZoneDeleteFixture', 'ApplyRefusalFixture', 'RefrigerationFixture', 'PowerFixture', 'CleanlinessFixture', 'DefenseFixture', 'LightingFixture', 'FlooringFixture', 'RoutesFixture', 'HutShellFixture', 'QuietStorytellerFixture', 'DebugStartFixture', 'LetterFixture', 'FreezeNeedsFixture', 'WallUpgradeFixture', 'ShutdownFixture', 'FarmEnvironmentFixture', 'DialogFixture', 'ProductionLadderFixture', 'BlightFixture', 'PlantationFixture', 'LatticeSowFixture', 'NonFoodFieldFixture','WinterFixture', 'ShrineFixture', 'SubdueFixture', 'ArrestFixture', 'LayoutGridFixture', 'HazardFixture', 'StartupLaborFixture', 'BuriedSteelFixture', 'ArtFixture', 'ShelterFixture', 'ColonyReviewFixture', 'BabyCareFixture', 'MechGestationFixture', 'FirstRitualFixture', 'HorrorIncidentFixture', 'EntityHoldFixture', 'PlanStageFixture', 'MonolithAwakenFixture')]
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
$RimWorldManagedDir = (Resolve-Path -LiteralPath $RimWorldManagedDir).Path
$HarmonyAssembly = (Resolve-Path -LiteralPath $HarmonyAssembly).Path
foreach ($taskRequired in @($taskProject, (Join-Path $RimWorldManagedDir 'Assembly-CSharp.dll'),
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
    $taskFrom = Join-Path $taskSource $taskDirectory
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFrom -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin|Assemblies|BridgeTools)[\\/]'
    }) {
        $taskRelative = $taskFile.FullName.Substring($taskSource.Length + 1)
        $taskTo = Join-Path $taskCopyNative $taskRelative
        New-Item -ItemType Directory -Path (Split-Path $taskTo -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTo
    }
}
# The vendored GABP host (fork of RimBridgeServer and Lib.GAB) builds from the same private copy.
$taskCopyHost = Join-Path $taskCopyRoot 'integrations/rimgovernor-host'
foreach ($taskFile in @(Get-ChildItem -LiteralPath (Join-Path $taskHostSource 'src') -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
    }) + @(Get-Item -LiteralPath (Join-Path $taskHostSource 'Directory.Build.props'))) {
    $taskTo = Join-Path $taskCopyHost $taskFile.FullName.Substring($taskHostSource.Length + 1)
    New-Item -ItemType Directory -Path (Split-Path $taskTo -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTo
}
# Canonical Protobuf sources and official generator inputs travel with the build.
# No experimental JSON generator or second contract tree participates.
foreach ($taskContractDirectory in @('contracts/proto', 'contracts/generated/protobuf/csharp', 'tools/protobuf')) {
    $taskFrom = Join-Path $taskRepo $taskContractDirectory
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFrom -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Extension -notin @('.dll', '.pdb', '.exe') -and
        $_.FullName -notmatch '[\\/]csharp[\\/]Defs\.cs$'
    }) {
        $taskRelative = $taskFile.FullName.Substring($taskRepo.Length + 1)
        $taskTo = Join-Path $taskCopyRoot $taskRelative
        New-Item -ItemType Directory -Path (Split-Path $taskTo -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTo
    }
}
$taskScripts = Join-Path $taskCopyRoot 'scripts'
New-Item -ItemType Directory -Path $taskScripts -Force | Out-Null
Copy-Item -LiteralPath $PSCommandPath -Destination $taskScripts
# The C# generator is a self-contained stdlib Go program: `go run scripts/generate_protobuf.go`.
Copy-Item -LiteralPath (Join-Path $taskRepo 'go/internal/protobufgen/cmd/generatecsharp/main.go') -Destination (Join-Path $taskScripts 'generate_protobuf.go')
$taskFixtureSource = Join-Path $taskRepo 'scripts/fixtures'
foreach ($taskFile in Get-ChildItem -LiteralPath $taskFixtureSource -Recurse -File | Where-Object {
    $_.Extension -in @('.cs', '.csproj') -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
}) {
    $taskDestination = Join-Path $taskScripts ('fixtures/' + $taskFile.FullName.Substring($taskFixtureSource.Length + 1))
    New-Item -ItemType Directory -Path (Split-Path $taskDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
Copy-Item -LiteralPath (Join-Path $taskRepo 'THIRD_PARTY.md') -Destination $taskCopyRoot
Copy-Item -LiteralPath (Join-Path $taskSource 'README.md') -Destination $taskCopyNative
# sourceTree hashes the copied inputs (before the build writes obj/bin into
# them) so an acceptance harness can tell, without git, whether the installed
# package matches its worktree (nativeaccept.SourceTreeHash reproduces this
# list from the copy rules above): sorted ordinal by copy-relative path with
# forward slashes, one "<path>`t<sha256>`n" line each, SHA-256 of the lines.
$taskSourceLines = [System.Collections.Generic.List[string]]::new()
foreach ($taskFile in Get-ChildItem -LiteralPath $taskCopyRoot -Recurse -File) {
    $taskRelative = $taskFile.FullName.Substring($taskCopyRoot.Length + 1).Replace('\', '/')
    $taskSourceLines.Add($taskRelative + "`t" + (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + "`n")
}
$taskSourceLines.Sort([System.StringComparer]::Ordinal)
$taskSourceHasher = [System.Security.Cryptography.SHA256]::Create()
$taskSourceTree = [BitConverter]::ToString($taskSourceHasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(-join $taskSourceLines))).Replace('-', '').ToLowerInvariant()
$taskSourceHasher.Dispose()
# Defs.cs (49 MB) is not committed: generate it into the private copy with the
# bundled generator (pinned Grpc.Tools protoc) after the source hash is taken.
Push-Location $taskCopyRoot
try {
    & go run (Join-Path $taskScripts 'generate_protobuf.go') -root $taskCopyRoot -dotnet $taskCompiler -output (Join-Path $taskBuild 'protobuf') *> (Join-Path $taskBuild 'generate.log')
    if ($LASTEXITCODE) { throw "Defs.cs generation failed; inspect $taskBuild/generate.log" }
} finally { Pop-Location }
$taskHostCompiled = Join-Path $taskBuild 'host-compiled'
$taskHostArgs = @('build', (Join-Path $taskCopyHost 'src/Host/RimGovernor.Host.csproj'),
    '-c', 'Release', '-v', 'minimal', '-p:RestoreLockedMode=true', "-p:OutputPath=$taskHostCompiled/",
    "-p:RimWorldManagedDir=$RimWorldManagedDir", "-p:HarmonyAssembly=$HarmonyAssembly")
& $taskCompiler @taskHostArgs *> (Join-Path $taskBuild 'host-build.log')
if ($LASTEXITCODE) { throw "Host build failed; inspect $taskBuild/host-build.log" }
$taskArgs = @('build', (Join-Path $taskCopyNative 'src/Bridge/RimGovernor.Bridge.csproj'),
    '-c', 'Release', '-v', 'minimal', '-p:RestoreLockedMode=true', "-p:OutputPath=$taskCompiled/",
    "-p:RimWorldManagedDir=$RimWorldManagedDir", "-p:HarmonyAssembly=$HarmonyAssembly")
foreach ($taskFlag in $taskFixtures) { $taskArgs += "-p:${taskFlag}=true" }
& $taskCompiler @taskArgs *> (Join-Path $taskBuild 'build.log')
if ($LASTEXITCODE) { throw "Native build failed; inspect $taskBuild/build.log" }
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
    $taskNotice = Join-Path $taskCopyNative ('Notices/protobuf/' + $taskFields[0].ToLowerInvariant() + '/' + $taskFields[1])
    if (-not (Test-Path -LiteralPath (Join-Path $taskNotice 'LICENSE'))) { throw "Missing runtime dependency notice: $taskLine" }
    $taskDll = Join-Path $taskCompiled $taskFields[2]
    Copy-Item -LiteralPath $taskDll -Destination (Join-Path $taskPackage 'BridgeTools/RimGovernor')
    $taskRuntimeDependencies += [ordered]@{ package = $taskFields[0]; version = $taskFields[1]; file = $taskFields[2] }
}
Copy-Item -LiteralPath (Join-Path $taskCopyNative 'About'), (Join-Path $taskCopyNative 'Defs'), (Join-Path $taskCopyNative 'Textures'), (Join-Path $taskCopyNative 'Notices') -Destination $taskPackage -Recurse
Copy-Item -LiteralPath (Join-Path $taskCopyNative 'README.md') -Destination $taskPackage
$taskSnapshot = Join-Path $taskPackage 'Source'
New-Item -ItemType Directory -Path $taskSnapshot -Force | Out-Null
foreach ($taskFile in Get-ChildItem -LiteralPath $taskCopyRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Extension -notin @('.dll', '.pdb', '.exe') -and
    $_.FullName -notmatch '[\\/]csharp[\\/]Defs\.cs$'
}) {
    $taskRelative = $taskFile.FullName.Substring($taskCopyRoot.Length + 1)
    $taskDestination = Join-Path $taskSnapshot $taskRelative
    New-Item -ItemType Directory -Path (Split-Path $taskDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
$taskInputs = @($HarmonyAssembly) + @(Get-ChildItem -LiteralPath $RimWorldManagedDir -Filter '*.dll' -File | ForEach-Object FullName)
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
    files = @(Get-ChildItem -LiteralPath $taskPackage -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($taskPackage.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$taskManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $taskPackage 'native-manifest.json') -Encoding UTF8
Write-Output $taskPackage
