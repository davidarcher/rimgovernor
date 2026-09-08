[CmdletBinding()]
param([int]$Port=8787,[string]$Model='qwen3.5-9b',[string]$ModelsConfig='', [switch]$FreshGame,[switch]$NoGame,[switch]$NoBrowser,[switch]$Reload)
$ErrorActionPreference='Stop'
Set-Location -LiteralPath $PSScriptRoot
$taskPython=Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
$taskRoot=Join-Path $PSScriptRoot '.rimbot/bridge'
if (!(Test-Path $taskPython) -or !(Test-Path 'controller/rimbot/static/index.html')) { throw 'Run setup.ps1 first.' }
if (!(Test-Path "$taskRoot/config/config.json") -or !(Test-Path "$taskRoot/gabs/gabs-v1.1.1-windows-amd64/gabs.exe")) { throw 'Prepare the native bridge profile and GABS first; see README.md.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose a port between 1024 and 65535.' }
if ($FreshGame -and $NoGame) { throw 'Use either FreshGame or NoGame.' }
$taskUrl="http://127.0.0.1:$Port"
$taskHealth=$null
try { $taskHealth=Invoke-RestMethod "$taskUrl/api/health" -TimeoutSec 2 } catch {}
if ($taskHealth) {
 if ($taskHealth.backend -ne 'rimbridge' -or $taskHealth.source_root -ne $PSScriptRoot) { throw "Port $Port belongs to another or outdated controller." }
 if ($FreshGame) { throw 'Close the existing controller/game before requesting a fresh fixture.' }
 Write-Host 'Reusing the native colony controller.'
} else {
 if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is occupied." }
 $taskGame=Get-Process RimWorldWin64 -ErrorAction SilentlyContinue
 if ($FreshGame -and $taskGame) { throw 'Close RimWorld before starting a fresh fixture.' }
 if ($NoGame -and !$taskGame) { throw 'NoGame requires a running game with RimBridgeServer.' }
 $env:RIMBOT_MODEL=$Model
 if ($ModelsConfig) { $env:RIMBOT_MODELS_CONFIG=(Resolve-Path -LiteralPath $ModelsConfig).Path } else { Remove-Item Env:RIMBOT_MODELS_CONFIG -ErrorAction SilentlyContinue }
 $taskArguments=@('-m','rimbot','--port',"$Port")
 if ($FreshGame -or (!$NoGame -and !$taskGame)) { $taskArguments+='--fresh-game' }
 if ($Reload) { $taskArguments+='--reload' }
 $taskStamp=Get-Date -Format 'yyyyMMdd-HHmmss'
 $taskOut=Join-Path $taskRoot "controller-$taskStamp.out.log"
 $taskErr=Join-Path $taskRoot "controller-$taskStamp.err.log"
 $taskProcess=Start-Process -FilePath $taskPython -ArgumentList $taskArguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $taskOut -RedirectStandardError $taskErr -PassThru
 for ($taskAttempt=0;$taskAttempt -lt 60;$taskAttempt++) {
  if ($taskProcess.HasExited) { throw "Controller exited; check $taskErr" }
  try { $taskHealth=Invoke-RestMethod "$taskUrl/api/health" -TimeoutSec 1;break } catch { Start-Sleep -Milliseconds 250 }
 }
 if (!$taskHealth -or $taskHealth.backend -ne 'rimbridge') { throw "Controller failed to start; check $taskErr" }
}
if (!$NoBrowser) { Start-Process $taskUrl }
Write-Host "Colony dashboard: $taskUrl"
