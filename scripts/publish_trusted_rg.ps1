[CmdletBinding()]
param(
    # The trusted (workflow_sha) checkout; rg.exe is built from here and never from the tested checkout.
    [Parameter(Mandatory)][string]$Trusted,
    # The gate's evidence directory (bundle.json and the inventory it pinned).
    [string]$Evidence = 'C:\rg\evidence',
    # Where rg.exe is published.
    [string]$Out = 'C:\rg',
    # Private scratch for the ciphertext and the decrypted reference assemblies; removed before this script returns.
    [string]$Work = 'C:\gamerefs'
)
# Publishes the trusted tool host (tools/rg) as a self-contained win-x64 single file.
#
# brain/Game.props builds Bridge, Simulation, Observation and Runtime empty unless RimWorldDir holds the game's
# Assembly-CSharp.dll, UnityStubs.dll and Waylith.dll, and tools/rg reaches them through Acceptance and Acceptance.Cases,
# so the compile needs those three assemblies. They exist only in the encrypted release bundle, and the bundle reader is
# itself inside rg.exe, so this script reads the bundle with the same pinned age and 7-Zip tools bootstrap_remote.ps1
# uses: it downloads the digest-checked ciphertext parts, decrypts, extracts ONLY those three files and checks them
# against the pinned inventory. They are deleted before this script returns and are never inside a published artifact.
#
# Run only after the authorization gate, on a runner in the remote-acceptance environment, with
# REMOTE_BUNDLE_IDENTITY (the age identity) and GH_TOKEN in the environment of this step alone.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gameFiles = @('Assembly-CSharp.dll', 'UnityStubs.dll', 'Waylith.dll')
$work = [IO.Path]::GetFullPath($Work).TrimEnd('\')
if ($work -eq [IO.Path]::GetPathRoot($work).TrimEnd('\')) { throw 'Refusing a drive-root work directory' }
if (Test-Path -LiteralPath $work) { throw 'Work must be a new private directory' }
if ([string]::IsNullOrEmpty($env:REMOTE_BUNDLE_IDENTITY)) { throw 'REMOTE_BUNDLE_IDENTITY is required' }
if ($env:REMOTE_BUNDLE_SHA256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Pinned bundle digest is required' }

function Read-JSON($Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json }
function Get-Digest($Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

New-Item -ItemType Directory -Path $work | Out-Null
$tools = Join-Path $work 'tools'
$cipherDir = Join-Path $work 'ciphertext'
$game = Join-Path $work 'game'
$identity = Join-Path $work 'identity.txt'
New-Item -ItemType Directory -Path $tools, $cipherDir | Out-Null
try {
    # The manifest was digest-checked by the gate; check again so this script stands alone.
    $manifestPath = Join-Path $Evidence 'bundle.json'
    if ((Get-Digest $manifestPath) -cne $env:REMOTE_BUNDLE_SHA256) { throw 'Bundle manifest digest mismatch' }
    $manifest = Read-JSON $manifestPath
    if ($manifest.origin.repository -cne $env:GITHUB_REPOSITORY -or $manifest.encryption.format -cne 'age-v1') {
        throw 'Unsupported encrypted bundle origin'
    }
    $inventoryPath = Join-Path $Evidence $manifest.inventory.path
    if ((Get-Digest $inventoryPath) -cne $manifest.inventory.sha256) { throw 'Inventory digest mismatch' }
    $inventory = Read-JSON $inventoryPath

    # The pinned age and 7-Zip (the dotnet SDK comes from setup-dotnet).
    $lock = Read-JSON "$PSScriptRoot/remote-tools.windows.json"
    if ($lock.schema_version -ne 1) { throw 'Unsupported tool lock version' }
    $exe = @{}
    foreach ($tool in @($lock.tools | Where-Object name -In @('age', '7z'))) {
        if ($tool.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $tool.url -notmatch '^https://' -or $tool.format -notin @('zip', 'exe')) {
            throw "Invalid pinned tool: $($tool.name)"
        }
        $download = Join-Path $tools ($tool.name + '.download')
        Invoke-WebRequest -Uri $tool.url -OutFile $download
        if ((Get-Digest $download) -cne $tool.sha256) { throw "Tool archive checksum mismatch: $($tool.name)" }
        $install = Join-Path $tools $tool.name
        New-Item -ItemType Directory -Path $install | Out-Null
        $path = [IO.Path]::GetFullPath((Join-Path $install $tool.executable))
        if (-not $path.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Tool executable escapes installation directory' }
        if ($tool.format -eq 'zip') { Expand-Archive -LiteralPath $download -DestinationPath $install }
        else { Copy-Item -LiteralPath $download -Destination $path }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Pinned tool executable missing' }
        $exe[$tool.name] = $path
    }
    if (-not ($exe.ContainsKey('age') -and $exe.ContainsKey('7z'))) { throw 'Tool lock must pin age and 7z' }

    # Ciphertext parts: size and digest from the pinned manifest, fetched by release asset id (the repository is public).
    $cipher = Join-Path $work 'payload.age'
    $output = [IO.File]::Open($cipher, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        foreach ($part in @($manifest.parts)) {
            if ($part.name -cnotmatch '^[A-Za-z0-9._-]+$' -or [long]$part.asset_id -le 0) { throw 'Invalid bundle part' }
            $file = Join-Path $cipherDir $part.name
            Invoke-WebRequest -Uri "https://api.github.com/repos/$($manifest.origin.repository)/releases/assets/$($part.asset_id)" `
                -Headers @{Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/octet-stream'} -OutFile $file
            if ((Get-Item -LiteralPath $file).Length -ne [long]$part.bytes -or (Get-Digest $file) -cne $part.sha256) {
                throw "Bundle part $($part.name) size or digest mismatch"
            }
            $input = [IO.File]::OpenRead($file)
            try { $input.CopyTo($output) } finally { $input.Dispose() }
            Remove-Item -LiteralPath $file
        }
    } finally { $output.Dispose() }

    # Decrypt, then extract only the named assemblies; 7-Zip ignores everything else in the archive.
    [IO.File]::WriteAllText($identity, $env:REMOTE_BUNDLE_IDENTITY)
    $archive = Join-Path $work 'payload.7z'
    & $exe['age'] -d -i $identity -o $archive $cipher
    if ($LASTEXITCODE) { throw 'age decrypt failed' }
    Remove-Item -LiteralPath $identity, $cipher
    & $exe['7z'] x -y "-o$work\extract" $archive @($gameFiles | ForEach-Object { "game/$_" })
    if ($LASTEXITCODE) { throw '7z extract failed' }
    Remove-Item -LiteralPath $archive

    New-Item -ItemType Directory -Path $game | Out-Null
    foreach ($name in $gameFiles) {
        $want = @($inventory.files | Where-Object path -CEQ "game/$name")
        if ($want.Count -ne 1) { throw "Inventory does not list game/$name" }
        $extracted = Join-Path "$work\extract\game" $name
        if (-not (Test-Path -LiteralPath $extracted -PathType Leaf) -or (Get-Digest $extracted) -cne $want[0].sha256) {
            throw "game/$name does not match the pinned inventory"
        }
        Move-Item -LiteralPath $extracted -Destination (Join-Path $game $name)
    }

    # Built from the trusted checkout only. A global property reaches every project reference.
    $rimWorldDir = $game.Replace('\', '/')
    & dotnet publish (Join-Path $Trusted 'tools/rg/rg.csproj') -c Release -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:RimWorldDir=$rimWorldDir" -o $Out
    if ($LASTEXITCODE) { throw 'dotnet publish failed' }
    if (-not (Test-Path -LiteralPath (Join-Path $Out 'rg.exe'))) { throw 'rg.exe was not published' }
} finally {
    # The identity and every decrypted byte go, pass or fail.
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
}
