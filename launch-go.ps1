[CmdletBinding()]
# Starts the Go controller binary directly: no Python interpreter anywhere in
# this path (G01.13). Player-control building/draft/routine execution is the
# default; pass -ReadOnly for observation only. Natural-language player chat is
# not currently available in either runtime — see go/README.md for the current
# capability boundary.
param(
  [int]$Port = 8787,
  [switch]$ReadOnly,
  [string]$Profile = '',
  [string]$Gabs = '',
  [string]$Config = '',
  [string]$Game = 'rimgovernor',
  [string]$State = '',
  [string]$Assets = '',
  [switch]$NoBrowser,
  [switch]$Rebuild,
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$RoutineArgs
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

$goBinary = Join-Path $PSScriptRoot '.rimgovernor/go/rimgovernor.exe'
if ($Rebuild -or !(Test-Path -LiteralPath $goBinary)) {
  Write-Host 'Building the Go controller binary...'
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $goBinary) | Out-Null
  Push-Location (Join-Path $PSScriptRoot 'go')
  try {
    $env:GOTOOLCHAIN = 'go1.27.1'
    $env:CGO_ENABLED = '0'
    & go build -o $goBinary ./cmd/rimgovernor
    if ($LASTEXITCODE) { throw 'Go controller build failed.' }
  } finally { Pop-Location }
}

if (!$Gabs) { $Gabs = Join-Path $PSScriptRoot '.rimgovernor/bridge/gabs/gabs-v1.1.1-windows-amd64/gabs.exe' }
if (!$Config) { $Config = Join-Path $PSScriptRoot '.rimgovernor/bridge/config' }
if (!(Test-Path -LiteralPath $Gabs)) { throw 'Prepare the native bridge profile and GABS first; see docs/players/setup.md.' }
if (!(Test-Path -LiteralPath (Join-Path $Config 'config.json'))) { throw 'Missing GABS configuration; see docs/players/setup.md.' }

if (!$Assets) { $Assets = Join-Path $PSScriptRoot 'dashboard/dist' }
if (!(Test-Path -LiteralPath (Join-Path $Assets 'index.html'))) {
  Write-Host 'Building dashboard assets (no Python involved)...'
  $pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
  if (!$pnpm) { throw 'pnpm is required to build dashboard assets.' }
  Push-Location (Join-Path $PSScriptRoot 'dashboard')
  try {
    & $pnpm.Source install --frozen-lockfile
    if ($LASTEXITCODE) { throw 'Dashboard dependency install failed.' }
    & $pnpm.Source run build
    if ($LASTEXITCODE) { throw 'Dashboard build failed.' }
  } finally { Pop-Location }
}

if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Choose a port between 1024 and 65535.' }
if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) { throw "Port $Port is occupied." }

if (!$State) {
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $State = Join-Path $PSScriptRoot ".rimgovernor/go/state-$stamp.sqlite"
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $State) | Out-Null

$arguments = @('serve')
if ($ReadOnly) {
  $arguments += '--read-only'
} else {
  if (!$Profile) { $Profile = Join-Path $PSScriptRoot '.rimgovernor/bridge/profile' }
  if (!(Test-Path -LiteralPath $Profile)) { throw 'Player control requires an existing --profile directory; see docs/players/setup.md. Pass -ReadOnly for observation only.' }
  $arguments += @('--player-control', '--profile', (Resolve-Path -LiteralPath $Profile).Path)
}
$arguments += @(
  '--gabs', (Resolve-Path -LiteralPath $Gabs).Path,
  '--config', (Resolve-Path -LiteralPath $Config).Path,
  '--game', $Game,
  '--state', $State,
  '--assets', (Resolve-Path -LiteralPath $Assets).Path,
  '--listen', "127.0.0.1:$Port"
)
if ($RoutineArgs) { $arguments += $RoutineArgs }

$taskStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$taskRoot = Join-Path $PSScriptRoot '.rimgovernor/go'
$taskOut = Join-Path $taskRoot "controller-$taskStamp.out.log"
$taskErr = Join-Path $taskRoot "controller-$taskStamp.err.log"
$taskProcess = Start-Process -FilePath $goBinary -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -RedirectStandardOutput $taskOut -RedirectStandardError $taskErr -PassThru

$taskUrl = "http://127.0.0.1:$Port"
$taskHealth = $null
for ($taskAttempt = 0; $taskAttempt -lt 60; $taskAttempt++) {
  if ($taskProcess.HasExited) { throw "Go controller exited; check $taskErr" }
  try { $taskHealth = Invoke-RestMethod "$taskUrl/api/health" -TimeoutSec 1; break } catch { Start-Sleep -Milliseconds 250 }
}
if (!$taskHealth) { throw "Go controller failed to start; check $taskErr" }

if (!$NoBrowser) { Start-Process $taskUrl }
Write-Host "Go colony dashboard: $taskUrl"
