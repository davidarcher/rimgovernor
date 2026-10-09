# Native saved-state ownership inventory

Source baseline: `3fea7d5c`. This source audit contributes to
[N01](https://github.com/davidarcher/rimgovernor/issues?q=is%3Aissue+is%3Aopen+label%3A%22area%3AN01%22); it does not approve
state removal or establish save compatibility. Go coverage is tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38). N01.06 owns
the proposed migrations below.

## Coverage and interpretation

The source audit found nine component types and seven nested record types
(88 Scribe field/key pairs). Current sources live under
`integrations/rimgovernor-native/src/Runtime/Persistence`, compiled into the
early-loaded `RimGovernor.Runtime` assembly; headless code adds no saved fields.

The field tables retain the audit's proposed ownership and runtime-safety reasoning.
Old-save migration, retained CLR/assembly identities and compatibility tests are
out of scope under the active-development plan. New state may start clean. Native
writers remain until current consumers and live-job guards are replaced, rather
than until historical saves can be imported.

## Colony and timeline identity

Source: [identity/ColonyIdentity.cs](../integrations/rimgovernor-native/src/Runtime/Persistence/ColonyIdentity.cs).

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

No native lineage records. Go matches each applied building intent to the
blueprint, frame or building standing with its definition, stuff, anchor and
rotation (`policy.WorkOpen`); a match placed by anyone is the same end state.
`ConstructionLineageState` is an empty stand-in that drops any records on load.

## Guarded designations

Sources: [saved types](../integrations/rimgovernor-native/src/Runtime/Persistence/GuardState.cs),
[guard registry and hooks](../integrations/rimgovernor-native/src/Bridge/Protocol/Guards/NativeDesignationGuards.cs),
[wall-upgrade site check](../integrations/rimgovernor-native/src/Bridge/WallUpgradeSafety.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `GuardState.Records/rimgovernorGuardedDesignation` (deep `GuardedDesignation`) | Native guard | No; which designation a Designate placed under which guard. |
| `Guard/guard`, `Designation/designation`, `ExpectedDef/expectedDef`, `MapId/mapId`, `X/x`, `Z/z`, `ThingId/thingId` | Native guard | Fresh designation facts; the guard membership is not. |
| `Finished/finished`, `Cancelled/cancelled`, `Blocker/blocker` | Native guard | No; closed records are dropped on save. |
| `Wall/wallUpgrade` (deep `WallRemovalRecord`: site roles, backups, material, `Load`, `UiRevision`) | Native guard | No; old load/UI authority is never restored to a new load. |

One ledger holds every guarded designation: enclosure, mine_safety,
wall_upgrade and acquisition. A Mine record is keyed by cell and
rock definition, so compressed rock recreated with fresh ids on load stays
guarded. The job hooks re-check the guard before work lands; a failed check
drops the designation and closes the record. Revoking authority releases
every open record. Wall-upgrade admission is capped at 512 open records.

## Equipment ownership

Sources: [equipment operations](../integrations/rimgovernor-native/src/Bridge/GearUpkeepTool.cs).

No native saved state. The former `GearOwnership` component
(`rimgovernorUpkeepWeapons`, pawn unique-load-ID -> weapon unique-load-ID) was
write-only: upkeep replacement never gated on a recorded claim, and any pawn's
weapon, controller-equipped or player-equipped, is eligible for replacement
once it fails the quality/wear checks in `GearUpkeepTool.WeaponEligible`. The
controller's dispatch receipt is the only record of who ordered an equip; the
equipped item itself is observable. Native equip work can finish while
disconnected.

## Home coverage

Sources: [saved map state](../integrations/rimgovernor-native/src/Runtime/Persistence/HomeCoverageState.cs),
[mutation observations](../integrations/rimgovernor-native/src/Bridge/HomeCoverageTool.cs).

| Field/key | Sole target owner | Reconstructible? |
| --- | --- | --- |
| `Revision/rimgovernorHomeRevision` | Native per-map freshness token | Its value need not survive if load identity invalidates every old request. |

Native Home remains game-owned. Autonomous `AreaIntent` set_cells on home adds cells
within the observed batch of an owned facility's connected enclosed rooms
or an exact owned stockpile.
Set, Clear and Invert advance that revision. Removing Home during Manual
becomes restoration work after Auto resumes, under the
[Manual control contract](../docs/developers/architecture/control-loop.md#manual-control).
The `rimgovernorHomeInitialized` and
`rimgovernorHomeExcluded` save fields are neither read nor written, so restoration
is always autonomous. The wire field `excluded_cells` is emitted as zero.

Acceptance: `upkeep/home-coverage` proves connected coverage, restoration,
stale proposal refusal and native Home persistence across a save reload.

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
