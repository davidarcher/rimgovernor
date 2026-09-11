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

Native acceptance still needs graphical camera/selection comparisons and unchanged
view/ticks, headless unavailability with a real roster, selected Thing/Zone/Plan and
overflow cases. Compilation and protocol tests do not establish those outcomes.
