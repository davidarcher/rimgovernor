[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Jobs,
    [string]$Tools = 'C:\rg\dotnet-dump',
    [int]$CommandSeconds = 120
)
# Turns each CoreCLR crash minidump a case launch left (coreclr-<pid>.dmp in a
# case output, #3190) into coreclr-<pid>.analysis.txt beside it. Dumps hold
# process memory and stay on the runner; the exporter skips .dmp and carries
# the text under its usual scan. The analysis loads the DAC the game ships
# next to its coreclr.dll, so it runs before cleanup removes the layout.
# dotnet-dump is installed only when a dump exists. Every failure is written
# into the analysis or the job log; this script never fails the shard.
$ErrorActionPreference = 'Continue'
$version = '10.0.750501'
# One analyze per command: a command that errors (no exception on the thread,
# no crash info) ends dotnet-dump's command chain, so chaining would drop the rest.
$commands = @('eeversion', 'threads', 'clrthreads', 'clrstack -all -f', 'pe -nested', 'modules')

# Runner paths shrink to their file name, which keeps module and source names the
# exporter's [runner-path] scrub would erase. A path runs to the end of the line
# (modules, which may contain spaces), a closing bracket or " @ " (a source line).
function Get-Leaf([string]$s) { [regex]::Replace($s, '(?i)[a-z]:[\\/][^"<>\]]*?(?=\s+@\s|[\]"<>]|\s*$)', { param($m) ($m.Value -split '[\\/]')[-1] }) }

try {
    $outputs = @((Get-Content -LiteralPath $Jobs -Raw | ConvertFrom-Json) | ForEach-Object output)
    $dumps = @($outputs | Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -File -Filter 'coreclr-*.dmp' -ErrorAction SilentlyContinue })
    if (-not $dumps) { 'No CoreCLR crash dump in this shard.'; return }

    $tool = Join-Path $Tools 'dotnet-dump.exe'
    if (-not (Test-Path -LiteralPath $tool)) {
        & dotnet tool install dotnet-dump --version $version --tool-path $Tools 2>&1 | Out-Host
        if (-not (Test-Path -LiteralPath $tool)) { "dotnet-dump $version did not install; $($dumps.Count) dump(s) left unanalyzed."; return }
    }

    foreach ($dump in $dumps) {
        $lines = @("dotnet-dump $version analyze of $($dump.Name) ($($dump.Length) bytes, written $($dump.LastWriteTimeUtc.ToString('o')))")
        foreach ($c in $commands) {
            $lines += '', "=== $c"
            $log = Join-Path ([IO.Path]::GetTempPath()) "dump-analyze-$([guid]::NewGuid().ToString('N')).txt"
            $p = Start-Process -FilePath $tool -ArgumentList @('analyze', "`"$($dump.FullName)`"", '-c', "`"$c`"", '-c', 'exit') `
                -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
            if (-not $p.WaitForExit($CommandSeconds * 1000)) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
                $lines += "(timed out after $CommandSeconds s)"
            } elseif ($p.ExitCode) {
                $lines += "(exit code $($p.ExitCode))"
            }
            # The first stdout line repeats the dump's runner path ("Loading core dump: ...").
            $out = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Select-Object -Skip 1) + @(Get-Content -LiteralPath "$log.err" -ErrorAction SilentlyContinue)
            $lines += @($out | ForEach-Object { Get-Leaf $_ })
            Remove-Item -LiteralPath $log, "$log.err" -ErrorAction SilentlyContinue
        }
        $target = Join-Path $dump.DirectoryName ($dump.BaseName + '.analysis.txt')
        Set-Content -LiteralPath $target -Value $lines -Encoding utf8
        "Analyzed $($dump.Name) into $($dump.BaseName).analysis.txt"
    }
} catch {
    "Dump analysis failed: $($_.Exception.Message)"
}
