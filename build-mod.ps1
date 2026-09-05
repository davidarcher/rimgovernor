param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\RimWorld',
    [string]$FrameworkPath = '',
    [switch]$Live,
    [string]$Model = 'qwen/qwen3.5-9b'
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'tmp\dotnet-home'
$localSdk = Join-Path $PSScriptRoot 'tmp\dotnet\dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
if (!$FrameworkPath) {
    $localRefs = Join-Path $PSScriptRoot 'tmp\net472\build\.NETFramework\v4.7.2'
    if (Test-Path -LiteralPath $localRefs) { $FrameworkPath = $localRefs }
}
$flags = @('-c','Release','-p:CI=true',"-p:RimWorldManagedPath=$GamePath\RimWorldWin64_Data\Managed")
if ($FrameworkPath) { $flags += "-p:FrameworkPathOverride=$FrameworkPath" }
& $sdk build 'RimBot\RimBot.csproj' @flags --no-restore
if ($LASTEXITCODE) { throw 'Mod build failed.' }
& $sdk build 'tests\RimBot.Tests.csproj' @flags --no-restore
if ($LASTEXITCODE) { throw 'Test build failed.' }
& '.\tests\bin\Release\RimBot.Tests.exe'
if ($LASTEXITCODE) { throw 'Regression checks failed.' }
if ($Live) {
    & '.\tests\bin\Release\RimBot.Tests.exe' --live $Model
    if ($LASTEXITCODE) { throw 'LM Studio smoke test failed.' }
}
$package = Join-Path $PSScriptRoot 'tmp\package\RimBot'
New-Item -ItemType Directory -Path "$package\About","$package\1.6\Assemblies","$package\1.6\Defs" -Force | Out-Null
Copy-Item 'About\About.xml' "$package\About\About.xml"
Copy-Item 'RimBot\bin\Release\RimBot.dll','RimBot\bin\Release\Newtonsoft.Json.dll' "$package\1.6\Assemblies"
Copy-Item '1.6\Defs\ColonyManager.xml' "$package\1.6\Defs\ColonyManager.xml"
Compress-Archive -Path $package -DestinationPath 'tmp\RimBot-ColonyManager.zip' -Force
Write-Output 'Ready: tmp\RimBot-ColonyManager.zip. Build never installs or starts RimWorld.'
