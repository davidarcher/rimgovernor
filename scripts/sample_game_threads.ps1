[CmdletBinding()]
param(
    # The CSV the samples are appended to; the collect step copies it into the diagnostics upload.
    [string]$Out = 'C:\rg-sampler\thread-samples.csv',
    [int]$IntervalSeconds = 10,
    # Hang guard for the sampler itself; the workflow also stops it with the play step.
    [int]$MaxMinutes = 60
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

New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
'time,load,pid,tid,name,cpu_ms,state,wait' | Set-Content -LiteralPath $Out -Encoding ascii
$previous = @{}
$deadline = (Get-Date).AddMinutes($MaxMinutes)
while ((Get-Date) -lt $deadline) {
    $load = (Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average
    # One marker row per sample, so a quiet or missing game is still visible next to the machine's load.
    '{0:o},{1},0,0,sample,0,,' -f (Get-Date).ToUniversalTime(), $load | Add-Content -LiteralPath $Out -Encoding ascii
    $rows = foreach ($process in Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like 'RimWorld*' }) {
        foreach ($thread in $process.Threads) {
            $cpu = $thread.TotalProcessorTime.TotalMilliseconds
            $key = "$($process.Id):$($thread.Id)"
            $delta = $cpu - $(if ($previous.ContainsKey($key)) { $previous[$key] } else { 0 })
            $previous[$key] = $cpu
            # Idle threads are noise; keep any that used CPU or that are not simply waiting.
            if ($delta -lt 5 -and $thread.ThreadState -eq 'Wait') { continue }
            $name = [ThreadNames]::Of([uint32]$thread.Id) -replace ',', ';'
            $wait = if ($thread.ThreadState -eq 'Wait') { $thread.WaitReason } else { '' }
            '{0:o},{1},{2},{3},{4},{5:F0},{6},{7}' -f (Get-Date).ToUniversalTime(), $load, $process.Id, $thread.Id, $name, $delta, $thread.ThreadState, $wait
        }
    }
    if ($rows) { $rows | Add-Content -LiteralPath $Out -Encoding ascii }
    Start-Sleep -Seconds $IntervalSeconds
}
