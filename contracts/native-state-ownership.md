# Native saved-state ownership inventory

Source baseline: `3fea7d5c`. This source audit contributes to
[N01.00](../docs/BACKLOG.md#n01--unified-rimgovernor-native-mod); it does not approve
state removal or establish save compatibility. Existing controller persistence is
inventoried in [state-inventory.json](state-inventory.json). N01.06 and G01.04/G01.08
jointly own the proposed migrations below. The backlog remains the work queue.

## Coverage and interpretation

All C# sources in `integrations/colony-bridge` and `integrations/headless-rim` were
searched for `Scribe_`, `ExposeData`, `IExposable`, `GameComponent`, `MapComponent`,
`WorldComponent` and `ModSettings`. The saved surface comprises nine component
types and seven nested record types (88 Scribe field/key pairs), all in namespace `HomeBridge.BridgeTools` in
the early-loaded `RimGovernor.ColonyIdentity` assembly. The nine source links below
contain every discovered Scribe call. No headless-owned saved fields were found.

[ColonyObservations.csproj](../integrations/colony-bridge/src/ColonyObservations.csproj)
excludes `identity/**/*.cs` because its referenced
[ColonyIdentity.csproj](../integrations/colony-bridge/src/identity/ColonyIdentity.csproj)
compiles them separately. It also excludes `PawnSettingsRead.cs` and
`StockpileFilter.cs`; both were searched and neither serializes fields. The root
`src/ColonyIdentity.cs` exports `ColonyIdentityTools`, not another saved
component. Preserve assembly/type names and keys through N01.01. Do not delete
apparent duplicates based on the observation assembly's exclusion list.

In the field tables, `Field/key` gives the exact C# field and Scribe key; grouped
fields have identical ownership and reconstruction decisions. Collection rows
include their element/key meaning. **SQL** means the controller's SQLite store is
the sole proposed authority for that metadata. **Native** means a narrowly scoped
proposed native exception, not approval of the current entire component. Actual
game objects/settings always remain RimWorld-owned. **Fresh** means current values
can be observed but their historical association cannot be inferred. **No** means
ordinary current game state cannot reconstruct the field's historical meaning.
Each row inherits its family's disconnect, migration/removal and acceptance rule.

Native exception proposals below still need G01 agreement and isolated acceptance.
Existing saves must keep their current behavior until that gate passes. No copied
save, DLL discovery, native load or disconnected gameplay was executed for this
documentation slice; current-behavior statements are source findings.

## Colony and timeline identity

Source: [identity/ColonyIdentity.cs](../integrations/colony-bridge/src/identity/ColonyIdentity.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `ColonyId/rimgovernorColonyId` | Native minimal timeline marker | No; generate only for a genuinely unassociated save. |

`LoadToken` is a readonly fresh GUID and is deliberately not serialized. Missing
colony IDs are populated after loading. The current saved ID alone cannot identify
which branched snapshot a save represents; adding ticks does not solve that.

Disconnect: the native save remains independently playable; reattachment enters
Manual. Migration target: retain `ColonyId`, extend a minimal versioned snapshot
association agreed with G01.04/G01.08, and keep controller history solely in SQL.
Do not treat a rotated load token as proof that newer database history belongs to
this snapshot. Required acceptance: same colony/tick in two branches, ordinary
save/autosave, old save against newer SQL, missing database and missing identity;
ambiguous association must hold and preserve newer history separately.

## Construction lineage

Sources: [saved types](../integrations/colony-bridge/src/identity/ConstructionLineageState.cs),
[transition hooks](../integrations/colony-bridge/src/ConstructionLineage.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `ConstructionLineageState.Records/rimgovernorConstructionLineage` (deep `ConstructionLineageRecord`) | SQL | No; container of historical associations. |
| `Origin/origin` | SQL | No after the blueprint is replaced. |
| `Current/current` | SQL | Fresh object ID; origin-to-current association requires evidence. |
| `Definition/definition`, `Stuff/stuff` | SQL | Fresh while matching object survives; original specification is historical. |
| `Stage/stage` | SQL | Fresh object stage, not historical completion of an owned action. |
| `Blocker/blocker`, `Failures/failures` | SQL | No; ambiguous transitions and prior failed builds are history. |
| `MapId/mapId`, `X/x`, `Z/z`, `Rotation/rotation` | SQL | Fresh for surviving objects; original association is historical. |
| `Started/started` | SQL | No. |

Current hooks follow blueprint -> frame -> building and failed frame -> blueprint.
Registration refuses above 4096 records. Hooks update records without controller
connectivity while installed; reads compare current objects with recorded geometry.
Migration: consume typed identity-transition events durably in SQL, then remove
this authoritative native list. Retain a bounded native delivery journal only if
missed transitions cannot safely be reconstructed; it may not become a plan ledger.
Disconnect must retain delivery evidence or explicitly report a gap, never infer
that a similar building at the same cell completed this action.

Acceptance gap: disconnect before each transition, including a failed build;
destroy/rebuild the same definition at the same cell; save/load at each stage;
overflow and crash around event acknowledgement. Verify no substituted ownership
and no duplicated construction. Existing entry points include
`scripts/construction_resume_acceptance.py` and
`scripts/construction_recovery_acceptance.py`; these do not certify the new journal.

## Hauling and quantity identity

Sources: [saved types](../integrations/colony-bridge/src/identity/HaulTrackingState.cs),
[quantity hooks](../integrations/colony-bridge/src/HaulTracking.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `HaulTrackingState.Records/rimgovernorHaulTracking` (deep `HaulRecord`) | SQL | No. |
| `HaulRecord.Id/id`, `Source/source`, `Pawn/pawn` | SQL | No for admitted association; surviving pawn/source IDs are fresh evidence only. |
| `Definition/definition`, `MapId/mapId` | SQL | Fresh; original source association is historical. |
| `OriginalCount/originalCount`, `RequiredCount/requiredCount` | SQL | No after splits/merges, including mixed untracked stock. |
| `Started/started`, `CompletedTick/completedTick` | SQL | No. |
| `Accepted/accepted`, `Complete/complete`, `Blocker/blocker` | SQL | No; current roofed stock does not prove original delivery. |
| `Portions/portions` (deep `HaulPortion`) | SQL | No; tracks lineage of conservatively expanded mixed stacks. |
| `HaulPortion.Id/id`, `Count/count` | SQL | Fresh current stacks/counts, not the tracked-source association. |

`HaulPortion.Cached` is an unsaved `Thing` cache; reads can resolve it from map,
inventory and carried things. Current hooks observe merge/split/destruction/spawn;
reads also check completion. Limits are 512 records and 128 portions per active
record. Destruction before protection sets a blocker rather than completion.

Migration/removal: replace the saved action/quantity list with SQL evidence plus
the bounded transition delivery contract. While disconnected, native hooks must
capture necessary transitions or mark an evidence gap. Current stack counts alone
must not be used to allocate surviving mixed units to a destroyed original source.
Acceptance gap: partial merges into tracked and untracked stacks, split during
disconnect, carried stack save/load, loss before delivery, duplicate events and
overflow; prove quantity conservation and distinguish protection from loss.
Existing entry points: `scripts/resumed_haul_acceptance.py` and
`scripts/supply_recovery_acceptance.py`.

## Mining and drilling

Sources: [saved types/rebind](../integrations/colony-bridge/src/identity/MiningState.cs),
[mining guard](../integrations/colony-bridge/src/MiningGuard.cs),
[current contract](../docs/reference/mining-contracts.md).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `MiningState.Records/rimgovernorMining` (deep `MiningRecord`) | SQL | No. |
| `MiningState.Drills/rimgovernorDrilling` (deep `DrillingRecord`) | SQL | No. |
| `MiningRecord.ThingId/thingId` | SQL | Fresh; compressed rocks may receive new IDs on load. |
| `SourceId/sourceId` | SQL | No; stable historical source association. |
| `Definition/definition`, `Resource/resource`, `MapId/mapId`, `X/x`, `Z/z` | SQL | Fresh existing rock/deposit facts; original admission is historical. |
| `RebindVerified/rebindVerified`, `SavedHitPoints/savedHitPoints` | SQL | No for the exact prior save; fresh HP does not prove identity. |
| `Started/started`, `Finished/finished`, `Recovered/recovered` | SQL | No; completion tick and measured output increase are historical. |
| `Cancelled/cancelled`, `Blocker/blocker` | SQL | No; current designation absence cannot establish who cancelled it. |
| `DrillingRecord.Definition/definition`, `Resource/resource`, `MapId/mapId`, `X/x`, `Z/z` | SQL | Fresh facility/deposit facts, not ownership. |
| `ThingId/thingId`, `PendingId/pendingId` | SQL | Fresh surviving object ID; blueprint/frame/building association requires events. |
| `Recovered/recovered` | SQL | No; current stock is affected by consumption and other production. |

Current saving verifies pending rocks by ID/cell/definition and captures HP;
`FinalizeInit` rebinds verified records only to matching cell/definition/HP, otherwise
cancels. Mining hooks recheck native safety on hits and measure output at destruction.
`DrillingRecord.Target` is unsaved, re-admitted policy; drilling supervision and
ordinary player simulation have different guard behavior (see the linked contract).

Migration/removal: move records and output history to SQL. Before removing save-time
rebind fields, establish timeline-associated identity evidence that handles compressed
rocks; if required, justify a minimal save-local rebind witness as a separate native
exception. Do not assume present coordinates/HP uniquely identify a historical rock.
Keep only per-load native eligibility configuration, with safe retirement of already
admitted work before forgetting which targets need protection. Disconnected ordinary
player work must remain distinct from fresh automated admission and cannot fabricate
recovered output. Required acceptance: compressed-rock reload, replacement at the
same cell with same HP, cancelled designation, missing SQL, disconnected destruction,
drill frame completion, depletion and player drill replacement. Existing entry points:
`scripts/mining_acceptance.py` and `scripts/deep_mining_acceptance.py`.

## Wall replacement

Sources: [saved types](../integrations/colony-bridge/src/identity/WallRemovalState.cs),
[guard and release](../integrations/colony-bridge/src/WallUpgradeTool.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `WallRemovalState.Records/rimgovernorWallRemoval` (deep `WallRemovalRecord`) | SQL | No. |
| `Id/id` | SQL | No; native removal receipt identity. |
| `Target/target`, `Original/original`, `Left/left`, `Right/right`, `Permanent/permanent` | SQL | Fresh surviving IDs, not their historical roles or authorization. |
| `Backup/backup` (list of string wall IDs) | SQL | Fresh matching enclosure, not owned temporary-wall history. |
| `Material/material`, `MapId/mapId`, `X/x`, `Z/z`, `Nx/nx`, `Nz/nz` | SQL | Fresh geometry/material; planned orientation and original association are historical. |
| `Load/load`, `UiRevision/uiRevision` | SQL | No; old authority must never be restored to a new load. |
| `CompletedTick/completedTick`, `Complete/complete` | SQL | No; missing wall is not proof of safe owned demolition. |
| `PlayerOwned/playerOwned`, `Retired/retired`, `Blocker/blocker` | SQL | No; designation ownership/cancellation history. |

Current guards require supervision, matching load/UI revision and safe enclosure,
support and resource facts. A replaced designation retires controller ownership.
Release keeps records to guard already-running jobs after removing designations;
registration is capped at 512. This is a concrete reason not to simply delete the
component after copying its list into SQL.

Migration/removal: SQL owns removal intent and completion; a per-load native guard
must survive disconnect long enough to stop owned jobs. If ordinary save/load needs
a minimal deny/cleanup marker for admitted jobs, demonstrate that exception before
deleting old records. Never serialize reusable load/UI authority. Acceptance gap:
disconnect just before final removal, save/load a running job, Manual/UI interruption,
player re-designation, removed backup and missing SQL; verify intact roof/enclosure
and no duplicate demolition. Entry points: `scripts/wall_upgrade_acceptance.py` and
`scripts/wall_material_acceptance.py`.

## Production limits

Sources: [saved fields](../integrations/colony-bridge/src/identity/ProductionPolicyState.cs),
[selection/consumption guards](../integrations/colony-bridge/src/ProductionPolicyTool.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `Floors/rimgovernorProductionFloors` (map-ID/resource string -> integer reserve) | SQL policy; native enforcement only | No; desired reserve is not current stock. |
| `Stopped/rimgovernorProductionStopped` (map-ID/resource strings) | SQL policy; native enforcement only | No; spending prohibition is not a bill setting. |

`Commitments` is an unsaved map/resource count dictionary, applied only during
supervision. In contrast, saved `Floors` and `Stopped` affect ingredient selection
and consumption outside supervision too. Vanilla bills, ingredients and settings
remain game-owned; do not import them as controller ownership.

Migration/removal: lease configuration from SQL, but retire or safely hold admitted
consumers before expiring a floor/stop. A minimal native safety latch may be needed
on load/disconnect; its lifetime and release must be defined independently of the
authoritative policy. Do not drop the current saved protections merely because
the database or controller is absent. Required acceptance: disconnect at ingredient
selection and final consumption, carried/unfinished ingredients, load without SQL,
expired commitments with active jobs and explicit player policy changes. Prove
stock limits and player bill filters survive. Existing fixture neighbor:
`controller_tests/test_production_policy.py`; fixtures alone cannot close this gate.

## Equipment ownership

Sources: [saved map](../integrations/colony-bridge/src/identity/GearOwnership.cs),
[equipment operations](../integrations/colony-bridge/src/GearUpkeepTool.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `Weapons/rimgovernorUpkeepWeapons` (pawn unique-load-ID -> weapon unique-load-ID) | SQL | No; equipped item is observable, who assigned it is not. |

Current code records the claim when an equip job becomes current and checks the
exact primary weapon before upkeep replacement. The component itself has no tick
writer; native equip work can finish while disconnected. Migration: import claims
as untrusted historical evidence into matched SQL, then remove the saved map and
require fresh native evidence plus SQL ownership for replacement. Missing SQL must
preserve equipped weapons as player-owned. Required acceptance: lost equip receipt,
player swap and swap-back, older branch, pending equip save/load and missing SQL;
never claim a weapon solely because it matches an old ID. Existing entry point:
`scripts/gear_upkeep_acceptance.py`.

## Recovery areas

Sources: [saved claims and cleanup](../integrations/colony-bridge/src/identity/RecoveryAreas.cs),
[setter ownership invalidation](../integrations/colony-bridge/src/RecoveryAreaOwnership.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `RecoveryAreas.Claims/rimgovernorRecoveryAreas` (deep `RecoveryAreaClaim`) | Native minimal cleanup obligation; SQL owns intent/history | No for previous settings. |
| `RecoveryAreaClaim.Pawn/pawn` (reference) | Native cleanup target | Fresh pawn, not cleanup association. |
| `Before/before`, `Assigned/assigned` (area references) | Native compare-and-restore obligation | Current area is fresh; prior area and assignment provenance are not. |
| `Owner/owner`, `Until/until` | Native cleanup expiry discriminator | No; must not grant resumed automation authority. |
| `RecoveryAreas.Overrides/rimgovernorRecoveryAreaOverrides` (pawn references) | SQL override history | No; a player setter may restore the same value. |

Current tick cleanup restores only an unchanged assigned area on the matching map,
when the tick deadline expires, load token changes or toxic fallout ends. A pawn
on another map waits until return. Missing pawn/settings/assigned area removes the
claim; an observed changed assignment records an override. Patched setters also
invalidate claims, including a same-value setter.

Proposed native exception: the five claim fields are a bounded cleanup obligation
needed when the controller is absent, not a second decision ledger. Reduce them only
after demonstrating independent restoration with player ownership preserved. Migrate
override history to SQL through durable player-change events; a missing database
must not allow old claims to be reacquired. Required acceptance: absent controller,
load before expiry, player same-value setter, removed area/pawn, map departure/return
and fallout ending while disconnected. Test the retained cleanup path without any
bridge tool discovery. Existing entry point: `scripts/disaster_recovery_acceptance.py`.

## Home exclusions

Sources: [saved map state](../integrations/colony-bridge/src/identity/HomeCoverageState.cs),
[mutation observations](../integrations/colony-bridge/src/HomeCoverageTool.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `Excluded/rimgovernorHomeExcluded` (`BoolGrid`, deep) | SQL player-exclusion history | No; unset Home cells do not distinguish deliberate removal from never-covered cells. |
| `Initialized/rimgovernorHomeInitialized` | SQL observation-baseline metadata | No; whether omissions were previously classified is historical. |
| `Revision/rimgovernorHomeRevision` | Native per-load freshness token, unsaved target | No need to preserve its value if load identity invalidates all old requests. |

Current `State` classifies initial facility omissions conservatively; installed
patches record Home removal, clear and invert, incrementing the saved revision.
This tracking can run without controller connectivity. Actual Home area stays
game-owned. Migration/removal: deliver exclusion changes to SQL durably; when
history is missing/gapped, conservatively treat existing omissions as excluded and
require fresh admission. Drop the saved revision only after requests bind freshness
to load/map plus a per-load revision. No native exclusion-history exception is
approved merely to preserve the old implementation.

Required acceptance: disconnect across clear/invert and remove/re-add, restart with
missing SQL, old save against newer exclusions, changed facility geometry and stale
same-number revision from another load. Never expand Home across a player omission
because an event was lost. Existing entry point: `scripts/home_coverage_acceptance.py`.

## Shared migration gate

All SQL rows require a versioned, idempotent legacy import tied to the exact saved
timeline, with source provenance and conflict handling. Disagreement creates a
hold, not merged completion. Retain old readers/types until supported saves have
an accepted migration; remove writers only after consumer readiness. Rollback
requires the old package and pre-migration save/database pair unless reverse
compatibility has actually passed.

Any retained native event journal must define event IDs, snapshot/branch identity,
ordering, duplicate delivery, missing ranges, capacity, replay and retention.
Acknowledge only after the SQL transaction commits. Test crashes on both sides of
that commit/ack boundary, overflow, lost acknowledgements and old-save replay against
newer SQL. These are missing compatibility acceptance cases, not claimed features.
Scenario entry points above are reuse candidates; retained fixtures and actual
unified-package acceptance remain N01.00/N01.06 backlog work.
