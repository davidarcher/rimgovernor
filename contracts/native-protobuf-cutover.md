# Current native Protobuf capabilities

The current native source exports these fixed Protobuf capabilities. Each takes
one `request` ProtoJSON string through the SDK's raw object parameter and returns
one `payload` ProtoJSON string plus host operation metadata.

| Native capability | Canonical method | Production source |
|---|---|---|
| rimgovernor/lifecycle_read_identity | rimgovernor.lifecycle.v1.Lifecycle/ReadIdentity | Protocol/ProtoIdentityTools.cs |
| rimgovernor/authority_read_status | rimgovernor.authority.v1.Authority/ReadStatus | Protocol/NativeAuthorityTools.cs |
| rimgovernor/placement_preview | rimgovernor.placement.v1.Placement/Preview | PlacementPreviewsTool.cs, Protocol/PlacementProtocol.cs, PlacementPreviewOperation.cs |
| rimgovernor/authority_control | rimgovernor.authority.v1.Authority/Control | Protocol/NativeAuthorityControlTools.cs |
| rimgovernor/observations_read_status | rimgovernor.observations.v1.Observations/ReadStatus | Protocol/NativeObservationTools.cs |
| rimgovernor/observations_get_cells | rimgovernor.observations.v1.Observations/GetCells | Protocol/NativeObservationTools.cs |
| rimgovernor/observations_list_buildings | rimgovernor.observations.v1.Observations/ListBuildings | Protocol/NativeBuildingObservationTools.cs |
| rimgovernor/observations_list_supplies | rimgovernor.observations.v1.Observations/ListSupplies | Protocol/NativeSuppliesObservationTools.cs |
| rimgovernor/operations_preview | rimgovernor.operations.v1.Operations/Preview | Protocol/NativeOperationTools.cs |
| rimgovernor/operations_execute | rimgovernor.operations.v1.Operations/Execute | Protocol/NativeOperationTools.cs |
| rimgovernor/receipts_lookup | rimgovernor.receipts.v1.Attempts/Lookup | Protocol/NativeOperationTools.cs |
| rimgovernor/receipts_observe_progress | rimgovernor.receipts.v1.Attempts/ObserveProgress | Protocol/NativeOperationTools.cs |
| rimgovernor/clock_start | rimgovernor.clock.v1.Clock/Start | Protocol/NativeClockTools.cs |
| rimgovernor/clock_renew | rimgovernor.clock.v1.Clock/Renew | Protocol/NativeClockTools.cs |
| rimgovernor/clock_change_speed | rimgovernor.clock.v1.Clock/ChangeSpeed | Protocol/NativeClockTools.cs |
| rimgovernor/clock_pause | rimgovernor.clock.v1.Clock/Pause | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_status | rimgovernor.clock.v1.Clock/ReadStatus | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_events | rimgovernor.clock.v1.Clock/ReadEvents | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_attempt | rimgovernor.clock.v1.Clock/ReadAttempt | Protocol/NativeClockTools.cs |

Paths are under `integrations/rimgovernor-native/src/Bridge`. The shared
`Protocol/ProtoBoundary.cs` validates original outer arguments and uses official
Protobuf parsing/formatting. Generated compile inputs come from
`contracts/generated/protobuf/csharp`; their fingerprints and project membership
are included in `domain-inventory.json.native_surface.source_baseline`.

`Operations/Preview` and `Operations/Execute` implement ordinary `PlaceBuilding`
only; other command variants return unsupported. Their presence does not advertise
the entire operations schema as implemented. `Protocol/NativeConstruction.cs`
owns native placement and tracked construction transitions;
`Protocol/NativeConstructionCausality.cs` checks exact factory/spawn attribution.
`Protocol/NativeConstructionHookSet.cs` verifies each required live patch before
admission. `Protocol/NativeOperationEnvelope.cs` checks receipt size before
immutable finalization; unrepresentable evidence produces bounded uncertainty.
`Protocol/NativeAttemptLedger.cs` owns the unsaved per-load attempt ledger, replay
and uncertainty. Receipt admission is distinct from observed pawn completion.

Authority control belongs to the authenticated host's explicit player-control
capability. A direction value does not authenticate its caller. The runtime's
`Runtime/Control/NativeAuthorityHooks.cs` verifies native invalidation hooks and
initializes `Runtime/Persistence/NativeControlAuthority.cs` on the game thread;
no hook acquires authority, and ordinary pause does not imply Manual. These two
paths are relative to `integrations/rimgovernor-native/src`, outside Bridge.

The private `scripts/fixtures/GuardedConstructionFixture.cs` supplies
`test/guarded_construction_prepare` and `test/guarded_construction_control` under
the fixture compile condition. Both require exclusion from production discovery.

`home/placement_previews` has been removed, with no alias. Its historical Python
consumer row remains in the 97-entry domain inventory because that inventory
records the porting baseline, not current installed discovery. `home/colony_identity`
and `home/status` remain separately exported old surfaces pending their consumers'
cutover; they are not aliases implemented by the new adapters. All other retained
production exports keep their current source ownership entries.

`Observations/ListBuildings` returns complete bounded building, blueprint and frame
rows, including individual walls and construction work/resources. Entity CAS,
settings, bills, inspect detail and network/service/thermal facts remain explicitly
unsupported or incomplete. Collection and geometry limits refuse incomplete facts.

`Observations/ListSupplies` defaults to haulable stock, definitions with usable
colony units, and held stock included. Every returned definition retains its full
filtered units and ownership buckets, including fogged and other-faction stock.
Exact definition/region filters and complete bounded item, holder and corpse lists
replace samples. Worn gear, orbital stock and delivered construction materials are
excluded. Disabling held stock leaves held counters unknown with explicit issues.
No CAS snapshots or frozen pages are issued; oversized or unreadable traversals
return unavailable. Use `native_supplies_acceptance.py` through the standard
container scenario launcher for native quantity, completeness and paused-read checks.

Typed clock controls use the shared per-load attempt ledger and capture the original
authority grant, epoch owner and observation context before the initial safety probe.
Leases use monotonic time. Exact-owner pause remains available for cleanup after
revocation. Event reads expose immutable observed context and explicit history gaps;
missing evidence never becomes an empty successful observation. Existing untyped
epochs and journal rows cannot supply canonical ownership evidence.

Current source inventory: 73 production exports, 55 fixture exports, 136 handwritten
C# source files and nine generated Protobuf compile inputs. Source declarations do
not establish gameplay acceptance. Actual installed discovery must match the private
build and prove fixture exclusion; pending native acceptance remains explicit in
each ownership row.

Run the inventory checks:

```powershell
python scripts/check_native_inventory.py --check --self-test
python scripts/check_domain_inventory.py --check
python scripts/check_go_coverage.py
```

The checker rejects missing/new exports without ownership, the retired placement
alias, mismatched canonical method/schema links, changed native signatures or
fingerprints, and changed generated compile inputs. The runtime source index is
navigation for handwritten native ownership, not a second generated schema.

`scripts/native_package_acceptance.py` now exercises identity, authority readback
and placement through these fixed Protobuf capabilities. It retains the current
`home/status` paused-clock check, camera/building readback and saved component
census. Use it with `scripts/container_scenario.py`, prepared private inputs and a
fresh output. Its small ProtoJSON smoke assertions do not replace official-parser
boundary tests or the deeper `scripts/native_protobuf_acceptance.py` matrix.

The older `native_compatibility_acceptance.py` and compatibility evidence describe
the earlier saved-game/wire baseline. They are retained evidence tooling, not the
current standard package check; their old placement requests do not match this
cutover. No saved-game compatibility acceptance is required for this active-dev
slice.
