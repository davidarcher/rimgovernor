[CmdletBinding()]
param(
    # The CSV the samples are appended to; the collect step copies it into the diagnostics upload.
    [string]$Out = 'C:\rg-sampler\thread-samples.csv',
    [int]$IntervalSeconds = 10,
    # Hang guard for the sampler itself; the workflow also stops it with the play step.
    [int]$MaxMinutes = 60,
    # Where the game's one-shot main-thread stack goes (see the hang capture below).
    [string]$StackOut = 'C:\rg-sampler\main-thread-stack.txt',
    # The game.log of the review's game; its [frames] lines are the proof the game thread is still coming round.
    [string]$GameLogGlob = 'C:\rg\fixture\job\layout\bridge\config-headless\*\game.log',
    [int]$HangSeconds = 90
)
# Samples where the game's CPU goes, for the colony review on a GPU-less runner: every IntervalSeconds, per thread of
# the RimWorld process, the CPU time used since the last sample, its state and wait reason and its name (the game
# names its main, job and render threads; lavapipe's workers show as llvmpipe), plus the machine's CPU load. It only
# reads the process table, so it cannot change what it measures. Rows: time,load,pid,tid,name,cpu_ms,state,wait.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ThreadNames {
    [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern int GetThreadDescription(IntPtr h, out IntPtr name);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    public static string Of(uint id) {
        var h = OpenThread(0x800, false, id); // THREAD_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "";
        try {
            IntPtr p;
            if (GetThreadDescription(h, out p) < 0 || p == IntPtr.Zero) return "";
            var s = Marshal.PtrToStringUni(p);
            LocalFree(p);
            return s ?? "";
        } finally { CloseHandle(h); }
    }
}
'@

# A pinned dotnet-stack (Microsoft's diagnostics tool, nuget.org) for one managed stack of a hung game thread. Best effort:
# without it the sampler still samples.
$stackTool = 'C:\rg-sampler\tools\dotnet-stack.exe'
try {
    if (-not (Test-Path -LiteralPath $stackTool)) {
        & dotnet tool install dotnet-stack --version 10.0.750501 --tool-path (Split-Path -Parent $stackTool) 2>&1 | Out-Null
    }
} catch { }

# Seconds since the game last logged a [frames] line (it logs one every ten seconds), or -1 when it never has.
function Get-FramesAge {
    $log = Get-ChildItem -Path $GameLogGlob -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return -1 }
    $stream = [IO.File]::Open($log.FullName, 'Open', 'Read', 'ReadWrite')
    try {
        $length = [Math]::Min($stream.Length, 262144)
        $null = $stream.Seek(-$length, 'End')
        $text = (New-Object IO.StreamReader($stream)).ReadToEnd()
    } finally { $stream.Dispose() }
    $last = [regex]::Matches($text, '(?m)^(\d\d):(\d\d):(\d\d)\s+INFO \[frames\] \d+ frames') | Select-Object -Last 1
    if (-not $last) { return -1 }
    $at = [TimeSpan]::new([int]$last.Groups[1].Value, [int]$last.Groups[2].Value, [int]$last.Groups[3].Value)
    $age = ((Get-Date).TimeOfDay - $at).TotalSeconds
    if ($age -lt 0) { $age += 86400 }
    return $age
}

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
'time,load,pid,tid,name,cpu_ms,state,wait' | Set-Content -LiteralPath $Out -Encoding ascii
$previous = @{}
$stackTaken = $false
$deadline = (Get-Date).AddMinutes($MaxMinutes)
while ((Get-Date) -lt $deadline) {
    $load = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    # One marker row per sample, so a quiet or missing game is still visible next to the machine's load.
    '{0:o},{1},0,0,sample,0,,' -f (Get-Date).ToUniversalTime(), $load | Add-Content -LiteralPath $Out -Encoding ascii
    $mainBusyMs = 0
    $gamePid = 0
    $rows = foreach ($process in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'RimWorld*' }) {
        foreach ($thread in $process.Threads) {
            $cpu = $thread.TotalProcessorTime.TotalMilliseconds
            $key = "$($process.Id):$($thread.Id)"
            $delta = $cpu - $(if ($previous.ContainsKey($key)) { $previous[$key] } else { 0 })
            $previous[$key] = $cpu
            # Idle threads are noise; keep any that used CPU or that are not simply waiting.
            if ($delta -lt 5 -and $thread.ThreadState -eq 'Wait') { continue }
            $name = [ThreadNames]::Of([uint32]$thread.Id) -replace ',', ';'
            if ($name -eq 'MainThread') { $mainBusyMs = $delta; $gamePid = $process.Id }
            $wait = if ($thread.ThreadState -eq 'Wait') { $thread.WaitReason } else { '' }
            '{0:o},{1},{2},{3},{4},{5:F0},{6},{7}' -f (Get-Date).ToUniversalTime(), $load, $process.Id, $thread.Id, $name, $delta, $thread.ThreadState, $wait
        }
    }
    if ($rows) { $rows | Add-Content -LiteralPath $Out -Encoding ascii }
    # A game thread that is busy yet has logged no frames line for HangSeconds is inside one call that does not return
    # (a hung tick, not a slow one): take its managed stack once, where it can be read after the runner is gone.
    if (-not $stackTaken -and $gamePid -ne 0 -and (Test-Path -LiteralPath $stackTool) -and $mainBusyMs -gt ($IntervalSeconds * 500)) {
        $age = Get-FramesAge
        if ($age -gt $HangSeconds) {
            $stackTaken = $true
            "{0:o} frames age {1:F0} s, MainThread {2:F0} ms busy; stack of pid {3}" -f (Get-Date).ToUniversalTime(), $age, $mainBusyMs, $gamePid | Set-Content -LiteralPath $StackOut -Encoding utf8
            try { & $stackTool report -p $gamePid 2>&1 | Add-Content -LiteralPath $StackOut -Encoding utf8 } catch { "stack capture failed: $_" | Add-Content -LiteralPath $StackOut -Encoding utf8 }
        }
    }
    Start-Sleep -Seconds $IntervalSeconds
}
