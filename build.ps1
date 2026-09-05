[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
if (!(Test-Path -LiteralPath $python)) { throw 'Run setup.ps1 first.' }
& $python -m pytest -q
if ($LASTEXITCODE) { throw 'Controller tests failed.' }
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
$bundled = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd'
$packageManager = if ($pnpm) { $pnpm.Source } elseif (Test-Path $bundled) { $bundled } else { throw 'pnpm is required.' }
Push-Location dashboard
try {
    & $packageManager run typecheck
    if ($LASTEXITCODE) { throw 'Type checking failed.' }
    & $packageManager run test
    if ($LASTEXITCODE) { throw 'Dashboard tests failed.' }
    & $packageManager run build
    if ($LASTEXITCODE) { throw 'Dashboard build failed.' }
} finally { Pop-Location }
Write-Host 'Controller checks passed and dashboard built. No mod DLL installed.'
