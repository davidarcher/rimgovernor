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
| rimgovernor/observations_read_colony_facts | rimgovernor.observations.v1.Observations/ReadColonyFacts | Protocol/NativeColonyObservationTools.cs |
| rimgovernor/observations_get_cells | rimgovernor.observations.v1.Observations/GetCells | Protocol/NativeObservationTools.cs |
| rimgovernor/observations_list_buildings | rimgovernor.observations.v1.Observations/ListBuildings | Protocol/NativeBuildingObservationTools.cs |
| rimgovernor/observations_list_pawns | rimgovernor.observations.v1.Observations/ListPawns | Protocol/NativePawnObservationTools.cs |
| rimgovernor/observations_list_supplies | rimgovernor.observations.v1.Observations/ListSupplies | Protocol/NativeSuppliesObservationTools.cs |
| rimgovernor/observations_list_rooms | rimgovernor.observations.v1.Observations/ListRooms | Protocol/NativeRoomObservationTools.cs |
| rimgovernor/observations_read_research | rimgovernor.observations.v1.Observations/ReadResearch | Protocol/NativeResearchObservationTools.cs |
| rimgovernor/observations_read_bills | rimgovernor.observations.v1.Observations/ReadBills | Protocol/NativeBillObservationTools.cs |
| rimgovernor/observations_read_recipes | rimgovernor.observations.v1.Observations/ReadRecipes | Protocol/NativeBillObservationTools.cs |
| rimgovernor/operations_preview | rimgovernor.operations.v1.Operations/Preview | Protocol/NativeOperationTools.cs |
| rimgovernor/operations_execute | rimgovernor.operations.v1.Operations/Execute | Protocol/NativeOperationTools.cs |
| rimgovernor/operations_release_owned_draft | rimgovernor.operations.v1.Operations/ReleaseOwnedDraft | Protocol/NativeOperationTools.cs |
| rimgovernor/receipts_lookup | rimgovernor.receipts.v1.Attempts/Lookup | Protocol/NativeOperationTools.cs |
| rimgovernor/receipts_observe_progress | rimgovernor.receipts.v1.Attempts/ObserveProgress | Protocol/NativeOperationTools.cs |
| rimgovernor/clock_start | rimgovernor.clock.v1.Clock/Start | Protocol/NativeClockTools.cs |
| rimgovernor/clock_renew | rimgovernor.clock.v1.Clock/Renew | Protocol/NativeClockTools.cs |
| rimgovernor/clock_change_speed | rimgovernor.clock.v1.Clock/ChangeSpeed | Protocol/NativeClockTools.cs |
| rimgovernor/clock_pause | rimgovernor.clock.v1.Clock/Pause | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_status | rimgovernor.clock.v1.Clock/ReadStatus | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_events | rimgovernor.clock.v1.Clock/ReadEvents | Protocol/NativeClockTools.cs |
| rimgovernor/clock_read_attempt | rimgovernor.clock.v1.Clock/ReadAttempt | Protocol/NativeClockTools.cs |
| rimgovernor/presentation_camera | rimgovernor.presentation.v1.PresentationReads/Camera | Protocol/NativePresentationReadTools.cs |
| rimgovernor/presentation_selection | rimgovernor.presentation.v1.PresentationReads/Selection | Protocol/NativePresentationReadTools.cs |
| rimgovernor/presentation_colonists | rimgovernor.presentation.v1.PresentationReads/Colonists | Protocol/NativePresentationReadTools.cs |

Paths are under `integrations/rimgovernor-native/src/Bridge`. The shared
`Protocol/ProtoBoundary.cs` validates original outer arguments and uses official
Protobuf parsing/formatting. Generated compile inputs come from
`contracts/generated/protobuf/csharp`.

`Operations/Preview` and `Operations/Execute` implement ordinary `PlaceBuilding`
and temporary `SetDrafted` plus exact `MovePawn` and guarded `AttackTarget` under an existing owned draft;
`DesignateThing` supports only Allow on exact eligible loose supply snapshots;
other command variants return unsupported. Their presence does not advertise
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

`home/placement_previews` has been removed, with no alias. `home/colony_identity`
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

Presentation reads expose native graphical camera/selection facts and spawned
colonists on loaded maps. Camera and selection are explicitly unavailable in
headless mode. Selection does not enumerate gizmos or inspect tabs, issue captured
target fingerprints, move the camera or grant input permission. Optional facts
remain absent when not observed. Bounded collection/reply overflow refuses the read.

`Observations/ListPawns` provides a complete bounded map pawn census with exact
intersecting filters and explicit optional-false semantics. Core, needs, health,
equipment, biography, settings and animal details use native facts. Social,
gear ownership/protection and additional animal management fields carry explicit
issues. Requested detail sections never become fabricated empty tracker data.
Readable idle job trackers report explicit `playerForced=false` and the actual
queued-job count, with absent current-job identity. Missing trackers remain
unavailable; an absent current job never hides queued orders.

`Observations/ReadResearch` returns bounded project, progress and prerequisite
facts without initializing saved progress dictionaries or category slots. Optional
unlocks and map-local bench/researcher rows retain unknown optional fields. Native
selection eligibility uses colony-wide bench requirements, which ignore power.
Oversized collections refuse rather than truncate; no frozen paging or CAS token
is issued. `native_research_acceptance.py` uses a private read-only fingerprint
fixture to verify saved-state invariance before a separate native getter audit.

`Observations/ListRooms` returns complete bounded native room geometry, statistics
and memberships. Exact room/region filters and optional cells/boundary contents
preserve unknown optional facts through issues; oversized results refuse without
truncation. Room IDs identify the current graph, with no frozen pages or CAS.
Headless and rendered acceptance cross-check populated indoor structures against
native reads and preserve paused context. Populated bed, pawn and stockpile
memberships remain acceptance gaps.

Live current-map pawns expose opaque control snapshots and canonical owned/unowned
draft claims. Animals without draft controllers expose unowned target snapshots
and remain ineligible for drafting. Tokens cover identity, draft and successful
ordered-job revisions, position, eligibility and current/queued job facts; they
are not health or settings CAS. Registration installs hooks; reads do not install
hooks or allocate authority. `SetDrafted` uses ordinary admission and causal
readback. Already-owned drafting preserves its claim without another setter.
`ReleaseOwnedDraft` checks exact original ownership and unchanged snapshot after
revocation and keeps its latest cleanup replay outside the ordinary attempt ledger.
Persistent drafting remains unsupported.

`MovePawn` requires an exact reachable destination, current pawn snapshot and
matching live owner/claim. Its receipt identifies the actual issued job; queued
work remains pending until the pawn starts that job and reaches the destination.
Player orders, changed claims and authority generations interrupt attribution.
Causal synchronous scope tracking preserves cleanup ownership if an admitted order
outlives its lease; it never grants another write or revives the expired lease.

`AttackTarget` requires exact attacker and target snapshots, the attacker's current
owned draft, and ordinary native violence, reach and melee-verb eligibility.
Requested hostility, standing and colony-health predicates are checked before
dispatch. Melee and ordinary native direct-bullet ranged attacks are supported;
Auto resolves through the native weapon choice. Explosive, overhead, beam and
custom projectile/verb paths remain Unsupported. A receipt certifies
the issued job. Completion requires positive native damage from that exact melee
attack to cause death, or downing when a standing target was required. Unrelated
death is unsuccessful; a vanished job without causal evidence remains unknown.
Fresh headless acceptance verifies actual melee death, player-order interruption,
fresh owned recovery, immutable replay and completed-before-Manual retention.
Compiled checks cover unrelated/nested damage refusal and repair of each required
live hook; their callback states do not replace game acceptance of those cases.

Direct bullets retain exact projectile and originating attack identity at each
native launch. Only the native direct damage calls in `Bullet.Impact` acquire
attribution; notification callbacks, shields, misses and other targets do not.
Impact requires the original control/claim state, while ordinary job completion
after launch does not erase the projectile identity. Late hits after Manual do
not acquire completion evidence. Lost tracking is explicit uncertainty; later
independently tracked hits can still prove terminal outcomes. Headless native
acceptance verifies an ordinary assault rifle's attributed target death, player
override, fresh owned recovery and completed-before-Manual cleanup. Native shield,
callback side-damage, tracking-capacity and late-flight interruption scenarios
remain separate acceptance work; compiled tests cover their attribution guards.
The same package also passes native melee death and ownership recovery. Interrupted
notification/health trials remain failure evidence alongside the successful runs.

Source declarations do not establish gameplay acceptance. Actual installed
discovery must match the private build and prove fixture exclusion. Generated
compile inputs are checked by `task protobuf:build` (`.github/workflows/ci.yml`); identity,
authority readback, placement and wire acceptance through these fixed Protobuf
capabilities is Go native acceptance tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38). No saved-game
compatibility acceptance is required.
