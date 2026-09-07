param([int]$Port = 8787, [switch]$FreshGame, [string]$Model = 'qwen3.5-4b', [switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is already in use" }
if ($FreshGame -and (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue)) { throw 'Close RimWorld before launching the isolated fresh game' }
$env:RIMBOT_MODEL = $Model
$arguments = @('-m','rimbot','--backend','rimbridge','--port',"$Port")
if ($FreshGame) { $arguments += '--fresh-game' }
New-Item -ItemType Directory -Force .rimbot/bridge | Out-Null
Start-Process -FilePath "$PSScriptRoot/.venv/Scripts/python.exe" -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput '.rimbot/bridge/controller.out.log' -RedirectStandardError '.rimbot/bridge/controller.err.log'
if (-not $NoBrowser) { Start-Process "http://127.0.0.1:$Port/" }
Write-Output "Colony dashboard: http://127.0.0.1:$Port/"
