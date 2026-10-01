#!/usr/bin/env bash
# Compile-checks the native mod (Bridge + Runtime) without a RimWorld install,
# against public reference assemblies from NuGet: Krafs.Rimworld.Ref (game and
# Unity), Lib.Harmony, RimBridgeServer.Sdk and Newtonsoft.Json. For Linux cloud
# sessions; the output is never installed into a game. Windows builds keep
# using build_native_mod.ps1 against the real install.
#
# Usage: scripts/build_native_ref.sh [-p:SomeFixture=true ...]
# Extra arguments pass through to dotnet build (e.g. fixture switches).
set -euo pipefail

RIMWORLD_REF_VERSION="${RIMWORLD_REF_VERSION:-1.6.4871}"
HARMONY_VERSION="${HARMONY_VERSION:-2.3.6}"
RIMBRIDGE_SDK_VERSION="${RIMBRIDGE_SDK_VERSION:-2.0.0}"
NEWTONSOFT_VERSION="${NEWTONSOFT_VERSION:-13.0.3}"

repo="$(cd "$(dirname "$0")/.." && pwd)"
cache="$repo/.rimgovernor/native-ref"
out="$cache/out"

command -v dotnet >/dev/null || { echo "dotnet not found (apt-get install -y dotnet-sdk-8.0)" >&2; exit 1; }

fetch() { # id version -> extracted dir
  local id="$1" version="$2" dir="$cache/pkgs/$1.$2"
  if [ ! -d "$dir" ]; then
    mkdir -p "$cache/pkgs"
    curl -sSfL "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -o "$dir.nupkg"
    mkdir -p "$dir.tmp" && unzip -qo "$dir.nupkg" -d "$dir.tmp" && mv "$dir.tmp" "$dir" && rm -f "$dir.nupkg"
  fi
  echo "$dir"
}

managed="$(fetch krafs.rimworld.ref "$RIMWORLD_REF_VERSION")/ref/net472"
harmony="$(fetch lib.harmony "$HARMONY_VERSION")/lib/net472/0Harmony.dll"
sdk="$cache/rimbridge-sdk"
mkdir -p "$sdk"
cp "$(fetch rimbridgeserver.sdk "$RIMBRIDGE_SDK_VERSION")/lib/net472/RimBridgeServer.Sdk.dll" "$sdk/"
cp "$(fetch newtonsoft.json "$NEWTONSOFT_VERSION")/lib/net45/Newtonsoft.Json.dll" "$sdk/"

dotnet build "$repo/integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj" \
  -c Release -nologo -o "$out" \
  "-p:RimWorldManagedDir=$managed" "-p:HarmonyAssembly=$harmony" "-p:RimBridgeSdkDir=$sdk" "$@"
