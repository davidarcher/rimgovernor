# Compiled presentation reads

Build `NativeProtoPresentation.csproj` with `-p:RestoreLockedMode=true`. Run its
net472 executable with private Bridge DLL, Runtime directory, SDK directory, game
managed directory and Harmony directory. Tests load the actual adapter, generated
messages, SDK binder and normalizer. No game state is created.

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

The script `scripts/native_presentation_acceptance.py`, launched by
`container_scenario.py`, verifies headless unavailability and actual colonists, or
uses `--rendered` to compare camera, exact selected-pawn facts and rosters with the
SDK. Selecting one observed pawn is explicit scenario setup. The subsequent read
interval checks unchanged paused context, native camera and selection. Reports
retain package hashes, discovery/detail and raw requests/replies, including failures.
The fresh scenario establishes one loaded map only; it does not establish multi-map,
selected zone/plan, overflow, camera movement, input or capture acceptance.
