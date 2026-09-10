[CmdletBinding()]
param([string]$PythonPath = '')
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (!$PythonPath) {
    $bundled = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    $available = Get-Command python -ErrorAction SilentlyContinue
    if ($available -and $available.Source -notmatch 'WindowsApps') { $PythonPath = $available.Source }
    elseif (Test-Path -LiteralPath $bundled) { $PythonPath = $bundled }
    else { throw 'Install Python 3.12+ or pass -PythonPath with its executable path.' }
}
& $PythonPath -c 'import sys; assert sys.version_info >= (3,12), "Python 3.12+ required"'
if ($LASTEXITCODE) { throw 'Python 3.12+ is required.' }
if (!(Test-Path '.venv\Scripts\python.exe')) { & $PythonPath -m venv .venv }
if ($LASTEXITCODE) { throw 'Could not create the Python environment.' }
& '.\.venv\Scripts\python.exe' -m pip install -e '.[test,video]'
if ($LASTEXITCODE) { throw 'Python dependency installation failed.' }
$pnpm = Get-Command pnpm -ErrorAction SilentlyContinue
$bundledPnpm = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\bin\fallback\pnpm.cmd'
if ($pnpm) { $packageManager = $pnpm.Source }
elseif (Test-Path -LiteralPath $bundledPnpm) { $packageManager = $bundledPnpm }
else { throw 'Install Node.js 22+ and pnpm to build the dashboard.' }
Push-Location dashboard
try {
    & $packageManager install --frozen-lockfile
    if ($LASTEXITCODE) { throw 'Dashboard dependency installation failed.' }
    & $packageManager run typecheck
    if ($LASTEXITCODE) { throw 'Dashboard type checking failed.' }
    & $packageManager run build
    if ($LASTEXITCODE) { throw 'Dashboard build failed.' }
} finally { Pop-Location }
Write-Host 'Ready. Run powershell -ExecutionPolicy Bypass -File .\launch.ps1'
