[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('authorize','gate','plan','execute','collect')][string]$Phase,
    [string]$Evidence = 'C:\rg\evidence',
    [string]$Repo = "$env:GITHUB_WORKSPACE\tested",
    [string]$Shard = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
function Write-JSON($Path, $Value) {
    [IO.Directory]::CreateDirectory((Split-Path $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 80) + "`n")
}
function Read-JSON($Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Invoke-API($Path) {
    $value = & gh api $Path
    if ($LASTEXITCODE) { throw "GitHub metadata unavailable: $Path" }
    $value | ConvertFrom-Json
}
function File-Reference($Relative) {
    @{path=$Relative; sha256=(Get-FileHash -LiteralPath (Join-Path $Evidence $Relative) -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Assert-Gate {
    if ($env:REMOTE_ACCEPTANCE_ENABLED -cne 'reviewed-v1' -or
        $env:GITHUB_REPOSITORY -cne 'davidarcher/rimgovernor' -or
        $env:GITHUB_REF -cne 'refs/heads/main' -or
        $env:GITHUB_REF_PROTECTED -cne 'true' -or
        $env:GITHUB_EVENT_NAME -notin @('workflow_dispatch','push','schedule') -or
        $env:GITHUB_WORKFLOW_SHA -cnotmatch '^[0-9a-f]{40}$' -or
        $env:GITHUB_SHA -cne $env:GITHUB_WORKFLOW_SHA) {
        throw 'Remote acceptance requires activation, protected main and its exact trusted workflow revision'
    }
    $maintainers = @($env:REMOTE_ACCEPTANCE_MAINTAINERS -split ',' | ForEach-Object { $_.Trim() })
    if ($env:GITHUB_ACTOR -notin $maintainers -or $env:GITHUB_TRIGGERING_ACTOR -notin $maintainers) {
        throw 'Both original actor and rerun actor must be configured maintainers'
    }
    $metadata = Invoke-API "repos/$env:GITHUB_REPOSITORY"
    if ($metadata.full_name -cne $env:GITHUB_REPOSITORY -or $metadata.visibility -cne 'public' -or $metadata.default_branch -cne 'main') {
        throw 'Visibility/default branch changed; revalidate storage, trust and billing before running'
    }
    if ($env:REMOTE_BUNDLE_SHA256 -cnotmatch '^[0-9a-f]{64}$' -or
        $env:REMOTE_BUNDLE_ASSET -notmatch '^[1-9][0-9]*$' -or
        $env:REMOTE_INVENTORY_ASSET -notmatch '^[1-9][0-9]*$') { throw 'Pinned bundle/inventory assets are required' }
}
function Download-Asset($ID,$Path) {
    $asset = Invoke-API "repos/$env:GITHUB_REPOSITORY/releases/assets/$ID"
    if ($asset.id -ne [long]$ID -or $asset.size -gt 16MB -or $asset.size -le 0) { throw 'Manifest/inventory asset exceeds its bounded allocation' }
    Invoke-WebRequest -Uri "https://api.github.com/repos/$env:GITHUB_REPOSITORY/releases/assets/$ID" `
        -Headers @{Authorization="Bearer $env:GH_TOKEN"; Accept='application/octet-stream'} -OutFile $Path
}

switch ($Phase) {
    'authorize' { Assert-Gate }
    'gate' {
        Assert-Gate
        $event = Read-JSON $env:GITHUB_EVENT_PATH
        $tier = 'smoke'; $head = $env:GITHUB_SHA; $base = $head; $shards = 2
        if ($env:GITHUB_EVENT_NAME -eq 'workflow_dispatch') {
            $head = $event.inputs.tested_commit; $base = $event.inputs.base_commit
            $tier = $event.inputs.tier; $shards = [int]$event.inputs.shards
            if ($event.inputs.reviewed_commit -cne 'true') { throw 'Dispatch must attest review of the exact tested commit' }
        } elseif ($env:GITHUB_EVENT_NAME -eq 'push') { $base = $event.before; $head = $event.after
        } else { $tier = 'full'; $shards = 32 }
        if ($head -cnotmatch '^[0-9a-f]{40}$' -or $base -cnotmatch '^[0-9a-f]{40}$' -or
            $head -eq ('0'*40) -or $base -eq ('0'*40) -or $tier -notin @('smoke','land','full') -or
            $shards -lt 1 -or $shards -gt 32) { throw 'Invalid source, base, tier or shard limit' }
        # Resolve immutable objects through the same repository, never a caller URL/ref.
        foreach ($oid in @($head,$base)) { if ((Invoke-API "repos/$env:GITHUB_REPOSITORY/commits/$oid").sha -cne $oid) { throw 'Commit unavailable in source repository' } }
        [IO.Directory]::CreateDirectory($Evidence) | Out-Null
        Download-Asset $env:REMOTE_BUNDLE_ASSET (Join-Path $Evidence 'bundle.json')
        if ((File-Reference 'bundle.json').sha256 -cne $env:REMOTE_BUNDLE_SHA256) { throw 'Bundle manifest digest mismatch' }
        $manifest = Read-JSON (Join-Path $Evidence 'bundle.json')
        if ($manifest.origin.repository -cne $env:GITHUB_REPOSITORY -or $manifest.encryption.format -cne 'age-v1' -or
            $manifest.inventory.path -cnotmatch '^inventory(?:-[0-9a-f]{64})?\.json$') { throw 'Unsupported encrypted bundle origin/inventory' }
        Download-Asset $env:REMOTE_INVENTORY_ASSET (Join-Path $Evidence $manifest.inventory.path)
        if ((File-Reference $manifest.inventory.path).sha256 -cne $manifest.inventory.sha256) { throw 'Inventory digest mismatch' }
        Write-JSON (Join-Path $Evidence 'run.json') @{
            schema_version=1; run_id="gh:$($env:GITHUB_REPOSITORY):$($env:GITHUB_RUN_ID):$($env:GITHUB_RUN_ATTEMPT)"
            repository=$env:GITHUB_REPOSITORY; workflow_commit=$env:GITHUB_WORKFLOW_SHA; tested_commit=$head; base_commit=$base; tier=$tier
            trigger=@{event=$env:GITHUB_EVENT_NAME; actor=$env:GITHUB_ACTOR; published_ref=$env:GITHUB_REF; actions_run_id=[long]$env:GITHUB_RUN_ID; actions_run_attempt=[int]$env:GITHUB_RUN_ATTEMPT}
            bundle=(File-Reference 'bundle.json')
            limits=@{runner_label='windows-2022';shards=$shards;max_parallel=4;workers_per_shard=1;job_timeout_minutes=360;suite_timeout_minutes=345;max_attempts=1;artifact_retention_days=7;artifact_max_bytes=1073741824;paid_usage_authorized=$false}
        }
        "head=$head" >> $env:GITHUB_OUTPUT
    }
    'plan' {
        $run = Read-JSON (Join-Path $Evidence 'run.json')
        Push-Location (Join-Path $Repo 'go')
        try {
            $selection = & go run ./internal/nativeaccept/cmd/acceptance plan -evidence $Evidence -run run.json -fetch
            if ($LASTEXITCODE) { throw 'Planner rejected the complete selection; see diagnostics' }
            [IO.File]::WriteAllText((Join-Path $Evidence 'selection.json'), ($selection -join "`n") + "`n")
        } finally { Pop-Location }
        $selection = Read-JSON (Join-Path $Evidence 'selection.json')
        $matrix = @{include=@($selection.shards | ForEach-Object { @{shard=$_.id} })} | ConvertTo-Json -Compress -Depth 5
        "matrix=$matrix" >> $env:GITHUB_OUTPUT
        "Planned $($selection.cases.Count) cases in $($selection.shards.Count) shards; tier $($run.tier)." >> $env:GITHUB_STEP_SUMMARY
    }
    'execute' {
        $run = Read-JSON (Join-Path $Evidence 'run.json')
        $selection = Read-JSON (Join-Path $Evidence 'selection.json')
        if ($Shard -cnotmatch '^s[1-9][0-9]*$' -or $run.workflow_commit -cne $env:GITHUB_WORKFLOW_SHA -or
            $run.bundle.sha256 -cne $env:REMOTE_BUNDLE_SHA256 -or
            $run.run_id -cne "gh:$($env:GITHUB_REPOSITORY):$($env:GITHUB_RUN_ID):$($env:GITHUB_RUN_ATTEMPT)") { throw 'Wrong run/shard input' }
        $planned = @($selection.shards | Where-Object id -CEQ $Shard)
        if ($planned.Count -ne 1) { throw 'Missing shard selection' }
        $rows = @($selection.cases | Where-Object name -CIn $planned[0].cases)
        if ($rows.Count -ne $planned[0].cases.Count -or @($rows | Where-Object mod_role -NotIn @('fixture','production')).Count) { throw 'Invalid role projection' }
        $roles = @($rows.mod_role | Sort-Object -Unique)
        $trust = 'C:\rg\trust.json'
        Write-JSON $trust @{repository=$run.repository;tested_commit=$run.tested_commit;workflow_commit=$run.workflow_commit;event=$run.trigger.event}
        $identity = 'C:\rg\identity.txt'
        [IO.File]::WriteAllText($identity,$env:REMOTE_BUNDLE_IDENTITY)
        Remove-Item Env:REMOTE_BUNDLE_IDENTITY
        $jobs = @(); $bad = $false
        try {
            # Both roles are built before any game is launched.
            foreach ($role in $roles) {
                $work = "C:\rg\$role"
                & "$PSScriptRoot/bootstrap_remote.ps1" -Repo $Repo -Work $work -Cache C:\rg\ciphertext `
                    -Manifest (Join-Path $Evidence 'bundle.json') -ManifestSHA256 $run.bundle.sha256 `
                    -Trust $trust -ToolLock "$PSScriptRoot/remote-tools.windows.json" -Identity $identity -Role $role
                $jobs += @{role=$role;output="$work\job\out";bootstrap="$work\job\bootstrap.json"}
            }
            # Bootstrap's authenticated origin access is over. No credential is inherited by a suite.
            Remove-Item -LiteralPath $identity
            Get-ChildItem Env: | Where-Object Name -Match 'TOKEN|SECRET|PASSWORD|PRIVATE_KEY' | ForEach-Object {
                [Environment]::SetEnvironmentVariable($_.Name, $null, 'Process')
            }
            $deadline = [DateTime]::UtcNow.AddMinutes($run.limits.suite_timeout_minutes)
            foreach ($job in $jobs) {
                $boot = Read-JSON $job.bootstrap
                $seconds = [int]($deadline - [DateTime]::UtcNow).TotalSeconds
                if ($seconds -le 0) { throw 'Shard suite allowance exhausted' }
                $names = @($rows | Where-Object mod_role -CEQ $job.role | ForEach-Object name) -join ','
                Push-Location (Join-Path $Repo 'go')
                try {
                    & $boot.acceptance suite -cases $names -workers 1 -timeout "$($seconds)s" -root $boot.root -output $job.output -rimgovernor $boot.controller -no-series
                    if ($LASTEXITCODE) { $bad = $true }
                } finally { Pop-Location }
            }
        } finally {
            if (Test-Path -LiteralPath $identity) { Remove-Item -LiteralPath $identity }
            Write-JSON 'C:\rg\export-jobs.json' @($jobs)
            foreach ($role in $roles) { & "$PSScriptRoot/cleanup_remote.ps1" -Work "C:\rg\$role" }
        }
        if ($bad) { throw 'One or more native cases failed; export retains their diagnostics' }
    }
    'collect' {
        $selection = Read-JSON (Join-Path $Evidence 'selection.json')
        $pages = & gh api --paginate --slurp "repos/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID/attempts/$env:GITHUB_RUN_ATTEMPT/jobs?per_page=100"
        if ($LASTEXITCODE) { throw 'Cannot authenticate shard job conclusions' }
        $jobs = @(($pages | ConvertFrom-Json) | ForEach-Object jobs)
        $shards = @()
        foreach ($sh in $selection.shards) {
            $job = @($jobs | Where-Object name -CEQ "acceptance-$($sh.id)")
            $status = 'missing'; $attempts = $null
            if ($job.Count -eq 1) {
                switch ($job[0].conclusion) {
                    'cancelled' { $status = 'cancelled' }
                    'timed_out' { $status = 'timed_out' }
                    'success' { $status = 'complete' }
                    # A failed case is a complete red report; an earlier failure lacks attempts.
                    'failure' {
                        $failedSteps = @($job[0].steps | Where-Object conclusion -EQ 'failure')
                        if ($failedSteps.Count -eq 1 -and $failedSteps[0].name -ceq 'Bootstrap private layouts and run exact shard cases') { $status = 'complete' }
                    }
                }
            }
            $path = "$($sh.id)/attempts.json"
            if (Test-Path -LiteralPath (Join-Path $Evidence $path)) { $attempts = File-Reference $path }
            $shards += @{id=$sh.id;status=$status;attempts=$attempts}
        }
        Write-JSON (Join-Path $Evidence 'shards.json') @($shards)
        # Bound the combined tree before upload; duplicate job and final artifacts
        # share the 1 GiB storage budget with the plan.
        $size = (Get-ChildItem -LiteralPath $Evidence -File -Recurse | Measure-Object Length -Sum).Sum
        if ($size -gt 512MB) { throw 'Combined evidence exceeds the run storage allowance' }
    }
}
