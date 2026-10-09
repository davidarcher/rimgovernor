[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [datetime]$Since = (Get-Date).AddHours(-7)
)
# Records why a game process exited on a remote runner : the exit
# status from process-termination audit events (enabled early in
# the shard job), Application Error / WER events, crash dump inventory and
# Unity crash-folder text logs. Dumps stay on the runner: they hold process
# memory, which the public-artifact boundary never exports. Only text
# leaves, with runner paths reduced to [runner-path].
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $Out | Out-Null
$names = 'RimWorldWin64|rimgovernor|acceptance'
function Get-CleanText([string]$s) { ($s -replace '(?i)[a-z]:[\\/][^\s"<>]*', '[runner-path]') }
function Save([string]$name, [string[]]$lines) {
    $text = ($lines | ForEach-Object { Get-CleanText $_ }) -join "`n"
    if ($text.Length -gt 262144) { $text = $text.Substring($text.Length - 262144) }
    Set-Content -LiteralPath (Join-Path $Out $name) -Value $text -Encoding utf8
}
function Event-Lines($events) {
    foreach ($e in $events) {
        "=== $($e.TimeCreated.ToString('o')) $($e.LogName) id=$($e.Id) $($e.ProviderName)"
        $e.Message
    }
}

# Exit status of every governed process (Security 4689 carries it as hex).
$exits = @()
try {
    $exits = @(Get-WinEvent -FilterHashtable @{LogName='Security'; Id=4689; StartTime=$Since} -ErrorAction Stop |
        Where-Object { $_.Message -match $names })
} catch { $exits = @() }
$exitLines = foreach ($e in $exits) {
    $xml = [xml]$e.ToXml()
    $d = @{}; foreach ($n in $xml.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    "$($e.TimeCreated.ToString('o')) pid=$([Convert]::ToInt64($d.ProcessId, 16)) status=$($d.Status) $([IO.Path]::GetFileName($d.ProcessName))"
}
if (-not $exitLines) { $exitLines = @('no process-termination audit events (auditing unavailable or no governed process exited)') }
Save 'process-exits.txt' $exitLines

# Application Error (1000), WER (1001), hang (1002) and .NET runtime (1026).
$app = @()
try {
    $app = @(Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$Since; Level=1,2,3} -ErrorAction Stop |
        Select-Object -First 200)
} catch { $app = @() }
$crashes = @($app | Where-Object { $_.Id -in 1000,1001,1002,1026 -and $_.Message -match $names })
Save 'application-crashes.txt' (Event-Lines $crashes)
Save 'application-tail.txt' (Event-Lines ($app | Select-Object -First 50))

# Crash dumps and Unity crash folders: inventory everywhere, text logs only.
$roots = @("$env:LOCALAPPDATA\CrashDumps", "$env:ProgramData\Microsoft\Windows\WER\ReportArchive",
    "$env:ProgramData\Microsoft\Windows\WER\ReportQueue", "$env:LOCALAPPDATA\Microsoft\Windows\WER",
    $env:TEMP, "$env:USERPROFILE\AppData\LocalLow")
$inventory = @(); $logs = @()
foreach ($r in $roots | Where-Object { $_ -and (Test-Path -LiteralPath $_) }) {
    Get-ChildItem -LiteralPath $r -Recurse -File -Depth 4 -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $Since -and ($_.FullName -match $names -or $_.FullName -match '\\Crash_' -or $_.Extension -eq '.dmp') } |
        ForEach-Object {
            $inventory += "$($_.LastWriteTime.ToString('o')) $($_.Length) $($_.Directory.Name)/$($_.Name)"
            if ($_.Extension -in '.log','.txt','.wer' -and $_.Length -lt 1MB) {
                $logs += "=== $($_.Directory.Name)/$($_.Name)"
                $logs += Get-Content -LiteralPath $_.FullName -Tail 400 -ErrorAction SilentlyContinue
            }
        }
}
if (-not $inventory) { $inventory = @('no crash dumps or Unity crash folders') }
Save 'crash-files.txt' $inventory
Save 'crash-logs.txt' $logs
