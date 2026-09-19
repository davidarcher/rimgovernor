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
foreach ($event in @('workflow_dispatch','push','schedule')) {
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
