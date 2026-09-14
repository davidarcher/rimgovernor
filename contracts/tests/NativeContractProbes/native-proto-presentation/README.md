# Compiled presentation reads

This probe is now part of the consolidated `NativeContractProbes.csproj`; it no
longer has its own `.csproj`. Build the merged project, then run it with the
private Bridge DLL, Runtime directory, SDK directory, game managed directory and
Harmony directory:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-proto-presentation <args...>
```

Tests load the actual adapter, generated messages, SDK binder and normalizer. No
game state is created.

Camera reads follow the audited SDK `RimWorldState.DescribeCamera`: native driver
position, root/zoom sizes, configuration bounds, current zoom range and view rect.
The only reflection is the exact static boolean
`RimBridgeServer.RimBridgeCameraConfig.CameraZoomExtensionEnabled` property; absence
returns unavailable. It is the SDK's backing-field read, not its state-changing
zoom extension setter. Headless camera and selection return Failure.Unavailable.

Graphical selection projects all native Thing, Zone and Plan IDs/maps up to4096;
unsupported kinds, ambiguous IDs or unreadable facts refuse the whole reply.
No inspect strings or gizmos are evaluated. Optional fingerprint, inspect detail
and visible-gizmo count are absent; this read never creates a capture/CAS token.
Colonists preserve the SDK `SelectionCapabilityModule.ListColonists` default
currentMapOnly=false and use FreeColonistsSpawned across loaded maps. World caravan
and unspawned pawns are outside that roster. The bounded roster refuses above256.
Whole replies refuse above1MiB rather than sampling.

Compilation and protocol tests do not establish gameplay outcomes.

The Python `scripts/native_presentation_acceptance.py`/`container_scenario.py`
scenario this section once described (headless unavailability, actual colonists,
and a `--rendered` comparison of camera, selected-pawn facts and rosters with the
SDK) was removed with the rest of the Python acceptance toolchain in
[G01.13](https://github.com/davidarcher/rimgovernor/issues/33); equivalent Go
coverage is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38).
The fresh scenario establishes one loaded map only; it does not establish multi-map,
selected zone/plan, overflow, camera movement, input or capture acceptance.
