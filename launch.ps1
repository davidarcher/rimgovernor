# Starts the external controller, RimWorld quicktest, and the local dashboard.
[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [int]$Port = 8787,
    [switch]$QuickTest = $true,
    [switch]$NormalGame,
    [switch]$NoGame,
    [switch]$NoBrowser,
    [switch]$Reload
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath $PSScriptRoot).Path
$python = Join-Path $repo '.venv\Scripts\python.exe'
$index = Join-Path $repo 'controller\rimbot\static\index.html'
if (!(Test-Path -LiteralPath $python) -or !(Test-Path -LiteralPath $index)) {
    throw 'Run powershell -ExecutionPolicy Bypass -File .\setup.ps1 first to prepare Python and build the dashboard.'
}
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose a port from 1024 to 65535.' }
$game = Join-Path $GamePath 'RimWorldWin64.exe'
if (!$NoGame -and !(Test-Path -LiteralPath $game -PathType Leaf)) { throw "RimWorld not found: $game" }
$url = "http://127.0.0.1:$Port"
$health = $null
try { $health = Invoke-RestMethod "$url/api/health" -TimeoutSec 2 } catch {}
if ($health -and ($health.service -ne 'rimbot' -or $health.source_root -ne $repo)) {
    throw "Port $Port belongs to another controller. Use -Port with a free port."
}
if (!$health) {
    $logDir = Join-Path $repo '.rimbot\logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $run = Get-Date -Format 'yyyyMMdd-HHmmss'
    $out = Join-Path $logDir "controller-$run.log"
    $err = Join-Path $logDir "controller-$run.error.log"
    $argsForController = @('-m','rimbot','--port',"$Port")
    if ($Reload) { $argsForController += '--reload' }
    $controller = Start-Process -FilePath $python -ArgumentList $argsForController -WorkingDirectory $repo -WindowStyle Hidden -RedirectStandardOutput $out -RedirectStandardError $err -PassThru
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        if ($controller.HasExited) { throw "Controller exited. Check $err" }
        try { $health = Invoke-RestMethod "$url/api/health" -TimeoutSec 1; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$health -or $health.service -ne 'rimbot' -or $health.source_root -ne $repo) { throw "Controller did not become ready. Check $err" }
    Write-Host "Controller started. Logs: $logDir"
} else {
    Write-Host "Reusing RimBot controller on port $Port."
}
if (!$NoGame) {
    $running = Get-Process RimWorldWin64 -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host 'RimWorld is already running; keeping the current game.'
    } else {
        $launch = @{ FilePath=$game; WorkingDirectory=$GamePath }
        if ($QuickTest -and !$NormalGame) { $launch.ArgumentList = '-quicktest' }
        # The game is intentionally visible; the controller above stays hidden.
        Start-Process @launch
        Write-Host 'RimWorld launched. Enable Run in background in its settings.'
    }
}
if (!$NoBrowser) { Start-Process $url }
Write-Host "Dashboard: $url"
Write-Host 'Use Automate in the dashboard when you want the manager to take control.'
