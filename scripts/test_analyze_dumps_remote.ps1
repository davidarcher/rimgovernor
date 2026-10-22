param([Parameter(Mandatory)][string]$TestRoot)
# Exercise the real analyzer orchestration without a licensed runtime or a crash.
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force "$TestRoot/out", "$TestRoot/config", "$TestRoot/game with spaces", "$TestRoot/tools" | Out-Null
$game = Join-Path $TestRoot 'game with spaces'
foreach ($file in @("$game/coreclr.dll", "$game/mscordaccore.dll", "$TestRoot/tools/dotnet-dump.exe", "$TestRoot/out/coreclr-123.dmp")) {
    Set-Content -LiteralPath $file -Value 'test fixture'
}
@{root=$TestRoot} | ConvertTo-Json | Set-Content "$TestRoot/bootstrap.json"
@{games=@{'rimgovernor-trial'=@{launchMode='DirectPath';target="$game/RimWorld.exe"}}} | ConvertTo-Json -Depth 5 | Set-Content "$TestRoot/config/config.json"
@(@{output="$TestRoot/out";bootstrap="$TestRoot/bootstrap.json"}) | ConvertTo-Json | Set-Content "$TestRoot/jobs.json"
$calls = [Collections.Generic.List[object]]::new()
function Start-Process {
    param($FilePath, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, $WindowStyle, [switch]$PassThru)
    $calls.Add($ArgumentList)
    if ($WindowStyle -ne 'Hidden') { throw 'Analyzer must be hidden' }
    Set-Content $RedirectStandardOutput @('Loading core dump: private path', 'analysis output')
    Set-Content $RedirectStandardError ''
    $p = [pscustomobject]@{ ExitCode=0; Id=123 }
    $p | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($milliseconds) return $true }
    return $p
}
& "$PSScriptRoot/analyze_dumps_remote.ps1" -Jobs "$TestRoot/jobs.json" -Tools "$TestRoot/tools"
if ($calls.Count -ne 6) { throw 'Lost diagnostic commands' }
foreach ($call in $calls) {
    if ($call[2] -ne '-c' -or $call[3] -ne "`"setclrpath \`"$game\`"`"" -or $call[4] -ne '-c') {
        throw "Matching DAC selection must precede every command, preserving spaces: $call"
    }
}
$text = Get-Content "$TestRoot/out/coreclr-123.analysis.txt" -Raw
if ($text -notmatch 'coreclr.dll file_version=.*sha256=[a-f0-9]{64}' -or $text -notmatch 'mscordaccore.dll file_version=.*sha256=[a-f0-9]{64}') { throw 'Runtime identity missing' }
Remove-Item -LiteralPath "$game/mscordaccore.dll"
$calls = [Collections.Generic.List[object]]::new()
& "$PSScriptRoot/analyze_dumps_remote.ps1" -Jobs "$TestRoot/jobs.json" -Tools "$TestRoot/tools"
$text = Get-Content "$TestRoot/out/coreclr-123.analysis.txt" -Raw
if ($calls.Count -ne 2 -or ($calls | ForEach-Object { $_ -join ' ' }) -match 'setclrpath|clrstack') { throw 'Missing DAC must not select another runtime' }
if ($text -notmatch 'DAC selection failed:' -or $text -notmatch 'Managed analysis unavailable') { throw 'Missing DAC was hidden' }
'Dump analysis matching-runtime and missing-DAC cases passed'
