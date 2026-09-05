[CmdletBinding()]
param(
 [ValidateSet('none','medium')][string]$Reasoning='none',
 [string]$Model='qwen/qwen3.5-9b',
 [string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
Set-Location -LiteralPath $repo
if(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before running the isolated gameplay test.' }
$run=Join-Path $repo 'tmp\bed-benchmark'
$config=Join-Path $run 'Config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
$profile=Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config'
Copy-Item -LiteralPath (Join-Path $profile 'ModsConfig.xml') -Destination $config -Force
# Only local model settings; never copy API keys into a test profile.
$settings=New-Object System.Xml.XmlDocument
$root=$settings.CreateElement('SettingsBlock'); $null=$settings.AppendChild($root)
$mod=$settings.CreateElement('ModSettings'); $mod.SetAttribute('Class','RimBot.RimBotSettings'); $null=$root.AppendChild($mod)
foreach($pair in @{managerModel=$Model; managerProvider='Local'; localUrl='http://localhost:1234/v1'; localReasoningEffort=$Reasoning; localRequestsPerHour='0'}.GetEnumerator()) {
 $node=$settings.CreateElement($pair.Key); $node.InnerText=$pair.Value; $null=$mod.AppendChild($node)
}
$settings.Save((Join-Path $config 'Mod_RimBot_RimBotMod.xml'))
'<PrefsData><runInBackground>True</runInBackground><volumeMaster>0</volumeMaster><fullscreen>False</fullscreen><screenWidth>1280</screenWidth><screenHeight>720</screenHeight></PrefsData>' | Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
$marker=Join-Path $run 'benchmark-started.txt'
if(Test-Path -LiteralPath $marker) { Remove-Item -LiteralPath $marker }
$env:DOTNET_CLI_HOME=Join-Path $repo 'tmp\dotnet-home'
& ./tmp/dotnet/dotnet.exe build RimBot/RimBot.csproj -c Release -p:CI=true -p:GameSmoke=true '-p:OutputPath=..\tmp\smoke-build\' "-p:RimWorldManagedPath=$GamePath\RimWorldWin64_Data\Managed" "-p:FrameworkPathOverride=$repo\tmp\net472\build\.NETFramework\v4.7.2" --no-restore
if($LASTEXITCODE) { throw 'Benchmark build failed' }
$installed=Join-Path $GamePath 'Mods\RimBot\1.6\Assemblies\RimBot.dll'
$backup=Join-Path $run 'installed-RimBot.dll.backup'
Copy-Item -LiteralPath $installed -Destination $backup -Force
$hash=(Get-FileHash -LiteralPath $backup).Hash
$result=Join-Path $run 'benchmark-result.json'
if(Test-Path -LiteralPath $result) { Move-Item -LiteralPath $result -Destination (Join-Path $run ('result-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')) }
$process=$null
try {
 Copy-Item -LiteralPath (Join-Path $repo 'tmp\smoke-build\RimBot.dll') -Destination $installed -Force
 $process=Start-Process -FilePath (Join-Path $GamePath 'RimWorldWin64.exe') -WorkingDirectory $GamePath -WindowStyle Hidden -PassThru -ArgumentList @('-quicktest','-rimbot-benchmark',"-rimbot-reasoning=$Reasoning",('-savedatafolder="'+$run+'"'),('-logFile "'+(Join-Path $run 'Player.log')+'"'))
 $process.Id | Set-Content (Join-Path $run 'process-id.txt')
 if(!$process.WaitForExit(840000)) { $process.Kill(); $process.WaitForExit(); throw 'Benchmark process timed out (14 minutes).' }
 if(!(Test-Path -LiteralPath $result)) { throw 'Game exited without a benchmark result; inspect tmp/bed-benchmark/Player.log.' }
 Get-Content -LiteralPath $result
} finally {
 if($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
 Copy-Item -LiteralPath $backup -Destination $installed -Force
 if((Get-FileHash -LiteralPath $installed).Hash -ne $hash) { throw 'Installed mod restoration failed.' }
 Write-Host 'Original installed mod restored and verified.'
}
if(!(Get-Content -LiteralPath $result | ConvertFrom-Json).passed) { throw 'Gameplay regression failed.' }
