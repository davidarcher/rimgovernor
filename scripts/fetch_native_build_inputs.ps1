# Stages the three inputs scripts/build_native_mod.ps1 needs without an
# installed game, so CI (and any machine without RimWorld) can compile the
# native mod and the fixture projects:
#   <OutputRoot>/managed/*.dll        Krafs.Rimworld.Ref (publicised RimWorld
#                                     reference assemblies for the pinned game build)
#   <OutputRoot>/0Harmony.dll         Lib.Harmony, the version the workshop mod ships
#   <OutputRoot>/rimbridge/*.dll      RimBridgeServer release Assemblies (SDK + Newtonsoft.Json)
# Every download is pinned by version and SHA-256. Bump the pins together with
# the installed game, Harmony and RimBridgeServer versions so CI compiles
# against what the acceptance harnesses actually run.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$inputs = @(
    @{ Name = 'krafs.rimworld.ref.1.6.4871.nupkg'
       Url = 'https://api.nuget.org/v3-flatcontainer/krafs.rimworld.ref/1.6.4871/krafs.rimworld.ref.1.6.4871.nupkg'
       Sha256 = '81b168db70a9aceaeab293b1519bb695bc5167f4510966a45f7ff38cb236f54e'
       Entries = 'ref/net472/'; Destination = 'managed' },
    @{ Name = 'lib.harmony.2.4.1.nupkg'
       Url = 'https://api.nuget.org/v3-flatcontainer/lib.harmony/2.4.1/lib.harmony.2.4.1.nupkg'
       Sha256 = 'ff0183a1f27167f3543a0e095b4930a96c833ce783453e85a1e8a8ed33cd2f6d'
       Entries = 'lib/net472/'; Destination = '' },
    @{ Name = 'RimBridgeServer-2.1.1.zip'
       Url = 'https://github.com/pardeike/RimBridgeServer/releases/download/v2.1.1/RimBridgeServer.zip'
       Sha256 = '09b4c7ed5c17bd9c459b599e5216709afc05faa0d017ee524d1929f09c4808ce'
       Entries = 'RimBridgeServer/1.6/Assemblies/'; Destination = 'rimbridge' }
)
$root = [IO.Path]::GetFullPath($OutputRoot)
$downloads = Join-Path $root 'downloads'
New-Item -ItemType Directory -Path $downloads -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($input in $inputs) {
    $archive = Join-Path $downloads $input.Name
    if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $input.Sha256) {
        Invoke-WebRequest -Uri $input.Url -OutFile $archive -UseBasicParsing
    }
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $input.Sha256) { throw "SHA-256 mismatch for $($input.Url): expected $($input.Sha256), got $actual" }
    $destination = Join-Path $root $input.Destination
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $extracted = 0
        foreach ($entry in $zip.Entries) {
            if (-not $entry.FullName.StartsWith($input.Entries) -or -not $entry.Name.EndsWith('.dll')) { continue }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $destination $entry.Name), $true)
            $extracted++
        }
    } finally { $zip.Dispose() }
    if (-not $extracted) { throw "No assemblies under $($input.Entries) in $($input.Name)" }
}
foreach ($required in @('managed/Assembly-CSharp.dll', '0Harmony.dll', 'rimbridge/RimBridgeServer.Sdk.dll', 'rimbridge/Newtonsoft.Json.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Staged native build input missing: $required" }
}
Write-Output $root.Replace('\', '/')
