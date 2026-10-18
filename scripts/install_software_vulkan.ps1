[CmdletBinding()]
param(
    # Where the loader's driver (lavapipe) is unpacked; kept for the job, removed with the runner.
    [string]$Dir = 'C:\rg\vulkan'
)
# Gives a GPU-less hosted runner a Vulkan device so the colony review's rendered game (RimWorld 1.7 starts through
# Vulkan and dies without a vulkan-1.dll: "No Vulkan loader found") can start. Two pinned, digest-checked downloads:
#   - the Khronos/LunarG loader vulkan-1.dll (1.3.204.1), put in System32 where the game looks;
#   - Mesa's lavapipe CPU driver (mesa-dist-win release-msvc), registered as the loader's only driver through the
#     Khronos registry key, which every child process sees (no environment variable to scrub).
# Neither file touches the decrypted game bundle or the identity. Run before the play step, in the review job only.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$loader = @{
    Url    = 'https://github.com/GpuZelenograd/memtest_vulkan/releases/download/support/vulkan-1.dll'
    Sha256 = '3c3aa8ea119e69f4d245637efef46232ad56f062e87cdad738a4e0dabd8d9687'
}
$mesa = @{
    Url    = 'https://github.com/pal1000/mesa-dist-win/releases/download/26.2.4/mesa3d-26.2.4-release-msvc.7z'
    Sha256 = '351fc8c8b695878ffb3eaa044b3ead08672a48b1a045e3c3e3975811df0f6695'
}

function Get-Verified($Item, $Path) {
    Invoke-WebRequest -Uri $Item.Url -OutFile $Path
    $got = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($got -cne $Item.Sha256) { throw "Checksum mismatch for $($Item.Url): $got" }
}

New-Item -ItemType Directory -Force $Dir | Out-Null
$loaderFile = Join-Path $Dir 'vulkan-1.dll'
$archive = Join-Path $Dir 'mesa.7z'
Get-Verified $loader $loaderFile
Get-Verified $mesa $archive

# The hosted image ships 7-Zip; take only the lavapipe driver (any folder in the archive, flattened).
$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip)) { throw "7-Zip not found at $sevenZip" }
& $sevenZip e -y "-o$Dir" $archive 'vulkan_lvp.dll' -r
if ($LASTEXITCODE) { throw '7z extract failed' }
Remove-Item -LiteralPath $archive
$driver = Join-Path $Dir 'vulkan_lvp.dll'
if (-not (Test-Path -LiteralPath $driver)) { throw 'vulkan_lvp.dll is not in the Mesa archive' }

# Our own manifest: the archive's names and relative paths vary by release.
$manifest = Join-Path $Dir 'lvp_icd.json'
$json = @{ file_format_version = '1.0.0'; ICD = @{ library_path = $driver; api_version = '1.3.0' } } | ConvertTo-Json
[IO.File]::WriteAllText($manifest, $json)

Copy-Item -LiteralPath $loaderFile -Destination (Join-Path $env:SystemRoot 'System32\vulkan-1.dll') -Force
$key = 'HKLM:\SOFTWARE\Khronos\Vulkan\Drivers'
New-Item -Path $key -Force | Out-Null
New-ItemProperty -Path $key -Name $manifest -PropertyType DWord -Value 0 -Force | Out-Null

Write-Host "software Vulkan: loader $((Get-Item $loaderFile).VersionInfo.FileVersion), driver $driver"
