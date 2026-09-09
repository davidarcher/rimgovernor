[CmdletBinding()]
param([int]$Port=8787,[string]$Python='')
$ErrorActionPreference='Stop'
$taskSource=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$taskUrl="http://127.0.0.1:$Port"
$taskHealth=Invoke-RestMethod "$taskUrl/api/health"
if ($taskHealth.backend -ne 'rimbridge' -or $taskHealth.source_root -ne $taskSource) { throw 'Port belongs to another controller.' }
$taskProcess=Get-Process -Id $taskHealth.pid
$taskBirth=$taskProcess.StartTime.ToUniversalTime()
$null=$taskProcess.Handle # Retain the process handle so PID reuse cannot redirect termination.
$taskState=Invoke-RestMethod "$taskUrl/api/state"
$taskCheckpoint=Invoke-RestMethod "$taskUrl/api/session/checkpoint" -Method Post -Headers @{'X-RimBot'='1'} -ContentType application/json -Body (@{session_id=$taskState.sessionId}|ConvertTo-Json) -TimeoutSec 120
$taskManifest=$taskCheckpoint.manifest_path
if (!(Test-Path -LiteralPath $taskManifest)) { throw 'Verified checkpoint is unavailable; controller was not stopped.' }
$taskCurrent=Invoke-RestMethod "$taskUrl/api/health"
if ($taskCurrent.pid -ne $taskHealth.pid -or $taskCurrent.source_root -ne $taskSource -or $taskProcess.HasExited -or $taskProcess.StartTime.ToUniversalTime() -ne $taskBirth) { throw 'Controller identity changed; checkpoint retained without stopping another process.' }
$taskPython=if($Python){(Resolve-Path -LiteralPath $Python).Path}else{Join-Path $taskSource '.venv/Scripts/python.exe'}
if (!(Test-Path -LiteralPath $taskPython)) { throw 'Python environment is unavailable; controller was not stopped.' }
$env:PYTHONPATH=Join-Path $taskSource 'controller'
& $taskPython -c 'import sys; from rimbot.session_checkpoint import read_checkpoint; read_checkpoint(sys.argv[1])' $taskManifest
if ($LASTEXITCODE -ne 0) { throw 'Checkpoint verification failed; controller was not stopped.' }
$taskLatest=Invoke-RestMethod "$taskUrl/api/state"
if ($taskLatest.sessionId -ne $taskState.sessionId -or $taskLatest.mode -ne 'manual' -or !$taskLatest.game.paused -or $taskLatest.game.tick -ne $taskCheckpoint.tick) { throw 'Colony changed after checkpoint; controller was not stopped.' }
$null=Invoke-RestMethod "$taskUrl/api/session/stop" -Method Post -Headers @{'X-RimBot'='1'} -ContentType application/json -Body (@{session_id=$taskState.sessionId;manifest_path=$taskManifest}|ConvertTo-Json) -TimeoutSec 120
$taskProcess.Kill()
if (!$taskProcess.WaitForExit(30000)) { throw 'Controller did not exit; checkpoint retained.' }
$env:PYTHONPATH=Join-Path $taskSource 'controller'
$env:RIMBOT_MODEL=$taskState.chatModel
Remove-Item Env:RIMBOT_RESUME_CHECKPOINT -ErrorAction SilentlyContinue
$taskLog=Join-Path (Split-Path $taskManifest) ('restart-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
Start-Process -FilePath $taskPython -ArgumentList @('-m','rimbot','--port',"$Port",'--resume',"`"$taskManifest`"") -WorkingDirectory $taskSource -WindowStyle Hidden -RedirectStandardOutput "$taskLog.out.log" -RedirectStandardError "$taskLog.err.log"
Write-Host "Saved at tick $($taskCheckpoint.tick). Restarting in Manual: $taskUrl"
Write-Host "Checkpoint retained: $taskManifest"
$taskDeadline=(Get-Date).AddSeconds(120)
do {
 Start-Sleep -Milliseconds 500
 try {
  $taskRestored=Invoke-RestMethod "$taskUrl/api/state" -TimeoutSec 2
  if ($taskRestored.connected) {
   $taskReadyHealth=Invoke-RestMethod "$taskUrl/api/health"
   $taskColonyPrefix=$taskState.sessionId.Substring(0,$taskState.sessionId.LastIndexOf(':')+1)
   if ($taskReadyHealth.source_root -ne $taskSource -or !$taskRestored.sessionId.StartsWith($taskColonyPrefix)) { throw 'Resumed state differs from the checkpoint.' }
   if ($taskRestored.mode -ne 'manual' -or !$taskRestored.game.paused -or $taskRestored.game.tick -lt $taskCheckpoint.tick -or $taskRestored.game.tick -gt ($taskCheckpoint.tick+1)) { throw 'Resumed state differs from the checkpoint.' }
   Write-Host 'Restart verified. Colony is paused in Manual.'
   return
  }
 } catch { if ($_.Exception.Message -eq 'Resumed state differs from the checkpoint.') { throw } }
} while ((Get-Date) -lt $taskDeadline)
throw "Restart did not become ready. Check $taskLog.err.log; checkpoint remains available."
