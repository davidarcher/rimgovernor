# Native pawn observation checks

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the private native package with
`go run ./internal/nativeaccept/cmd/acceptance setup -production -skip-binaries`
from `go/`, then build the merged project in Release. Run it
with:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -c Release -- native-pawn-observations <args...>
```

where `<args...>` is the compiled `RimGovernor.Bridge.dll`, licensed game Managed
directory, installed RimBridgeServer assembly directory, Harmony assembly
directory and the directory containing
`RimGovernor.Runtime.dll` (`Mods/RimGovernor/Assemblies`). The bridge is under
`Mods/RimGovernor/BridgeTools/RimGovernor`.

The executable loads the actual generated wire types and SDK binder. It checks
presence-sensitive intersecting filters, exact IDs, distance boundaries, query and
reply limits, detail defaults and opt-outs, missing tracker facts, and refusing
work-priority initialization. Work snapshots bind pawn, priority mode, work rows,
world identity, allowed area (including unrestricted) and timetable contents.
It makes no game-state or gameplay claim. Native
acceptance must additionally verify paused tick/context invariance, actual pawn
facts, dead-pawn scope, filter counts and complete typed readback in Docker.
