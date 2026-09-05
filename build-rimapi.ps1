[CmdletBinding()]
param([string]$FrameworkPath = '')
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath $PSScriptRoot).Path
Push-Location -LiteralPath $repoRoot
try {
    $python = Join-Path $repoRoot '.venv\Scripts\python.exe'
    $localSdk = Join-Path $repoRoot 'tmp\dotnet\dotnet.exe'
    $sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
    if (!$FrameworkPath) {
        $localRefs = Join-Path $repoRoot 'tmp\net472\build\.NETFramework\v4.7.2'
        if (Test-Path -LiteralPath $localRefs) { $FrameworkPath = $localRefs }
    }
    $project = 'integrations/RIMAPI/Source/RIMAPI/RimApi.csproj'
    $flags = @('-p:Configuration=Release-1.6')
    if ($FrameworkPath) { $flags += "-p:FrameworkPathOverride=$FrameworkPath" }
    & $python scripts/generate_native_models.py --check
    if ($LASTEXITCODE) { throw 'Construction generated files are stale.' }
    & $python scripts/generate_http_contracts.py --check
    if ($LASTEXITCODE) { throw 'OpenAPI generated files are stale.' }
    New-Item -ItemType Directory -Path '.rimbot' -Force | Out-Null
    # Restore before asking MSBuild for resolved references on a fresh checkout.
    & $sdk restore $project @flags --verbosity quiet
    if ($LASTEXITCODE) { throw 'RIMAPI restore failed.' }
    $references = & $sdk msbuild $project -target:ResolveReferences -getItem:ReferencePath @flags -verbosity:quiet
    if ($LASTEXITCODE) { throw 'Native reference resolution failed.' }
    $references | Set-Content -Encoding utf8 '.rimbot/api-references.json'
    & $sdk run --project scripts/ApiAudit -- integrations/RIMAPI/Source/RIMAPI .rimbot/api-references.json .rimbot/api-audit.json
    if ($LASTEXITCODE) { throw 'Native semantic audit failed.' }
    & $python scripts/check_http_api.py .rimbot/api-audit.json
    if ($LASTEXITCODE) { throw 'Native API drift: review the OpenAPI contract before building.' }
    & $sdk build $project @flags --no-restore --nologo --verbosity quiet
    if ($LASTEXITCODE) { throw 'RIMAPI build failed.' }
    Write-Host 'RIMAPI compiled and contract checks passed. No DLL installed.'
} finally { Pop-Location }
