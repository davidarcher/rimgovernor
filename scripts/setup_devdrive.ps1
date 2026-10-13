#Requires -RunAsAdministrator
<#
  Creates a fixed 200 GB VHDX-backed Dev Drive (ReFS) mounted at D:, and a
  logon task that re-attaches it after every reboot. Idempotent: re-running
  skips whatever already exists. Does not move or touch any repository.
#>
[CmdletBinding()]
param(
    [string]$VhdPath = 'C:\DevDrive\dev.vhdx',
    [int]$SizeGB = 200,
    [char]$Letter = 'D',
    [string]$Label = 'DevDrive',
    [string]$TaskName = 'Mount DevDrive VHDX'
)
$ErrorActionPreference = 'Stop'

# Dev Drive needs Windows 11 23H2+ (Format-Volume -DevDrive).
if (-not (Get-Command Format-Volume).Parameters.ContainsKey('DevDrive')) {
    throw 'This Windows build has no Format-Volume -DevDrive; update Windows 11.'
}

$existing = Get-Volume -DriveLetter $Letter -ErrorAction SilentlyContinue
$mounted  = (Test-Path $VhdPath) -and (Get-DiskImage -ImagePath $VhdPath).Attached
if ($existing -and -not $mounted) {
    throw "$($Letter): is already in use by another volume. Pick a different -Letter."
}

# 1. Fixed VHDX (diskpart, so Hyper-V does not need to be installed).
if (-not (Test-Path $VhdPath)) {
    $free = (Get-Volume -DriveLetter (Split-Path $VhdPath -Qualifier)[0]).SizeRemaining / 1GB
    if ($free -lt ($SizeGB + 20)) { throw "Only $([int]$free) GB free; need $SizeGB GB plus headroom." }
    New-Item -ItemType Directory -Force (Split-Path $VhdPath) | Out-Null
    Write-Host "Creating fixed $SizeGB GB VHDX at $VhdPath (this takes a few minutes)..."
    $dp = New-TemporaryFile
    "create vdisk file=`"$VhdPath`" maximum=$($SizeGB * 1024) type=fixed" | Set-Content $dp
    diskpart /s $dp; if ($LASTEXITCODE) { throw 'diskpart create failed' }
    Remove-Item $dp
}

# 2. Attach, partition, format as Dev Drive.
if (-not $mounted) {
    Mount-DiskImage -ImagePath $VhdPath | Out-Null
    $disk = Get-DiskImage -ImagePath $VhdPath | Get-Disk
    if ($disk.PartitionStyle -eq 'RAW') {
        Initialize-Disk -Number $disk.Number -PartitionStyle GPT
        New-Partition -DiskNumber $disk.Number -UseMaximumSize -DriveLetter $Letter | Out-Null
        Format-Volume -DriveLetter $Letter -DevDrive -FileSystem ReFS `
            -NewFileSystemLabel $Label -Confirm:$false | Out-Null
    }
}

# 3. Re-attach at logon (a VHDX does not auto-mount).
if (-not (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -WindowStyle Hidden -Command `"Mount-DiskImage -ImagePath '$VhdPath' -ErrorAction SilentlyContinue`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $princ   = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $princ | Out-Null
}

# 4. Verify.
Write-Host ''
fsutil devdrv query
fsutil devdrv query "$($Letter):"
Get-Volume -DriveLetter $Letter | Format-List DriveLetter, FileSystemType, FileSystemLabel, Size, SizeRemaining
