[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Work,
    [Parameter(Mandatory)][string]$Cache,
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$ManifestSHA256,
    [Parameter(Mandatory)][string]$Trust,
    [Parameter(Mandatory)][string]$ToolLock,
    [Parameter(Mandatory)][string]$Identity,
    [ValidateSet('fixture', 'production')][string]$Role = 'fixture',
    [string]$Suite = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskRepoPrefix = [IO.Path]::GetFullPath($Repo).TrimEnd('\') + '\'
foreach ($taskTrustedInput in @($Trust, $ToolLock)) {
    if ([IO.Path]::GetFullPath($taskTrustedInput).StartsWith($taskRepoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Copy authorization and the reviewed tool lock from the trusted workflow revision outside the tested checkout'
    }
}

# This script and its authorization/lock must come from the trusted workflow
# revision, not from the commit that is about to execute. Gate BEFORE caches,
# downloads, builds, or access to the age identity.
$taskTrust = Get-Content -LiteralPath $Trust -Raw | ConvertFrom-Json
$taskHead = (& git -C $Repo rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -or $taskTrust.repository -cne $env:GITHUB_REPOSITORY -or
    $taskTrust.tested_commit -cne $taskHead -or
    $taskTrust.workflow_commit -cne $env:GITHUB_WORKFLOW_SHA -or
    $taskTrust.event -cne $env:GITHUB_EVENT_NAME -or
    $taskTrust.event -notin @('push', 'workflow_dispatch', 'schedule') -or
    $taskHead -cnotmatch '^[0-9a-f]{40}$') { throw 'Runner trust/provenance mismatch' }
if ($taskTrust.event -in @('push', 'schedule') -and
    ($env:GITHUB_REF -cne 'refs/heads/main' -or $env:GITHUB_REF_PROTECTED -cne 'true')) {
    throw 'Only protected main pushes may bootstrap'
}
if ((& git -C $Repo status --porcelain --untracked-files=no | Out-String).Trim()) { throw 'Tested checkout is dirty' }
$taskWork = [IO.Path]::GetFullPath($Work)
if (Test-Path -LiteralPath $taskWork) { throw 'Work must be a new, short, private job directory' }
if ($taskWork.Length -gt 90) { throw 'Use a short job path, for example C:\rg\j1' }
New-Item -ItemType Directory -Path $taskWork | Out-Null
[IO.File]::WriteAllText((Join-Path $taskWork '.remote-acceptance-job'), $taskWork)
$taskTimer = [Diagnostics.Stopwatch]::StartNew()
$taskHardware = Get-CimInstance Win32_OperatingSystem
$taskDrive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($taskWork))
if ($taskDrive.AvailableFreeSpace -lt 8GB) { throw 'At least 8 GiB free disk is required before bootstrap' }
$taskLock = Get-Content -LiteralPath $ToolLock -Raw | ConvertFrom-Json
if ($taskLock.schema_version -ne 1) { throw 'Unsupported tool lock version' }
$taskNames = @($taskLock.tools | ForEach-Object name | Sort-Object)
if (($taskNames -join ',') -cne '7z,age,dotnet,go') { throw 'Tool lock must pin exactly go, dotnet, age and 7z' }
$taskExecutables = @{}
$taskToolsRoot = Join-Path $taskWork 'tools'
New-Item -ItemType Directory -Path $taskToolsRoot | Out-Null
foreach ($taskTool in $taskLock.tools) {
    if ($taskTool.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $taskTool.version -notmatch '^\d+\.\d+' -or
        $taskTool.url -notmatch '^https://' -or $taskTool.format -notin @('zip', 'exe')) {
        throw "Invalid pinned tool: $($taskTool.name)"
    }
    $taskArchive = Join-Path $taskToolsRoot ($taskTool.name + '.download')
    Invoke-WebRequest -Uri $taskTool.url -OutFile $taskArchive
    if ((Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $taskTool.sha256) {
        throw "Tool archive checksum mismatch: $($taskTool.name)"
    }
    $taskInstall = Join-Path $taskToolsRoot $taskTool.name
    New-Item -ItemType Directory -Path $taskInstall | Out-Null
    $taskExecutable = [IO.Path]::GetFullPath((Join-Path $taskInstall $taskTool.executable))
    if (-not $taskExecutable.StartsWith($taskInstall + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Tool executable escapes installation directory'
    }
    if ($taskTool.format -eq 'zip') {
        # Trusted digest-pinned tool archives, not the game bundle.
        [IO.Compression.ZipFile]::ExtractToDirectory($taskArchive, $taskInstall)
    } else {
        Copy-Item -LiteralPath $taskArchive -Destination $taskExecutable
    }
    if (-not (Test-Path -LiteralPath $taskExecutable -PathType Leaf)) { throw 'Pinned tool executable missing' }
    $taskExecutables[$taskTool.name] = $taskExecutable
    $env:PATH = (Split-Path $taskExecutable -Parent) + ';' + $env:PATH
}
$env:DOTNET_ROOT = Split-Path $taskExecutables['dotnet'] -Parent
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:GOROOT = Split-Path (Split-Path $taskExecutables['go'] -Parent) -Parent
$env:GOTOOLCHAIN = 'local'
$env:GOWORK = 'off'
$env:CGO_ENABLED = '0'
$taskGoVersion = (Get-Content -LiteralPath (Join-Path $Repo 'go/.go-version') -Raw).Trim()
$taskActualGo = (& $taskExecutables['go'] version | Out-String).Trim()
if ($taskActualGo -notmatch ('go' + [regex]::Escape($taskGoVersion) + ' ')) { throw 'Installed Go does not match go/.go-version' }
$taskDotNetVersion = (& $taskExecutables['dotnet'] --version | Out-String).Trim()
$taskPinnedDotNet = ($taskLock.tools | Where-Object name -eq 'dotnet').version
if ($taskDotNetVersion -cne $taskPinnedDotNet) { throw '.NET SDK version mismatch' }
$taskProvisionMS = $taskTimer.ElapsedMilliseconds
$taskJob = Join-Path $taskWork 'job'
$taskReportPath = Join-Path $taskJob 'bootstrap.json'
$taskOldLocation = Get-Location
try {
    Set-Location (Join-Path $Repo 'go')
    & $taskExecutables['go'] run ./cmd/remotebundle bootstrap -repo $Repo -out $taskJob -cache $Cache `
        -manifest $Manifest -sha256 $ManifestSHA256 -trust $Trust -identity $Identity -role $Role `
        -7z $taskExecutables['7z'] -age $taskExecutables['age']
    if ($LASTEXITCODE) {
        # Disposable runners must retain the compiler failure in Actions logs.
        $taskBuildRoot = Join-Path $taskJob 'layout/native-builds'
        if (Test-Path -LiteralPath $taskBuildRoot) {
            foreach ($taskBuildLog in Get-ChildItem -LiteralPath $taskBuildRoot -Filter build.log -File -Recurse) {
                Get-Content -LiteralPath $taskBuildLog.FullName | Write-Output
            }
        }
        throw 'Bootstrap failed; compiler diagnostics are above'
    }
    $taskReport = Get-Content -LiteralPath $taskReportPath -Raw | ConvertFrom-Json
    $taskReport | Add-Member -NotePropertyName provision_ms -NotePropertyValue $taskProvisionMS
    $taskReport | Add-Member -NotePropertyName memory_bytes -NotePropertyValue ([long]$taskHardware.TotalVisibleMemorySize * 1024)
    $taskReport.free_disk_bytes = $taskDrive.AvailableFreeSpace
    $taskReport | Add-Member -NotePropertyName tool_lock_sha256 -NotePropertyValue (Get-FileHash -LiteralPath $ToolLock -Algorithm SHA256).Hash.ToLowerInvariant()
    $taskReport | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskReportPath -Encoding utf8NoBOM
    if ($Suite) {
        & $taskReport.acceptance suite -suite $Suite -workers 1 -timeout 45m -root $taskReport.root `
            -output (Join-Path $taskJob 'out') -rimgovernor $taskReport.controller
        if ($LASTEXITCODE) { throw "Suite failed ($LASTEXITCODE); retain the job report/evidence" }
    }
} finally {
    Set-Location $taskOldLocation
    # No image-name kills or orphan sweeping. Match the executable's directory
    # boundary to this fresh job, then stop only those PIDs. Workflow cancellation
    # must also run cleanup_remote.ps1 in an always() step.
    & (Join-Path $PSScriptRoot 'cleanup_remote.ps1') -Work $taskWork
}
