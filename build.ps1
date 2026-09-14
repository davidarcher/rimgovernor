[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
Push-Location go
try {
    $env:GOTOOLCHAIN = 'go1.27.1'
    $env:CGO_ENABLED = '0'
    & go vet ./...
    if ($LASTEXITCODE) { throw 'go vet failed.' }
    & go test ./...
    if ($LASTEXITCODE) { throw 'Go controller tests failed.' }
    & go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor
    if ($LASTEXITCODE) { throw 'Go controller build failed.' }
} finally { Pop-Location }
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
Write-Host 'Go controller checks passed and dashboard built. No mod DLL installed.'
