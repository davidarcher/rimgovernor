[CmdletBinding()]
param([Parameter(Mandatory)][string]$Work)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($Work).TrimEnd('\')
if ($taskRoot -eq [IO.Path]::GetPathRoot($taskRoot).TrimEnd('\')) { throw 'Refusing drive-root cleanup' }
if (-not (Test-Path -LiteralPath $taskRoot)) { return }
$taskMarker = Join-Path $taskRoot '.remote-acceptance-job'
if (-not (Test-Path -LiteralPath $taskMarker -PathType Leaf) -or
    (Get-Content -LiteralPath $taskMarker -Raw).TrimEnd('\') -cne $taskRoot) {
    throw 'Cleanup requires the ownership marker created by bootstrap_remote.ps1'
}
$taskPrefix = $taskRoot + '\'
Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @('RimWorldWin64.exe', 'gabs.exe', 'rimgovernor.exe', 'acceptance.exe') -and
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($taskPrefix, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
