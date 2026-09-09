param([switch]$Install,[string]$RimWorld='C:\Program Files (x86)\Steam\steamapps\common\RimWorld',[string]$DotNet='')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path $PSScriptRoot -Parent
$taskSource=Join-Path $taskRepo 'integrations/headless-rim'
$taskDotnet=if($DotNet){$DotNet}else{Join-Path $taskRepo 'tmp/dotnet/dotnet.exe'}
$env:DOTNET_ROOT=Split-Path $taskDotnet -Parent
& $taskDotnet build "$taskSource/src/HeadlessRim.csproj" -c Release -v quiet "-p:RimWorldManagedDir=$RimWorld/RimWorldWin64_Data/Managed"
if($LASTEXITCODE -ne 0){throw 'Headless patch build failed'}
if($Install){
 if(Get-Process RimWorldWin64 -ErrorAction SilentlyContinue){throw 'Close RimWorld before installing the headless DLL'}
 $taskDestination=Join-Path $RimWorld 'Mods/RimBotHeadless'
 New-Item -ItemType Directory -Force $taskDestination | Out-Null
 Copy-Item -LiteralPath "$taskSource/Assemblies","$taskSource/About","$taskSource/LICENSE" -Destination $taskDestination -Recurse -Force
}
