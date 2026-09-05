# Run from an elevated PowerShell if the Steam folder requires administrator access.
# .\launch.ps1              Install the compiled package and launch normally.
# .\launch.ps1 -QuickTest   Install and launch directly into a test colony.
# .\launch.ps1 -InstallOnly Install without launching.
[CmdletBinding()]
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [switch]$QuickTest,
    [switch]$InstallOnly
)
$ErrorActionPreference = 'Stop'
if (Get-Process RimWorldWin64 -ErrorAction SilentlyContinue) {
    throw 'Close RimWorld before installing.'
}
$source = Join-Path $PSScriptRoot 'tmp\package\RimBot'
$target = Join-Path $GamePath 'Mods\RimBot'
$executable = Join-Path $GamePath 'RimWorldWin64.exe'
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "RimWorld executable not found: $executable"
}
$files = @(
    'About\About.xml'
    '1.6\Assemblies\RimBot.dll'
    '1.6\Assemblies\Newtonsoft.Json.dll'
    '1.6\Defs\ColonyManager.xml'
)
# Check the entire package before changing the installed mod.
foreach ($file in $files) {
    if (!(Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "Compiled package is missing $file. Run .\build.ps1 first."
    }
}
$dll = [System.IO.File]::ReadAllBytes((Join-Path $source '1.6\Assemblies\RimBot.dll'))
if ([System.Text.Encoding]::ASCII.GetString($dll).Contains('GameSmoke')) {
    throw 'Refusing to install the test harness. Run .\build.ps1 to create a production package.'
}
foreach ($file in $files) {
    $from = Join-Path $source $file
    $to = Join-Path $target $file
    New-Item -ItemType Directory -Path (Split-Path $to) -Force | Out-Null
    Copy-Item -LiteralPath $from -Destination $to -Force
    if ((Get-FileHash -LiteralPath $from).Hash -ne (Get-FileHash -LiteralPath $to).Hash) {
        throw "Verification failed: $file"
    }
}
Write-Host 'RimBot installed and verified.'
if (!$InstallOnly) {
    $launch = @{ FilePath = $executable; WorkingDirectory = $GamePath }
    if ($QuickTest) { $launch.ArgumentList = '-quicktest' }
    # This is the visible game the user requested, not a background helper.
    Start-Process @launch
}
