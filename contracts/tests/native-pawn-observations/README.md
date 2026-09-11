# Native pawn observation checks

Build the private native package with `scripts/build_native_mod.ps1`, then build
`NativePawnObservations.csproj` in Release. Run its net472 executable with these
arguments: the compiled `RimGovernor.Bridge.dll`, licensed game Managed directory,
installed RimBridgeServer assembly directory and Harmony assembly directory.

The executable loads the actual generated wire types and SDK binder. It checks
presence-sensitive intersecting filters, exact IDs, distance boundaries, query and
reply limits, detail defaults and opt-outs, missing tracker facts, and refusing
work-priority initialization. It makes no game-state or gameplay claim. Native
acceptance must additionally verify paused tick/context invariance, actual pawn
facts, dead-pawn scope, filter counts and complete typed readback in Docker.
