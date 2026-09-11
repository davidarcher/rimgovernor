[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RimWorldManagedDir,
    [Parameter(Mandatory = $true)][string]$HarmonyAssembly,
    [Parameter(Mandatory = $true)][string]$RimBridgeSdkDir,
    [string]$DotNet = 'dotnet',
    [string]$OutputRoot = '',
    [ValidateSet('ResearchObservationFixture', 'RoutineSleepingFixture', 'RoutineProductionFixture', 'GuardedConstructionFixture', 'ThroughputFixture', 'WasteFixture', 'DisasterFixture', 'GearFixture',
        'MoodFixture', 'PopulationFixture', 'HusbandryFixture', 'MedicalManagementFixture',
        'MiningFixture', 'FoodObservationFixture', 'UpkeepFixture', 'TradeFixture',
        'ScenarioStartFixture', 'ConstructionLedgerFixture', 'EmergencyDevelopmentFixture',
        'InstallFixture', 'ForecastFixture', 'InspectorFixture', 'ModalFixture', 'CampaignMetricsFixture')]
    [string[]]$Fixture = @()
)
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path $PSScriptRoot -Parent
$taskSource = Join-Path $taskRepo 'integrations/rimgovernor-native'
$taskProject = Join-Path $taskSource 'src/Bridge/RimGovernor.Bridge.csproj'
$taskFixtures = @($Fixture | Sort-Object -Unique)
$taskRole = if ($taskFixtures.Count) { 'fixture' } else { 'production' }
if (-not $OutputRoot) {
    $OutputRoot = Join-Path $taskRepo ('.rimgovernor/native-builds/' + $taskRole + '-' + [guid]::NewGuid().ToString('N'))
}
$taskOutput = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $taskOutput) { throw "Build output must be fresh: $taskOutput" }
$RimWorldManagedDir = (Resolve-Path -LiteralPath $RimWorldManagedDir).Path
$HarmonyAssembly = (Resolve-Path -LiteralPath $HarmonyAssembly).Path
$RimBridgeSdkDir = (Resolve-Path -LiteralPath $RimBridgeSdkDir).Path
foreach ($taskRequired in @($taskProject, (Join-Path $RimWorldManagedDir 'Assembly-CSharp.dll'),
        $HarmonyAssembly, (Join-Path $RimBridgeSdkDir 'RimBridgeServer.Sdk.dll'),
        (Join-Path $RimBridgeSdkDir 'Newtonsoft.Json.dll'),
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
foreach ($taskDirectory in @('src', 'About', 'Notices')) {
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
# Canonical Protobuf sources and official generator inputs travel with the build.
# No experimental JSON generator or second contract tree participates.
foreach ($taskContractDirectory in @('contracts/proto', 'contracts/generated/protobuf/csharp', 'tools/protobuf')) {
    $taskFrom = Join-Path $taskRepo $taskContractDirectory
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskFrom -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Extension -notin @('.dll', '.pdb', '.exe')
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
Copy-Item -LiteralPath (Join-Path $taskRepo 'scripts/generate_protobuf.py') -Destination $taskScripts
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
$taskArgs = @('build', (Join-Path $taskCopyNative 'src/Bridge/RimGovernor.Bridge.csproj'),
    '-c', 'Release', '-v', 'minimal', '-p:RestoreLockedMode=true', "-p:OutputPath=$taskCompiled/",
    "-p:RimWorldManagedDir=$RimWorldManagedDir", "-p:HarmonyAssembly=$HarmonyAssembly",
    "-p:RimBridgeSdkDir=$RimBridgeSdkDir")
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
# Only resolved NuGet runtime DLLs are redistributed, beside their requesting
# Bridge assembly where RimBridgeServer's scoped resolver searches first.
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
Copy-Item -LiteralPath (Join-Path $taskCopyNative 'About'), (Join-Path $taskCopyNative 'Notices') -Destination $taskPackage -Recurse
Copy-Item -LiteralPath (Join-Path $taskCopyNative 'README.md') -Destination $taskPackage
$taskSnapshot = Join-Path $taskPackage 'Source'
New-Item -ItemType Directory -Path $taskSnapshot -Force | Out-Null
foreach ($taskFile in Get-ChildItem -LiteralPath $taskCopyRoot -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Extension -notin @('.dll', '.pdb', '.exe')
}) {
    $taskRelative = $taskFile.FullName.Substring($taskCopyRoot.Length + 1)
    $taskDestination = Join-Path $taskSnapshot $taskRelative
    New-Item -ItemType Directory -Path (Split-Path $taskDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
}
$taskInputs = @($HarmonyAssembly) + @(Get-ChildItem -LiteralPath $RimWorldManagedDir, $RimBridgeSdkDir -Filter '*.dll' -File | ForEach-Object FullName)
$taskManifest = [ordered]@{
    formatVersion = 1
    packageId = 'davidarcher.rimgovernor.native'
    role = $taskRole
    fixtures = $taskFixtures
    runtimeDependencies = $taskRuntimeDependencies
    sourceRevision = (& git -C $taskRepo rev-parse HEAD)
    sourceDirty = [bool](& git -C $taskRepo status --porcelain)
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
