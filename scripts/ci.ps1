#Requires -Version 7
<#
.SYNOPSIS
Local continuous-integration gate. Runs the checks the retired GitHub workflows
ran, plus the native mod build those workflows never covered.

.DESCRIPTION
Stages (all run by default; select with -Stage):
  go        pinned toolchain, gofmt, module integrity/tidy drift, vet, tests with
            a 10s per-test ceiling, gated build, race tests (was go.yml)
  dashboard frozen-lockfile install, typecheck, tests, build (was checks.yml)
  protobuf  official C#/Go regeneration drift and cross-language exchange
            (was protobuf.yml); driven by go/internal/protobufgen
  native    Bridge, Runtime and contract-probe builds with warnings as errors and
            locked restore; reports unavailable when the game DLLs are missing

Every stage records pass/FAIL/unavailable and the script exits non-zero if any
stage failed. Unavailable coverage is reported, never hidden.

.EXAMPLE
pwsh scripts/ci.ps1
pwsh scripts/ci.ps1 -Stage go,dashboard
#>
[CmdletBinding()]
param(
    [ValidateSet('go', 'dashboard', 'protobuf', 'native')]
    [string[]]$Stage = @('go', 'dashboard', 'protobuf', 'native'),
    [string]$RimWorldManagedDir = 'C:/Program Files (x86)/Steam/steamapps/common/RimWorld/RimWorldWin64_Data/Managed',
    [string]$HarmonyAssembly = 'C:/Program Files (x86)/Steam/steamapps/workshop/content/294100/2009463077/Current/Assemblies/0Harmony.dll',
    [string]$RimBridgeSdkDir = 'C:/Program Files (x86)/Steam/steamapps/common/RimWorld/Mods/RimBridgeServer/1.6/Assemblies'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$runID = [guid]::NewGuid().ToString('N').Substring(0, 8)
$scratch = Join-Path $repo ".rimgovernor/ci/$runID"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$results = [ordered]@{}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "--- $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Body
    if ($LASTEXITCODE) { throw "$Name failed (exit $LASTEXITCODE)" }
}

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "=== $Name" -ForegroundColor Green
    $started = Get-Date
    try {
        $outcome = & $Body
        $results[$Name] = if ($outcome -eq 'unavailable') { 'unavailable' } else { 'pass' }
    } catch {
        Write-Host $_ -ForegroundColor Red
        $results[$Name] = 'FAIL'
    }
    $global:LASTEXITCODE = 0
    Write-Host ("=== {0}: {1} ({2:N0}s)" -f $Name, $results[$Name], ((Get-Date) - $started).TotalSeconds)
}

function Test-Tool {
    param([string]$Name)
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

if ($Stage -contains 'go') {
    Invoke-Stage 'go' {
        Push-Location (Join-Path $repo 'go')
        try {
            $env:GOTOOLCHAIN = 'go' + (Get-Content .go-version).Trim()
            $env:CGO_ENABLED = '0'
            Invoke-Step 'pinned toolchain' {
                if ((go env GOVERSION) -ne $env:GOTOOLCHAIN) { throw 'Go toolchain differs from .go-version' }
            }
            Invoke-Step 'gofmt' {
                $unformatted = gofmt -l .
                if ($LASTEXITCODE -ne 0 -or $unformatted) { throw "Run gofmt: $unformatted" }
            }
            Invoke-Step 'go mod verify' { go mod verify }
            Invoke-Step 'go mod tidy -diff' { go mod tidy -diff }
            Invoke-Step 'go vet' { go vet ./... }
            Invoke-Step 'staticcheck (pinned go.mod tool)' { go tool staticcheck ./... }
            Invoke-Step 'go test (10s per-test ceiling)' {
                go test -json ./... | go run ./internal/testkit/cmd/checktesttimes -max 10s
            }
            Invoke-Step 'go build' { go build -trimpath -o "$scratch/rimgovernor.exe" ./cmd/rimgovernor }
            if (Test-Tool 'gcc') {
                $env:CGO_ENABLED = '1'
                # Ownership tests exercise a one-second subprocess shutdown deadline.
                $env:GORACE = 'atexit_sleep_ms=0'
                Invoke-Step 'go test -race' { go test -race ./... }
            } else {
                Write-Host 'race: unavailable (no C compiler on PATH)' -ForegroundColor Yellow
            }
        } finally {
            Pop-Location
            Remove-Item Env:GOTOOLCHAIN, Env:CGO_ENABLED, Env:GORACE -ErrorAction SilentlyContinue
        }
    }
}

if ($Stage -contains 'dashboard') {
    Invoke-Stage 'dashboard' {
        if (-not (Test-Tool 'pnpm')) { Write-Host 'pnpm missing' -ForegroundColor Yellow; return 'unavailable' }
        Push-Location (Join-Path $repo 'dashboard')
        try {
            Invoke-Step 'pnpm install --frozen-lockfile' { pnpm install --frozen-lockfile }
            Invoke-Step 'typecheck' { pnpm run typecheck }
            Invoke-Step 'lint (typing-escape gate)' { pnpm run lint }
            Invoke-Step 'test' { pnpm test }
            Invoke-Step 'build' { pnpm build }
        } finally { Pop-Location }
    }
}

if ($Stage -contains 'protobuf') {
    Invoke-Stage 'protobuf' {
        if (-not (Test-Tool 'dotnet')) {
            Write-Host 'dotnet missing' -ForegroundColor Yellow; return 'unavailable'
        }
        Push-Location $repo
        try {
            # The workflow ran with GOTOOLCHAIN=local because setup-go had already installed
            # the pinned version; locally the pin has to come from .go-version.
            $env:GOTOOLCHAIN = 'go' + (Get-Content (Join-Path $repo 'go/.go-version')).Trim()
            $env:GOWORK = 'off'
            Invoke-Step 'official C# generation drift' {
                go -C go run ./internal/protobufgen/cmd/generatecsharp --check --output "$scratch/protobuf-csharp-check"
            }
            Invoke-Step 'official Go generation drift' {
                $protoc = "$env:USERPROFILE/.nuget/packages/grpc.tools/2.72.0/tools/windows_x64/protoc.exe"
                go -C go run ./internal/protobufgen/cmd/generatego --protoc $protoc --check --output "$scratch/protobuf-go-check"
            }
            Invoke-Step 'go proof tests' { go -C tools/protobuf/go test -mod=readonly ./... }
            Invoke-Step 'go proof vet' { go -C tools/protobuf/go vet -mod=readonly ./... }
            Invoke-Step 'go origin fixtures' { go -C tools/protobuf/go run -mod=readonly . --output "$scratch/protobuf-go-origin" }
            Invoke-Step 'C# runtime exchange' {
                go -C go run ./internal/protobufgen/cmd/generatecsharp --check --proof --cross-language-inputs "$scratch/protobuf-go-origin" --output "$scratch/protobuf-exchange"
            }
            Invoke-Step 'Go runtime exchange' {
                go -C tools/protobuf/go run -mod=readonly . --output "$scratch/protobuf-go-return" --cross-language-inputs "$scratch/protobuf-exchange/roundtrip" --check-go-echo
            }
            Invoke-Step 'C#/Go/C# equality' {
                & "$scratch/protobuf-exchange/source/tools/protobuf/bin/Release/net472/ProtobufProof.exe" --verify-return "$scratch/protobuf-exchange/roundtrip" "$scratch/protobuf-go-return"
            }
        } finally {
            Pop-Location
            Remove-Item Env:GOTOOLCHAIN, Env:GOWORK -ErrorAction SilentlyContinue
        }
    }
}

if ($Stage -contains 'native') {
    Invoke-Stage 'native' {
        $inputs = @((Join-Path $RimWorldManagedDir 'Assembly-CSharp.dll'), $HarmonyAssembly, (Join-Path $RimBridgeSdkDir 'RimBridgeServer.Sdk.dll'))
        $missing = @($inputs | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
        if (-not (Test-Tool 'dotnet') -or $missing.Count) {
            Write-Host "native build inputs missing: $($missing -join ', ')" -ForegroundColor Yellow
            return 'unavailable'
        }
        Invoke-Step 'mod build (Bridge + Runtime, locked restore, warnings as errors)' {
            & (Join-Path $PSScriptRoot 'build_native_mod.ps1') -RimWorldManagedDir $RimWorldManagedDir `
                -HarmonyAssembly $HarmonyAssembly -RimBridgeSdkDir $RimBridgeSdkDir -OutputRoot "$scratch/native-mod" | Out-Null
        }
        # Independent projects: build every one so a single stale project does not hide the others.
        $failed = @()
        foreach ($project in @('contracts/tests/NativeContractProbes.csproj', 'scripts/fixtures/CombatFixtures.csproj', 'scripts/fixtures/InterruptionFixtures.csproj')) {
            $projectName = [IO.Path]::GetFileNameWithoutExtension($project)
            try {
                Invoke-Step "$projectName build (locked restore, warnings as errors)" {
                    dotnet build (Join-Path $repo $project) -c Release -v minimal -nologo `
                        -p:RestoreLockedMode=true "-p:OutputPath=$scratch/$projectName/" "-p:BaseIntermediateOutputPath=$scratch/$projectName-obj/" `
                        "-p:RimWorldManagedDir=$RimWorldManagedDir" "-p:HarmonyAssembly=$HarmonyAssembly" "-p:RimBridgeSdkDir=$RimBridgeSdkDir"
                }
            } catch { Write-Host $_ -ForegroundColor Red; $failed += $projectName }
        }
        if ($failed) { throw "native projects failed: $($failed -join ', ')" }
    }
}

Write-Host ''
Write-Host 'Summary' -ForegroundColor Green
foreach ($entry in $results.GetEnumerator()) { Write-Host ("  {0,-10} {1}" -f $entry.Key, $entry.Value) }
Write-Host "  evidence   $scratch"
if ($results.Values -contains 'FAIL') { exit 1 }
