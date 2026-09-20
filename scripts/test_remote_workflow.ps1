param([Parameter(Mandatory)][string]$TestRoot)
# Fast authorization/collection tests. GitHub is mocked; no bundle/game access.
$ErrorActionPreference = 'Stop'
function global:gh {
    $global:LASTEXITCODE = 0
    '{"full_name":"davidarcher/rimgovernor","visibility":"public","default_branch":"main"}'
}
$baseline = @{
    REMOTE_ACCEPTANCE_ENABLED='reviewed-v1'; REMOTE_ACCEPTANCE_MAINTAINERS='maintainer'
    REMOTE_BUNDLE_SHA256=('a'*64); REMOTE_BUNDLE_ASSET='123'; REMOTE_INVENTORY_ASSET='456'
    GITHUB_REPOSITORY='davidarcher/rimgovernor';GITHUB_REF='refs/heads/main';GITHUB_REF_PROTECTED='true'
    GITHUB_EVENT_NAME='workflow_dispatch';GITHUB_SHA=('b'*40);GITHUB_WORKFLOW_SHA=('b'*40)
    GITHUB_ACTOR='maintainer';GITHUB_TRIGGERING_ACTOR='maintainer'
}
foreach ($key in $baseline.Keys) { [Environment]::SetEnvironmentVariable($key,$baseline[$key],'Process') }
foreach ($event in @('workflow_dispatch','schedule')) {
    $env:GITHUB_EVENT_NAME=$event
    & "$PSScriptRoot/remote_workflow.ps1" -Phase authorize
}
$env:GITHUB_EVENT_NAME='workflow_dispatch'
$bad = @{
    REMOTE_ACCEPTANCE_ENABLED='';GITHUB_REPOSITORY='fork/rimgovernor';GITHUB_REF='refs/heads/topic'
    GITHUB_REF_PROTECTED='false';GITHUB_EVENT_NAME='pull_request_target';GITHUB_WORKFLOW_SHA=('c'*40)
    GITHUB_ACTOR='outsider';GITHUB_TRIGGERING_ACTOR='outsider';REMOTE_BUNDLE_SHA256='bad'
    REMOTE_BUNDLE_ASSET='https://attacker.invalid';REMOTE_INVENTORY_ASSET='0'
}
foreach ($key in $bad.Keys) {
    [Environment]::SetEnvironmentVariable($key,$bad[$key],'Process')
    $rejected=$false
    try { & "$PSScriptRoot/remote_workflow.ps1" -Phase authorize } catch { $rejected=$true }
    [Environment]::SetEnvironmentVariable($key,$baseline[$key],'Process')
    if (-not $rejected) { throw "Untrusted context accepted: $key" }
}
function global:gh {
    $global:LASTEXITCODE = 0
    '{"full_name":"davidarcher/rimgovernor","visibility":"private","default_branch":"main"}'
}
$rejected=$false
try { & "$PSScriptRoot/remote_workflow.ps1" -Phase authorize } catch { $rejected=$true }
if (-not $rejected) { throw 'Visibility change bypassed billing gate' }
'Remote workflow authorization gates passed'

# Exercise complete native failures, infrastructure failures and cancellation
# through the same authenticated-job projection used by the final verdict job.
$env:GITHUB_RUN_ID='1';$env:GITHUB_RUN_ATTEMPT='1'
[IO.File]::WriteAllText((Join-Path $TestRoot 'run.json'), '{"limits":{"artifact_max_bytes":0}}')
[IO.File]::WriteAllText((Join-Path $TestRoot 'selection.json'), '{"shards":[{"id":"s1","cases":["smoke/identity"]}]}')
[IO.Directory]::CreateDirectory((Join-Path $TestRoot 's1')) | Out-Null
[IO.File]::WriteAllText((Join-Path $TestRoot 's1/attempts.json'), '{}')
function global:gh { $global:LASTEXITCODE=0; $global:taskWorkflowJobs }
foreach ($case in @(
    @{conclusion='success';step='';want='complete'},
    @{conclusion='failure';step='Bootstrap private layouts and run exact shard cases';want='complete'},
    @{conclusion='failure';step='Export allowlisted diagnostics even after native failure';want='missing'},
    @{conclusion='cancelled';step='';want='cancelled'},
    @{conclusion='timed_out';step='';want='timed_out'}
)) {
    $global:taskWorkflowJobs = ConvertTo-Json -Depth 8 -InputObject @(@{jobs=@(@{name='acceptance-s1';conclusion=$case.conclusion;steps=@(@{name=$case.step;conclusion='failure'})})})
    & "$PSScriptRoot/remote_workflow.ps1" -Phase collect -Evidence $TestRoot
    $raw=Get-Content -LiteralPath (Join-Path $TestRoot 'shards.json') -Raw
    if (-not $raw.TrimStart().StartsWith('[')) { throw 'Single-shard manifest lost array shape' }
    $row=@($raw | ConvertFrom-Json)[0]
    if ($row.status -cne $case.want -or -not $row.attempts.sha256) { throw "Incorrect job projection: $($case.conclusion) / $($case.step)" }
}
'Remote workflow collection gates passed'

# Source selection resolves branch names once and pins every downstream job to
# that SHA. Historical explicit-SHA dispatches remain valid.
$global:sourceLookups = 0
function global:gh {
    $global:LASTEXITCODE = 0
    if ($args[1] -ceq 'repos/davidarcher/rimgovernor') {
        return '{"full_name":"davidarcher/rimgovernor","visibility":"public","default_branch":"main"}'
    }
    if ($args[1] -ceq 'repos/davidarcher/rimgovernor/commits/feature%2Fci') {
        $global:sourceLookups++
        return ('{"sha":"' + ('c'*40) + '"}')
    }
    throw "Unexpected API request: $args"
}
. "$PSScriptRoot/remote_workflow.ps1" -Phase authorize
$event = '{"inputs":{"tested_ref":"feature/ci","tier":"full","shards":"32","reviewed_commit":"true"}}' | ConvertFrom-Json
$source = Resolve-Source $event
if ($source.head -cne ('c'*40) -or $source.base -cne $source.head -or $source.tier -cne 'full' -or $source.shards -ne 32 -or $global:sourceLookups -ne 1) {
    throw 'Branch dispatch did not pin its source and default base'
}
$event.inputs | Add-Member tested_commit ('d'*40)
$source = Resolve-Source $event
if ($source.head -cne ('d'*40) -or $global:sourceLookups -ne 1) { throw 'Explicit SHA did not override the branch' }
$event.inputs.tier = 'land'
$rejected=$false
try { Resolve-Source $event | Out-Null } catch { $rejected=$true }
if (-not $rejected) { throw 'Land accepted a missing ancestor base' }
$event.inputs | Add-Member base_commit ('a'*40)
$source = Resolve-Source $event
if ($source.base -cne ('a'*40)) { throw 'Explicit land base was lost' }
$event.inputs.reviewed_commit = 'false'
$rejected=$false
try { Resolve-Source $event | Out-Null } catch { $rejected=$true }
if (-not $rejected) { throw 'Unreviewed source was accepted' }
$env:GITHUB_EVENT_NAME='schedule'
$source = Resolve-Source ([pscustomobject]@{})
if ($source.head -cne $env:GITHUB_SHA -or $source.base -cne $source.head -or $source.tier -cne 'full' -or $source.shards -ne 32) { throw 'Nightly source changed' }
$env:GITHUB_EVENT_NAME='push'
$rejected=$false
try { Assert-Gate } catch { $rejected=$true }
if (-not $rejected) { throw 'Automatic push was accepted' }
'Remote workflow source selection passed'
