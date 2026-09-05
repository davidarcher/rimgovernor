[CmdletBinding()]
param(
 [ValidateSet('construction','full')][string]$Scenario='construction',
 [string]$GamePath='C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
Set-Location -LiteralPath $repo
if(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) { throw 'Close RimWorld before running the isolated gameplay test.' }
$run=Join-Path $repo 'tmp\native-smoke'
$config=Join-Path $run 'Config'
New-Item -ItemType Directory -Path $config -Force | Out-Null
$profile=Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios\Config'
Copy-Item -LiteralPath (Join-Path $profile 'ModsConfig.xml') -Destination $config -Force
# Only local model settings; never copy API keys into a test profile.
$settings=New-Object System.Xml.XmlDocument
$root=$settings.CreateElement('SettingsBlock'); $null=$settings.AppendChild($root)
$mod=$settings.CreateElement('ModSettings'); $mod.SetAttribute('Class','RimBot.RimBotSettings'); $null=$root.AppendChild($mod)
foreach($pair in @{managerModel='native-smoke-no-model'; managerProvider='Local'; localUrl='http://localhost:1234/v1'; strategicReasoningEffort='none'; localRequestsPerHour='0'}.GetEnumerator()) {
 $node=$settings.CreateElement($pair.Key); $node.InnerText=$pair.Value; $null=$mod.AppendChild($node)
}
$settings.Save((Join-Path $config 'Mod_RimBot_RimBotMod.xml'))
'<PrefsData><runInBackground>True</runInBackground><volumeMaster>0</volumeMaster><fullscreen>False</fullscreen><screenWidth>1280</screenWidth><screenHeight>720</screenHeight></PrefsData>' | Set-Content -LiteralPath (Join-Path $config 'Prefs.xml') -Encoding utf8
$env:DOTNET_CLI_HOME=Join-Path $repo 'tmp\dotnet-home'
& ./tmp/dotnet/dotnet.exe build RimBot/RimBot.csproj -c Release -p:CI=true -p:GameSmoke=true '-p:OutputPath=..\tmp\smoke-build\' "-p:RimWorldManagedPath=$GamePath\RimWorldWin64_Data\Managed" "-p:FrameworkPathOverride=$repo\tmp\net472\build\.NETFramework\v4.7.2" --no-restore
if($LASTEXITCODE) { throw 'Native smoke build failed' }
$installed=Join-Path $GamePath 'Mods\RimBot\1.6\Assemblies\RimBot.dll'
$backup=Join-Path $run 'installed-RimBot.dll.backup'
Copy-Item -LiteralPath $installed -Destination $backup -Force
$hash=(Get-FileHash -LiteralPath $backup).Hash
$result=Join-Path $run 'Player.log'
if(Test-Path -LiteralPath $result) { Move-Item -LiteralPath $result -Destination (Join-Path $run ('result-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.log')) }
$process=$null
try {
 Copy-Item -LiteralPath (Join-Path $repo 'tmp\smoke-build\RimBot.dll') -Destination $installed -Force
 $gameArgs=@('-quicktest','-rimbot-selftest',('-savedatafolder="'+$run+'"'),('-logFile "'+(Join-Path $run 'Player.log')+'"'))
 if($Scenario -eq 'construction') { $gameArgs+='-rimbot-construction-smoke' }
 # Full mode requires native OnGUI initialization; hidden environments may not render it.
 $process=Start-Process -FilePath (Join-Path $GamePath 'RimWorldWin64.exe') -WorkingDirectory $GamePath -WindowStyle Hidden -PassThru -ArgumentList $gameArgs
 $process.Id | Set-Content (Join-Path $run 'process-id.txt')
 $deadline=[DateTime]::UtcNow.AddMinutes(8)
 while(!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 500; $process.Refresh() }
 if(!$process.HasExited) { $process.Kill(); $process.WaitForExit(); throw 'Native smoke process timed out (8 minutes).' }
 if(!(Test-Path -LiteralPath $result)) { throw 'Game exited without a native smoke log.' }
 Select-String -LiteralPath $result -Pattern '\[RimBot Smoke\]' | ForEach-Object { $_.Line }
} finally {
 if($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
 # Windows can briefly retain the assembly mapping after process exit.
 for($attempt=0;$attempt -lt 20;$attempt++) {
  try { Copy-Item -LiteralPath $backup -Destination $installed -Force; break }
  catch { if($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 250 }
 }
 if((Get-FileHash -LiteralPath $installed).Hash -ne $hash) { throw 'Installed mod restoration failed.' }
 Write-Host 'Original installed mod restored and verified.'
}
$log=Get-Content -LiteralPath $result -Raw
if($log.Contains('[RimBot Smoke] FAIL:')) { throw 'Native smoke failed; inspect tmp/native-smoke/Player.log.' }
$passMarker=if($Scenario -eq 'construction') { '[RimBot Smoke] PASS: construction-only lifecycle checks complete.' } else { '[RimBot Smoke] PASS: five locally measured objectives, zero model calls in Manual mode, Automate enabled reviews and Manual disabled them.' }
if(!$log.Contains($passMarker)) { throw 'Native smoke did not reach its final PASS.' }
Write-Host 'Native smoke passed.'
