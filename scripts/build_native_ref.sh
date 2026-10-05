#!/usr/bin/env bash
# Compile-checks the native mod (Bridge + Runtime) without a RimWorld install,
# against public reference assemblies from NuGet: Krafs.Rimworld.Ref (game and
# Unity) and Lib.Harmony; the vendored host (integrations/rimgovernor-host) builds from source. For Linux cloud
# sessions; the output is never installed into a game. Windows builds keep
# using build_native_mod.ps1 against the real install.
#
# Needs Go to generate the gitignored contracts/generated/protobuf/csharp/Defs.cs.
#
# Usage: scripts/build_native_ref.sh [-p:SomeFixture=true ...]
# Extra arguments pass through to dotnet build (e.g. fixture switches).
set -euo pipefail

RIMWORLD_REF_VERSION="${RIMWORLD_REF_VERSION:-1.6.4871}"
HARMONY_VERSION="${HARMONY_VERSION:-2.3.6}"

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

# Defs.cs (49 MB) is generated, not committed: build it with the pinned
# Grpc.Tools protoc (generatecsharp) when missing or older than defs.proto.
defs="$repo/contracts/generated/protobuf/csharp/Defs.cs"
if [ ! -f "$defs" ] || [ "$repo/contracts/proto/defs.proto" -nt "$defs" ]; then
  command -v go >/dev/null || { echo "go not found (needed to generate Defs.cs)" >&2; exit 1; }
  gen="$cache/protobuf-gen"
  rm -rf "$gen"
  go -C "$repo/go" run ./internal/protobufgen/cmd/generatecsharp --dotnet dotnet --output "$gen"
  rm -rf "$gen"
fi

dotnet build "$repo/integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj" \
  -c Release -nologo -o "$out" \
  "-p:RimWorldManagedDir=$managed" "-p:HarmonyAssembly=$harmony" "$@"
dotnet build "$repo/integrations/rimgovernor-host/src/Host/RimGovernor.Host.csproj" \
  -c Release -nologo -o "$out/host" \
  "-p:RimWorldManagedDir=$managed" "-p:HarmonyAssembly=$harmony"
